using SimosaBRM.ConsoleSerial.Core;
using Xunit;

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>回寫格式(console-serial.md 第 3、6 節):LED 只在改變時送、DISP 格式與 5 Hz 限制、新警報 BUZZ 一次、Reset 重送不重鳴。</summary>
public class FeedbackFormatterTests
{
    private static string State(double rudder = 0, double rpm = 0, double heading = 0, string tel = "STOP", string mode = "fu", string alerts = "[]", string bnwas = "{\"mode\":\"off\",\"active\":false,\"stage\":0,\"dormant_s\":300}", long tick = 1)
        => $"{{\"tick\":{tick},\"rudder\":{rudder.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"rpm\":{rpm.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"heading\":{heading.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"telegraph\":\"{tel}\",\"steering\":{{\"mode\":\"{mode}\",\"pump\":\"1\"}},\"alerts\":{alerts},\"bnwas\":{bnwas}}}";

    private static ConsoleStateView View(string json) => ConsoleStateView.TryParse(json) ?? throw new Xunit.Sdk.XunitException("應為狀態幀");

    [Fact]
    public void StateView_ParsesNeededFieldsAndSkipsNonStateFrames()
    {
        var v = View(State(rudder: -12.34, rpm: 85.6, heading: 359.6, tel: "HAH", mode: "nfu"));
        Assert.Equal(-12.34, v.Rudder);
        Assert.Equal("HAH", v.Telegraph);
        Assert.Equal("nfu", v.SteeringMode);
        Assert.Null(ConsoleStateView.TryParse("{\"type\":\"event\",\"event\":{\"kind\":\"collision\"}}"));
        Assert.Null(ConsoleStateView.TryParse("{\"type\":\"ack\",\"ok\":true}"));
        Assert.Null(ConsoleStateView.TryParse("not json"));
        Assert.Null(ConsoleStateView.TryParse("[1,2]"));
        Assert.Null(ConsoleStateView.TryParse("{\"rudder\":1}"));   // 無 tick 不是狀態幀
    }

    [Fact]
    public void FirstUpdate_SendsEverything_ThenOnlyChanges()
    {
        var f = new FeedbackFormatter { CoreConnected = true };
        var lines = f.Update(View(State(rudder: 10.04, rpm: 85.6, heading: 86.5, tel: "HAH")), 0);
        Assert.Equal(new[] { "LED,LINK,1", "LED,NFU,0", "LED,AUTO,0", "LED,EMERG,0", "LED,FU,1", "LED,ALARM,0", "LED,BNWAS,0", "DISP,RUD,10.0", "DISP,RPM,86", "DISP,HDG,087", "DISP,TEL,HAH" }, lines);
        Assert.Empty(f.Update(View(State(rudder: 10.04, rpm: 85.6, heading: 86.5, tel: "HAH", tick: 2)), 300));
        Assert.Equal(new[] { "DISP,HDG,088" }, f.Update(View(State(rudder: 10.04, rpm: 85.6, heading: 87.5, tel: "HAH", tick: 3)), 600));
    }

    [Fact]
    public void Display_FormatsRudderRpmHeadingTelegraph()
    {
        var f = new FeedbackFormatter();
        var lines = f.Update(View(State(rudder: -0.04, rpm: -42.5, heading: 359.7, tel: "DSAS")), 0);
        Assert.Contains("DISP,RUD,0.0", lines);   // -0.04 → 0.0(不顯示 -0.0)
        Assert.Contains("DISP,RPM,-43", lines);   // 遠離零四捨五入
        Assert.Contains("DISP,HDG,000", lines);   // 359.7 → 360 → 000
        Assert.Contains("DISP,TEL,DSAS", lines);
        Assert.Contains("LED,LINK,0", lines);
    }

    [Fact]
    public void Display_RateLimitedTo5Hz_PendingFlushedByPoll()
    {
        var f = new FeedbackFormatter();
        f.Update(View(State(rudder: 0)), 0);
        Assert.DoesNotContain(f.Update(View(State(rudder: 1, tick: 2)), 40), l => l.StartsWith("DISP,RUD"));
        Assert.DoesNotContain(f.Update(View(State(rudder: 2, tick: 3)), 80), l => l.StartsWith("DISP,RUD"));
        Assert.Empty(f.Poll(150));
        Assert.Equal(new[] { "DISP,RUD,2.0" }, f.Poll(200));   // 最後值補送
        Assert.Empty(f.Poll(300));
        // 回到已送出值:待送作廢
        f.Update(View(State(rudder: 3, tick: 4)), 250);
        f.Update(View(State(rudder: 2, tick: 5)), 260);
        Assert.Empty(f.Poll(500));
    }

    [Fact]
    public void SteeringLeds_AreMutuallyExclusive()
    {
        var f = new FeedbackFormatter();
        f.Update(View(State(mode: "fu")), 0);
        var lines = f.Update(View(State(mode: "nfu", tick: 2)), 300);
        Assert.Equal(new[] { "LED,FU,0", "LED,NFU,1" }, lines.Where(l => l.StartsWith("LED,")));
        lines = f.Update(View(State(mode: "autopilot", tick: 3)), 600);
        Assert.Equal(new[] { "LED,NFU,0", "LED,AUTO,1" }, lines.Where(l => l.StartsWith("LED,")));
        lines = f.Update(View(State(mode: "emergency", tick: 4)), 900);
        Assert.Equal(new[] { "LED,AUTO,0", "LED,EMERG,1" }, lines.Where(l => l.StartsWith("LED,")));
    }

    private const string Unacked = "[{\"id\":\"gyroFailure\",\"state\":\"activeUnacknowledged\",\"audible\":true,\"raisedAt_s\":12.5}]";
    private const string Acked = "[{\"id\":\"gyroFailure\",\"state\":\"activeAcknowledged\",\"audible\":false,\"raisedAt_s\":12.5}]";
    private const string Silenced = "[{\"id\":\"gyroFailure\",\"state\":\"activeSilenced\",\"audible\":false,\"raisedAt_s\":12.5}]";
    private const string Rectified = "[{\"id\":\"gyroFailure\",\"state\":\"rectifiedUnacknowledged\",\"audible\":false,\"raisedAt_s\":12.5}]";

    [Fact]
    public void Alarm_BlinkWhenUnacknowledged_BuzzOncePerNewAlert_SteadyWhenAcknowledged()
    {
        var f = new FeedbackFormatter();
        f.Update(View(State()), 0);
        var lines = f.Update(View(State(alerts: Unacked, tick: 2)), 300);
        Assert.Contains("LED,ALARM,blink", lines);
        Assert.Contains("BUZZ,500", lines);
        lines = f.Update(View(State(alerts: Unacked, tick: 3)), 600);
        Assert.Empty(lines);   // 同一警報不再蜂鳴、不重送燈號
        lines = f.Update(View(State(alerts: Silenced, tick: 4)), 900);
        Assert.Equal(new[] { "LED,ALARM,1" }, lines);
        lines = f.Update(View(State(alerts: Acked, tick: 5)), 1200);
        Assert.Empty(lines);
        lines = f.Update(View(State(alerts: Rectified, tick: 6)), 1500);
        Assert.Equal(new[] { "LED,ALARM,blink" }, lines);   // 已排除未確認:閃爍但不可聽 → 不蜂鳴
        lines = f.Update(View(State(alerts: "[]", tick: 7)), 1800);
        Assert.Equal(new[] { "LED,ALARM,0" }, lines);
        // 同 id 再次觸發(raisedAt_s 不同)視為新警報
        lines = f.Update(View(State(alerts: Unacked.Replace("12.5", "99"), tick: 8)), 2100);
        Assert.Contains("BUZZ,500", lines);
    }

    [Fact]
    public void Bnwas_OffWatchAndAlarmStages()
    {
        var f = new FeedbackFormatter();
        var off = f.Update(View(State()), 0);
        Assert.Contains("LED,BNWAS,0", off);
        var watch = f.Update(View(State(bnwas: "{\"mode\":\"on\",\"active\":true,\"stage\":0,\"dormant_s\":300}", tick: 2)), 300);
        Assert.Equal(new[] { "LED,BNWAS,1" }, watch);
        var alarm = f.Update(View(State(bnwas: "{\"mode\":\"on\",\"active\":true,\"stage\":2,\"dormant_s\":300}", tick: 3)), 600);
        Assert.Equal(new[] { "LED,BNWAS,blink" }, alarm);
    }

    [Fact]
    public void Reset_ResendsAllLedsAndDisplays_WithoutBuzz()
    {
        var f = new FeedbackFormatter { CoreConnected = true };
        f.Update(View(State(rudder: 5, alerts: Unacked)), 0);
        var again = f.Resend(1000);
        Assert.Contains("LED,LINK,1", again);
        Assert.Contains("LED,FU,1", again);
        Assert.Contains("LED,ALARM,blink", again);
        Assert.Contains("DISP,RUD,5.0", again);
        Assert.Contains("DISP,TEL,STOP", again);
        Assert.DoesNotContain("BUZZ,500", again);
    }

    [Fact]
    public void LinkLed_FollowsCoreConnection_EvenWithoutState()
    {
        var f = new FeedbackFormatter();
        Assert.Equal(new[] { "LED,LINK,0" }, f.Update(null, 0));
        f.CoreConnected = true;
        Assert.Equal(new[] { "LED,LINK,1" }, f.Update(null, 10));
        Assert.Empty(f.Update(null, 20));
    }
}
