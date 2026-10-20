using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using LinkLab.Core;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

internal static class MqttTests
{
    public static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }
    public static MqttServer Broker(int port) => new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(port).Build());
    public static async Task RunBroker(int port) { using var broker = Broker(port); await broker.StartAsync(); Console.WriteLine("READY " + port); await Task.Delay(TimeSpan.FromSeconds(90)); await broker.StopAsync(); }
    private static void Check(bool condition, string text = "MQTT assertion failed") { if (!condition) throw new Exception(text); }
    private static async Task<MqttRecord> Read(Channel<MqttRecord> channel) => await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("MQTT topic validation and shared subscription syntax", () =>
        {
            foreach (string topic in new[] { "sensors/+/status", "a/#", "/+", "$share/group/a/+", "#" }) MqttSession.ValidateTopic(topic, true);
            foreach (var (topic, filter) in new[] { ("a/#/b", true), ("a+", true), ("a/#", false), ("", true), ("$share//a", true), ("a\0b", false) })
            { try { MqttSession.ValidateTopic(topic, filter); throw new Exception("Invalid topic accepted"); } catch (ArgumentException) { } } return Task.CompletedTask;
        });
        foreach (bool v5 in new[] { false, true })
        {
            await test($"MQTT {(v5 ? "5.0" : "3.1.1")} QoS 0/1/2 wildcard UTF8 and HEX", async () =>
            {
                int port = FreePort(); using var broker = Broker(port); await broker.StartAsync();
                await using var sub = new MqttSession(); await using var pub = new MqttSession(); var received = Channel.CreateUnbounded<MqttRecord>(); sub.Message += m => { if (m.Direction == "RX") received.Writer.TryWrite(m); };
                await sub.ConnectAsync(new(Port: port, ClientId: "sub", V5: v5)); await pub.ConnectAsync(new(Port: port, ClientId: "pub", V5: v5));
                await sub.SubscribeAsync("test/+/value", 2);
                for (int qos = 0; qos <= 2; qos++)
                { byte[] data = qos == 0 ? [0, 255, 254, 128] : Encoding.UTF8.GetBytes("中文消息 你好 " + qos); await pub.PublishAsync("test/device/value", data, qos, false); var message = await Read(received); Check(message.Qos == qos && message.Payload.SequenceEqual(data)); }
                await sub.UnsubscribeAsync("test/+/value"); await pub.PublishAsync("test/device/value", [1], 1, false); await Task.Delay(120); Check(!received.Reader.TryRead(out _));
                await broker.StopAsync();
            });
        }
        await test("MQTT retained delivery and empty retained clear", async () =>
        {
            int port = FreePort(); using var broker = Broker(port); await broker.StartAsync();
            await using var pub = new MqttSession(); await pub.ConnectAsync(new(Port: port, ClientId: "pub")); await pub.PublishAsync("retained/status", [7, 8], 1, true);
            await using var sub = new MqttSession(); var received = Channel.CreateUnbounded<MqttRecord>(); sub.Message += m => received.Writer.TryWrite(m); await sub.ConnectAsync(new(Port: port, ClientId: "sub")); await sub.SubscribeAsync("retained/#", 1);
            var record = await Read(received); Check(record.Retain && record.Payload.SequenceEqual(new byte[] { 7, 8 }));
            await pub.PublishAsync("retained/status", [], 1, true); _ = await Read(received);
            await sub.UnsubscribeAsync("retained/#"); await sub.SubscribeAsync("retained/#", 1); await Task.Delay(120); Check(!received.Reader.TryRead(out _)); await broker.StopAsync();
        });
        await test("MQTT rejected credentials and subscription ACK errors", async () =>
        {
            int port = FreePort(); using var broker = Broker(port);
            broker.ValidatingConnectionAsync += e => { if (e.UserName != "tester" || e.Password != "secret") e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword; return Task.CompletedTask; };
            broker.InterceptingSubscriptionAsync += e => { if (e.TopicFilter.Topic == "forbidden/#") e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized; return Task.CompletedTask; };
            await broker.StartAsync(); await using var client = new MqttSession();
            try { await client.ConnectAsync(new(Port: port, User: "tester", Password: "wrong")); throw new Exception("Bad password accepted"); } catch (Exception e) when (e is IOException or MQTTnet.Exceptions.MqttCommunicationException) { }
            await client.ConnectAsync(new(Port: port, User: "tester", Password: "secret"));
            try { await client.SubscribeAsync("forbidden/#", 1); throw new Exception("Denied subscription accepted"); } catch (IOException) { }
            await broker.StopAsync();
        });
        await test("MQTT reconnect restores subscriptions after broker restart", async () =>
        {
            int port = FreePort(); using var broker = Broker(port); await broker.StartAsync();
            await using var client = new MqttSession(); var received = Channel.CreateUnbounded<MqttRecord>(); client.Message += m => { if (m.Direction == "RX") received.Writer.TryWrite(m); };
            await client.ConnectAsync(new(Port: port)); await client.SubscribeAsync("resume/#", 1);
            await broker.StopAsync(); for (int i = 0; i < 30 && client.Connected; i++) await Task.Delay(20); Check(!client.Connected);
            await broker.StartAsync(); await client.ReconnectAsync(); await client.PublishAsync("resume/1", [3], 1, false); Check((await Read(received)).Payload[0] == 3); await broker.StopAsync();
        });
        await test("MQTT TLS rejects untrusted certificate and accepts custom CA", async () =>
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var serverCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);
            int port = FreePort(); var opts = new MqttServerOptionsBuilder().WithoutDefaultEndpoint().WithEncryptedEndpoint().WithEncryptedEndpointPort(port).WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback).WithEncryptionCertificate(serverCert).Build();
            var logger = new MQTTnet.Diagnostics.Logger.MqttNetEventLogger(); logger.LogMessagePublished += (_, e) => { if (e.LogMessage.Exception is not null) Console.WriteLine("TLS broker: " + e.LogMessage.Exception.Message); };
            using var broker = new MqttServerFactory(logger).CreateMqttServer(opts); await broker.StartAsync(); await using var client = new MqttSession();
            try { await client.ConnectAsync(new(Host: "localhost", Port: port, Transport: 1, TimeoutMs: 1500)); throw new Exception("Untrusted TLS accepted"); } catch (Exception e) when (e is System.Security.Authentication.AuthenticationException or MQTTnet.Exceptions.MqttCommunicationException or IOException) { }
            string ca = Path.Combine(Path.GetTempPath(), "LinkLab-test-ca-" + Guid.NewGuid().ToString("N") + ".pem"); await File.WriteAllTextAsync(ca, cert.ExportCertificatePem());
            try
            {
                await client.ConnectAsync(new(Host: "localhost", Port: port, Transport: 1, CaFile: ca)); Check(client.Connected);
                await broker.StopAsync(); opts.TlsEndpointOptions.ClientCertificateRequired = true;
                using var clientKey = System.Security.Cryptography.RSA.Create(2048);
                var clientReq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=LinkLab-test-client", clientKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                using var clientCert = clientReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                opts.TlsEndpointOptions.RemoteCertificateValidationCallback = (_, remote, _, _) => remote?.GetCertHashString() == clientCert.Thumbprint;
                string pfx = ca + ".pfx"; await File.WriteAllBytesAsync(pfx, clientCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test"));
                try
                {
                    using var mutualBroker = new MqttServerFactory(logger).CreateMqttServer(opts); await mutualBroker.StartAsync();
                    await using var mutual = new MqttSession(); await mutual.ConnectAsync(new(Host: "localhost", Port: port, Transport: 1, ClientId: "mutual", CaFile: ca, ClientCertificate: pfx, CertificatePassword: "test")); Check(mutual.Connected, "mTLS client certificate"); await mutualBroker.StopAsync();
                }
                finally { File.Delete(pfx); }
            }
            finally { File.Delete(ca); await broker.StopAsync(); }
        });
        await test("MQTT WebSocket path, protocol handshake and message receive", async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var stop = new CancellationTokenSource(5000);
            var peer = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync(stop.Token); using var stream = tcp.GetStream(); var header = new List<byte>(); byte[] one = new byte[1];
                while (header.Count < 8192) { await stream.ReadExactlyAsync(one, stop.Token); header.Add(one[0]); if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] {13,10,13,10})) break; }
                string request = Encoding.ASCII.GetString(header.ToArray()); Check(request.StartsWith("GET /mqtt "), "WebSocket URL path");
                string key = request.Split("\r\n").Single(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                string accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: mqtt\r\n\r\n"), stop.Token);
                using var ws = System.Net.WebSockets.WebSocket.CreateFromStream(stream, true, "mqtt", Timeout.InfiniteTimeSpan); var buffer = new byte[4096]; var first = await ws.ReceiveAsync(buffer.AsMemory(), stop.Token); Check(first.Count > 0 && buffer[0] == 0x10, "MQTT CONNECT over WebSocket");
                await ws.SendAsync(new byte[] {0x20, 2, 0, 0}, System.Net.WebSockets.WebSocketMessageType.Binary, true, stop.Token);
                await ws.SendAsync(new byte[] {0x30, 8, 0, 4, (byte)'t', (byte)'e', (byte)'s', (byte)'t', 0x4F, 0x4B}, System.Net.WebSockets.WebSocketMessageType.Binary, true, stop.Token);
                _ = await ws.ReceiveAsync(buffer.AsMemory(), stop.Token);
            }, stop.Token);
            try
            {
                var received = Channel.CreateUnbounded<MqttRecord>(); await using (var client = new MqttSession())
                { client.Message += m => received.Writer.TryWrite(m); await client.ConnectAsync(new(Host: $"ws://127.0.0.1:{port}/mqtt", Transport: 2, V5: false)); var m = await Read(received); Check(m.Topic == "test" && m.Text == "OK"); }
                await peer;
            }
            finally { listener.Stop(); }
        });
    }
}
