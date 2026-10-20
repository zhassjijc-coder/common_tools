using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LinkLab.Core;

namespace LinkLab.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
    public void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class Command(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

public sealed class ParameterCommand(Action<object?> execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> error) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); } catch (Exception ex) { error(ex); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    public event EventHandler? CanExecuteChanged;
}

public sealed class LogRow(WireEvent entry)
{
    public WireEvent Entry { get; } = entry;
    public string Time => Entry.Time.ToString("HH:mm:ss.fff");
    public string Source => Entry.Source;
    public string Direction => Entry.Direction;
    public string Peer => Entry.Peer;
    public int Length => Entry.Data.Length;
    public string Hex => ByteTools.Hex(Entry.Data);
    public string Text => ByteTools.Printable(Entry.Data);
    public string Note => Entry.Note;
    public string Color => Direction switch { "TX" => "SerialAccent", "RX" => "NetworkAccent", "ERR" => "ErrorAccent", _ => "Muted" };
}

public sealed class SessionChoice(RawTransport transport)
{
    public RawTransport Transport { get; } = transport;
    public string Name => Transport.Name;
    public override string ToString() => Name;
}

public sealed class Hub : Observable
{
    private readonly ConcurrentQueue<WireEvent> _pending = new();
    private readonly DispatcherTimer _timer;
    private long _tx, _rx, _dropped;
    public ObservableCollection<LogRow> Logs { get; } = [];
    public ObservableCollection<SessionChoice> Sessions { get; } = [];
    public string TxText => $"{Interlocked.Read(ref _tx):N0} B";
    public string RxText => $"{Interlocked.Read(ref _rx):N0} B";
    public string DroppedText => $"丢弃显示记录 {Interlocked.Read(ref _dropped):N0}";
    public string ConnectionText => $"{Sessions.Count(s => s.Transport.IsOpen)} 个原始连接";
    private string _notice = "准备就绪 · 所有通信在本机执行";
    public string Notice { get => _notice; set => Set(ref _notice, value); }
    public Hub()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Flush(); _timer.Start();
    }
    public void Record(WireEvent e)
    {
        if (e.Direction == "TX") Interlocked.Add(ref _tx, e.Data.Length);
        if (e.Direction == "RX") Interlocked.Add(ref _rx, e.Data.Length);
        if (_pending.Count >= 4000) { Interlocked.Increment(ref _dropped); return; }
        _pending.Enqueue(e);
    }
    public void Flush()
    {
        int added = 0;
        while (added < 250 && _pending.TryDequeue(out var item))
        {
            Logs.Add(new(item)); added++;
            while (Logs.Count > 2000) Logs.RemoveAt(0);
        }
        if (added > 0) { Raise(nameof(TxText)); Raise(nameof(RxText)); Raise(nameof(DroppedText)); }
    }
    public void Error(Exception ex) { Notice = ex is OperationCanceledException ? "操作已停止" : ex.Message; Record(new(DateTimeOffset.Now, "系统", "ERR", [], Note: Notice)); }
    public void Attach(RawTransport transport)
    {
        transport.Traffic += Record;
        transport.Changed += () => Application.Current.Dispatcher.BeginInvoke(() => Raise(nameof(ConnectionText)));
        Sessions.Add(new(transport)); Raise(nameof(ConnectionText));
    }
    public void Detach(RawTransport transport)
    {
        var found = Sessions.FirstOrDefault(x => x.Transport == transport);
        if (found is not null) Sessions.Remove(found);
        Raise(nameof(ConnectionText));
    }
    public void Clear() { while (_pending.TryDequeue(out _)) { } Logs.Clear(); }
}

public sealed class Preferences
{
    public bool Light { get; set; } = true;
    public int ThemeRevision { get; set; }
    public Dictionary<string, string> Values { get; set; } = [];
    public static string FilePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkLab", "settings.json");
    public static Preferences Load()
    {
        try { var result = JsonSerializer.Deserialize<Preferences>(System.IO.File.ReadAllText(FilePath)); return result?.Values is not null ? result : new(); }
        catch (Exception ex) when (ex is System.IO.IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void UpgradeTheme()
    {
        if (ThemeRevision >= 1) return;
        Light = true; ThemeRevision = 1;
    }
    public void Save()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        System.IO.File.Move(temp, FilePath, true);
    }
    public string Get(string key, string fallback) => Values.GetValueOrDefault(key, fallback);
    public void Transfer(string prefix, object target, bool save)
    {
        foreach (var p in target.GetType().GetProperties().Where(p => p.IsDefined(typeof(SavedAttribute), true)))
        {
            string key = prefix + "." + p.Name;
            if (save) Values[key] = Convert.ToString(p.GetValue(target), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            else if (Values.TryGetValue(key, out var v))
            {
                try { p.SetValue(target, p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, v) : Convert.ChangeType(v, p.PropertyType, System.Globalization.CultureInfo.InvariantCulture)); }
                catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or System.Reflection.TargetInvocationException) { }
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Property)] public sealed class SavedAttribute : Attribute { }

public abstract class PageVm(Hub hub) : Observable
{
    public Hub Hub { get; } = hub;
    protected AsyncCommand Async(Func<Task> run) => new(run, Hub.Error);
    protected static int Int(string text, int min, int max, string label)
    {
        int value;
        try { value = ByteTools.Number(text); } catch (Exception e) when (e is FormatException or OverflowException) { throw new ArgumentException($"{label}必须是整数，支持 0x 十六进制。"); }
        if (value < min || value > max) throw new ArgumentException($"{label}范围为 {min}–{max}。");
        return value;
    }
}
