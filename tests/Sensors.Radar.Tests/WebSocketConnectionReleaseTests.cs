using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SimosaBRM.Sensors.Radar.Net;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 第三十輪審查(C-F1,修正前就存在;與 tests/SimCore.Tests/WebSocketConnectionReleaseTests.cs 相同):Linux / macOS 的受控 HttpListener 只
/// <c>WebSocket.Dispose()</c> 不會關閉底層 socket,每條關閉的連線留下 1 個檔案描述元。確定性的外部行為:關閉握手後伺服端必須關閉 TCP(客戶端讀到 EOF)。
/// Windows(http.sys)行為未驗證,只在非 Windows 執行。
/// </summary>
public class WebSocketConnectionReleaseTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task RadarServer_ClosesTcpAfterCloseHandshake()
    {
        if (OperatingSystem.IsWindows()) return;   // http.sys 由核心管理連線,與受控實作不同(未驗證)
        await using var server = new RadarWebSocketServer(FreePort(), new[] { "x" }, allInterfaces: false);
        server.Start();
        for (var round = 0; round < 3; round++)
            await AssertServerClosesTcpAfterCloseAsync(server.Port, "/radar/x", $"雷達 X 第 {round + 1} 條");
        for (var i = 0; i < 500 && server.ClientCount("x") > 0; i++) await Task.Delay(10);
        Assert.Equal(0, server.ClientCount("x"));
    }

    /// <summary>最小 WebSocket 客戶端:握手、送遮罩的關閉框、讀到 EOF(ClientWebSocket 關閉後最多只等伺服端關 TCP 1 秒,看不出有沒有關)</summary>
    private static async Task AssertServerClosesTcpAfterCloseAsync(int port, string path, string what)
    {
        using var cts = new CancellationTokenSource(Limit);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var s = tcp.GetStream();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        await s.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"), cts.Token);
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await s.ReadAsync(one, cts.Token) == 0) throw new Xunit.Sdk.XunitException($"{what}:握手完成前連線就被關閉");
            head.Add(one[0]);
        }
        var response = Encoding.ASCII.GetString(head.ToArray());
        Assert.True(response.StartsWith("HTTP/1.1 101", StringComparison.Ordinal), $"{what}:握手回應 {response}");
        byte[] mask = { 0x11, 0x22, 0x33, 0x44 };
        await s.WriteAsync(new byte[] { 0x88, 0x82, mask[0], mask[1], mask[2], mask[3], (byte)(0x03 ^ mask[0]), (byte)(0xE8 ^ mask[1]) }, cts.Token);
        var buf = new byte[4096];
        var received = 0L;
        try
        {
            int n;
            while ((n = await s.ReadAsync(buf, cts.Token)) > 0) received += n;
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"{what}:關閉握手後 {Limit.TotalSeconds:0} s 內伺服端沒有關閉 TCP(HttpListener 的連線沒有釋放,每條留下 1 個 fd);期間收到 {received} 位元組");
        }
        catch (IOException) { /* 對方以 RST 結束也算已關閉 */ }
        Assert.True(received > 0, $"{what}:伺服端關閉 TCP 前沒有回關閉框");
    }
}
