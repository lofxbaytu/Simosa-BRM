using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SimosaBRM.SimCore.Host;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 第三十輪審查(C-F1,修正前就存在):Linux / macOS 的受控 HttpListener 在 WebSocket 連線結束後,只 <c>WebSocket.Dispose()</c> 不會關閉底層 socket
/// (連線的 NetworkStream 不擁有 socket),HttpConnection / HttpListenerContext / Socket 一直留在 listener 內部的連線表 —
/// 每條關閉的連線留下 1 個檔案描述元與約 15 KB 受控記憶體(審查探針:2000 次連線 fd 318 → 2320)。收尾要再 <c>ctx.Response.Abort()</c>。
/// 這裡不量 fd 或記憶體(xunit 平行執行下不穩),改以確定性的外部行為驗證:完成關閉握手後,伺服端必須關閉 TCP(客戶端讀到 EOF)。
/// 修正前伺服端永遠不關 → 讀取逾時、測試失敗。Windows(http.sys)是另一套實作,行為未驗證,這裡只在非 Windows 執行。
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
    public async Task StateServer_ClosesTcpAfterCloseHandshake()
    {
        if (OperatingSystem.IsWindows()) return;   // http.sys 由核心管理連線,與受控實作不同(未驗證)
        await using var server = new WebSocketStateServer(TestData.Engine(), FreePort());
        server.Start();
        for (var round = 0; round < 3; round++)   // 連續幾條:每條都要關,不只第一條
            await RawWebSocket.AssertServerClosesTcpAfterCloseAsync(server.Port, "/", Limit, $"狀態 WebSocket 第 {round + 1} 條");
        for (var i = 0; i < 500 && server.ClientCount > 0; i++) await Task.Delay(10);
        Assert.Equal(0, server.ClientCount);
    }

    [Fact]
    public async Task VhfServer_ClosesTcpAfterCloseHandshake()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var server = new VhfRelayServer(FreePort());
        server.Start();
        for (var round = 0; round < 3; round++)
            await RawWebSocket.AssertServerClosesTcpAfterCloseAsync(server.Port, "/", Limit, $"VHF 第 {round + 1} 條");
        for (var i = 0; i < 500 && server.StationCount > 0; i++) await Task.Delay(10);
        Assert.Equal(0, server.StationCount);
    }
}

/// <summary>
/// 不經 ClientWebSocket 的最小 WebSocket 客戶端(RFC 6455):握手、送遮罩的關閉框、讀到 EOF。ClientWebSocket 在關閉握手後最多只等伺服端關 TCP 1 秒,
/// 看不出伺服端到底有沒有關;直接讀 TCP 才看得到。
/// </summary>
internal static class RawWebSocket
{
    public static async Task AssertServerClosesTcpAfterCloseAsync(int port, string path, TimeSpan limit, string what)
    {
        using var cts = new CancellationTokenSource(limit);
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
        // 關閉框(FIN + opcode 8)、遮罩、狀態碼 1000
        byte[] mask = { 0x11, 0x22, 0x33, 0x44 };
        await s.WriteAsync(new byte[] { 0x88, 0x82, mask[0], mask[1], mask[2], mask[3], (byte)(0x03 ^ mask[0]), (byte)(0xE8 ^ mask[1]) }, cts.Token);
        // 伺服端回關閉框後必須關 TCP:讀到 EOF(ReadAsync 回 0)
        var buf = new byte[4096];
        var received = new List<byte>();
        try
        {
            int n;
            while ((n = await s.ReadAsync(buf, cts.Token)) > 0) received.AddRange(buf.AsSpan(0, n).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"{what}:關閉握手後 {limit.TotalSeconds:0} s 內伺服端沒有關閉 TCP(HttpListener 的連線沒有釋放,每條留下 1 個 fd);" +
                $"期間收到 {received.Count} 位元組{(HasCloseFrame(received) ? ",含伺服端的關閉框" : ",沒有關閉框")}");
        }
        catch (IOException) { /* 對方以 RST 結束也算已關閉 */ }
        Assert.True(HasCloseFrame(received), $"{what}:伺服端關閉 TCP 前沒有回關閉框(收到 {received.Count} 位元組)");
    }

    /// <summary>伺服端送來的(不遮罩)框中有沒有關閉框(opcode 8)</summary>
    private static bool HasCloseFrame(List<byte> data)
    {
        var i = 0;
        while (i + 2 <= data.Count)
        {
            var opcode = data[i] & 0x0F;
            long len = data[i + 1] & 0x7F;
            var hdr = 2;
            if (len == 126) { if (i + 4 > data.Count) return false; len = (data[i + 2] << 8) | data[i + 3]; hdr = 4; }
            else if (len == 127) { if (i + 10 > data.Count) return false; len = 0; for (var k = 0; k < 8; k++) len = (len << 8) | data[i + 2 + k]; hdr = 10; }
            if (opcode == 8) return true;
            i += hdr + (int)len;
        }
        return false;
    }
}
