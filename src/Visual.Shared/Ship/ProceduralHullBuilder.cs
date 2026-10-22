using SimosaBRM.Visual.Shared.Area;
using SimosaBRM.Visual.Shared.Scene;

namespace SimosaBRM.Visual.Shared.Ship;

/// <summary>船模各部件(Godot 端給不同材質)。</summary>
public sealed record ShipMeshSet(MeshData Hull, MeshData Deck, MeshData Superstructure, MeshData Forecastle, MeshData Mast)
{
    public IEnumerable<MeshData> All => new[] { Hull, Deck, Superstructure, Forecastle, Mast };
    public int TotalTriangles => All.Sum(m => m.TriangleCount);
}

/// <summary>
/// 程序化船體網格(示意;待替換為實船模型)。船節點區域座標:x 右舷、y 上、z 向艉(前方 −z);原點在船舯水線(y = 0 為水線)。
/// 船殼:沿船長取 Sections 個剖面,半寬 = (B/2)·f(x),f 為艏段拋物線收尖、舯段平行、艉段漸縮至 transom;
/// 剖面為 U 形(底部圓弧以 3 段近似)。LOD:lowPoly = true 時剖面數減半、底弧改 1 段(目標船遠距離用)。
/// </summary>
public static class ProceduralHullBuilder
{
    public static ShipMeshSet Build(HullParameters p, bool lowPoly = false)
    {
        var sections = lowPoly ? 8 : 16;
        var arcSegments = lowPoly ? 1 : 3;
        var hull = BuildHull(p, sections, arcSegments);
        var deck = BuildDeck(p, sections);
        var super = Box("superstructure", p.SuperstructureAftX, p.SuperstructureForeX, -p.Beam * 0.42, p.Beam * 0.42, p.Depth, p.BridgeDeckHeight + 2.8, p.Draft);
        var fc = Box("forecastle", p.Loa / 2 - p.ForecastleLength, p.Loa / 2 - 0.5, -p.Beam * 0.35, p.Beam * 0.35, p.Depth, p.Depth + p.ForecastleHeight, p.Draft);
        var mastX = (p.SuperstructureAftX + p.SuperstructureForeX) / 2;
        var mast = Box("mast", mastX - 0.3, mastX + 0.3, -0.3, 0.3, p.BridgeDeckHeight + 2.8, p.MastTopAboveBase, p.Draft);
        return new ShipMeshSet(hull, deck, super, fc, mast);
    }

    /// <summary>半寬分布 f(x/L ∈ [−0.5, 0.5]);艏段 (0.25..0.5) 拋物線到 0(艏柱),艉段 (−0.5..−0.3) 漸縮到 transom 半寬 0.55。</summary>
    public static double HalfBreadthFactor(double xi, bool transom)
    {
        if (xi >= 0.25)
        {
            var t = (xi - 0.25) / 0.25;                 // 0 舯、1 艏柱
            return Math.Max(0.0, 1.0 - t * t);
        }
        if (xi <= -0.3)
        {
            var t = (-0.3 - xi) / 0.2;                  // 0 舯、1 艉
            var end = transom ? 0.55 : 0.05;
            return 1.0 - (1.0 - end) * t * t;
        }
        return 1.0;
    }

    /// <summary>龍骨深度分布(艏艉上翹)。</summary>
    public static double KeelFactor(double xi)
    {
        if (xi >= 0.35) return Math.Max(0.15, 1.0 - (xi - 0.35) / 0.15 * 0.85);
        if (xi <= -0.4) return Math.Max(0.4, 1.0 - (-0.4 - xi) / 0.1 * 0.6);
        return 1.0;
    }

    private static MeshData BuildHull(HullParameters p, int sections, int arcSegments)
    {
        var mesh = new MeshData { Name = "hull" };
        var halfB = p.Beam / 2;
        var draftY = -p.Draft;               // 基線在水線下 Draft
        var deckY = p.Depth - p.Draft;       // 上甲板在水線上 Depth − Draft
        // 每剖面的點:左舷甲板緣 → 左舷底弧 → 龍骨 → 右舷底弧 → 右舷甲板緣
        var ringSize = 2 * (arcSegments + 1) + 1;
        var rings = new int[sections + 1][];
        for (var s = 0; s <= sections; s++)
        {
            var xi = -0.5 + (double)s / sections;
            var xFwd = xi * p.Loa;
            var hb = halfB * HalfBreadthFactor(xi, p.TransomStern);
            if (hb < 0.3) hb = 0.3;          // 艏柱留一點寬度避免退化三角形
            var keelY = draftY * KeelFactor(xi);
            var ring = new int[ringSize];
            var k = 0;
            // 左舷(y 負)自甲板緣下到龍骨
            for (var a = 0; a <= arcSegments; a++)
            {
                var t = (double)a / arcSegments;        // 0 甲板緣、1 底
                var ang = t * Math.PI / 2;              // 四分之一圓弧:側邊垂直段 + 圓角
                var side = a == 0 ? -hb : -hb * (0.15 + 0.85 * Math.Cos(ang));
                var y = a == 0 ? deckY : keelY + (deckY - keelY) * (1 - Math.Sin(ang)) * 0.35;
                var nUp = a == 0 ? 0 : -Math.Sin(ang);
                var nStbd = a == 0 ? -1 : -Math.Cos(ang);
                ring[k++] = AddBody(mesh, xFwd, side, y, 0, nStbd, nUp);
            }
            ring[k++] = AddBody(mesh, xFwd, 0, keelY, 0, 0, -1);
            for (var a = arcSegments; a >= 0; a--)
            {
                var t = (double)a / arcSegments;
                var ang = t * Math.PI / 2;
                var side = a == 0 ? hb : hb * (0.15 + 0.85 * Math.Cos(ang));
                var y = a == 0 ? deckY : keelY + (deckY - keelY) * (1 - Math.Sin(ang)) * 0.35;
                var nUp = a == 0 ? 0 : -Math.Sin(ang);
                var nStbd = a == 0 ? 1 : Math.Cos(ang);
                ring[k++] = AddBody(mesh, xFwd, side, y, 0, nStbd, nUp);
            }
            rings[s] = ring;
        }
        // 連接相鄰剖面(自外側看逆時針)
        for (var s = 0; s < sections; s++)
        {
            for (var i = 0; i + 1 < ringSize; i++)
            {
                var a = rings[s][i];
                var b = rings[s][i + 1];
                var c = rings[s + 1][i + 1];
                var d = rings[s + 1][i];
                mesh.AddQuad(a, d, c, b);
            }
        }
        // 艉板(transom)封閉
        var aft = rings[0];
        var centreAft = AddBody(mesh, -0.5 * p.Loa, 0, (deckY + draftY * KeelFactor(-0.5)) / 2, -1, 0, 0);
        for (var i = 0; i + 1 < ringSize; i++) mesh.AddTriangle(centreAft, aft[i], aft[i + 1]);
        // 艏柱封閉
        var fwd = rings[sections];
        var centreFwd = AddBody(mesh, 0.5 * p.Loa, 0, (deckY + draftY * KeelFactor(0.5)) / 2, 1, 0, 0);
        for (var i = 0; i + 1 < ringSize; i++) mesh.AddTriangle(centreFwd, fwd[i + 1], fwd[i]);
        return mesh;
    }

    private static MeshData BuildDeck(HullParameters p, int sections)
    {
        var mesh = new MeshData { Name = "deck" };
        var deckY = p.Depth - p.Draft + 0.02;
        var halfB = p.Beam / 2;
        var prevL = -1; var prevR = -1;
        for (var s = 0; s <= sections; s++)
        {
            var xi = -0.5 + (double)s / sections;
            var hb = Math.Max(0.3, halfB * HalfBreadthFactor(xi, p.TransomStern));
            var l = AddBody(mesh, xi * p.Loa, -hb, deckY, 0, 0, 1);
            var r = AddBody(mesh, xi * p.Loa, hb, deckY, 0, 0, 1);
            if (s > 0)
            {
                // 自上方看逆時針(Godot 正面)
                mesh.AddQuad(prevL, prevR, r, l);
            }
            prevL = l; prevR = r;
        }
        return mesh;
    }

    /// <summary>船體座標的長方體(xAft..xFore 向艏、yPort..yStbd 右舷、zBottom..zTop 基線以上),轉為節點區域座標(水線 y=0)。</summary>
    public static MeshData Box(string name, double xAft, double xFore, double yPort, double yStbd, double zBottom, double zTop, double draft)
    {
        var mesh = new MeshData { Name = name };
        var b = zBottom - draft;
        var t = zTop - draft;
        // 8 個角(節點區域座標 (stbd, up, −fwd)),每面獨立頂點以保持平直法線
        void Face(double nx, double ny, double nz, params (double f, double s, double u)[] c)
        {
            var i0 = AddBody(mesh, c[0].f, c[0].s, c[0].u, nx, ny, nz);
            var i1 = AddBody(mesh, c[1].f, c[1].s, c[1].u, nx, ny, nz);
            var i2 = AddBody(mesh, c[2].f, c[2].s, c[2].u, nx, ny, nz);
            var i3 = AddBody(mesh, c[3].f, c[3].s, c[3].u, nx, ny, nz);
            mesh.AddQuad(i0, i1, i2, i3);
        }
        // 頂面(法線 +y):自上看逆時針(在 Godot 的 x–z 平面,z 朝觀者下方)
        Face(0, 0, 1, (xAft, yPort, t), (xAft, yStbd, t), (xFore, yStbd, t), (xFore, yPort, t));
        // 底面
        Face(0, 0, -1, (xAft, yPort, b), (xFore, yPort, b), (xFore, yStbd, b), (xAft, yStbd, b));
        // 右舷面
        Face(0, 1, 0, (xAft, yStbd, b), (xFore, yStbd, b), (xFore, yStbd, t), (xAft, yStbd, t));
        // 左舷面
        Face(0, -1, 0, (xAft, yPort, b), (xAft, yPort, t), (xFore, yPort, t), (xFore, yPort, b));
        // 前面(艏)
        Face(1, 0, 0, (xFore, yPort, b), (xFore, yPort, t), (xFore, yStbd, t), (xFore, yStbd, b));
        // 後面(艉)
        Face(-1, 0, 0, (xAft, yPort, b), (xAft, yStbd, b), (xAft, yStbd, t), (xAft, yPort, t));
        return mesh;
    }

    /// <summary>船體座標(fwd, stbd, up 相對水線)→ 節點區域座標頂點;法線亦由船體座標轉換。</summary>
    private static int AddBody(MeshData mesh, double fwd, double stbd, double up, double nFwd, double nStbd, double nUp)
    {
        var v = SceneCoordinates.FromBody(fwd, stbd, up);
        var n = SceneCoordinates.FromBody(nFwd, nStbd, nUp);
        return mesh.AddVertex(v.X, v.Y, v.Z, n.X, n.Y, n.Z);
    }
}
