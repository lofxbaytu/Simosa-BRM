using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using SimosaBRM.SimCore.Host;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 關機時仍有 WebSocket 客戶端連著(瀏覽器分頁、教官筆電、視景都可能如此):伺服端的 DisposeAsync 必須在短時間內返回,
/// 主控程序才來得及寫紀錄 footer(stop-all 等 10 秒後會強制結束核心)。
/// 2026-11-22 Windows CI 實測:Windows(http.sys)的 HttpListener.Stop() 在仍有連線時不返回 — Edge 分頁連著時核心 10 秒內關不掉,
/// 紀錄沒有 footer。Linux 的受控實作沒有這個行為,所以這些測試要在 Windows CI 的 dotnet test 才有鑑別力。
/// </summary>
public class ServerShutdownTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<List<ClientWebSocket>> ConnectIdleClientsAsync(string url, int count)
    {
        var clients = new List<ClientWebSocket>();
        for (var i = 0; i < count; i++)
        {
            var c = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await c.ConnectAsync(new Uri(url), cts.Token);
            clients.Add(c);   // 連上後什麼都不做:不讀、不關(模擬被節流的背景分頁)
        }
        return clients;
    }

    private static async Task AssertDisposesPromptlyAsync(IAsyncDisposable server, string name)
    {
        var sw = Stopwatch.StartNew();
        // 經 Task.Run 呼叫:DisposeAsync 的同步段若阻塞(Windows 上 Cancel()/Abort() 曾卡住),本測試要「失敗」而不是「卡住不返回」
        var dispose = Task.Run(() => server.DisposeAsync().AsTask());
        var first = await Task.WhenAny(dispose, Task.Delay(Limit));
        Assert.True(ReferenceEquals(first, dispose), $"{name}:仍有客戶端連線時 DisposeAsync 未在 {Limit.TotalSeconds:0} s 內返回(核心會被 stop-all 強制結束,紀錄沒有 footer)");
        await dispose;
        Assert.True(sw.Elapsed < Limit, $"{name}:DisposeAsync 耗時 {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task StateServerDisposesPromptlyWithConnectedIdleClients()
    {
        var server = new WebSocketStateServer(TestData.Engine(), FreePort());
        server.Start();
        var clients = await ConnectIdleClientsAsync($"ws://127.0.0.1:{server.Port}/", 3);
        for (var i = 0; i < 300 && server.ClientCount < 3; i++) await Task.Delay(10);
        Assert.Equal(3, server.ClientCount);
        try { await AssertDisposesPromptlyAsync(server, "狀態 WebSocket"); }
        finally { foreach (var c in clients) c.Dispose(); }
        // 可重複呼叫
        await server.DisposeAsync();
    }

    /// <summary>
    /// 最壞情況:引擎仍在步進(伺服端不斷送狀態幀)、客戶端完全不讀(送出卡在 TCP 背壓上)時關閉。
    /// Windows 的 HttpListener WebSocket 在這種情況下 Abort()/取消回呼可能同步阻塞;DisposeAsync 仍須在時限內返回(關機流程之後還要寫紀錄 footer)。
    /// </summary>
    [Fact]
    public async Task StateServerDisposesPromptlyWhileSendingToClientsThatNeverRead()
    {
        var engine = TestData.Engine();
        var server = new WebSocketStateServer(engine, FreePort());
        server.Start();
        var clients = await ConnectIdleClientsAsync($"ws://127.0.0.1:{server.Port}/", 3);
        for (var i = 0; i < 300 && server.ClientCount < 3; i++) await Task.Delay(10);
        Assert.Equal(3, server.ClientCount);
        using var stop = new CancellationTokenSource();
        // 盡快步進約 1.5 s:每個客戶端的送出迴圈持續送幀直到 TCP 緩衝區塞滿(客戶端不讀)
        var stepping = Task.Run(() => { while (!stop.IsCancellationRequested) engine.Step(); });
        try
        {
            await Task.Delay(1500);
            await AssertDisposesPromptlyAsync(server, "狀態 WebSocket(送出中、客戶端不讀)");
        }
        finally
        {
            stop.Cancel();
            await stepping.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var c in clients) c.Dispose();
        }
    }

    [Fact]
    public async Task VhfServerDisposesPromptlyWithConnectedIdleClients()
    {
        var server = new VhfRelayServer(FreePort());
        server.Start();
        var clients = await ConnectIdleClientsAsync($"ws://127.0.0.1:{server.Port}/", 2);
        await Task.Delay(200);
        try { await AssertDisposesPromptlyAsync(server, "VHF 頻道"); }
        finally { foreach (var c in clients) c.Dispose(); }
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MediaServerDisposesPromptlyWithAnOpenConnection()
    {
        var root = Path.Combine(TestData.TestRecordsDirectory, $"shutdown-media-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var server = new MediaUploadServer(Path.Combine(root, "media"), FreePort()) { ProjectRoot = root, MinFreeBytes = 0 };
        Assert.True(server.TryStart(out var error), error);
        // 一條已連線但不送出請求的 TCP 連線(keep-alive 連線、送到一半的上傳)
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await Task.Delay(100);
        await AssertDisposesPromptlyAsync(server, "錄影上傳端");
        await server.DisposeAsync();
    }
}
