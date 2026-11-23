using System.Globalization;
using SimosaBRM.ConsoleSerial.Bridge;
using SimosaBRM.ConsoleSerial.Core;
using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>
/// 第三十輪 2 小時搜尋 serial-todevice-unbounded:送往裝置的行改為有界並合併(<see cref="DeviceLineQueue"/>)。修正前是無界通道,
/// 裝置仍送心跳但不讀(序列埠寫出每行 1 s 逾時)時,LED/DISP 行以 3.9 行/s 線性累積(300 s 1172 行),顯示延遲也無上限。
/// 這裡以確定性的條件驗證:同類行只留最新一行、位置不變;不合併的行超過上限丟最舊;橋接在裝置卡住時佇列不隨狀態幀數成長,
/// 裝置恢復後收到的是各欄最新值。
/// </summary>
public class DeviceLineQueueTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("LED,FU,1", "LED,FU")]
    [InlineData("LED,ALARM,blink*3C", "LED,ALARM")]
    [InlineData("DISP,RUD,-12.5", "DISP,RUD")]
    [InlineData("DISP,TEL,HAH*1A", "DISP,TEL")]
    [InlineData("BUZZ,500", "BUZZ")]
    [InlineData("BUZZ,500*2F", "BUZZ")]
    [InlineData("ACK", null)]
    [InlineData("ACK*41", null)]
    [InlineData("LED,FU", null)]
    [InlineData("BUZZER", null)]
    public void KeyOf_ClassifiesStateLines(string line, string? key) => Assert.Equal(key, DeviceLineQueue.KeyOf(line));

    [Fact]
    public void SameKindLines_KeepOnlyTheLatest_AtTheOriginalPosition()
    {
        var q = new DeviceLineQueue();
        Assert.True(q.TryWrite("LED,LINK,1"));
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(q.TryWrite($"DISP,RUD,{i}"));
            Assert.True(q.TryWrite(i % 2 == 0 ? "LED,ALARM,blink" : "LED,ALARM,0"));
            Assert.True(q.TryWrite("BUZZ,500"));
        }
        Assert.True(q.TryWrite("ACK"));
        Assert.Equal(5, q.Count);
        Assert.Equal(3 * 999, q.Coalesced);
        Assert.Equal(0, q.Dropped);
        var lines = new List<string>();
        while (q.TryRead(out var l)) lines.Add(l);
        // 各類第一次排隊的順序,值為最新
        Assert.Equal(new[] { "LED,LINK,1", "DISP,RUD,999", "LED,ALARM,0", "BUZZ,500", "ACK" }, lines);
        // 讀出後同類再寫入是新的一行(排在最後)
        Assert.True(q.TryWrite("DISP,RUD,1000"));
        Assert.True(q.TryRead(out var again));
        Assert.Equal("DISP,RUD,1000", again);
    }

    [Fact]
    public void UnkeyedLines_AreBounded_DroppingTheOldest()
    {
        var q = new DeviceLineQueue(capacity: 8);
        Assert.True(q.TryWrite("LED,FU,1"));
        for (var i = 0; i < 20; i++) Assert.True(q.TryWrite($"ACK*{i:X2}"));
        Assert.Equal(8, q.Count);
        Assert.Equal(13, q.Dropped);
        var lines = new List<string>();
        while (q.TryRead(out var l)) lines.Add(l);
        Assert.Equal(Enumerable.Range(12, 8).Select(i => $"ACK*{i:X2}"), lines);
        // 被丟掉的 LED,FU 的類別也一併移除:同類新行是新的一行
        Assert.True(q.TryWrite("LED,FU,0"));
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public async Task WaitToRead_WakesOnWrite_AndEndsAfterCompleteOnceDrained()
    {
        var q = new DeviceLineQueue();
        var wait = q.WaitToReadAsync(CancellationToken.None).AsTask();
        Assert.False(wait.IsCompleted);
        q.TryWrite("LED,LINK,1");
        Assert.True(await wait.WaitAsync(Limit));
        q.TryWrite("ACK");
        q.Complete();
        Assert.False(q.TryWrite("ACK"));   // 完成後不再接受
        Assert.True(await q.WaitToReadAsync(CancellationToken.None).AsTask().WaitAsync(Limit));   // 已排隊的仍可讀完
        Assert.True(q.TryRead(out var a));
        Assert.Equal("LED,LINK,1", a);
        Assert.True(q.TryRead(out var b));
        Assert.Equal("ACK", b);
        Assert.False(await q.WaitToReadAsync(CancellationToken.None).AsTask().WaitAsync(Limit));
    }

    /// <summary>寫出卡住的裝置(模擬序列埠 WriteTimeout 而裝置不讀):放行前每次寫出都等待;讀取端由測試餵行。</summary>
    private sealed class StuckDevice : IDeviceLink
    {
        private readonly System.Threading.Channels.Channel<string?> _in = System.Threading.Channels.Channel.CreateUnbounded<string?>();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Written = new();
        public int WritesStarted;
        public string Description => "stuck";
        public void Feed(string line) => _in.Writer.TryWrite(line);
        public void Release() => _release.TrySetResult();
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            try { return await _in.Reader.ReadAsync(ct); }
            catch (System.Threading.Channels.ChannelClosedException) { return null; }
        }
        public async Task WriteLineAsync(string line, CancellationToken ct)
        {
            Interlocked.Increment(ref WritesStarted);
            await _release.Task.WaitAsync(ct);
            lock (Written) Written.Add(line);
        }
        public ValueTask DisposeAsync()
        {
            _in.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCore : ICoreLink
    {
        public bool Connected => true;
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? FrameReceived;
        public void Send(SimCommand command) { }
        public void Start() => ConnectionChanged?.Invoke(true);
        public void Frame(string json) => FrameReceived?.Invoke(json);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string State(double rudder, double heading, long tick)
        => string.Create(CultureInfo.InvariantCulture,
            $"{{\"tick\":{tick},\"rudder\":{rudder:0.0},\"rpm\":{tick % 100},\"heading\":{heading:0.0},\"telegraph\":\"HAH\",\"steering\":{{\"mode\":\"{(tick % 2 == 0 ? "fu" : "nfu")}\"}},\"alerts\":[],\"bnwas\":{{\"mode\":\"off\",\"active\":false,\"stage\":0}}}}");

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"逾時:{what}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Bridge_DeviceNotReading_QueueStaysBounded_AndDeviceGetsLatestValuesAfterRecovery()
    {
        var device = new StuckDevice();
        var core = new FakeCore();
        var session = new ConsoleSession(heartbeatTimeoutMs: 1e9);
        var clock = 0L;
        var bridge = new ConsoleBridge(device, core, session, _ => { }, () => Interlocked.Read(ref clock)) { PollIntervalMs = 5 };
        using var cts = new CancellationTokenSource();
        var run = bridge.RunAsync(cts.Token);
        device.Feed("HB");   // 裝置已連線(之後的狀態幀才回寫)
        await WaitUntilAsync(() => session.DeviceConnected, "裝置連線");
        await WaitUntilAsync(() => device.WritesStarted >= 1, "寫出迴圈卡在第一行");
        // 裝置不讀:2000 個狀態幀,每幀舵角、艏向、轉速、操舵燈都變(時間每幀 +250 ms,不受 DISP 5 Hz 限制)
        var maxQueue = 0;
        for (var i = 1; i <= 2000; i++)
        {
            Interlocked.Add(ref clock, 250);
            core.Frame(State(rudder: (i % 70) - 35, heading: i % 360, tick: i));
            if (i % 50 == 0) device.Feed("HB");
            maxQueue = Math.Max(maxQueue, bridge.DeviceQueueLength);
        }
        // 修正前:佇列約 2000 × 5 行;修正後最多 LED 7 類 + DISP 4 欄 + BUZZ
        Assert.InRange(maxQueue, 1, 12);
        Assert.True(bridge.DeviceLinesCoalesced > 5000, $"合併 {bridge.DeviceLinesCoalesced} 行");
        Assert.Equal(1, device.WritesStarted);
        // 裝置恢復:收到的是各欄最新值(i = 2000:舵角 (2000 % 70) − 35 = 5、艏向 200、轉速 0、操舵模式 fu)
        device.Release();
        await WaitUntilAsync(() => bridge.DeviceQueueLength == 0 && device.WritesStarted == device.Written.Count, "佇列寫完");
        List<string> written;
        lock (device.Written) written = device.Written.ToList();
        Assert.InRange(written.Count, 2, 14);
        Assert.Equal("DISP,RUD,5.0", written.Last(l => l.StartsWith("DISP,RUD,", StringComparison.Ordinal)));
        Assert.Equal("DISP,HDG,200", written.Last(l => l.StartsWith("DISP,HDG,", StringComparison.Ordinal)));
        Assert.Equal("DISP,RPM,0", written.Last(l => l.StartsWith("DISP,RPM,", StringComparison.Ordinal)));
        Assert.Equal("LED,FU,1", written.Last(l => l.StartsWith("LED,FU,", StringComparison.Ordinal)));
        Assert.Equal("LED,NFU,0", written.Last(l => l.StartsWith("LED,NFU,", StringComparison.Ordinal)));
        cts.Cancel();
        await run.WaitAsync(Limit);
        await bridge.DisposeAsync();
    }
}
