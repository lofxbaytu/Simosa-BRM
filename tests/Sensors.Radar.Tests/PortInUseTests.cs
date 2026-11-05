using System.Net;
using System.Net.Sockets;
using SimosaBRM.Sensors.Radar.Net;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 雷達 WebSocket 伺服器在埠被占用時的優雅降級(與 SimCore.Host 的 PortInUseTests 同一作法;Net/HttpListenerStarter):
/// TryStart 回 false 並回報原因、Start 拋 HttpListenerException、DisposeAsync 不拋例外且可重複呼叫;釋放埠後同一埠可再啟動。
/// </summary>
public class PortInUseTests
{
    private static TcpListener Hold(out int port)
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        return l;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PortInUse_TryStartReportsAndDisposeIsSafe(bool allInterfaces)
    {
        var hold = Hold(out var port);
        try
        {
            var server = new RadarWebSocketServer(port, new[] { "x", "s" }, allInterfaces);
            var logs = new List<string>();
            server.Log += logs.Add;
            Assert.False(server.TryStart(out var error));
            Assert.NotNull(error);
            Assert.Contains(port.ToString(), error);
            Assert.Equal(error, server.StartError);
            Assert.False(server.Started);
            Assert.Null(server.Prefix);
            await server.DisposeAsync();
            await server.DisposeAsync(); // 可重複呼叫
            var server2 = new RadarWebSocketServer(port, new[] { "x" }, allInterfaces);
            var ex = Assert.Throws<HttpListenerException>(server2.Start);
            Assert.Contains(port.ToString(), ex.Message);
            await server2.DisposeAsync();
        }
        finally { hold.Stop(); }
    }

    [Fact]
    public async Task FailedStart_DoesNotPoisonPort_ServerStartsOnSamePortAfterRelease()
    {
        var hold = Hold(out var port);
        var failed = new RadarWebSocketServer(port, new[] { "x" }, allInterfaces: false);
        try { Assert.False(failed.TryStart(out _)); }
        finally
        {
            await failed.DisposeAsync();
            hold.Stop();
        }
        await using var server = new RadarWebSocketServer(port, new[] { "x" }, allInterfaces: false);
        Assert.True(server.TryStart(out var error), error);
        Assert.True(server.Started);
        Assert.Equal($"http://localhost:{port}/radar/", server.Prefix);
        Assert.Null(server.StartError);
        Assert.Throws<InvalidOperationException>(() => server.TryStart(out _));
        await server.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => server.TryStart(out _));
    }
}
