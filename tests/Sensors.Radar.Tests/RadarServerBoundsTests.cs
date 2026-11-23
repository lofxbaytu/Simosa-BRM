using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.Sensors.Radar.Net;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// 第三十輪 2 小時搜尋(雷達):
/// <list type="bullet">
/// <item>radar-R1 / R3:送出迴圈每圈對 Frames / Json 各新建 WaitToReadAsync;待機、失效或 /radar/replay 時 Frames 沒有資料,每則 Json 就在 Frames 多排 1 個等待者
///       (待機 2 小時每客戶端 +2 MiB)。改用 <see cref="ChannelWaitSet{T}"/>(與 SimCore.Host 同一作法)。確定性驗證:Frames 閒置時每則 Json 只新建 1 個等待。</item>
/// <item>radar-R6:異常客戶端的分段訊息無上限累積 → 單則訊息上限 <see cref="RadarWebSocketServer.MaxMessageBytes"/>,超過以 MessageTooBig 關閉。</item>
/// <item>radar-R2:主控程式的 cpaErrors / trackCounts 兩個 List 無上限 → <see cref="ArpaTruthSummary"/> 只留累計值與每組配對的最後一筆,摘要數值與修正前相同。</item>
/// <item>host-accept-loop-death(同一寫法):GET 後 RST 不得讓接受迴圈結束。</item>
/// </list>
/// </summary>
public class RadarServerBoundsTests
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

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"逾時:{what}");
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task WaitSet_IdleChannel_KeepsTheSamePendingWait()
    {
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
        var json = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        using var cts = new CancellationTokenSource();
        var set = new ChannelWaitSet<byte[]>(cts.Token, json.Reader, frames.Reader);
        Task<bool>? frameWait = null;
        const int rounds = 300;
        for (var i = 0; i < rounds; i++)
        {
            var wake = set.WaitAnyAsync();
            Assert.False(wake.IsCompleted);
            frameWait ??= set.Pending(1);
            Assert.Same(frameWait, set.Pending(1));   // 待機:Frames 一直是同一個未完成的等待(修正前每圈多排 1 個)
            Assert.True(json.Writer.TryWrite(new byte[] { (byte)i }));
            Assert.True(await wake.WaitAsync(Limit));
            Assert.True(json.Reader.TryRead(out var m));
            Assert.Equal((byte)i, m[0]);
        }
        Assert.Equal(rounds + 1, set.WaitsStarted);
        // 影像幀恢復:保留的 Frames 等待完成並喚醒
        Assert.True(frames.Writer.TryWrite(new byte[] { 1 }));
        Assert.True(await set.WaitAnyAsync().WaitAsync(Limit));
        Assert.True(frameWait!.IsCompletedSuccessfully);
        frames.Writer.TryComplete();
        json.Writer.TryComplete();
        Assert.True(frames.Reader.TryRead(out _));
        Assert.False(await set.WaitAnyAsync().WaitAsync(Limit));   // 兩個通道都完成且沒有資料
    }

    [Fact]
    public async Task Server_SendLoop_FramesIdle_CreatesOneNewWaitPerJsonMessage()
    {
        await using var server = new RadarWebSocketServer(FreePort(), new[] { "x" }, allInterfaces: false);
        server.Start();
        using var client = new ClientWebSocket();
        client.Options.Proxy = null;
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/radar/x"), CancellationToken.None);
        await WaitForAsync(() => server.SendWaitsStarted >= 2, "送出迴圈開始等待");
        Assert.Equal(2, server.SendWaitsStarted);
        var buffer = new byte[4096];
        const int messages = 40;
        for (var k = 1; k <= messages; k++)
        {
            server.PublishJson("x", Encoding.UTF8.GetBytes($"{{\"type\":\"tracks\",\"seq\":{k}}}"));
            using var rcv = new CancellationTokenSource(Limit);
            var r = await client.ReceiveAsync(buffer, rcv.Token);
            Assert.Equal($"{{\"type\":\"tracks\",\"seq\":{k}}}", Encoding.UTF8.GetString(buffer, 0, r.Count));
            // 只換掉完成的 Json 等待;閒置的 Frames 等待保留(修正前每則多排 1 個 Frames 等待者)
            await WaitForAsync(() => server.SendWaitsStarted >= 2 + k, $"第 {k} 則後送出迴圈再次等待");
            Assert.Equal(2 + k, server.SendWaitsStarted);
        }
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [Fact]
    public async Task Receive_FragmentedMessageOverLimit_ClosesWithMessageTooBig()
    {
        await using var server = new RadarWebSocketServer(FreePort(), new[] { "x" }, allInterfaces: false);
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new ClientWebSocket();
        client.Options.Proxy = null;
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/radar/x"), cts.Token);
        var fragment = new byte[64 * 1024];
        Array.Fill(fragment, (byte)'a');
        // 一直不結束的分段訊息:超過 1 MB 時伺服端關閉(修正前 MemoryStream 無上限成長,probe r6 送到 64 MiB 仍在收)
        var sending = Task.Run(async () =>
        {
            try { for (var i = 0; i < 64; i++) await client.SendAsync(fragment, WebSocketMessageType.Text, endOfMessage: false, cts.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        });
        var buffer = new byte[4096];
        WebSocketReceiveResult r;
        do r = await client.ReceiveAsync(buffer, cts.Token);
        while (r.MessageType != WebSocketMessageType.Close);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, r.CloseStatus);
        await sending.WaitAsync(Limit);
        Assert.DoesNotContain(server.Commands, c => !c.Disconnected);
        // 正常大小的指令照常收(新連線)
        using var ok = new ClientWebSocket();
        ok.Options.Proxy = null;
        await ok.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/radar/x"), cts.Token);
        await ok.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"gain\",\"value\":60}"), WebSocketMessageType.Text, true, cts.Token);
        await WaitForAsync(() => server.Commands.Any(c => !c.Disconnected && c.Json.Contains("gain")), "正常指令入列");
    }

    [Fact]
    public async Task GetThenReset_DoesNotStopTheAcceptLoop()
    {
        await using var server = new RadarWebSocketServer(FreePort(), new[] { "x" }, allInterfaces: false);
        server.Start();
        var get = Encoding.ASCII.GetBytes($"GET /radar/x HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n\r\n");
        for (var i = 0; i < 30; i++)
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.LingerState = new LingerOption(true, 0);   // Close 時送 RST
            await s.ConnectAsync(IPAddress.Loopback, server.Port);
            await s.SendAsync(get, SocketFlags.None);
            s.Close();
        }
        using var cts = new CancellationTokenSource(Limit);
        using var ws = new ClientWebSocket();
        ws.Options.Proxy = null;
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/radar/x"), cts.Token);
        Assert.Equal(WebSocketState.Open, ws.State);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Limit };
        Assert.Contains("radar endpoint", await http.GetStringAsync($"http://127.0.0.1:{server.Port}/radar/x"));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
    }

    [Fact]
    public void TruthSummary_MatchesTheListBasedSummary_WithBoundedPairs()
    {
        // 修正前的寫法(每筆都存進 List,結束時 LINQ)當對照
        var rng = new Random(1234);
        var list = new List<(double T, string Radar, int Id, string Truth, double Err, bool Stable)>();
        var counts = new List<(double T, string Radar, int Tracking, int Total)>();
        var summary = new ArpaTruthSummary();
        string[] truths = { "T1", "T2", "T3", "AIS416000001" };
        for (var step = 0; step < 20_000; step++)
        {
            var t = step * 1.0;
            foreach (var radar in new[] { "x", "s" })
            {
                if (radar == "s" && step % 3 == 0) continue;
                var tracking = rng.Next(0, 6);
                counts.Add((t, radar, tracking, tracking + 1));
                summary.AddTrackCount(radar, t, tracking, tracking + 1);
                for (var k = 0; k < tracking; k++)
                {
                    var id = rng.Next(1, 8);
                    var truth = truths[rng.Next(truths.Length)];
                    var err = rng.NextDouble() * 0.05;
                    var stable = rng.NextDouble() < 0.6;
                    list.Add((t, radar, id, truth, err, stable));
                    summary.AddCpaError(radar, t, id, truth, err, stable);
                }
            }
        }
        Assert.True(list.Count > 50_000);
        Assert.InRange(summary.PairCount, 1, 2 * 7 * truths.Length);   // 不隨筆數成長
        foreach (var radar in new[] { "x", "s", "none" })
        {
            var errs = list.Where(e => e.Radar == radar).ToList();
            var stable = errs.Where(e => e.Stable).ToList();
            var all = summary.AllErrors(radar);
            var st = summary.StableErrors(radar);
            Assert.Equal(errs.Count, all.Count);
            Assert.Equal(stable.Count, st.Count);
            if (errs.Count > 0)
            {
                Assert.Equal(errs.Average(e => e.Err), all.Average);   // 同樣的加總順序:逐位元相同
                Assert.Equal(errs.Max(e => e.Err), all.Max);
                Assert.Equal(stable.Average(e => e.Err), st.Average);
                Assert.Equal(stable.Max(e => e.Err), st.Max);
            }
            var lastCount = counts.LastOrDefault(c => c.Radar == radar);
            if (lastCount != default) Assert.Equal((lastCount.T, lastCount.Tracking, lastCount.Total), (summary.LastTrackCount(radar)!.Value.T, summary.LastTrackCount(radar)!.Value.Tracking, summary.LastTrackCount(radar)!.Value.Total));
            else Assert.Null(summary.LastTrackCount(radar));
            var expected = errs.GroupBy(e => (e.Id, e.Truth)).OrderBy(g => g.Key.Id).Select(g => g.Last()).Select(e => (e.Id, e.Truth, e.T, e.Err, e.Stable)).ToList();
            var actual = summary.LastByPair(radar).Select(p => (p.TrackId, p.TruthId, p.T, p.Err, p.Stable)).ToList();
            Assert.Equal(expected, actual);
        }
    }
}
