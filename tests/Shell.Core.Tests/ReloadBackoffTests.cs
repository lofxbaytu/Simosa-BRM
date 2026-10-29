// ReloadBackoff:指數退避、上限、歸零、說明文字
using SimosaBRM.Shell.Core;
using Xunit;

namespace SimosaBRM.Shell.Core.Tests;

public class ReloadBackoffTests
{
    [Fact]
    public void Default_DoublesFromOneSecond_CappedAtFifteen()
    {
        var b = new ReloadBackoff();
        Assert.Equal(TimeSpan.FromSeconds(1), b.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(2), b.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(4), b.DelayFor(3));
        Assert.Equal(TimeSpan.FromSeconds(8), b.DelayFor(4));
        Assert.Equal(TimeSpan.FromSeconds(15), b.DelayFor(5));
        Assert.Equal(TimeSpan.FromSeconds(15), b.DelayFor(40));
        Assert.Equal(TimeSpan.FromSeconds(15), b.DelayFor(2000));   // 2^1999 不溢位
    }

    [Fact]
    public void NextDelay_CountsAttempts_ResetClears()
    {
        var b = new ReloadBackoff(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
        Assert.Equal(0, b.Attempt);
        Assert.Equal(TimeSpan.FromMilliseconds(500), b.NextDelay());
        Assert.Equal(TimeSpan.FromMilliseconds(1000), b.NextDelay());
        Assert.Equal(TimeSpan.FromMilliseconds(2000), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(3), b.NextDelay());
        Assert.Equal(4, b.Attempt);
        b.Reset();
        Assert.Equal(0, b.Attempt);
        Assert.Equal(TimeSpan.FromMilliseconds(500), b.NextDelay());
    }

    [Fact]
    public void MonotonicNonDecreasing()
    {
        var b = new ReloadBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), 1.5);
        var prev = TimeSpan.Zero;
        for (var i = 1; i <= 30; i++) { var d = b.DelayFor(i); Assert.True(d >= prev); Assert.True(d <= b.Max); prev = d; }
    }

    [Fact]
    public void InvalidConstruction_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReloadBackoff(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReloadBackoff(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReloadBackoff(factor: 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReloadBackoff().DelayFor(0));
    }

    [Fact]
    public void Describe_IsChinese_WithSecondsAndReason()
    {
        Assert.Equal("載入失敗(第 3 次:ConnectionAborted),4 秒後重試", ReloadBackoff.Describe(3, TimeSpan.FromSeconds(4), "ConnectionAborted"));
        Assert.Equal("載入失敗(第 1 次),0.5 秒後重試", ReloadBackoff.Describe(1, TimeSpan.FromMilliseconds(500)));
        Assert.Equal("載入失敗(第 9 次),15 秒後重試", ReloadBackoff.Describe(9, TimeSpan.FromSeconds(15), " "));
    }
}
