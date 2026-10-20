using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LinkLab.Core;
using Microsoft.Win32;

namespace LinkLab.App;

public sealed record MqttSubscription(string Topic, int Qos);
public sealed class MqttVm : PageVm
{
    private MqttSession? _session;
    private CancellationTokenSource? _stop;
    private Task? _monitor;
    private Task? _disconnectTask;
    private readonly ConcurrentQueue<MqttRecord> _pending = new();
    private readonly DispatcherTimer _timer;
    private bool _closing;
    private long _dropped;
    private string _status = "配置 Broker 后连接 · 默认不会连接公共服务器", _filter = "", _direction = "全部", _display = "Text";
    private MqttRecord? _selected;
    [Saved] public string Host { get; set; } = "127.0.0.1";
    [Saved] public string Port { get; set; } = "1883";
    [Saved] public string ClientId { get; set; } = "LinkLab-" + Guid.NewGuid().ToString("N")[..8];
    [Saved] public int Transport { get; set; }
    [Saved] public int Version { get; set; } = 1;
    [Saved] public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    [Saved] public string KeepAlive { get; set; } = "60";
    [Saved] public bool CleanSession { get; set; } = true;
    [Saved] public string SessionExpiry { get; set; } = "3600";
    [Saved] public string Timeout { get; set; } = "5000";
    [Saved] public bool AutoReconnect { get; set; } = true;
    [Saved] public string CaFile { get; set; } = "";
    [Saved] public string ClientCertificate { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    [Saved] public string WillTopic { get; set; } = "";
    [Saved] public string WillPayload { get; set; } = "offline";
    [Saved] public int WillQos { get; set; }
    [Saved] public bool WillRetain { get; set; }
    [Saved] public string SubscribeTopic { get; set; } = "linklab/#";
    [Saved] public int SubscribeQos { get; set; } = 1;
    [Saved] public string PublishTopic { get; set; } = "linklab/device/status";
    private string _payload = "{\n  \"device\": \"demo-01\",\n  \"temperature\": 23.5,\n  \"online\": true\n}";
    [Saved] public string Payload { get => _payload; set => Set(ref _payload, value); }
    [Saved] public string PayloadFormat { get; set; } = "JSON";
    [Saved] public int PublishQos { get; set; } = 1;
    [Saved] public bool Retain { get; set; }
    public string[] Transports { get; } = ["TCP", "TLS", "WebSocket", "WebSocket TLS"];
    public string[] Versions { get; } = ["MQTT 3.1.1", "MQTT 5.0"];
    public int[] QosOptions { get; } = [0, 1, 2];
    public string[] Formats { get; } = ["Text", "JSON", "HEX"];
    public string[] Directions { get; } = ["全部", "RX", "TX"];
    public bool Active => _session is not null || _disconnectTask is { IsCompleted: false };
    public bool Connected => _session?.Connected == true;
    public bool CanConfigure => !Active;
    public string ConnectText => Active ? "断开连接" : "连接 Broker";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string CountText => $"{Messages.Count} 条消息 · 最近 500 条 · 丢弃显示 {_dropped} 条";
    public ObservableCollection<MqttSubscription> Subscriptions { get; } = [];
    public MqttSubscription? SelectedSubscription { get; set; }
    public ObservableCollection<MqttRecord> Messages { get; } = [];
    public ICollectionView MessageView { get; }
    public string Filter { get => _filter; set { Set(ref _filter, value); MessageView.Refresh(); } }
    public string Direction { get => _direction; set { Set(ref _direction, value); MessageView.Refresh(); } }
    public string Display { get => _display; set { Set(ref _display, value); Raise(nameof(Detail)); } }
    public MqttRecord? Selected { get => _selected; set { Set(ref _selected, value); Raise(nameof(Detail)); } }
    public string Detail
    {
        get
        {
            if (Selected is null) return "选择消息查看完整负载（最多展示 64 KiB）。";
            if (Display == "HEX") return Selected.Hex;
            if (Display == "JSON") { try { using var json = JsonDocument.Parse(Selected.Payload); return JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }); } catch (JsonException) { return "此消息不是完整的 JSON：\n" + Selected.Text; } }
            return Selected.Text;
        }
    }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand SubscribeCommand { get; }
    public AsyncCommand UnsubscribeCommand { get; }
    public AsyncCommand PublishCommand { get; }
    public Command ClearCommand { get; }
    public Command FormatCommand { get; }
    public Command ExportCommand { get; }
    public Command SaveProfileCommand { get; }
    public Command LoadProfileCommand { get; }
    public Command CaCommand { get; }
    public Command CertificateCommand { get; }
    public MqttVm(Hub hub) : base(hub)
    {
        MessageView = CollectionViewSource.GetDefaultView(Messages);
        MessageView.Filter = o => o is MqttRecord m && (Direction == "全部" || m.Direction == Direction) && (string.IsNullOrEmpty(Filter) || m.Topic.Contains(Filter, StringComparison.OrdinalIgnoreCase) || m.Text.Contains(Filter, StringComparison.OrdinalIgnoreCase));
        ConnectCommand = Async(() => Active ? Disconnect() : Connect()); DisconnectCommand = Async(Disconnect);
        SubscribeCommand = Async(Subscribe); UnsubscribeCommand = Async(Unsubscribe); PublishCommand = Async(Publish);
        ClearCommand = Safe(() => { while (_pending.TryDequeue(out _)) { } Messages.Clear(); Selected = null; Raise(nameof(CountText)); });
        FormatCommand = Safe(() => { using var doc = JsonDocument.Parse(Payload); Payload = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }); });
        ExportCommand = Safe(Export); SaveProfileCommand = Safe(SaveProfile); LoadProfileCommand = Safe(LoadProfile);
        CaCommand = Safe(() => { var d = new OpenFileDialog { Filter = "CA 证书 PEM|*.pem;*.crt;*.cer|所有文件|*.*" }; if (d.ShowDialog() == true) { CaFile = d.FileName; Raise(nameof(CaFile)); } });
        CertificateCommand = Safe(() => { var d = new OpenFileDialog { Filter = "客户端证书 PFX / P12|*.pfx;*.p12" }; if (d.ShowDialog() == true) { ClientCertificate = d.FileName; Raise(nameof(ClientCertificate)); } });
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; _timer.Tick += (_, _) => Flush(); _timer.Start();
    }
    private Command Safe(Action action) => new(() => { try { action(); } catch (Exception e) { Hub.Error(e); } });
    public MqttSettings Settings() => new(Host, Int(Port, 1, 65535, "端口"), ClientId, Transport, Version == 1, Username, Password,
        Int(KeepAlive, 5, 65535, "Keep Alive"), CleanSession, Int(SessionExpiry, 0, 604800, "会话过期"), Int(Timeout, 100, 60000, "超时"), CaFile, ClientCertificate, CertificatePassword, WillTopic, WillPayload, WillQos, WillRetain);
    public async Task Connect()
    {
        var settings = Settings(); if (Active) throw new InvalidOperationException("当前配置已连接或正在重连。");
        var session = new MqttSession(); var stop = new CancellationTokenSource(); _session = session; _stop = stop; Status = "正在连接 Broker…"; Changed();
        session.Message += m => { if (_pending.Count >= 1000) { Interlocked.Increment(ref _dropped); return; } _pending.Enqueue(m); };
        session.Status += s => Application.Current.Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_session, session) && !_closing) { Status = s; Changed(); } });
        try
        {
            await session.ConnectAsync(settings, stop.Token);
            if (!ReferenceEquals(_session, session)) return;
            foreach (var subscription in Subscriptions.ToArray()) await session.SubscribeAsync(subscription.Topic, subscription.Qos, stop.Token);
            if (!ReferenceEquals(_session, session)) return;
            _monitor = Monitor(session, stop.Token); Changed();
        }
        catch { if (ReferenceEquals(_session, session)) await Disconnect(); throw; }
    }
    private async Task Monitor(MqttSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct); Changed();
                if (!session.Connected && AutoReconnect)
                {
                    Status = "连接断开，正在重连…";
                    try { await session.ReconnectAsync(ct); } catch (Exception ex) when (ex is not OperationCanceledException) { Status = $"重连失败，稍后再试 · {ex.Message}"; }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task Subscribe()
    {
        var session = _session ?? throw new InvalidOperationException("请先连接 Broker。"); string topic = SubscribeTopic; int qos = SubscribeQos;
        await session.SubscribeAsync(topic, qos); var old = Subscriptions.FirstOrDefault(x => x.Topic == topic); if (old is not null) Subscriptions.Remove(old); Subscriptions.Add(new(topic, qos));
    }
    private async Task Unsubscribe()
    {
        var item = SelectedSubscription ?? throw new InvalidOperationException("请先选择要取消的订阅。");
        await (_session ?? throw new InvalidOperationException("请先连接 Broker。")).UnsubscribeAsync(item.Topic); Subscriptions.Remove(item);
    }
    public async Task Publish()
    {
        byte[] payload;
        if (PayloadFormat == "HEX") payload = ByteTools.ParseHex(Payload);
        else { if (PayloadFormat == "JSON") { using var doc = JsonDocument.Parse(Payload); } payload = Encoding.UTF8.GetBytes(Payload); }
        await (_session ?? throw new InvalidOperationException("请先连接 Broker。")).PublishAsync(PublishTopic, payload, PublishQos, Retain);
    }
    public void Flush()
    {
        int n = 0;
        while (n++ < 100 && _pending.TryDequeue(out var m))
        {
            Messages.Add(m); while (Messages.Count > 500) Messages.RemoveAt(0);
            Hub.Record(new(m.Time, "MQTT 负载", m.Direction, m.Payload, m.Topic, m.Flags));
        }
        Raise(nameof(CountText));
    }
    public Task Disconnect()
    {
        if (_disconnectTask is { IsCompleted: false }) return _disconnectTask;
        var session = _session; if (session is null) return Task.CompletedTask;
        var stop = _stop; var monitor = _monitor; _session = null; _stop = null; _monitor = null; stop?.Cancel();
        return _disconnectTask = DisconnectCore(session, stop, monitor);
    }
    private async Task DisconnectCore(MqttSession session, CancellationTokenSource? stop, Task? monitor)
    {
        // Keep the transition active before yielding so a new connection cannot reuse its cleanup state.
        await Task.Yield();
        try { if (monitor is not null) await monitor; await session.DisposeAsync(); }
        finally { stop?.Dispose(); _disconnectTask = null; Status = "已断开 · 订阅清单保留，下次连接时恢复"; Changed(); }
    }
    public async Task Close() { _closing = true; _timer.Stop(); await Disconnect(); }
    private void Changed() { Raise(nameof(Active)); Raise(nameof(Connected)); Raise(nameof(CanConfigure)); Raise(nameof(ConnectText)); }
    private void Export()
    {
        var dialog = new SaveFileDialog { Filter = "MQTT 消息 JSON|*.json", FileName = "LinkLab-mqtt-messages.json" };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(MessageView.Cast<MqttRecord>().Select(m => new { m.Time, m.Direction, m.Topic, m.Qos, m.Retain, m.OriginalLength, m.Truncated, PayloadBase64 = Convert.ToBase64String(m.Payload), m.Text }), new JsonSerializerOptions { WriteIndented = true }));
    }
    private void SaveProfile()
    {
        var dialog = new SaveFileDialog { Filter = "MQTT 配置模板|*.json", FileName = "LinkLab-mqtt-profile.json" };
        if (dialog.ShowDialog() != true) return; var prefs = new Preferences(); prefs.Transfer("mqtt", this, true);
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(prefs.Values, new JsonSerializerOptions { WriteIndented = true })); Hub.Notice = "MQTT 模板已保存（不含密码和客户端证书密码）";
    }
    private void LoadProfile()
    {
        if (Active) throw new InvalidOperationException("请先断开连接，再载入配置模板。");
        var dialog = new OpenFileDialog { Filter = "MQTT 配置模板|*.json" }; if (dialog.ShowDialog() != true) return;
        if (new FileInfo(dialog.FileName).Length > 1_000_000) throw new IOException("配置模板过大。");
        var prefs = new Preferences { Values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(dialog.FileName)) ?? throw new FormatException("无效模板。") };
        prefs.Transfer("mqtt", this, false); Password = ""; CertificatePassword = ""; Subscriptions.Clear(); Raise(""); Hub.Notice = "模板已载入，请按需填写密码后连接";
    }
}

public static class PasswordBinding
{
    static PasswordBinding() => EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent, new RoutedEventHandler(PasswordChanged));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached("Value", typeof(string), typeof(PasswordBinding), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChanged));
    public static string GetValue(DependencyObject o) => (string)o.GetValue(ValueProperty);
    public static void SetValue(DependencyObject o, string value) => o.SetValue(ValueProperty, value);
    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not PasswordBox box) return;
        if (box.Password != (string)e.NewValue) box.Password = (string)e.NewValue;
    }
    private static void PasswordChanged(object sender, RoutedEventArgs e) { var box = (PasswordBox)sender; if (BindingOperations.IsDataBound(box, ValueProperty) && GetValue(box) != box.Password) box.SetCurrentValue(ValueProperty, box.Password); }
}
