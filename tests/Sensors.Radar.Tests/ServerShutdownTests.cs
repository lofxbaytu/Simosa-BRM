using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using SimosaBRM.Sensors.Radar.Net;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 關機時仍有顯示端連著:RadarWebSocketServer.DisposeAsync 必須在短時間內返回
/// (Windows 的 HttpListener.Stop() 在仍有連線時不返回;同 tests/SimCore.Tests/ServerShutdownTests.cs,2026-11-22 Windows CI 實測)。
/// </summary>
public class ServerShutdownTests
{
    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task DisposesPromptlyWithConnectedIdleClients()
    {
        var port = FreeTcpPort();
        var server = new RadarWebSocketServer(port, new[] { "x", "s" }, allInterfaces: false);
        server.Start();
        var clients = new List<ClientWebSocket>();
        try
        {
            foreach (var id in new[] { "x", "s", "x" })
            {
                var c = new ClientWebSocket();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await c.ConnectAsync(new Uri($"ws://localhost:{port}/radar/{id}"), cts.Token);
                clients.Add(c);   // 連上後不讀、不關
            }
            await Task.Delay(200);
            var limit = TimeSpan.FromSeconds(5);
            var sw = Stopwatch.StartNew();
            var dispose = server.DisposeAsync().AsTask();
            var first = await Task.WhenAny(dispose, Task.Delay(limit));
            Assert.True(ReferenceEquals(first, dispose), $"仍有顯示端連線時 DisposeAsync 未在 {limit.TotalSeconds:0} s 內返回");
            await dispose;
            Assert.True(sw.Elapsed < limit, $"DisposeAsync 耗時 {sw.Elapsed.TotalSeconds:0.0} s");
        }
        finally { foreach (var c in clients) c.Dispose(); }
        await server.DisposeAsync();   // 可重複呼叫
    }
}
