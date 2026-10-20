using System.Buffers;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace LinkLab.Core;

public sealed record MqttSettings(string Host = "127.0.0.1", int Port = 1883, string ClientId = "LinkLab", int Transport = 0,
    bool V5 = true, string User = "", string Password = "", int KeepAlive = 60, bool CleanSession = true, int SessionExpiry = 3600,
    int TimeoutMs = 5000, string CaFile = "", string ClientCertificate = "", string CertificatePassword = "",
    string WillTopic = "", string WillPayload = "", int WillQos = 0, bool WillRetain = false);
public sealed record MqttRecord(DateTimeOffset Time, string Direction, string Topic, byte[] Payload, int Qos, bool Retain, long OriginalLength)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string Text => Encoding.UTF8.GetString(Payload);
    public string Hex => ByteTools.Hex(Payload);
    public bool Truncated => OriginalLength > Payload.Length;
    public string Flags => $"QoS {Qos} · {(Retain ? "Retained" : "Live")}{(Truncated ? " · 显示已截断" : "")}";
}

public sealed class MqttSession : IAsyncDisposable
{
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, int> _subscriptions = [];
    private readonly List<X509Certificate2> _certificates = [];
    private MqttClientOptions? _options;
    private int _timeout = 5000;
    public bool Connected => _client.IsConnected;
    public event Action<MqttRecord>? Message;
    public event Action<string>? Status;
    public MqttSession()
    {
        _client.ApplicationMessageReceivedAsync += e =>
        {
            var m = e.ApplicationMessage; long size = m.Payload.Length;
            byte[] bytes = m.Payload.Slice(0, Math.Min(size, 65536)).ToArray();
            Message?.Invoke(new(DateTimeOffset.Now, "RX", m.Topic, bytes, (int)m.QualityOfServiceLevel, m.Retain, size)); return Task.CompletedTask;
        };
        _client.DisconnectedAsync += e => { if (!_stop.IsCancellationRequested) Status?.Invoke($"连接已断开 · {e.Reason}"); return Task.CompletedTask; };
    }
    public static void ValidateTopic(string topic, bool filter)
    {
        if (string.IsNullOrEmpty(topic) || topic.Contains('\0') || new UTF8Encoding(false, true).GetByteCount(topic) > 65535) throw new ArgumentException("主题不能为空，不能包含 NUL，UTF-8 长度不能超过 65535。");
        if (!filter && (topic.Contains('#') || topic.Contains('+'))) throw new ArgumentException("发布主题不能包含 + 或 # 通配符。");
        if (filter)
        {
            string effective = topic;
            if (topic.StartsWith("$share/", StringComparison.Ordinal))
            { string[] parts = topic.Split('/', 3); if (parts.Length != 3 || parts[1].Length == 0 || parts[1].ContainsAny('+', '#')) throw new ArgumentException("共享订阅格式为 $share/组名/过滤主题。"); effective = parts[2]; if (effective.Length == 0) throw new ArgumentException("过滤主题不能为空。"); }
            string[] levels = effective.Split('/');
            for (int i = 0; i < levels.Length; i++) if ((levels[i].Contains('#') && (levels[i] != "#" || i != levels.Length - 1)) || (levels[i].Contains('+') && levels[i] != "+")) throw new ArgumentException("+ 必须独占一层，# 必须独占最后一层。");
        }
    }
    public Task ConnectAsync(MqttSettings s, CancellationToken ct = default) => Guard(async token =>
    {
        if (Connected) throw new InvalidOperationException("请先断开当前 MQTT 连接。");
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.ClientId) || s.Port is < 1 or > 65535 || s.KeepAlive is < 5 or > 65535 || s.TimeoutMs is < 100 or > 60000 || s.SessionExpiry is < 0 or > 604800 || s.Transport is < 0 or > 3) throw new ArgumentException("请检查 MQTT 主机、Client ID、端口及超时参数。");
        _timeout = s.TimeoutMs;
        var b = new MqttClientOptionsBuilder().WithClientId(s.ClientId).WithProtocolVersion(s.V5 ? MqttProtocolVersion.V500 : MqttProtocolVersion.V311)
            .WithCleanSession(s.CleanSession).WithKeepAlivePeriod(TimeSpan.FromSeconds(s.KeepAlive)).WithTimeout(TimeSpan.FromMilliseconds(s.TimeoutMs));
        if (s.V5) b.WithSessionExpiryInterval(s.CleanSession ? 0u : (uint)s.SessionExpiry).WithMaximumPacketSize(1_048_576);
        if (s.Transport < 2) b.WithTcpServer(s.Host.Trim(), s.Port);
        else
        {
            if (!Uri.TryCreate(s.Host.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != (s.Transport == 3 ? "wss" : "ws")) throw new ArgumentException("WebSocket 地址需要完整的 ws:// 或 wss:// URL（包含端口和路径）。");
            b.WithWebSocketServer(o => o.WithUri(uri.ToString()));
        }
        if (s.User.Length > 0 || s.Password.Length > 0) b.WithCredentials(s.User, s.Password);
        foreach (var cert in _certificates) cert.Dispose(); _certificates.Clear();
        if (s.Transport is 1 or 3)
        {
            b.WithTlsOptions(t =>
            {
                t.UseTls().WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
                if (!string.IsNullOrWhiteSpace(s.CaFile)) { var ca = X509CertificateLoader.LoadCertificateFromFile(s.CaFile); _certificates.Add(ca); t.WithTrustChain(new X509Certificate2Collection(ca)); }
                if (!string.IsNullOrWhiteSpace(s.ClientCertificate))
                {
                    // Windows Schannel needs a key container; disposal removes it (no PersistKeySet).
                    var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
                    var cert = X509CertificateLoader.LoadPkcs12FromFile(s.ClientCertificate, s.CertificatePassword, flags);
                    _certificates.Add(cert); if (!cert.HasPrivateKey) throw new ArgumentException("客户端 PFX 证书必须包含私钥。"); t.WithClientCertificates([cert]);
                }
            });
        }
        if (s.WillTopic.Length > 0)
        { ValidateTopic(s.WillTopic, false); Qos(s.WillQos); b.WithWillTopic(s.WillTopic).WithWillPayload(Encoding.UTF8.GetBytes(s.WillPayload)).WithWillQualityOfServiceLevel((MqttQualityOfServiceLevel)s.WillQos).WithWillRetain(s.WillRetain); }
        _options = b.Build(); await ConnectInternal(token);
    }, ct, s.TimeoutMs);
    public Task ReconnectAsync(CancellationToken ct = default) => Guard(async token =>
    {
        if (_options is null) throw new InvalidOperationException("没有可重连的配置。");
        if (Connected) return;
        await ConnectInternal(token);
        foreach (var (topic, qos) in _subscriptions) await SubscribeInternal(topic, qos, token);
    }, ct);
    private async Task ConnectInternal(CancellationToken ct)
    { var result = await _client.ConnectAsync(_options!, ct); if (result.ResultCode != MqttClientConnectResultCode.Success) throw new IOException($"Broker 拒绝连接：{result.ResultCode}"); Status?.Invoke($"已连接 · {_options!.ProtocolVersion} · SessionPresent={result.IsSessionPresent}"); }
    public Task SubscribeAsync(string topic, int qos, CancellationToken ct = default) => Guard(async token =>
    { RequireConnection(); ValidateTopic(topic, true); Qos(qos); int granted = await SubscribeInternal(topic, qos, token); _subscriptions[topic] = granted; }, ct);
    private async Task<int> SubscribeInternal(string topic, int qos, CancellationToken ct)
    {
        var result = await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)).Build(), ct);
        var code = result.Items.Single().ResultCode; if ((int)code >= 128) throw new IOException($"Broker 拒绝订阅 {topic}：{code}"); Status?.Invoke($"已订阅 {topic} · 授予 QoS {(int)code}"); return (int)code;
    }
    public Task UnsubscribeAsync(string topic, CancellationToken ct = default) => Guard(async token =>
    {
        RequireConnection(); ValidateTopic(topic, true); var result = await _client.UnsubscribeAsync(new MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(topic).Build(), token);
        if (result.Items.Any(i => (int)i.ResultCode >= 128)) throw new IOException("Broker 拒绝取消订阅。"); _subscriptions.Remove(topic); Status?.Invoke($"已取消订阅 {topic}");
    }, ct);
    public Task PublishAsync(string topic, byte[] payload, int qos, bool retain, CancellationToken ct = default) => Guard(async token =>
    {
        RequireConnection(); ValidateTopic(topic, false); Qos(qos); if (payload.Length > 65536) throw new ArgumentException("单次发布负载最多 64 KiB。");
        var result = await _client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos).WithRetainFlag(retain).Build(), token);
        if (!result.IsSuccess) throw new IOException($"Broker 拒绝发布：{result.ReasonCode}");
        Message?.Invoke(new(DateTimeOffset.Now, "TX", topic, payload.ToArray(), qos, retain, payload.Length)); Status?.Invoke($"发布完成 · {topic} · {payload.Length} B · QoS {qos}");
    }, ct);
    private async Task Guard(Func<CancellationToken, Task> action, CancellationToken ct, int? timeoutMs = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token); await _gate.WaitAsync(stop.Token);
        try { stop.CancelAfter(timeoutMs ?? _timeout); await action(stop.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stop.IsCancellationRequested) { throw new TimeoutException("MQTT 操作超时，未收到完整确认。发布不会由界面自动重复提交。"); }
        finally { _gate.Release(); }
    }
    private void RequireConnection() { if (!Connected) throw new InvalidOperationException("请先连接 MQTT Broker。"); }
    private static void Qos(int qos) { if (qos is < 0 or > 2) throw new ArgumentException("QoS 范围是 0、1、2。"); }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); await _gate.WaitAsync();
        try
        {
            if (Connected) { using var deadline = new CancellationTokenSource(1500); try { await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), deadline.Token); } catch (Exception e) when (e is IOException or OperationCanceledException or MQTTnet.Exceptions.MqttCommunicationException) { } }
            _client.Dispose(); foreach (var c in _certificates) c.Dispose(); _certificates.Clear();
        }
        finally { _gate.Release(); _stop.Dispose(); }
    }
}
