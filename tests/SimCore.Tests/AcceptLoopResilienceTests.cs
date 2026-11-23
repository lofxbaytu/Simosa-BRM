using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using SimosaBRM.SimCore.Host;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 第三十輪 2 小時搜尋 host-accept-loop-death(major):修正前狀態 WebSocket 與 VHF 的接受迴圈在迴圈本體裡 await 健康檢查的回應,
/// 客戶端送出 <c>GET /</c> 後立刻 RST(SO_LINGER 0)時寫出丟例外,接受迴圈結束、沒有 log,之後所有站都連不上
/// (Linux 實測:新程序第 1 次 RST 就死 6/6;死掉後每次重連在受控 HttpListener 內部留下 1 條永遠沒人取走的連線)。
/// 這裡以確定性的外部行為驗證:連續 30 次 GET + RST 之後,WebSocket 仍能連線、健康檢查仍回 200。
/// 錄影上傳端本來就在背景處理每個請求,但寫出失敗時沒有 Abort,連線留在連線表(Linux 以反射數 listener 的連線表)。
/// </summary>
public class AcceptLoopResilienceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const int Resets = 30;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task AssertWebSocketConnectsAsync(int port, string what)
    {
        using var cts = new CancellationTokenSource(Limit);
        using var ws = new ClientWebSocket();
        ws.Options.Proxy = null;
        try { await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token); }
        catch (Exception ex) { throw new Xunit.Sdk.XunitException($"{what}:GET + RST 之後 WebSocket 連不上({ex.GetType().Name}:{ex.Message});接受迴圈已結束"); }
        Assert.Equal(WebSocketState.Open, ws.State);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
    }

    private static async Task<string> GetTextAsync(int port, string path = "")
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Limit };
        return await http.GetStringAsync($"http://127.0.0.1:{port}/{path}");
    }

    [Fact]
    public async Task StateServer_GetThenReset_DoesNotStopTheAcceptLoop()
    {
        await using var server = new WebSocketStateServer(TestData.Engine(), FreePort());
        server.Start();
        for (var i = 0; i < Resets; i++) await RawHttp.SendThenResetAsync(server.Port, RawHttp.Get(server.Port));
        await AssertWebSocketConnectsAsync(server.Port, "狀態 WebSocket");
        Assert.Contains("SimCore WebSocket endpoint", await GetTextAsync(server.Port));
    }

    [Fact]
    public async Task VhfServer_GetThenReset_DoesNotStopTheAcceptLoop()
    {
        await using var server = new VhfRelayServer(FreePort());
        server.Start();
        for (var i = 0; i < Resets; i++) await RawHttp.SendThenResetAsync(server.Port, RawHttp.Get(server.Port));
        await AssertWebSocketConnectsAsync(server.Port, "VHF");
        Assert.Contains("VHF relay endpoint", await GetTextAsync(server.Port));
    }

    [Fact]
    public async Task MediaServer_GetThenReset_KeepsAccepting_AndReleasesEveryConnection()
    {
        var dir = Path.Combine(TestData.Root, "build", "test-tmp", "media-accept", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            await using var server = new MediaUploadServer(dir, FreePort()) { MinFreeBytes = 0 };
            server.Start();
            var baseline = ListenerConnections.Count(server);
            for (var i = 0; i < Resets; i++) await RawHttp.SendThenResetAsync(server.Port, RawHttp.Get(server.Port));
            Assert.Contains("media upload endpoint", await GetTextAsync(server.Port));
            // Linux:寫出失敗的回應一律 Abort,連線表回到基準(修正前每次寫出失敗留 1 條,probeB 100 次 +7)
            if (baseline is { } b) await ListenerConnections.WaitUntilAtMostAsync(server, b, "GET + RST 後的連線");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}

/// <summary>原始 TCP:送出請求後以 RST(SO_LINGER 0)關閉,或送出後保持不送也不關(模擬對端消失)。</summary>
internal static class RawHttp
{
    public static byte[] Get(int port, string path = "/") => Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");

    /// <summary>POST 標頭(宣告 <paramref name="contentLength"/>)加 <paramref name="bodyBytes"/> 位元組的 body(不足宣告長度)</summary>
    public static byte[] PartialPost(int port, string path, long contentLength, int bodyBytes, int seq = 0)
    {
        var head = Encoding.ASCII.GetBytes($"POST {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nX-Seq: {seq}\r\nContent-Type: application/octet-stream\r\nContent-Length: {contentLength}\r\n\r\n");
        var all = new byte[head.Length + bodyBytes];
        head.CopyTo(all, 0);
        return all;
    }

    public static async Task SendThenResetAsync(int port, byte[] request, int delayMs = 0)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.LingerState = new LingerOption(true, 0);   // Close 時送 RST 而不是 FIN
        await s.ConnectAsync(IPAddress.Loopback, port);
        await s.SendAsync(request, SocketFlags.None);
        if (delayMs > 0) await Task.Delay(delayMs);
        s.Close();
    }

    /// <summary>送出後不再傳送、不關閉;回傳 socket(呼叫端負責 Dispose)</summary>
    public static async Task<Socket> SendThenHoldAsync(int port, byte[] request)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await s.ConnectAsync(IPAddress.Loopback, port);
        await s.SendAsync(request, SocketFlags.None);
        return s;
    }

    /// <summary>等伺服端關閉這條連線(讀到 EOF 或被重設);逾時回 false</summary>
    public static async Task<bool> ServerClosesAsync(Socket s, TimeSpan limit)
    {
        using var cts = new CancellationTokenSource(limit);
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                if (await s.ReceiveAsync(buffer, SocketFlags.None, cts.Token) == 0) return true;   // FIN
            }
        }
        catch (SocketException) { return true; }   // RST(伺服端 Abort)
        catch (OperationCanceledException) { return false; }
    }
}

/// <summary>
/// Linux / macOS 受控 HttpListener 內部的連線表(<c>HttpListener._connections</c>,.NET 8;Windows 的 http.sys 實作沒有這個欄位 → null,不檢查)。
/// 修正前寫出失敗而沒有 Close / Abort 的連線一直留在這裡(gcdump 的 HttpConnection 數)。
/// </summary>
internal static class ListenerConnections
{
    public static int? Count(MediaUploadServer server)
    {
        var listener = typeof(MediaUploadServer).GetField("_listener", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(server) as HttpListener;
        if (listener is null || OperatingSystem.IsWindows()) return null;
        var table = typeof(HttpListener).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(listener) as ICollection;
        if (table is null) return null;
        lock (table.SyncRoot) return table.Count;
    }

    public static async Task WaitUntilAtMostAsync(MediaUploadServer server, int max, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Count(server) is { } n && n > max)
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"{what}:listener 連線表 {n} 條,應回到 ≤ {max}(寫出失敗的回應沒有 Abort)");
            await Task.Delay(20);
        }
    }
}
