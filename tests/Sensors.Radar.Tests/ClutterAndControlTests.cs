using SimosaBRM.Sensors.Radar.Arpa;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>海雜波(風速/STC)、雨雜波(FTC、X/S 差異)、增益雜訊、調諧、故障(radarFailure/radarDegraded)與控制指令。</summary>
public class ClutterAndControlTests
{
    private static int CountAbove(byte[][] img, int thr, int maxCell = int.MaxValue)
    {
        var n = 0;
        foreach (var spoke in img)
            for (var i = 0; i < Math.Min(maxCell, spoke.Length); i++)
                if (spoke[i] >= thr) n++;
        return n;
    }

    private static byte[][] Scan(RadarUnit r, LinearScene s) => EchoTests.OneScan(r, s).Image;

    [Fact]
    public void SeaClutterGrowsWithWindAndIsSuppressedBySeaControl()
    {
        RadarUnit Make(double sea)
        {
            var r = RadarFactory.X();
            r.Apply("{\"cmd\":\"range\",\"value\":1.5}");
            r.Apply($"{{\"cmd\":\"sea\",\"value\":{sea}}}");
            return r;
        }
        var calm = Scan(Make(0), new LinearScene { WindKn = 3 });
        var breeze = Scan(Make(0), new LinearScene { WindKn = 12 });
        var gale = Scan(Make(0), new LinearScene { WindKn = 20 });
        var galeStc = Scan(Make(90), new LinearScene { WindKn = 20 });
        int c0 = CountAbove(calm, 40), c1 = CountAbove(breeze, 40), c2 = CountAbove(gale, 40), c3 = CountAbove(galeStc, 40);
        Assert.True(c0 < c1 && c1 < c2, $"雜波應隨風速增加:{c0} < {c1} < {c2}");
        Assert.True(c3 < c2 / 4, $"STC 應大幅抑制雜波:{c3} vs {c2}");
    }

    [Fact]
    public void SeaClutterIsStrongerUpwind()
    {
        var r = RadarFactory.X();
        r.Apply("{\"cmd\":\"range\",\"value\":3}");
        var img = Scan(r, new LinearScene { WindKn = 20, WindFromDeg = 90 });
        // 迎風(090)扇區 vs 背風(270)扇區
        int up = 0, down = 0;
        for (var k = 512 - 100; k <= 512 + 100; k++) up += img[k].Count(v => v >= 40);
        for (var k = 1536 - 100; k <= 1536 + 100; k++) down += img[k].Count(v => v >= 40);
        Assert.True(up > down * 1.2, $"迎風 {up} 應多於背風 {down}");
    }

    [Fact]
    public void RainClutterIsReducedByFtcAndWeakerOnSBand()
    {
        RadarUnit Make(bool s, double rain)
        {
            var env = RadarEnvironment.Empty();
            env.AddRain(new RainCell(0, 3.0 * 1852, 1500, 16));
            var cfg = s ? RadarConfig.SBand(18) : RadarConfig.XBand(20);
            cfg.ShadowSectors.Clear();
            var r = new RadarUnit(cfg, 42, env);
            r.Apply("{\"cmd\":\"range\",\"value\":6}");
            r.Apply($"{{\"cmd\":\"rain\",\"value\":{rain}}}");
            return r;
        }
        var scene = new LinearScene();
        var x0 = CountAbove(Scan(Make(false, 0), scene), 200);
        var x80 = CountAbove(Scan(Make(false, 80), scene), 200);
        var s0 = CountAbove(Scan(Make(true, 0), scene), 200);
        Assert.True(x0 > 500, $"X 頻雨回波 {x0}");
        Assert.True(x80 < x0 / 3, $"FTC 應抑制雨回波:{x80} vs {x0}");
        Assert.True(s0 < x0 / 3, $"S 頻雨回波應較弱:{s0} vs {x0}");
    }

    [Fact]
    public void GainControlsNoiseSpeckle()
    {
        RadarUnit Make(double gain)
        {
            var r = RadarFactory.X();
            r.Apply($"{{\"cmd\":\"gain\",\"value\":{gain}}}");
            return r;
        }
        var low = CountAbove(Scan(Make(50), new LinearScene()), 1);
        var high = CountAbove(Scan(Make(95), new LinearScene()), 1);
        Assert.Equal(0, low);
        Assert.True(high > 10000, $"高增益雜訊斑點 {high}");
    }

    [Fact]
    public void DetuningAndDegradedFaultReduceEchoStrength()
    {
        double Sum(RadarUnit r, LinearScene s) => Scan(r, s).Sum(sp => sp.Sum(v => (double)v));
        LinearScene Scene()
        {
            var s = new LinearScene();
            s.Targets.Add(new LinearTarget("T", 4000, 4000, 0, 0, 25, 6, "fishing"));
            return s;
        }
        var nominal = Sum(RadarFactory.X(), Scene());
        var detuned = RadarFactory.X();
        Assert.True(detuned.Apply("{\"cmd\":\"tune\",\"value\":10}").Ok);
        Assert.False(detuned.Controls.AutoTune);
        var tunedSum = Sum(detuned, Scene());
        var degradedScene = Scene();
        degradedScene.Faults.Add("radarDegraded");
        var degraded = RadarFactory.X();
        var degradedSum = Sum(degraded, degradedScene);
        Assert.True(tunedSum < nominal * 0.8, $"失調 {tunedSum} vs {nominal}");
        Assert.True(degradedSum < nominal * 0.8, $"劣化 {degradedSum} vs {nominal}");
        Assert.True(degraded.Degraded);
        Assert.True(degraded.Status().Degraded);
    }

    [Fact]
    public void RadarFailureStopsTransmissionOfXOnlyAndTracksAreLost()
    {
        var scene = new LinearScene { OwnSpeedKn = 10 };
        scene.Targets.Add(new LinearTarget("T", 2000, 6000, 270, 10));
        var x = RadarFactory.X();
        var s = RadarFactory.S();
        foreach (var r in new[] { x, s })
        {
            scene.Run(r, 0, 2);
            var g = scene.Truth("T", 2);
            Assert.True(r.Apply($"{{\"cmd\":\"acquire\",\"bearing\":{SimosaBRM.SimCore.Contracts.Units.RadToDeg(g.BearingRad):F2},\"range\":{g.RangeNm:F3}}}").Ok);
            scene.Run(r, 2.04, 80);
            Assert.Equal(TrackState.Tracking, r.Arpa.Tracks.Single().State);
        }
        scene.Faults.Add("radarFailure");
        var xr = scene.Run(x, 80.04, 120);
        var sr = scene.Run(s, 80.04, 120);
        Assert.False(x.Transmitting);
        Assert.True(x.Failed);
        Assert.All(xr, r => Assert.Empty(r.Frames));
        Assert.Contains(xr.SelectMany(r => r.Events), e => e.Kind == "radarFailure");
        Assert.Contains(xr.SelectMany(r => r.Events), e => e.Kind == "targetLost");
        Assert.Equal(TrackState.Lost, x.Arpa.Tracks.Single().State);
        // S 頻(另一台)不受 radarFailure 影響
        Assert.True(s.Transmitting);
        Assert.Contains(sr, r => r.Frames.Count > 0);
        Assert.Equal(TrackState.Tracking, s.Arpa.Tracks.Single().State);
        // 恢復
        scene.Faults.Clear();
        var back = scene.Run(x, 120.04, 125);
        Assert.Contains(back.SelectMany(r => r.Events), e => e.Kind == "radarRestored");
        Assert.True(x.Transmitting);
    }

    [Fact]
    public void BlackoutStopsBothRadars()
    {
        var scene = new LinearScene();
        scene.Faults.Add("blackout");
        var x = RadarFactory.X();
        var s = RadarFactory.S();
        scene.Run(x, 0, 1);
        scene.Run(s, 0, 1);
        Assert.False(x.Transmitting);
        Assert.False(s.Transmitting);
    }

    [Fact]
    public void StandbyClearsTracksAndStopsFrames()
    {
        var scene = new LinearScene();
        scene.Targets.Add(new LinearTarget("T", 0, 3000, 90, 5));
        var x = RadarFactory.X();
        scene.Run(x, 0, 1);
        Assert.True(x.Apply("{\"cmd\":\"acquire\",\"bearing\":0,\"range\":1.62}").Ok);
        Assert.True(x.Apply("{\"cmd\":\"standby\"}").Ok);
        Assert.Empty(x.Arpa.Tracks);
        Assert.False(x.Apply("{\"cmd\":\"acquire\",\"bearing\":0,\"range\":1.62}").Ok);
        Assert.All(scene.Run(x, 1.04, 3), r => Assert.Empty(r.Frames));
        Assert.True(x.Apply("{\"cmd\":\"transmit\"}").Ok);
        Assert.Contains(scene.Run(x, 3.04, 4), r => r.Frames.Count > 0);
    }

    [Fact]
    public void CommandsValidateAndSnapRange()
    {
        var x = RadarFactory.X();
        var ack = x.Apply("{\"cmd\":\"range\",\"value\":5,\"reqId\":\"a1\"}");
        Assert.True(ack.Ok);
        Assert.Equal(6.0, ack.Value);
        Assert.Equal("a1", ack.ReqId!.Value.GetString());
        Assert.Equal(6.0, x.Controls.RangeNm);
        Assert.Equal(75.0, x.DisplayGrid.PulseM);
        Assert.True(x.Apply("{\"cmd\":\"range\",\"value\":0.75}").Ok);
        Assert.Equal(10.5, x.DisplayGrid.PulseM);
        Assert.False(x.Apply("{\"cmd\":\"range\",\"value\":200}").Ok);
        Assert.False(x.Apply("{\"cmd\":\"nope\"}").Ok);
        Assert.False(x.Apply("not json").Ok);
        Assert.Equal(100.0, x.Apply("{\"cmd\":\"gain\",\"value\":150}").Value);
        Assert.True(x.Apply("{\"cmd\":\"guardZone\",\"enabled\":true,\"inner_nm\":1,\"outer_nm\":2,\"from\":300,\"to\":60}").Ok);
        Assert.True(x.Controls.Guard.Contains(1.5, 10, 0));
        Assert.False(x.Controls.Guard.Contains(1.5, 90, 0));
        Assert.True(x.Controls.Guard.Contains(1.5, 100, 90)); // 相對方位 010
        Assert.False(x.Apply("{\"cmd\":\"guardZone\",\"inner_nm\":3,\"outer_nm\":2}").Ok);
        Assert.True(x.Apply("{\"cmd\":\"cpaLimits\",\"cpa_nm\":0.5,\"tcpa_min\":10}").Ok);
        Assert.Equal(0.5, x.Arpa.CpaLimitNm);
        Assert.True(x.Apply("{\"cmd\":\"environment\",\"seaState\":4,\"rainCells\":[{\"x\":0,\"y\":5000,\"radius_m\":800,\"rate_mmh\":8}]}").Ok);
        Assert.Equal(4.0, x.Environment.SeaStateOverride);
        Assert.Single(x.Environment.RainCells);
        Assert.True(x.Apply("{\"cmd\":\"trialManoeuvre\",\"course\":90,\"speed\":8,\"delay\":1}").Ok);
        Assert.NotNull(x.Controls.Trial);
        Assert.True(x.Apply("{\"cmd\":\"trialManoeuvre\",\"active\":false}").Ok);
        Assert.Null(x.Controls.Trial);
    }
}
