using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using SimosaBRM.SimCore.Host;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 第三十輪:WebSocketStateServer 送出迴圈的等待者洩漏回歸(docs/design/testing.md 第 6.6 節)。修正前三個通道都空時每圈重新
/// WaitToReadAsync:SingleReader 無界的回覆通道以新等待者取代並取消舊的,舊等待者在伺服器生命週期權杖上的取消註冊不釋放
/// (每條連線約 1.3 MB/min)。這裡不量記憶體(xunit 平行執行下不穩),改以確定性的條件驗證:通道閒置時連續多次喚醒拿到的是
/// 同一個未完成的等待、不會被取消,建立的等待數 = 喚醒次數 + 閒置通道數;伺服器層以 SendWaitsStarted 確認送出迴圈確實這樣等。
/// </summary>
public class ChannelWaitSetTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>與 WebSocketStateServer.Client 相同的三個通道</summary>
    private sealed class Channels
    {
        public readonly Channel<byte[]> Replies = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public readonly Channel<byte[]> Events = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        public readonly Channel<byte[]> Outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        public ChannelWaitSet<byte[]> Set(CancellationToken token) => new(token, Replies.Reader, Events.Reader, Outbox.Reader);
    }

    [Fact]
    public async Task IdleChannels_KeepTheSamePendingWait_AcrossManyWakeups()
    {
        var ch = new Channels();
        using var cts = new CancellationTokenSource();
        var set = ch.Set(cts.Token);
        const int rounds = 200;   // 狀態幀 25 Hz 的 8 秒
        Task<bool>? replyWait = null, eventWait = null;
        var seen = new List<Task<bool>>();
        for (var i = 0; i < rounds; i++)
        {
            var wake = set.WaitAnyAsync();
            Assert.False(wake.IsCompleted, $"第 {i} 圈:三個通道都空,不應立即喚醒");
            replyWait ??= set.Pending(0);
            eventWait ??= set.Pending(1);
            // 閒置的回覆 / 事件通道:每圈都是同一個未完成的等待(修正前每圈新建,舊的回覆等待被取消)
            Assert.Same(replyWait, set.Pending(0));
            Assert.Same(eventWait, set.Pending(1));
            seen.Add(set.Pending(0)!);
            seen.Add(set.Pending(1)!);
            Assert.True(ch.Outbox.Writer.TryWrite(new byte[] { (byte)i }));
            Assert.True(await wake.WaitAsync(WaitLimit));
            Assert.True(ch.Outbox.Reader.TryRead(out var frame));
            Assert.Equal((byte)i, frame[0]);
        }
        Assert.False(replyWait!.IsCompleted);
        Assert.False(eventWait!.IsCompleted);
        Assert.DoesNotContain(seen, t => t.IsCanceled);
        // 每次喚醒只換掉完成的狀態等待:rounds 個狀態等待 + 回覆、事件各 1 個
        Assert.Equal(rounds + 2, set.WaitsStarted);
    }

    [Fact]
    public async Task OnlyCompletedWaitsAreReplaced_DataArrivingWhileAwakeIsNotLost()
    {
        var ch = new Channels();
        using var cts = new CancellationTokenSource();
        var set = ch.Set(cts.Token);
        var wake = set.WaitAnyAsync();
        var stateWait = set.Pending(2);
        Assert.Equal(3, set.WaitsStarted);
        // 回覆到達:回覆等待完成並喚醒;下一次只換回覆等待,事件與狀態等待保留
        Assert.True(ch.Replies.Writer.TryWrite(new byte[] { 1 }));
        Assert.True(await wake.WaitAsync(WaitLimit));
        Assert.True(ch.Replies.Reader.TryRead(out _));
        var eventWait = set.Pending(1);
        wake = set.WaitAnyAsync();
        Assert.Equal(4, set.WaitsStarted);
        Assert.Same(eventWait, set.Pending(1));
        Assert.Same(stateWait, set.Pending(2));
        // 事件在送出迴圈醒著(兩次 WaitAnyAsync 之間)時到達:保留的事件等待已完成 → 下一次立即喚醒,不會漏
        Assert.True(ch.Events.Writer.TryWrite(new byte[] { 2 }));
        Assert.True(await wake.WaitAsync(WaitLimit));
        Assert.True(ch.Events.Reader.TryRead(out _));
        Assert.True(ch.Events.Writer.TryWrite(new byte[] { 3 }));
        Assert.True(await set.WaitAnyAsync().WaitAsync(WaitLimit));
        Assert.True(ch.Events.Reader.TryRead(out var ev));
        Assert.Equal(3, ev[0]);
    }

    [Fact]
    public async Task CompletedChannels_ReturnFalse_AndCancellationThrows()
    {
        var ch = new Channels();
        using var cts = new CancellationTokenSource();
        var set = ch.Set(cts.Token);
        var wake = set.WaitAnyAsync();
        ch.Outbox.Writer.TryComplete();
        Assert.False(await wake.WaitAsync(WaitLimit));   // 該通道已完成且沒有資料(送出迴圈再檢查三個通道是否都完成)

        var ch2 = new Channels();
        using var cts2 = new CancellationTokenSource();
        var set2 = ch2.Set(cts2.Token);
        var wake2 = set2.WaitAnyAsync();
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wake2.WaitAsync(WaitLimit));
    }

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
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"逾時:{what}");
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task Server_SendLoop_CreatesOneNewWaitPerStateFrame_NotOnePerChannel()
    {
        var engine = TestData.Engine();
        await using var server = new WebSocketStateServer(engine, FreePort());
        server.Start();
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/"), CancellationToken.None);
        // 送出迴圈停在等待:回覆、事件、狀態各 1 個等待者
        await WaitForAsync(() => server.SendWaitsStarted >= 3, "送出迴圈開始等待");
        Assert.Equal(3, server.SendWaitsStarted);
        var buffer = new byte[4096];
        const int frames = 40;
        for (var k = 1; k <= frames; k++)
        {
            server.Publish(System.Text.Encoding.UTF8.GetBytes($"{{\"type\":\"state\",\"seq\":{k}}}"));
            using var rcv = new CancellationTokenSource(WaitLimit);
            var r = await client.ReceiveAsync(buffer, rcv.Token);
            Assert.Equal($"{{\"type\":\"state\",\"seq\":{k}}}", System.Text.Encoding.UTF8.GetString(buffer, 0, r.Count));
            // 送完一幀、三個通道又都空:只換掉完成的狀態等待(修正前每圈 3 個,閒置的回覆等待被取消而洩漏)
            await WaitForAsync(() => server.SendWaitsStarted >= 3 + k, $"第 {k} 幀後送出迴圈再次等待");
            Assert.Equal(3 + k, server.SendWaitsStarted);
        }
    }
}
