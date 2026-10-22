using SimosaBRM.Visual.Shared.Scene;

namespace SimosaBRM.Visual.Shared.Area;

/// <summary>
/// 區域多邊形 → 低面數擠出幾何(規劃書第 7.3 節:港口模型用低面數版本;示意港口,非實測模型)。
/// 頂面以耳切法(ear clipping)三角化簡單多邊形(區域檔載入時已檢查不自交),側面為每邊一個四邊形;底面不畫(在水下看不到)。
/// 多邊形方向任意(載入時先統一為逆時針)。淺灘以負高度表示(頂面在水下),鳥瞰視角可見。
/// </summary>
public static class HarbourMeshBuilder
{
    /// <summary>擠出多邊形:baseY 為底(預設 −2 m,藏在海面下避免縫隙),頂面在 poly.HeightM。</summary>
    public static MeshData Extrude(ExtrudedPolygon poly, double baseY = -2.0)
    {
        var pts = EnsureCounterClockwise(poly.Points);
        var mesh = new MeshData { Name = poly.Name };
        var top = Math.Max(poly.HeightM, baseY + 0.01);
        var n = pts.Count;

        // 頂面(法線朝上)
        var topIdx = new int[n];
        for (var i = 0; i < n; i++)
        {
            var p = SceneCoordinates.FromEnu(pts[i].East, pts[i].North, top);
            topIdx[i] = mesh.AddVertex(p.X, p.Y, p.Z, 0, 1, 0);
        }
        // Godot 以「從法線方向看為逆時針」為正面:從 +y 俯視 x–z 平面時 +x 在右、+z 在下(即北在上),
        // ENU 逆時針(東 → 北)在畫面上也是逆時針,索引順序可直接沿用(tests: HarbourMeshBuilderTests)。
        foreach (var (a, b, c) in Triangulate(pts))
            mesh.AddTriangle(topIdx[a], topIdx[b], topIdx[c]);

        // 側面
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            var a = pts[i];
            var b = pts[j];
            var ex = b.East - a.East;
            var ny = b.North - a.North;
            var len = Math.Sqrt(ex * ex + ny * ny);
            if (len < 1e-9) continue;
            // 逆時針多邊形的外法線為邊向量的右側 (ny, −ex)
            var ox = ny / len;
            var oy = -ex / len;
            var normal = SceneCoordinates.FromEnu(ox, oy, 0);
            var pa0 = SceneCoordinates.FromEnu(a.East, a.North, baseY);
            var pa1 = SceneCoordinates.FromEnu(a.East, a.North, top);
            var pb0 = SceneCoordinates.FromEnu(b.East, b.North, baseY);
            var pb1 = SceneCoordinates.FromEnu(b.East, b.North, top);
            var i0 = mesh.AddVertex(pa0.X, pa0.Y, pa0.Z, normal.X, normal.Y, normal.Z);
            var i1 = mesh.AddVertex(pb0.X, pb0.Y, pb0.Z, normal.X, normal.Y, normal.Z);
            var i2 = mesh.AddVertex(pb1.X, pb1.Y, pb1.Z, normal.X, normal.Y, normal.Z);
            var i3 = mesh.AddVertex(pa1.X, pa1.Y, pa1.Z, normal.X, normal.Y, normal.Z);
            // 由外側看逆時針:a0 → b0 → b1 → a1(觀者站在外法線方向,b 在其右手邊)
            mesh.AddQuad(i0, i1, i2, i3);
        }
        return mesh;
    }

    /// <summary>碼頭:沿碼頭線往陸側擠出 widthM 的長方體(高 DefaultQuayHeightM)。</summary>
    public static MeshData Quay(BerthGeometry berth, double widthM = 30.0, double heightM = AreaLoader.DefaultQuayHeightM)
    {
        var a = berth.Quay[0];
        var b = berth.Quay[^1];
        var landA = AreaLoader.AlongQuay(berth.Quay, berth.WaterSide, 0.0, -widthM);
        var len = Math.Sqrt((b.East - a.East) * (b.East - a.East) + (b.North - a.North) * (b.North - a.North));
        var landB = AreaLoader.AlongQuay(berth.Quay, berth.WaterSide, len, -widthM);
        var poly = new ExtrudedPolygon("quay", berth.Name, new[] { a, b, landB, landA }, heightM);
        return Extrude(poly);
    }

    /// <summary>小方塊(碰墊、繫船柱、導燈座):中心 ENU 與尺寸(m)。</summary>
    public static MeshData Box(string name, EnuPoint centre, double baseY, double sizeX, double sizeY, double sizeZ)
    {
        var half = sizeX / 2;
        var halfN = sizeZ / 2;
        var pts = new[]
        {
            new EnuPoint(centre.East - half, centre.North - halfN),
            new EnuPoint(centre.East + half, centre.North - halfN),
            new EnuPoint(centre.East + half, centre.North + halfN),
            new EnuPoint(centre.East - half, centre.North + halfN),
        };
        return Extrude(new ExtrudedPolygon("box", name, pts, baseY + sizeY), baseY);
    }

    /// <summary>有號面積(ENU;正 = 逆時針)。</summary>
    public static double SignedArea(IReadOnlyList<EnuPoint> pts)
    {
        double a = 0;
        for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            a += (pts[j].East * pts[i].North) - (pts[i].East * pts[j].North);
        return a / 2;
    }

    public static IReadOnlyList<EnuPoint> EnsureCounterClockwise(IReadOnlyList<EnuPoint> pts)
        => SignedArea(pts) >= 0 ? pts : pts.Reverse().ToList();

    /// <summary>耳切法三角化(輸入須為逆時針簡單多邊形);回傳頂點索引三元組。退化時以扇形三角化保底。</summary>
    public static List<(int A, int B, int C)> Triangulate(IReadOnlyList<EnuPoint> pts)
    {
        var result = new List<(int, int, int)>();
        var n = pts.Count;
        if (n < 3) return result;
        var idx = Enumerable.Range(0, n).ToList();
        var guard = 0;
        while (idx.Count > 3 && guard++ < n * n)
        {
            var found = false;
            for (var i = 0; i < idx.Count; i++)
            {
                var ia = idx[(i + idx.Count - 1) % idx.Count];
                var ib = idx[i];
                var ic = idx[(i + 1) % idx.Count];
                if (!IsConvex(pts[ia], pts[ib], pts[ic])) continue;
                var ear = true;
                foreach (var k in idx)
                {
                    if (k == ia || k == ib || k == ic) continue;
                    if (PointInTriangle(pts[k], pts[ia], pts[ib], pts[ic])) { ear = false; break; }
                }
                if (!ear) continue;
                result.Add((ia, ib, ic));
                idx.RemoveAt(i);
                found = true;
                break;
            }
            if (!found)
            {
                // 退化(共線/近似自交):扇形保底
                for (var i = 1; i + 1 < idx.Count; i++) result.Add((idx[0], idx[i], idx[i + 1]));
                return result;
            }
        }
        if (idx.Count == 3) result.Add((idx[0], idx[1], idx[2]));
        return result;
    }

    private static bool IsConvex(EnuPoint a, EnuPoint b, EnuPoint c)
        => (b.East - a.East) * (c.North - a.North) - (b.North - a.North) * (c.East - a.East) > 1e-9;

    private static bool PointInTriangle(EnuPoint p, EnuPoint a, EnuPoint b, EnuPoint c)
    {
        var d1 = Sign(p, a, b);
        var d2 = Sign(p, b, c);
        var d3 = Sign(p, c, a);
        var hasNeg = d1 < -1e-12 || d2 < -1e-12 || d3 < -1e-12;
        var hasPos = d1 > 1e-12 || d2 > 1e-12 || d3 > 1e-12;
        return !(hasNeg && hasPos);
    }

    private static double Sign(EnuPoint p1, EnuPoint p2, EnuPoint p3)
        => (p1.East - p3.East) * (p2.North - p3.North) - (p2.East - p3.East) * (p1.North - p3.North);
}
