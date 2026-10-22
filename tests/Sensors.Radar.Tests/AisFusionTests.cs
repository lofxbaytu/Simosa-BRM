using SimosaBRM.Sensors.Radar.Arpa;
using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>
/// AIS 融合(radar-arpa.md 第 3.1 節;IEC 62288 雷達/AIS 關聯概念):正確關聯(距離門檻 + 航向/速度一致 + 連續 N 次)、
/// AIS 關閉或 aisError 偏離門檻時不關聯、保持期內顯示差異、兩目標的報告交換時關聯跟著位置走、tracks[].ais 輸出。
/// </summary>
public class AisFusionTests
{
    private static RadarUnit Acquire(LinearScene scene, RadarUnit radar, string id, double t)
    {
        var g = scene.Truth(id, t);
        Assert.True(radar.Apply($"{{\"cmd\":\"acquire\",\"bearing\":{Units.RadToDeg(g.BearingRad):F2},\"range\":{g.RangeNm:F3}}}").Ok);
        return radar;
    }

    private static ArpaTrack Track(RadarUnit radar, int id) => radar.Arpa.Tracks.Single(t => t.Id == id);

    [Fact]
    public void AssociatesCorrectAisAfterConsecutiveHits()
    {
        var scene = new LinearScene { OwnSpeedKn = 8 };
        scene.Targets.Add(new LinearTarget("ALFA", 2500, 4000, 250, 10, AisOn: true, Mmsi: 416001001));
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        Acquire(scene, radar, "ALFA", 2);
        var events = new List<ArpaEvent>();
        var associatedAt = double.NaN;
        scene.Run(radar, 2.04, 120, each: (t, r) =>
        {
            events.AddRange(r.Events);
            if (double.IsNaN(associatedAt) && Track(radar, 1).AisAssociated) associatedAt = t;
        });
        var tr = Track(radar, 1);
        Assert.Equal(TrackState.Tracking, tr.State);
        Assert.True(tr.AisAssociated);
        Assert.Equal(416001001, tr.AisMmsi);
        Assert.Equal("ALFA", tr.AisName);
        Assert.InRange(tr.AisOffsetM, 0, 60);
        // 擷取中(無向量)不關聯;進入追蹤後連續 3 次(1 Hz)才關聯
        var trackingAt = events.First(e => e.Kind == "tracking").T;
        Assert.InRange(associatedAt - trackingAt, 2.0, 4.1);
        Assert.Contains(events, e => e.Kind == "aisAssociated" && e.TargetId == 1);
        // tracks[].ais 輸出
        var json = RadarJson.Text(radar.Tracks());
        Assert.Contains("\"ais\":{\"mmsi\":416001001,\"name\":\"ALFA\",\"associated\":true,\"positionOffset_m\":", json);
    }

    [Fact]
    public void NoAssociationWhenAisOffOrReportTooFar()
    {
        // AIS 關閉
        var off = new LinearScene { OwnSpeedKn = 8 };
        off.Targets.Add(new LinearTarget("OFF", 2500, 4000, 250, 10, AisOn: false, Mmsi: 416001002));
        var radarOff = RadarFactory.X();
        radarOff.DisplayEnabled = false;
        off.Run(radarOff, 0, 2);
        Acquire(off, radarOff, "OFF", 2);
        off.Run(radarOff, 2.04, 120);
        Assert.Equal(TrackState.Tracking, Track(radarOff, 1).State);
        Assert.False(Track(radarOff, 1).AisAssociated);
        Assert.Equal(0, Track(radarOff, 1).AisMmsi);
        Assert.DoesNotContain("\"ais\"", RadarJson.Text(radarOff.Tracks()));

        // aisError:報告位置偏 1200 m(超過門檻 150 m + 2% × 距離 ≈ 250 m)→ 不關聯
        var far = new LinearScene { OwnSpeedKn = 8 };
        far.Targets.Add(new LinearTarget("FAR", 2500, 4000, 250, 10, AisOn: true, Mmsi: 416001003, AisErrEast: 1200));
        var radarFar = RadarFactory.X();
        radarFar.DisplayEnabled = false;
        far.Run(radarFar, 0, 2);
        Acquire(far, radarFar, "FAR", 2);
        far.Run(radarFar, 2.04, 120);
        Assert.Equal(TrackState.Tracking, Track(radarFar, 1).State);
        Assert.False(Track(radarFar, 1).AisAssociated);
        Assert.Equal(0, Track(radarFar, 1).AisMmsi);

        // 向量一致性:位置在門檻內但速度門檻收到 0 kn(ARPA 速度估計永遠有微小誤差)→ 不關聯;航向門檻收到 5°(估計誤差通常 < 5°)仍關聯
        var consistent = new LinearScene { OwnSpeedKn = 8 };
        consistent.Targets.Add(new LinearTarget("CRS", 2500, 4000, 250, 10, AisOn: true, Mmsi: 416001004));
        var radarCrs = RadarFactory.X(configure: c => c.Arpa.AisCourseDeg = 5);
        radarCrs.DisplayEnabled = false;
        consistent.Run(radarCrs, 0, 2);
        Acquire(consistent, radarCrs, "CRS", 2);
        var radarSpd = RadarFactory.X(configure: c => c.Arpa.AisSpeedKn = 0.0);
        radarSpd.DisplayEnabled = false;
        consistent.Run(radarSpd, 0, 2);
        Acquire(consistent, radarSpd, "CRS", 2);
        consistent.Run(radarCrs, 2.04, 120);
        consistent.Run(radarSpd, 2.04, 120);
        Assert.True(Track(radarCrs, 1).AisAssociated);
        Assert.False(Track(radarSpd, 1).AisAssociated);
    }

    [Fact]
    public void ModerateAisErrorKeepsAssociationButShowsOffset()
    {
        // 先正確關聯,之後報告偏 350 m(超過門檻、仍在保持門檻 2× 內)→ 維持關聯並顯示 positionOffset_m;偏 1500 m → 連續 3 次後解除
        var scene = new LinearScene { OwnSpeedKn = 8 };
        var target = new LinearTarget("ERR", 2500, 4000, 250, 10, AisOn: true, Mmsi: 416001005);
        scene.Targets.Add(target);
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        Acquire(scene, radar, "ERR", 2);
        scene.Run(radar, 2.04, 100);
        Assert.True(Track(radar, 1).AisAssociated);
        scene.Targets[0] = target with { AisErrNorth = 350 };
        scene.Run(radar, 100.04, 110);
        var tr = Track(radar, 1);
        Assert.True(tr.AisAssociated);
        Assert.InRange(tr.AisOffsetM, 300, 420);
        var json = RadarJson.Text(radar.Tracks());
        Assert.Contains("\"associated\":true", json);
        scene.Targets[0] = target with { AisErrNorth = 1500 };
        scene.Run(radar, 110.04, 116);
        tr = Track(radar, 1);
        Assert.False(tr.AisAssociated);
        Assert.Equal(0, tr.AisMmsi);
    }

    [Fact]
    public void SwappedReportsFollowPositionsAndOneReportAssociatesOneTrack()
    {
        // 兩目標各有 AIS;報告互換(A 報 B 的位置、B 報 A 的位置)後關聯跟著報告位置走,一筆報告只關聯一個目標
        var scene = new LinearScene { OwnSpeedKn = 8 };
        var a = new LinearTarget("A", 2500, 4000, 250, 10, AisOn: true, Mmsi: 416000001);
        var b = new LinearTarget("B", -3000, 3000, 90, 8, AisOn: true, Mmsi: 416000002);
        scene.Targets.Add(a);
        scene.Targets.Add(b);
        var radar = RadarFactory.X();
        radar.DisplayEnabled = false;
        scene.Run(radar, 0, 2);
        Acquire(scene, radar, "A", 2);
        Acquire(scene, radar, "B", 2);
        scene.Run(radar, 2.04, 110);
        Assert.Equal(416000001, Track(radar, 1).AisMmsi);
        Assert.Equal(416000002, Track(radar, 2).AisMmsi);
        Assert.True(Track(radar, 1).AisAssociated && Track(radar, 2).AisAssociated);

        // 互換:以 aisError 偏移把 A 的報告放到 B 的位置、B 的放到 A 的位置(同航向/速度才可能通過一致性 → 這裡速度不同,先只驗證解除再重關聯)
        var (ax, ay) = a.At(110);
        var (bx, by) = b.At(110);
        var swappedA = a with { AisErrEast = bx - ax, AisErrNorth = by - ay, CourseDeg = a.CourseDeg };
        var swappedB = b with { AisErrEast = ax - bx, AisErrNorth = ay - by };
        // 互換的報告帶著原本的 COG/SOG,與另一目標的向量不一致 → 兩者都解除且不會誤關聯
        scene.Targets[0] = swappedA;
        scene.Targets[1] = swappedB;
        scene.Run(radar, 110.04, 118);
        Assert.False(Track(radar, 1).AisAssociated);
        Assert.False(Track(radar, 2).AisAssociated);

        // 兩目標同航向同速度時互換報告 → 關聯依位置交換(MMSI 跟著報告位置)
        var scene2 = new LinearScene { OwnSpeedKn = 8 };
        var c = new LinearTarget("C", 2500, 4000, 250, 10, AisOn: true, Mmsi: 416000003);
        var d = new LinearTarget("D", -3000, 3000, 250, 10, AisOn: true, Mmsi: 416000004);
        scene2.Targets.Add(c);
        scene2.Targets.Add(d);
        var radar2 = RadarFactory.X();
        radar2.DisplayEnabled = false;
        scene2.Run(radar2, 0, 2);
        Acquire(scene2, radar2, "C", 2);
        Acquire(scene2, radar2, "D", 2);
        scene2.Run(radar2, 2.04, 110);
        Assert.Equal(416000003, Track(radar2, 1).AisMmsi);
        Assert.Equal(416000004, Track(radar2, 2).AisMmsi);
        var (cx, cy) = c.At(0);
        var (dx, dy) = d.At(0);
        scene2.Targets[0] = c with { AisErrEast = dx - cx, AisErrNorth = dy - cy };
        scene2.Targets[1] = d with { AisErrEast = cx - dx, AisErrNorth = cy - dy };
        scene2.Run(radar2, 110.04, 120);
        Assert.Equal(416000004, Track(radar2, 1).AisMmsi);
        Assert.Equal(416000003, Track(radar2, 2).AisMmsi);
        Assert.True(Track(radar2, 1).AisAssociated && Track(radar2, 2).AisAssociated);
        var mmsis = radar2.Tracks().Targets.Select(t => t.Ais!.Mmsi).ToList();
        Assert.Equal(mmsis.Count, mmsis.Distinct().Count());
    }
}
