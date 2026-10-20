using System.Diagnostics;
using System.Security.Principal;

namespace LinkLab.App;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _lifetime;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _wait;
    internal bool IsPrimary { get; }
    internal SingleInstance(string? name = null)
    {
        name ??= $"Local\\LinkLab-{WindowsIdentity.GetCurrent().User!.Value}-{Process.GetCurrentProcess().SessionId}";
        // Object lifetime, rather than recursive mutex ownership, identifies the resident instance.
        _lifetime = new Mutex(false, name, out bool first); IsPrimary = first;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Activate");
    }
    internal void RequestShow() => _activate.Set();
    internal void Listen(Action show)
    {
        if (!IsPrimary) throw new InvalidOperationException("只有主实例可以接收唤醒请求。");
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => show(), null, Timeout.Infinite, false);
    }
    public void Dispose() { _wait?.Unregister(null); _activate.Dispose(); _lifetime.Dispose(); }
}
