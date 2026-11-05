using SimosaBRM.ConsoleSerial.Core;
using Xunit;

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>去抖與速率限制(console-serial.md 第 5 節):首筆一律送、差小於門檻丟棄、間隔未到記為待送並於 Poll 補送、回到已送位置作廢待送。</summary>
public class ValueDebouncerTests
{
    [Fact]
    public void FirstValueAlwaysSent_SmallDeltaDropped()
    {
        var d = new ValueDebouncer(50, 0.5);
        Assert.Equal(0.0, d.Offer(0.0, 0));
        Assert.Null(d.Offer(0.4, 100));
        Assert.Null(d.Offer(-0.49, 200));
        Assert.Equal(0.5, d.Offer(0.5, 300));
        Assert.Equal(0.5, d.LastSent);
    }

    [Fact]
    public void RateLimited_PendingIsFlushedByPoll_NotLost()
    {
        var d = new ValueDebouncer(50, 0.5);
        Assert.Equal(10.0, d.Offer(10.0, 1000));
        Assert.Null(d.Offer(12.0, 1010));   // 間隔未到:待送
        Assert.True(d.HasPending);
        Assert.Null(d.Offer(15.0, 1020));   // 待送被新值取代
        Assert.Null(d.Poll(1040));          // 未滿 50 ms
        Assert.Equal(15.0, d.Poll(1050));   // 最後位置補送
        Assert.False(d.HasPending);
        Assert.Null(d.Poll(1200));
    }

    [Fact]
    public void ReturningToSentValueClearsPending()
    {
        var d = new ValueDebouncer(50, 0.5);
        d.Offer(10.0, 0);
        Assert.Null(d.Offer(13.0, 10));
        Assert.True(d.HasPending);
        Assert.Null(d.Offer(10.2, 20));     // 回到已送出位置附近:待送作廢
        Assert.False(d.HasPending);
        Assert.Null(d.Poll(100));
    }

    [Fact]
    public void TwentyHertzCap_ContinuousStream()
    {
        var d = new ValueDebouncer(50, 0.5);
        var sent = 0;
        for (var t = 0L; t < 1000; t += 5)
        {
            if (d.Offer(t / 10.0, t) is not null) sent++;
            if (d.Poll(t) is not null) sent++;
        }
        Assert.InRange(sent, 19, 21);
    }
}
