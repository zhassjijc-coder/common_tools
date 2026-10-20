using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using LinkLab.Core;
using Microsoft.Win32;

namespace LinkLab.App;

public sealed class WorkbenchVm : PageVm
{
    public QueryVm Query { get; } = new();
    public string[] Modes { get; } = ["Modbus RTU", "Modbus TCP", "自定义 HEX"];
    private int _mode;
    [Saved] public int Mode { get => _mode; set { Set(ref _mode, value); Raise(nameof(IsModbus)); Raise(nameof(IsCustom)); } }
    public bool IsModbus => Mode != 2;
    public bool IsCustom => !IsModbus;
    [Saved] public string Transaction { get; set; } = "1";
    [Saved] public string Custom { get; set; } = "01 03 00 00 00 0A";
    [Saved] public ChecksumKind Checksum { get; set; } = ChecksumKind.Crc16Modbus;
    public ChecksumKind[] Checksums { get; } = Enum.GetValues<ChecksumKind>();
    private string _frame = "01 03 00 00 00 0A C5 CD";
    public string Frame { get => _frame; set { Set(ref _frame, value); Raise(nameof(LengthText)); } }
    public string LengthText { get { try { return $"{ByteTools.ParseHex(Frame).Length} 字节 · 可编辑后发送"; } catch { return "HEX 格式待修正"; } } }
    private string _details = "站号 01 / 功能 03 / 地址 0000 / 数量 000A / CRC C5 CD";
    public string Details { get => _details; set => Set(ref _details, value); }
    public SessionChoice? SelectedSession { get; set; }
    public string SelectedPeer { get; set; } = "";
    public Command GenerateCommand { get; }
    public Command CopyCommand { get; }
    public Command SaveCommand { get; }
    public Command LoadCommand { get; }
    public AsyncCommand SendCommand { get; }
    public WorkbenchVm(Hub hub) : base(hub)
    {
        GenerateCommand = new(() => Run(Generate));
        CopyCommand = new(() => Run(() => { Clipboard.SetText(Frame); Hub.Notice = "报文已复制"; }));
        SaveCommand = new(() => Run(Save)); LoadCommand = new(() => Run(Load));
        SendCommand = Async(Send);
    }
    private void Run(Action action) { try { action(); } catch (Exception ex) { Hub.Error(ex); } }
    public void Generate()
    {
        byte[] bytes;
        if (IsModbus)
        {
            var request = Query.Build();
            bytes = Mode == 0 ? request.Rtu() : request.Tcp((ushort)Int(Transaction, 0, 65535, "事务 ID"));
            Details = ModbusProtocol.Describe(bytes, Mode == 1) + $"\n请求地址：{request.Address}  |  数量：{request.Quantity}";
        }
        else
        {
            bytes = ByteTools.AppendChecksum(ByteTools.ParseHex(Custom), Checksum);
            Details = $"自定义数据 + {Checksum}\nCRC16-Modbus 和 CRC32 按低字节在前追加；其他校验为单字节。";
        }
        Frame = ByteTools.Hex(bytes); Hub.Notice = "报文已生成，确认目标连接后可发送";
    }
    private async Task Send()
    {
        var choice = SelectedSession ?? throw new InvalidOperationException("请先在串口或网络模块建立连接，并选择目标连接。");
        byte[] bytes = ByteTools.ParseHex(Frame);
        if (bytes.Length is < 1 or > 65507) throw new ArgumentException("报文长度应为 1–65507 字节。");
        await choice.Transport.SendAsync(bytes, SelectedPeer); Hub.Notice = $"已通过 {choice.Name} 发送 {bytes.Length} 字节";
    }
    private sealed record FrameTemplate(string Name, string Hex, string Description);
    private void Save()
    {
        _ = ByteTools.ParseHex(Frame);
        var dialog = new SaveFileDialog { Filter = "设备联调工具报文模板|*.json", FileName = "设备联调工具-报文模板.json" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new FrameTemplate(Path.GetFileNameWithoutExtension(dialog.FileName), Frame, Details), new JsonSerializerOptions { WriteIndented = true }));
        Hub.Notice = "模板已保存";
    }
    private void Load()
    {
        var dialog = new OpenFileDialog { Filter = "设备联调工具报文模板|*.json" }; if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 2_000_000) throw new ArgumentException("模板文件过大。");
        var template = JsonSerializer.Deserialize<FrameTemplate>(File.ReadAllText(dialog.FileName)) ?? throw new FormatException("模板内容为空。");
        _ = ByteTools.ParseHex(template.Hex); Mode = 2; Custom = template.Hex; Checksum = ChecksumKind.None;
        Raise(nameof(Custom)); Raise(nameof(Checksum)); Frame = template.Hex; Details = template.Description; Hub.Notice = "模板已载入";
    }
}

public sealed class MonitorVm : PageVm
{
    public ICollectionView View { get; }
    private string _filter = "", _direction = "全部", _display = "HEX";
    private bool _paused;
    public string Filter { get => _filter; set { Set(ref _filter, value); View.Refresh(); } }
    public string Direction { get => _direction; set { Set(ref _direction, value); View.Refresh(); } }
    public string Display { get => _display; set => Set(ref _display, value); }
    public string[] Directions { get; } = ["全部", "TX", "RX", "ERR"];
    public string[] Displays { get; } = ["HEX", "文本"];
    public bool Paused { get => _paused; set { Set(ref _paused, value); Raise(nameof(PauseText)); } }
    public string PauseText => Paused ? "恢复自动滚动" : "暂停自动滚动";
    private LogRow? _selected;
    public LogRow? Selected { get => _selected; set { Set(ref _selected, value); if (value is not null) { Input = value.Hex; Raise(nameof(Input)); Details = $"{value.Time} · {value.Source} · {value.Peer}\n{value.Note}\nUTF-8：{value.Text}"; } } }
    [Saved] public string Input { get; set; } = "01 03 00 00 00 0A C5 CD";
    public string[] Protocols { get; } = ["Modbus RTU", "Modbus TCP"];
    public int Protocol { get; set; }
    private string _details = "选择记录可查看完整数据；也可粘贴 HEX 进行解析。";
    public string Details { get => _details; set => Set(ref _details, value); }
    [Saved] public string NumberInput { get; set; } = "1234";
    private string _numberResult = "十进制 1234  /  十六进制 0x04D2";
    public string NumberResult { get => _numberResult; set => Set(ref _numberResult, value); }
    public Command PauseCommand { get; }
    public Command ClearCommand { get; }
    public Command ExportCommand { get; }
    public Command ImportCommand { get; }
    public Command AnalyzeCommand { get; }
    public Command ChecksumsCommand { get; }
    public Command NumberCommand { get; }
    public Command CopyCommand { get; }
    public MonitorVm(Hub hub) : base(hub)
    {
        View = new ListCollectionView(hub.Logs);
        View.Filter = o => o is LogRow x && (Direction == "全部" || x.Direction == Direction) && (string.IsNullOrWhiteSpace(Filter) || $"{x.Hex} {x.Text} {x.Source} {x.Peer} {x.Note}".Contains(Filter, StringComparison.OrdinalIgnoreCase));
        PauseCommand = new(() => Paused = !Paused); ClearCommand = new(hub.Clear);
        ExportCommand = new(() => Safe(Export)); ImportCommand = new(() => Safe(Import));
        AnalyzeCommand = new(() => Safe(() => Details = ModbusProtocol.Describe(ByteTools.ParseHex(Input), Protocol == 1)));
        ChecksumsCommand = new(() => Safe(Checksums)); NumberCommand = new(() => Safe(Number));
        CopyCommand = new(() => Safe(() => Clipboard.SetText(Input)));
    }
    private void Safe(Action action) { try { action(); } catch (Exception ex) { Hub.Error(ex); } }
    private void Checksums()
    {
        byte[] data = ByteTools.ParseHex(Input);
        Details = $"对输入的全部 {data.Length} 字节计算：\nCRC16-Modbus：0x{ByteTools.Crc16(data):X4}（低字节先发）\nCRC32/ISO-HDLC：0x{ByteTools.Crc32(data):X8}\n" + string.Join("  /  ", new[] { ChecksumKind.Sum8, ChecksumKind.Xor8, ChecksumKind.Lrc }.Select(k => $"{k}：{ByteTools.AppendChecksum(data, k)[^1]:X2}"));
    }
    private void Number()
    {
        int value = ByteTools.Number(NumberInput); uint raw = unchecked((uint)value);
        NumberResult = $"十进制：{value}\nHEX：0x{raw:X8}\n二进制：{Convert.ToString(value, 2).PadLeft(32, '0')}\n小端：{ByteTools.Hex(BitConverter.GetBytes(value))}\n大端：{ByteTools.Hex(BitConverter.GetBytes(value).Reverse().ToArray())}";
    }
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = "CSV 表格|*.csv|JSON 报文记录|*.json", FileName = $"设备联调工具-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog() != true) return;
        var rows = View.Cast<LogRow>().ToArray();
        if (Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(rows.Select(x => x.Entry), new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            static string Csv(string s) { if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
            var csv = new StringBuilder("时间,模块,方向,对端,字节数,HEX,文本,备注\r\n");
            foreach (var r in rows) csv.AppendLine(string.Join(",", new[] { r.Entry.Time.ToString("O"), r.Source, r.Direction, r.Peer, r.Length.ToString(), r.Hex, r.Text, r.Note }.Select(Csv)));
            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
        }
        Hub.Notice = $"已导出当前筛选的 {rows.Length} 条记录";
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "设备联调工具 JSON 报文记录|*.json" }; if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 32_000_000) throw new ArgumentException("导入文件最大为 32 MB。");
        var rows = JsonSerializer.Deserialize<WireEvent[]>(File.ReadAllText(dialog.FileName)) ?? [];
        foreach (var e in rows.TakeLast(2000)) if (e.Data is not null) Hub.Record(e with { Source = "导入 · " + e.Source });
        Hub.Notice = $"已载入 {Math.Min(rows.Length, 2000)} 条离线记录（数据不会发送）";
    }
}
