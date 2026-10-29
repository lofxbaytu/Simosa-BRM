using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.Visual.Shared.Area;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.View;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

public class AreaLoaderTests
{
    private static readonly LocalTangentPlane Kaohsiung = new(22.53, 120.24);

    [Fact]
    public void LoadsLandPolygonsGivenAsLatLonArrays()
    {
        var a = AreaLoader.Load(Path.Combine(TestPaths.Root, "data", "areas", "kaohsiung-approach.json"), Kaohsiung);
        Assert.Equal("kaohsiung-approach", a.Id);
        Assert.Equal(2, a.Land.Count);
        Assert.Equal(300, a.Land[1].HeightM);
        Assert.Equal(12, a.Land[0].Points.Count);
        // 壽山在原點(22.53N 120.24E)北北東約 12–15 km
        var shoushan = a.Land[1].Points[0];
        Assert.InRange(shoushan.North, 11000, 12500);
        Assert.InRange(shoushan.East, 2500, 3500);
    }

    [Fact]
    public void LoadsBreakwatersBerthsShoalsAndChannels()
    {
        var a = AreaLoader.Load(Path.Combine(TestPaths.Root, "data", "areas", "kaohsiung-e20-passage.json"), Kaohsiung);
        Assert.Equal(2, a.Breakwaters.Count);
        Assert.Equal(AreaLoader.DefaultBreakwaterHeightM, a.Breakwaters[0].HeightM);
        Assert.Equal(2, a.Shoals.Count);
        Assert.True(a.Shoals[0].HeightM < 0);
        Assert.Contains(a.WaterZones, z => z.Kind == "channel");
        Assert.Contains(a.WaterZones, z => z.Kind == "berthSpeedZone");
        var berth = Assert.Single(a.Berths);
        Assert.Equal("KHH-E20-OIL", berth.Id);
        Assert.Equal(2, berth.Quay.Count);
        Assert.True(berth.Fenders.Count >= 2);
        Assert.True(berth.Bollards.Count >= 2);
        // 碰墊沿碼頭線:F01 在 chainage 20 m → 距 quay[0] 20 m
        var f = berth.Fenders[0].Pos;
        var q0 = berth.Quay[0];
        var d = Math.Sqrt(Math.Pow(f.East - q0.East, 2) + Math.Pow(f.North - q0.North, 2));
        Assert.InRange(d, 19.9, 20.1);
        // 繫船柱往陸側退 setback 3 m:距碼頭線應約 3 m
        var b = berth.Bollards[0].Pos;
        var q1 = berth.Quay[1];
        var dist = Polygon2D.DistanceToSegment(b.East, b.North, q0.East, q0.North, q1.East, q1.North);
        Assert.InRange(dist, 2.9, 3.1);
        Assert.Contains(a.Markers, m => m.Kind == "harbourEntrance");
        Assert.Contains(a.Markers, m => m.Kind == "waypoint");
    }

    [Fact]
    public void LoadsLeadingLightsAndBreakwaterHeads()
    {
        var a = AreaLoader.Load(Path.Combine(TestPaths.Root, "data", "areas", "taichung-harbour-entrance.json"), new LocalTangentPlane(24.30, 120.47));
        Assert.NotNull(a.LeadingLights);
        Assert.Equal(125, a.LeadingLights!.BearingDeg);
        Assert.Equal(3, a.Land.Count);
        Assert.Contains(a.Markers, m => m.Kind == "breakwaterHead");
        Assert.NotNull(a.Bounds());
    }

    [Fact]
    public void EveryAreaFileLoadsWithoutError()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(TestPaths.Root, "data", "areas"), "*.json"))
        {
            var a = AreaLoader.Load(f, Kaohsiung);
            Assert.False(string.IsNullOrEmpty(a.Id));
            foreach (var poly in a.Land.Concat(a.Breakwaters).Concat(a.Shoals).Concat(a.WaterZones))
            {
                Assert.True(poly.Points.Count >= 3, $"{f}:{poly.Name}");
                var mesh = HarbourMeshBuilder.Extrude(poly);
                Assert.True(mesh.TriangleCount >= poly.Points.Count, $"{f}:{poly.Name}");
            }
        }
    }

    [Fact]
    public void AlongQuayRespectsWaterSide()
    {
        var quay = new[] { new EnuPoint(0, 0), new EnuPoint(100, 0) }; // 向東
        var right = AreaLoader.AlongQuay(quay, "right", 50, 10);  // 水在右側(南)→ 往水側 = 南
        Assert.Equal(50, right.East, 6);
        Assert.Equal(-10, right.North, 6);
        var left = AreaLoader.AlongQuay(quay, "left", 50, 10);
        Assert.Equal(10, left.North, 6);
    }

    [Fact]
    public void CatalogueResolvesLongestPrefixAndPaths()
    {
        var cat = AreaCatalogue.Load(TestPaths.Root);
        var e05 = cat.AreasForScenario("E05_pilot_traffic");
        Assert.Contains("kaohsiung-approach", e05);
        Assert.Empty(cat.AreasForScenario("ZZZ"));
        Assert.Empty(cat.AreasForScenario(null));
        var c2 = new AreaCatalogue(TestPaths.Root);
        c2.Add("E2", "a");
        c2.Add("E20", "b");
        Assert.Equal(new[] { "b" }, c2.AreasForScenario("E20_x"));
        Assert.EndsWith(Path.Combine("data", "areas", "kaohsiung-approach.json"), cat.ResolvePath("kaohsiung-approach"));
        Assert.Throws<FileNotFoundException>(() => cat.ResolvePath("nope"));
    }
}

public class HarbourMeshBuilderTests
{
    private static readonly EnuPoint[] Square = { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };

    [Fact]
    public void ExtrudedSquareHasTopAndFourWalls()
    {
        var mesh = HarbourMeshBuilder.Extrude(new ExtrudedPolygon("land", "sq", Square, 5.0), baseY: -2.0);
        Assert.Equal(2 + 4 * 2, mesh.TriangleCount);
        var b = mesh.Bounds();
        Assert.Equal(-2f, b.MinY);
        Assert.Equal(5f, b.MaxY);
        Assert.Equal(0f, b.MinX); Assert.Equal(10f, b.MaxX);
        Assert.Equal(-10f, b.MinZ); Assert.Equal(0f, b.MaxZ); // 北 10 m → z = −10
        // 頂面兩個三角形由上方看為逆時針(法線 +y)
        Assert.True(mesh.TriangleNormalY(0) > 0);
        Assert.True(mesh.TriangleNormalY(1) > 0);
        Assert.True(MeshChecks.WindingMatchesNormals(mesh, 1.0));
    }

    [Fact]
    public void ClockwiseInputIsNormalised()
    {
        var cw = Enumerable.Reverse(Square).ToArray();
        var mesh = HarbourMeshBuilder.Extrude(new ExtrudedPolygon("land", "sq", cw, 5.0));
        Assert.True(mesh.TriangleNormalY(0) > 0);
        Assert.True(MeshChecks.WindingMatchesNormals(mesh, 1.0));
    }

    [Fact]
    public void ConcavePolygonIsTriangulatedWithoutOverlap()
    {
        // L 形:6 個頂點 → 4 個三角形,面積和等於多邊形面積(100 − 25 = 75)
        var l = new[] { new EnuPoint(0, 0), new(10, 0), new(10, 5), new(5, 5), new(5, 10), new(0, 10) };
        var tris = HarbourMeshBuilder.Triangulate(l);
        Assert.Equal(4, tris.Count);
        var area = 0.0;
        foreach (var (ta, tb, tc) in tris) area += Math.Abs(HarbourMeshBuilder.SignedArea(new[] { l[ta], l[tb], l[tc] }));
        Assert.InRange(area, 74.99, 75.01);
        Assert.InRange(HarbourMeshBuilder.SignedArea(l), 74.99, 75.01);
    }

    [Fact]
    public void QuayAndBoxBuild()
    {
        var berth = new BerthGeometry("B", "泊位", new[] { new EnuPoint(0, 0), new EnuPoint(100, 0) }, "right", Array.Empty<(string, EnuPoint)>(), Array.Empty<(string, EnuPoint)>());
        var quay = HarbourMeshBuilder.Quay(berth, widthM: 30);
        var b = quay.Bounds();
        Assert.InRange(b.MinZ, -30.01, -29.99); // 陸側在北(z 負)
        Assert.Equal(AreaLoader.DefaultQuayHeightM, b.MaxY, 3);
        var box = HarbourMeshBuilder.Box("f", new EnuPoint(5, 5), 0, 2, 1, 2);
        Assert.Equal(10, box.TriangleCount);
        Assert.True(MeshChecks.WindingMatchesNormals(box, 1.0));
    }
}

internal static class MeshChecks
{
    /// <summary>每個三角形由頂點順序算出的法線與儲存法線同向的比例是否 ≥ minFraction(Godot 逆時針正面)。</summary>
    public static bool WindingMatchesNormals(MeshData m, double minFraction)
    {
        var ok = 0; var total = 0;
        for (var t = 0; t < m.TriangleCount; t++)
        {
            var ia = m.Indices[t * 3]; var ib = m.Indices[t * 3 + 1]; var ic = m.Indices[t * 3 + 2];
            double ax = m.Vertices[ia * 3], ay = m.Vertices[ia * 3 + 1], az = m.Vertices[ia * 3 + 2];
            double bx = m.Vertices[ib * 3], by = m.Vertices[ib * 3 + 1], bz = m.Vertices[ib * 3 + 2];
            double cx = m.Vertices[ic * 3], cy = m.Vertices[ic * 3 + 1], cz = m.Vertices[ic * 3 + 2];
            var ux = bx - ax; var uy = by - ay; var uz = bz - az;
            var vx = cx - ax; var vy = cy - ay; var vz = cz - az;
            var nx = uy * vz - uz * vy; var ny = uz * vx - ux * vz; var nz = ux * vy - uy * vx;
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9) continue; // 退化三角形不計
            total++;
            var sx = (m.Normals[ia * 3] + m.Normals[ib * 3] + m.Normals[ic * 3]);
            var sy = (m.Normals[ia * 3 + 1] + m.Normals[ib * 3 + 1] + m.Normals[ic * 3 + 1]);
            var sz = (m.Normals[ia * 3 + 2] + m.Normals[ib * 3 + 2] + m.Normals[ic * 3 + 2]);
            if (nx * sx + ny * sy + nz * sz > 0) ok++;
        }
        return total == 0 || (double)ok / total >= minFraction;
    }
}

public class ShipModelTests
{
    private static ShipParticulars Fsb1 => ShipParticulars.LoadFromDataRoot(TestPaths.Root, "FSB1");
    private static ShipParticulars Fsb2 => ShipParticulars.LoadFromDataRoot(TestPaths.Root, "FSB2");

    [Fact]
    public void HullParametersFromFsb1Particulars()
    {
        var h = HullParameters.FromParticulars(Fsb1, LoadingCondition.Full);
        Assert.Equal(105.5, h.Loa);
        Assert.Equal(18.0, h.Beam);
        Assert.Equal(8.0, h.Depth);
        Assert.InRange(h.Draft, 5.70, 5.71);
        Assert.True(h.BulbousBow);
        Assert.True(h.TransomStern);
        Assert.InRange(h.MastTopAboveBase, 31.7, 31.8); // particulars.bridge.mastTopAboveBase_m
        Assert.True(h.EyeHeightEstimated);               // eyeHeightAboveBase_m 為 null
        Assert.InRange(h.EyeHeightAboveBase, 15, 22);
        Assert.True(h.BridgeX < 0);                      // 駕駛台在艉段
        Assert.True(h.EyeHeightAboveWater > 8);
        Assert.Contains("待替換", h.Note);
        var hb = HullParameters.FromParticulars(Fsb1, LoadingCondition.Ballast);
        Assert.True(hb.Draft < h.Draft);
        Assert.True(hb.EyeHeightAboveWater > h.EyeHeightAboveWater);
    }

    [Fact]
    public void HullParametersFromFsb2Particulars()
    {
        var h = HullParameters.FromParticulars(Fsb2, LoadingCondition.Full);
        Assert.Equal(109.9, h.Loa);
        Assert.Equal(20.0, h.Beam);
        Assert.InRange(h.MastTopAboveBase, 36.4, 36.5);
    }

    [Fact]
    public void ProceduralHullBoundsMatchParticulars()
    {
        var h = HullParameters.FromParticulars(Fsb1, LoadingCondition.Full);
        var set = ProceduralHullBuilder.Build(h);
        var b = set.Hull.Bounds();
        Assert.InRange(b.MinZ, -h.Loa / 2 - 0.01, -h.Loa / 2 + 0.01); // 艏在 −z
        Assert.InRange(b.MaxZ, h.Loa / 2 - 0.01, h.Loa / 2 + 0.01);
        Assert.InRange(b.MinX, -h.Beam / 2 - 0.01, -h.Beam / 2 + 0.01);
        Assert.InRange(b.MaxX, h.Beam / 2 - 0.01, h.Beam / 2 + 0.01);
        Assert.InRange(b.MinY, -h.Draft - 0.01, -h.Draft + 0.01);          // 龍骨在水線下 Draft
        Assert.InRange(b.MaxY, h.Depth - h.Draft - 0.01, h.Depth - h.Draft + 0.01); // 甲板在水線上
        Assert.True(set.TotalTriangles > 100);
        var mastTop = set.Mast.Bounds().MaxY;
        Assert.InRange(mastTop, h.MastTopAboveBase - h.Draft - 0.01, h.MastTopAboveBase - h.Draft + 0.01);
        var superTop = set.Superstructure.Bounds().MaxY;
        Assert.True(superTop > h.EyeHeightAboveWater - 2.8);
    }

    [Fact]
    public void WindingIsConsistentWithNormals()
    {
        var h = HullParameters.FromParticulars(Fsb1, LoadingCondition.Full);
        var set = ProceduralHullBuilder.Build(h);
        Assert.True(MeshChecks.WindingMatchesNormals(set.Superstructure, 1.0));
        Assert.True(MeshChecks.WindingMatchesNormals(set.Forecastle, 1.0));
        Assert.True(MeshChecks.WindingMatchesNormals(set.Mast, 1.0));
        Assert.True(MeshChecks.WindingMatchesNormals(set.Deck, 1.0));
        Assert.True(MeshChecks.WindingMatchesNormals(set.Hull, 0.9));
    }

    [Fact]
    public void LowPolyHasFewerTriangles()
    {
        var h = HullParameters.ForTarget("T1", "X", "cargo", 200, 32, 11);
        var full = ProceduralHullBuilder.Build(h);
        var low = ProceduralHullBuilder.Build(h, lowPoly: true);
        Assert.True(low.TotalTriangles < full.TotalTriangles / 2);
        Assert.Equal(200, low.Hull.Bounds().MaxZ - low.Hull.Bounds().MinZ, 1);
    }

    [Fact]
    public void TargetParametersForSmallCraftPutBridgeForward()
    {
        var fishing = HullParameters.ForTarget("T3", "CHIN FU 66", "fishing", 25, 6, 2.5);
        Assert.True(fishing.SuperstructureForeX > 0);
        Assert.True(fishing.Draft < fishing.Depth);
        var tanker = HullParameters.ForTarget("T2", "GOLDEN WAVE", "tanker", 150, 24, null);
        Assert.True(tanker.SuperstructureForeX < 0);
        Assert.True(tanker.Draft > 0);
    }

    [Fact]
    public void HalfBreadthProfileTapersAtBowAndStern()
    {
        Assert.Equal(1.0, ProceduralHullBuilder.HalfBreadthFactor(0, true));
        Assert.Equal(0.0, ProceduralHullBuilder.HalfBreadthFactor(0.5, true), 6);
        Assert.InRange(ProceduralHullBuilder.HalfBreadthFactor(-0.5, true), 0.54, 0.56);
        Assert.InRange(ProceduralHullBuilder.HalfBreadthFactor(-0.5, false), 0.04, 0.06);
    }
}

public class ViewDefinitionTests
{
    [Fact]
    public void ViewsUseBridgeEyeHeightAndWingHalfBreadth()
    {
        var h = HullParameters.FromParticulars(ShipParticulars.LoadFromDataRoot(TestPaths.Root, "FSB1"), LoadingCondition.Full);
        var bridge = ViewDefinition.For(ViewKind.Bridge, h);
        Assert.Equal(h.EyeHeightAboveWater, bridge.EyeUp);
        Assert.Equal(0, bridge.YawDeg);
        var port = ViewDefinition.For(ViewKind.PortWing, h);
        Assert.Equal(-9.0, port.EyeStarboard);
        Assert.True(port.YawDeg < 0);
        var stbd = ViewDefinition.For(ViewKind.StbdWing, h);
        Assert.Equal(9.0, stbd.EyeStarboard);
        var aft = ViewDefinition.For(ViewKind.Aft, h);
        Assert.Equal(180, aft.YawDeg);
        var top = ViewDefinition.For(ViewKind.Overview, h);
        Assert.True(top.FixedNorthUp);
        Assert.True(top.EyeUp >= 300);
    }

    [Fact]
    public void BridgeWingAndAftViewsFollowShipRollButOverviewDoesNot()
    {
        var h = HullParameters.FromParticulars(ShipParticulars.LoadFromDataRoot(TestPaths.Root, "FSB1"), LoadingCondition.Full);
        foreach (var kind in new[] { ViewKind.Bridge, ViewKind.PortWing, ViewKind.StbdWing, ViewKind.Aft })
        {
            var v = ViewDefinition.For(kind, h);
            Assert.True(v.FollowsRoll, kind.ToString());
            Assert.Equal(-3.5, v.CameraRollDeg(-3.5));
            Assert.Equal(0.0, v.CameraRollDeg(0.0));
        }
        var top = ViewDefinition.For(ViewKind.Overview, h);
        Assert.False(top.FollowsRoll);
        Assert.Equal(0.0, top.CameraRollDeg(12.0));
    }

    [Theory]
    [InlineData("bridge", ViewKind.Bridge)]
    [InlineData("portWing", ViewKind.PortWing)]
    [InlineData("STBDWING", ViewKind.StbdWing)]
    [InlineData("aft", ViewKind.Aft)]
    [InlineData("overview", ViewKind.Overview)]
    [InlineData("3", ViewKind.StbdWing)]
    public void ParsesViewNames(string s, ViewKind k)
    {
        Assert.Equal(k, ViewDefinition.ParseKind(s));
        Assert.Equal(k, ViewDefinition.ParseKind(ViewDefinition.KindName(k)));
    }
}
