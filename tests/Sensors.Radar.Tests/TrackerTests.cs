using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>ARPA 追蹤器行為:失追、擷取失敗、保護區自動擷取、交換風險、試操船、操船目標、容量與取消、CPA 解析函數。</summary>
public class TrackerTests
{
    private static int AcquireTruth(RadarUnit radar, LinearScene scene, string id, double t)
    {
        var g = scene.Truth(id, t);
        var ack = radar.Apply($"{{\"cmd\":\"acquire\",\"bearing\":{Units.RadToDeg(g.BearingRad):F3},\"range\":{g.RangeNm:F4}}}");
        Assert.True(ack.Ok, ack.Error);
        return ack.Id!.Value;
    }

    [Fact]
    public void CpaFunctionMatchesClosedForm()
    {
        // 自船 000° 10 kn,目標於正東 3 nm 以 270° 10 kn 駛來:相對速度 (−v, −v),TCPA = r/(2v)、CPA = r/√2
        var r = 3 * Units.MetresPerNauticalMile;
        var v = Units.KnToMps(10);
        // 直接以封閉解檢查 TrialCpa(無延遲、無改變 = 一般 CPA)
        var (cpa, tcpa) = ArpaTracker.TrialCpa(r, 0, -v, 0, 0, v, 0, v, 0);
        var w = Math.Sqrt(2) * v;
        Assert.Equal(r / Math.Sqrt(2), cpa, 6);
        Assert.Equal(r * (1 / Math.Sqrt(2)) / w, tcpa, 6);
    }

    [Fact]
    public void TrialManoeuvreMatchesBruteForce()
    {
        var rnd = new Random(5);
        for (var n = 0; n < 200; n++)
        {
            double rx = rnd.NextDouble() * 20000 - 10000, ry = rnd.NextDouble() * 20000 - 10000;
            double tvx = rnd.NextDouble() * 12 - 6, tvy = rnd.NextDouble() * 12 - 6;
            double ovx = rnd.NextDouble() * 8 - 4, ovy = rnd.NextDouble() * 8 - 4;
            double nvx = rnd.NextDouble() * 8 - 4, nvy = rnd.NextDouble() * 8 - 4;
            var delay = rnd.NextDouble() * 600;
            var (cpa, tcpa) = ArpaTracker.TrialCpa(rx, ry, tvx, tvy, ovx, ovy, nvx, nvy, delay);
            // 暴力取樣 0–4 h
            double best = double.PositiveInfinity, bestT = 0;
            for (var t = 0.0; t < 14400; t += 0.5)
            {
                double ox = t <= delay ? ovx * t : ovx * delay + nvx * (t - delay);
                double oy = t <= delay ? ovy * t : ovy * delay + nvy * (t - delay);
                var d = Math.Sqrt(Math.Pow(rx + tvx * t - ox, 2) + Math.Pow(ry + tvy * t - oy, 2));
                if (d < best) { best = d; bestT = t; }
            }
            if (tcpa >= 0)
            {
                Assert.InRange(cpa, best - 1.0, best + 1.0);
                if (bestT < 14000) Assert.InRange(tcpa, bestT - 2, bestT + 2);
            }
            else Assert.InRange(best, cpa - 1.0, Math.Sqrt(rx * rx + ry * ry) + 1.0); // 已通過:最近點在 t = 0 附近
        }
    }

    [Fact]
    public void TrialManoeuvreOnTrackedTargetMatchesAnalytic()
    {
        var scene = new LinearScene { OwnCourseDeg = 0, OwnSpeedKn = 10 };
        scene.Targets.Add(new LinearTarget("T", 7000, 7000, 270, 12));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        AcquireTruth(radar, scene, "T", 2);
        scene.Run(radar, 2.04, 200);
        Assert.True(radar.Apply("{\"cmd\":\"trialManoeuvre\",\"course\":60,\"speed\":10,\"delay\":2}").Ok);
        var msg = radar.Tracks();
        var tr = Assert.Single(msg.Targets);
        Assert.NotNull(tr.TrialCpaNm);
        Assert.Equal(60.0, msg.Trial!.Course);
        // 解析:真值相對位置/速度
        var (ox, oy) = scene.OwnAt(200);
        var (tx, ty) = scene.Targets[0].At(200);
        var (tve, tvn) = scene.Targets[0].Vel;
        var (ove, ovn) = scene.OwnVel;
        var v = Units.KnToMps(10);
        var (cpa, tcpa) = ArpaTracker.TrialCpa(tx - ox, ty - oy, tve, tvn, ove, ovn, v * Math.Sin(Units.DegToRad(60)), v * Math.Cos(Units.DegToRad(60)), 120);
        Assert.InRange(tr.TrialCpaNm!.Value, cpa / 1852 - 0.02, cpa / 1852 + 0.02);
        Assert.InRange(tr.TrialTcpaMin!.Value, tcpa / 60 - 0.5, tcpa / 60 + 0.5);
        // 向右轉 60° 讓路:試操船 CPA 應大於目前 CPA
        Assert.True(tr.TrialCpaNm > tr.CpaNm + 0.3, $"試操船 CPA {tr.TrialCpaNm} vs {tr.CpaNm}");
    }

    [Fact]
    public void TargetThatDisappearsIsLostThenDeleted()
    {
        var scene = new LinearScene { OwnSpeedKn = 8 };
        scene.Targets.Add(new LinearTarget("T", -3000, 5000, 120, 10));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        var id = AcquireTruth(radar, scene, "T", 2);
        scene.Run(radar, 2.04, 70);
        Assert.Equal(TrackState.Tracking, radar.Arpa.Tracks.Single().State);
        scene.Targets.Clear(); // 目標消失(例如進入雷達陰影或沉沒)
        var events = scene.Run(radar, 70.04, 95).SelectMany(r => r.Events).ToList();
        var lost = Assert.Single(events, e => e.Kind == "targetLost");
        Assert.Equal(id, lost.TargetId);
        Assert.InRange(lost.T, 70 + 5 * radar.Config.ScanPeriodS, 70 + 9 * radar.Config.ScanPeriodS);
        var tr = radar.Arpa.Tracks.Single();
        Assert.Equal(TrackState.Lost, tr.State);
        Assert.Equal("lost", radar.Tracks().Targets.Single().State);
        Assert.Null(radar.Tracks().Targets.Single().CpaNm);
        events = scene.Run(radar, 95.04, 160).SelectMany(r => r.Events).ToList();
        Assert.Contains(events, e => e.Kind == "deleted" && e.TargetId == id);
        Assert.Empty(radar.Arpa.Tracks);
    }

    [Fact]
    public void AcquireOnEmptyWaterFails()
    {
        var scene = new LinearScene();
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 1);
        Assert.True(radar.Apply("{\"cmd\":\"acquire\",\"bearing\":45,\"range\":3}").Ok);
        Assert.Equal("acquiring", radar.Tracks().Targets.Single().State);
        var events = scene.Run(radar, 1.04, 15).SelectMany(r => r.Events).ToList();
        Assert.Contains(events, e => e.Kind == "acquireFailed");
        Assert.Empty(radar.Arpa.Tracks);
    }

    [Fact]
    public void GuardZoneAutoAcquiresApproachingTarget()
    {
        var scene = new LinearScene { OwnSpeedKn = 10, WindKn = 6 };
        scene.Targets.Add(new LinearTarget("T", 2500, 3.4 * 1852, 250, 12, 60, 12, "cargo", 4));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        Assert.True(radar.Apply("{\"cmd\":\"guardZone\",\"enabled\":true,\"inner_nm\":0.5,\"outer_nm\":3.5}").Ok);
        var events = scene.Run(radar, 0, 150).SelectMany(r => r.Events).ToList();
        var e = Assert.Single(events, x => x.Kind == "newTarget");
        var tr = radar.Arpa.Tracks.Single(t => t.Id == e.TargetId);
        Assert.Equal(AcquisitionMode.Auto, tr.Mode);
        Assert.Equal(TrackState.Tracking, tr.State);
        Assert.Equal("auto", radar.Tracks().Targets.Single().Acquisition);
        // 進入 CPA 限值的危險目標事件
        var s = radar.Arpa.Solve(tr, 150, null);
        if (s.CpaNm < radar.Arpa.CpaLimitNm && s.TcpaMin is > 0 and < 12) Assert.Contains(events, x => x.Kind == "dangerousTarget");
    }

    [Fact]
    public void CloseTargetsAreFlaggedForSwapRisk()
    {
        // 兩船平行同向、相距 220 m(約 1 nm 處方位差約 5°,回波可分辨;強回波的波束展寬使更近的兩船合併為一個回波)
        var scene = new LinearScene { OwnSpeedKn = 0 };
        scene.Targets.Add(new LinearTarget("A", 1000, 1500, 0, 6, 60, 12));
        scene.Targets.Add(new LinearTarget("B", 1220, 1500, 0, 6, 60, 12));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        AcquireTruth(radar, scene, "A", 2);
        AcquireTruth(radar, scene, "B", 2);
        scene.Run(radar, 2.04, 80);
        var msg = radar.Tracks();
        Assert.Equal(2, msg.Targets.Count);
        Assert.All(msg.Targets, t => Assert.True(t.SwapRisk));
    }

    [Fact]
    public void ManoeuvringTargetIsReacquiredOnNewCourse()
    {
        // 目標 120 s 時由 270° 轉為 330°(12 kn);轉向後 120 s 航向誤差 < 5°
        var scene = new LinearScene { OwnSpeedKn = 0 };
        var first = new LinearTarget("T", 5000, 2000, 270, 12);
        scene.Targets.Add(first);
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        AcquireTruth(radar, scene, "T", 2);
        scene.Run(radar, 2.04, 120);
        var (x1, y1) = first.At(120);
        scene.Targets.Clear();
        // 新航段:起點換算使 t = 120 時位置連續
        var v = Units.KnToMps(12);
        var c = Units.DegToRad(330);
        scene.Targets.Add(new LinearTarget("T", x1 - v * Math.Sin(c) * 120, y1 - v * Math.Cos(c) * 120, 330, 12));
        var manoeuvreSeen = false;
        scene.Run(radar, 120.04, 240, each: (_, _) => manoeuvreSeen |= radar.Arpa.Tracks.Any(t => t.Manoeuvring));
        var tr = radar.Arpa.Tracks.Single();
        Assert.Equal(TrackState.Tracking, tr.State);
        var s = radar.Arpa.Solve(tr, 240, null);
        Assert.True(manoeuvreSeen, "應偵測到目標操船");
        Assert.InRange(Units.WrapDeg180(s.CourseDeg - 330), -5, 5);
        Assert.InRange(s.SpeedKn, 11, 13);
    }

    [Fact]
    public void CancelAndCapacity()
    {
        var scene = new LinearScene();
        var radar = RadarFactory.X(configure: c => c.Arpa.MaxTracks = 3);
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 1);
        for (var i = 0; i < 3; i++) Assert.True(radar.Apply($"{{\"cmd\":\"acquire\",\"bearing\":{i * 30},\"range\":2}}").Ok);
        var full = radar.Apply("{\"cmd\":\"acquire\",\"bearing\":200,\"range\":2}");
        Assert.False(full.Ok);
        Assert.Contains("上限", full.Error);
        Assert.True(radar.Apply("{\"cmd\":\"cancel\",\"id\":2}").Ok);
        Assert.False(radar.Apply("{\"cmd\":\"cancel\",\"id\":2}").Ok);
        var again = radar.Apply("{\"cmd\":\"acquire\",\"bearing\":200,\"range\":2}");
        Assert.True(again.Ok);
        Assert.Equal(2, again.Id); // 編號重用
        Assert.True(radar.Apply("{\"cmd\":\"cancel\",\"id\":\"all\"}").Ok);
        Assert.Empty(radar.Arpa.Tracks);
        // 相對方位擷取:艏向 000 時 relative 與 true 相同
        Assert.True(radar.Apply("{\"cmd\":\"acquire\",\"bearing\":90,\"range\":1,\"relative\":true}").Ok);
        Assert.InRange(radar.Tracks().Targets.Single().BearingDeg, 89.9, 90.1);
    }

    [Fact]
    public void PastPositionsAreRecorded()
    {
        var scene = new LinearScene { OwnSpeedKn = 5 };
        scene.Targets.Add(new LinearTarget("T", 3000, 3000, 180, 10));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        AcquireTruth(radar, scene, "T", 2);
        scene.Run(radar, 2.04, 200);
        var t = radar.Tracks().Targets.Single();
        Assert.NotNull(t.Past);
        Assert.InRange(t.Past!.Count, 4, 6); // 60 s 起每 30 s 一點
        Assert.True(t.Stable);
        Assert.All(t.Past, p => Assert.InRange(p.X, 2900, 3100));
    }
}
