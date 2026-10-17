using SimosaBRM.SimCore.Contracts;
using Xunit;

namespace SimosaBRM.Sensors.Radar.Tests;

/// <summary>回波位置(船首向上/北向上方位定義)、波束與脈衝展寬、陰影扇區、雷達地平線、陸地(規劃書第 14 章雷達列)。</summary>
public class EchoTests
{
    /// <summary>以靜止場景跑滿一圈(含少許餘量),回傳 [方位索引][格] 影像與最後一幀。</summary>
    internal static (byte[][] Image, SpokeFrame Last) OneScan(RadarUnit radar, LinearScene scene, double t0 = 0.0)
    {
        var results = scene.Run(radar, t0, t0 + radar.Config.ScanPeriodS + 0.2);
        var img = RadarFactory.CollectScan(results, radar.Config.Spokes);
        var last = SpokeFrame.Parse(results.Last(r => r.Frames.Count > 0).Frames.Last());
        for (var k = 0; k < img.Length; k++) img[k] ??= new byte[radar.Config.DisplayCells];
        return (img, last);
    }

    /// <summary>強度 ≥ thr 的格之加權方位(度,相對)與距離(m);方位以 refDeg 為中心展開避免跨 0°。</summary>
    internal static (double AzDeg, double RangeM, int Count, double AzSpanDeg, double RadialM) Centroid(byte[][] img, double cellM, int thr, double refDeg, double windowDeg = 20, double? minRangeM = null, double? maxRangeM = null)
    {
        double w = 0, wa = 0, wr = 0;
        var n = 0;
        double amin = double.PositiveInfinity, amax = double.NegativeInfinity, rmin = double.PositiveInfinity, rmax = double.NegativeInfinity;
        for (var k = 0; k < img.Length; k++)
        {
            var az = k * 360.0 / img.Length;
            var d = Units.WrapDeg180(az - refDeg);
            if (Math.Abs(d) > windowDeg) continue;
            for (var i = 0; i < img[k].Length; i++)
            {
                var v = img[k][i];
                if (v < thr) continue;
                var r = (i + 0.5) * cellM;
                if (minRangeM is { } mn && r < mn) continue;
                if (maxRangeM is { } mx && r > mx) continue;
                w += v; wa += v * d; wr += v * r; n++;
                amin = Math.Min(amin, d); amax = Math.Max(amax, d);
                rmin = Math.Min(rmin, r); rmax = Math.Max(rmax, r);
            }
        }
        return n == 0 ? (double.NaN, double.NaN, 0, 0, 0) : (Units.NormalizeHeadingDeg(refDeg + wa / w), wr / w, n, amax - amin, rmax - rmin);
    }

    private static LinearScene StaticScene(double ownHeading, double trueBearing, double rangeNm, string type = "cargo", double loa = 120, double beam = 20, double tHeading = 0)
    {
        var scene = new LinearScene { OwnCourseDeg = ownHeading, OwnSpeedKn = 0 };
        var r = rangeNm * Units.MetresPerNauticalMile;
        var b = Units.DegToRad(trueBearing);
        scene.Targets.Add(new LinearTarget("T", r * Math.Sin(b), r * Math.Cos(b), tHeading, 0, loa, beam, type));
        return scene;
    }

    [Fact]
    public void EchoAppearsAtRelativeBearingHeadUpAndTrueBearingNorthUp()
    {
        var scene = StaticScene(ownHeading: 30, trueBearing: 75, rangeNm: 3.0, tHeading: 165); // 目標正橫朝向雷達
        var radar = RadarFactory.X();
        var (img, last) = OneScan(radar, scene);
        var cell = last.RangeM / last.Cells;
        var c = Centroid(img, cell, 200, refDeg: 45);
        Assert.True(c.Count > 0, "應有回波");
        // 船首向上:方位索引 = 相對方位(真方位 − 艏向)
        Assert.InRange(Units.WrapDeg180(c.AzDeg - 45.0), -0.3, 0.3);
        // 北向上:真方位 = 相對方位 + 標頭 heading_deg
        Assert.Equal(30.0, last.HeadingDeg, 3);
        Assert.InRange(Units.WrapDeg180(c.AzDeg + last.HeadingDeg - 75.0), -0.3, 0.3);
        // 距離:回波段 [近端, 遠端 + 脈衝];6 nm 量程中長脈衝 75 m
        var expected = 3.0 * Units.MetresPerNauticalMile;
        Assert.InRange(c.RangeM, expected - 20 - cell, expected + 75 / 2.0 + 20 + cell);
    }

    [Fact]
    public void GyroErrorRotatesNorthUpPictureButNotHeadUp()
    {
        var scene = StaticScene(ownHeading: 0, trueBearing: 60, rangeNm: 2.0);
        scene.HeadingErrorDeg = 5.0; // 電羅經指示 +5°
        var radar = RadarFactory.X();
        var (img, last) = OneScan(radar, scene);
        var c = Centroid(img, last.RangeM / last.Cells, 200, refDeg: 60);
        Assert.InRange(Units.WrapDeg180(c.AzDeg - 60.0), -0.3, 0.3);           // 天線隨實際船首:相對方位不變
        Assert.InRange(Units.WrapDeg180(c.AzDeg + last.HeadingDeg - 65.0), -0.3, 0.3); // 北向上以錯誤航向穩定化 → 偏 5°
    }

    [Fact]
    public void BeamwidthAndPulseLengthStretchEcho()
    {
        // 小目標(25 m 漁船)在 1.5 nm 的回波:方位寬 ≥ 波束寬、徑向長 ≥ 脈衝長(中脈衝 45 m;1.5 nm 量程)
        var x = RadarFactory.X();
        var s = RadarFactory.S();
        foreach (var r in new[] { x, s }) r.Apply("{\"cmd\":\"range\",\"value\":1.5}");
        var sceneX = StaticScene(0, 90, 1.0, "fishing", 25, 6, tHeading: 0);
        var (imgX, lastX) = OneScan(x, sceneX);
        var (imgS, lastS) = OneScan(s, sceneX);
        var cx = Centroid(imgX, lastX.RangeM / lastX.Cells, 30, 90);
        var cs = Centroid(imgS, lastS.RangeM / lastS.Cells, 30, 90);
        Assert.True(cx.AzSpanDeg >= 1.0 - 0.2, $"X 方位寬 {cx.AzSpanDeg:F2}°");
        Assert.True(cs.AzSpanDeg > cx.AzSpanDeg * 1.5, $"S 頻波束較寬:{cs.AzSpanDeg:F2}° vs {cx.AzSpanDeg:F2}°");
        Assert.True(cx.RadialM >= 45.0 - 2 * lastX.RangeM / lastX.Cells, $"徑向長 {cx.RadialM:F1} m");
    }

    [Fact]
    public void ShadowSectorBlanksEchoes()
    {
        // 預設艉向陰影扇區 174–186°:相對 180° 的大船無回波;相對 160° 的同一目標可見
        var radar = RadarFactory.X(shadow: true);
        radar.Apply("{\"cmd\":\"gain\",\"value\":50}");
        var (img, last) = OneScan(radar, StaticScene(0, 180, 1.0, tHeading: 90));
        var cell = last.RangeM / last.Cells;
        var inSector = Centroid(img, cell, 1, 180, windowDeg: 5);
        Assert.Equal(0, inSector.Count);
        var radar2 = RadarFactory.X(shadow: true);
        radar2.Apply("{\"cmd\":\"gain\",\"value\":50}");
        var (img2, last2) = OneScan(radar2, StaticScene(0, 160, 1.0, tHeading: 70));
        Assert.True(Centroid(img2, last2.RangeM / last2.Cells, 128, 160, windowDeg: 5).Count > 0);
    }

    [Fact]
    public void RadarHorizonHidesDistantTargets()
    {
        // 天線 20 m、油輪(高約 23 m):地平線 2.21(√20 + √23) ≈ 20.5 nm
        var horizon = RadarConfig.HorizonNm(20.0, TargetModel.HeightM("tanker", 150));
        Assert.InRange(horizon, 20.0, 21.0);
        var near = RadarFactory.X();
        near.Apply("{\"cmd\":\"range\",\"value\":24}");
        var (img, last) = OneScan(near, StaticScene(0, 45, 18.0, "tanker", 150, 24, tHeading: 135));
        Assert.True(Centroid(img, last.RangeM / last.Cells, 100, 45, 5).Count > 0, "地平線內(18 nm)應可見");
        var far = RadarFactory.X();
        far.Apply("{\"cmd\":\"range\",\"value\":24}");
        far.Apply("{\"cmd\":\"gain\",\"value\":50}");
        var (img2, last2) = OneScan(far, StaticScene(0, 45, 22.0, "tanker", 150, 24, tHeading: 135));
        Assert.Equal(0, Centroid(img2, last2.RangeM / last2.Cells, 1, 45, 5, minRangeM: 20 * 1852).Count);
    }

    [Fact]
    public void LandPolygonEchoesAndShadowsTargetsBehind()
    {
        // 東方 2 nm 起的陸地(南北向海岸);陸地後方 3 nm 的目標被遮蔽,海岸前 1 nm 的目標可見
        RadarUnit Make()
        {
            var env = RadarEnvironment.Empty();
            var e0 = 2.0 * Units.MetresPerNauticalMile;
            env.AddLand(new LandPolygon { Name = "coast", HeightM = 20, Points = new[] { (e0, -8000.0), (e0 + 6000, -8000.0), (e0 + 6000, 8000.0), (e0, 8000.0) } });
            var r = RadarFactory.X(env: env);
            r.Apply("{\"cmd\":\"gain\",\"value\":50}");
            return r;
        }
        var e = 2.0 * Units.MetresPerNauticalMile;
        var with = new LinearScene { OwnCourseDeg = 0, OwnSpeedKn = 0, WindKn = 20 };
        with.Targets.Add(new LinearTarget("behind", 3.0 * 1852, 1500, 0, 0));
        with.Targets.Add(new LinearTarget("front", 1.0 * 1852, -1500, 90, 0));
        var without = new LinearScene { OwnCourseDeg = 0, OwnSpeedKn = 0, WindKn = 20 };
        without.Targets.Add(new LinearTarget("front", 1.0 * 1852, -1500, 90, 0));
        var (img, last) = OneScan(Make(), with);
        var (img0, _) = OneScan(Make(), without);
        var cell = last.RangeM / last.Cells;
        // 正東方向:海岸線在 2 nm(±2 格)
        var firstLand = Enumerable.Range((int)(1.5 * 1852 / cell), img[512].Length - (int)(1.5 * 1852 / cell)).First(i => img[512][i] >= 150);
        Assert.InRange((firstLand + 0.5) * cell, e - 2 * cell, e + 2 * cell);
        // 陸地後方的目標:同種子、有無此目標的影像完全相同(被陸地遮蔽,無任何貢獻)
        var behindK = (int)Math.Round(Units.RadToDeg(Math.Atan2(3.0 * 1852, 1500)) / 360.0 * 2048);
        for (var k = behindK - 10; k <= behindK + 10; k++) Assert.Equal(img0[k], img[k]);
        // 海岸前方的目標可見
        var frontAz = Units.RadToDeg(Math.Atan2(1852, -1500));
        Assert.True(Centroid(img, cell, 150, frontAz, 2.0, maxRangeM: 2600).Count > 0);
        // 陸地後方(陸地回波 3 km 截止後)無海雜波:海面被遮蔽
        var beyond = (int)((e + 3200) / cell);
        for (var k = 500; k <= 524; k++)
            for (var i = beyond; i < img[k].Length; i++) Assert.Equal(0, img[k][i]);
    }

    [Fact]
    public void ParticularsGiveAntennaHeightAboveWaterline()
    {
        var root = SimosaBRM.SimCore.Ship.RepositoryPaths.FindRoot();
        var ship = SimosaBRM.SimCore.Ship.ShipParticulars.LoadFromDataRoot(root, "FSB1");
        var x = RadarConfig.XBand().ApplyParticulars(ship, LoadingCondition.Full);
        // bridge.mastTopAboveBase_m 31.716 − 1 m − 平均吃水 5.701 m
        Assert.Equal(31.716 - 1.0 - 5.701, x.AntennaHeightM, 3);
        Assert.True(x.AntennaFwdM < 0, "天線在船舯後方(駕駛台頂)");
    }
}
