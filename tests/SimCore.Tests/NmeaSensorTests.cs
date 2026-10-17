using SimosaBRM.Gateway.Nmea;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>NMEA 輸出指示值與無效狀態(sensors-alerts.md 第 6 節):HDT/DPT 停送、GGA 品質 0、RMC V/N、VTG N、VBW V、ALR 句型。</summary>
public class NmeaSensorTests
{
    private static readonly DateTime Utc = new(2026, 11, 20, 13, 30, 0, DateTimeKind.Utc);

    private static SimulationEngine Engine(params string[] faults)
    {
        var e = TestData.MinimalEngine();
        e.Run(50);
        foreach (var f in faults) e.Enqueue(SimCommand.InjectFault(f));
        e.Run(10);
        return e;
    }

    private static string[] Fields(string sentence) => sentence[1..sentence.IndexOf('*')].Split(',');

    [Fact]
    public void NominalSentencesUnchanged()
    {
        var s = Engine().BuildState();
        Assert.Equal(new[] { "HDT", "ROT", "RSA" }, NmeaEncoder.FastSentences(s).Select(x => x[3..6]));
        Assert.Equal(new[] { "GGA", "RMC", "VTG", "VBW", "DPT", "MWV", "MWV" }, NmeaEncoder.SlowSentences(s, Utc).Select(x => x[3..6]));
        Assert.Empty(NmeaEncoder.AlrSentences(s, Utc));
    }

    [Fact]
    public void GyroDriftAndGpsOffsetAreSentAsIndicatedValues()
    {
        var e = TestData.MinimalEngine();
        e.Enqueue(SimCommand.InjectFault("gyroDrift", new { rate_degPerMin = 6.0 }));
        e.Enqueue(SimCommand.InjectFault("gpsJump", new { distance_m = 1852, bearing_deg = 0 }));
        e.Run(500); // 10 s → 漂移 1°
        var s = e.BuildState();
        Assert.Equal(NmeaSentence.F1(s.Heading), Fields(NmeaEncoder.Hdt(s))[1]);
        Assert.NotEqual(NmeaSentence.F1(s.Truth!.Heading), Fields(NmeaEncoder.Hdt(s))[1]);
        var gga = Fields(NmeaEncoder.Gga(s, Utc));
        Assert.Equal(NmeaSentence.FormatLatitude(s.Pos.Lat).Value, gga[2]);
        Assert.InRange((s.Pos.Lat - s.Truth.Pos.Lat) * 60.0, 0.99, 1.01); // 北移 1 nm ≈ 1′
    }

    [Fact]
    public void GyroFailureStopsHdtAndVoidsVbwGround()
    {
        var s = Engine("gyroFailure").BuildState();
        Assert.False(s.Sensors!.HeadingValid);
        Assert.Equal(new[] { "ROT", "RSA" }, NmeaEncoder.FastSentences(s).Select(x => x[3..6]));
        var vbw = Fields(NmeaEncoder.Vbw(s));
        Assert.Equal("A", vbw[3]);           // 對水仍有效
        Assert.Equal(("", "", "V"), (vbw[4], vbw[5], vbw[6]));
    }

    [Fact]
    public void GpsFailureGivesInvalidFixSentences()
    {
        var s = Engine("gpsFailure").BuildState();
        var gga = NmeaEncoder.Gga(s, Utc);
        var rmc = NmeaEncoder.Rmc(s, Utc);
        var vtg = NmeaEncoder.Vtg(s);
        Assert.True(NmeaSentence.Verify(gga) && NmeaSentence.Verify(rmc) && NmeaSentence.Verify(vtg));
        Assert.Equal("$GPGGA,133000.00,,,,,0,00,,,M,,M,,*", gga[..^2]);
        var r = Fields(rmc);
        Assert.Equal(("V", "", "N"), (r[2], r[3], r[12]));
        Assert.Equal("201126", r[9]);
        var v = Fields(vtg);
        Assert.Equal(("", "", "N"), (v[1], v[5], v[9]));
        Assert.Equal("V", Fields(NmeaEncoder.Vbw(s))[6]);
    }

    [Fact]
    public void EchoSounderAndLogFailures()
    {
        var s = Engine("echoSounderFailure", "logFailure").BuildState();
        Assert.DoesNotContain(NmeaEncoder.SlowSentences(s, Utc), x => x.Contains("DPT"));
        var vbw = Fields(NmeaEncoder.Vbw(s));
        Assert.Equal(("", "", "V"), (vbw[1], vbw[2], vbw[3]));
        Assert.Equal("A", vbw[6]);
    }

    [Fact]
    public void AlrSentencesReflectAlertStates()
    {
        var e = Engine("gyroFailure", "bowThruster");
        var alr = NmeaEncoder.AlrSentences(e.BuildState(), Utc);
        Assert.Equal(new[] { "$IIALR,133000.00,001,A,V,GYRO FAILURE", "$IIALR,133000.00,010,A,V,BOW THRUSTER FAILURE" }, alr.Select(x => x[..x.IndexOf('*')]));
        Assert.All(alr, x => Assert.True(NmeaSentence.Verify(x)));
        e.Enqueue(SimCommand.AlertAcknowledge("gyroFailure"));
        e.Enqueue(SimCommand.ClearFault("bowThruster"));
        e.Run(2);
        alr = NmeaEncoder.AlrSentences(e.BuildState(), Utc);
        Assert.Equal(new[] { "001,A,A", "010,V,V" }, alr.Select(x => string.Join(",", Fields(x)[2..5])));
    }

    [Fact]
    public void MwvTrueUsesTrueHeadingNotGyro()
    {
        // 審查發現:MWV(T) 為相對船艏的真風角,與電羅經無關;漂移或失效時不得帶入電羅經誤差
        var e = TestData.MinimalEngine(speedKn: 8);
        void Do(SimCommand c) { e.Enqueue(c); e.ProcessPendingCommands(); }
        Do(SimCommand.SetEnvironment(windTrueSpeedKn: 20, windTrueDirDeg: 90));
        Do(SimCommand.Autopilot(true, heading: 0));
        TestData.RunTo(e, 10);
        Do(SimCommand.InjectFault("gyroDrift", new { rate_degPerMin = 2.0 }));
        TestData.RunTo(e, 310);
        var s = e.BuildState();
        Assert.True(Math.Abs(s.Truth!.HeadingErrorDeg) > 5);
        var expected = NmeaSentence.F1(Units.NormalizeHeadingDeg(s.Wind.TrueDir - s.Truth.Heading));
        var f = Fields(NmeaEncoder.MwvTrue(s));
        Assert.Equal((expected, "T", "A"), (f[1], f[2], f[5]));

        Do(SimCommand.InjectFault("gyroFailure"));
        Do(SimCommand.Rudder(20));
        TestData.RunTo(e, 400);
        s = e.BuildState();
        Assert.False(s.Sensors!.HeadingValid);
        expected = NmeaSentence.F1(Units.NormalizeHeadingDeg(s.Wind.TrueDir - s.Truth!.Heading));
        f = Fields(NmeaEncoder.MwvTrue(s));
        Assert.Equal((expected, "A"), (f[1], f[5]));
        Assert.True(NmeaSentence.Verify(NmeaEncoder.MwvTrue(s)));
    }
}
