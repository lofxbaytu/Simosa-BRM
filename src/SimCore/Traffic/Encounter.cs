using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Traffic;

/// <summary>兩船會遇幾何(規劃書第 8.3 節避碰指標):距離、方位、CPA/TCPA、艏越距離/時間。內部 SI(m、s、弧度)。</summary>
public readonly record struct EncounterGeometry(
    /// <summary>距離(m)</summary>
    double RangeM,
    /// <summary>自船看目標的真方位(弧度,0–2π)</summary>
    double BearingRad,
    /// <summary>相對方位(相對自船艏向,弧度,0–2π)</summary>
    double RelBearingRad,
    /// <summary>最近會遇距離(m)</summary>
    double CpaM,
    /// <summary>到達 CPA 的時間(s;負 = 已通過;相對速度為零時 0)</summary>
    double TcpaS,
    /// <summary>艏越距離(m;正 = 目標由自船艏前通過、負 = 由艉後通過;無交會(平行或已通過)時 null)</summary>
    double? BcrM,
    /// <summary>艏越時間(s)</summary>
    double? BctS)
{
    public double RangeNm => RangeM / Units.MetresPerNauticalMile;
    public double CpaNm => CpaM / Units.MetresPerNauticalMile;
    public double TcpaMin => TcpaS / 60.0;
    public double? BcrNm => BcrM / Units.MetresPerNauticalMile;
    public double? BctMin => BctS / 60.0;
}

/// <summary>會遇幾何與船體外形相交的純函數(確定性,無狀態)。</summary>
public static class Encounter
{
    /// <summary>
    /// 以直線外推(等速)計算 CPA/TCPA/BCR:相對位置 r = 目標 − 自船、相對速度 v = 目標速度 − 自船速度(ENU,m/s),
    /// TCPA = −(r·v)/|v|²、CPA = |r + v·TCPA|;艏越為目標相對軌跡與自船艏向線的交點(交點在艏前為正)。
    /// </summary>
    public static EncounterGeometry Compute(
        double ownX, double ownY, double ownVelE, double ownVelN, double ownHeadingRad,
        double tgtX, double tgtY, double tgtVelE, double tgtVelN)
    {
        var rx = tgtX - ownX;
        var ry = tgtY - ownY;
        var vx = tgtVelE - ownVelE;
        var vy = tgtVelN - ownVelN;
        var range = Math.Sqrt(rx * rx + ry * ry);
        var bearing = range < 1e-9 ? 0.0 : Units.NormalizeHeadingRad(Math.Atan2(rx, ry));
        var relBearing = Units.NormalizeHeadingRad(bearing - ownHeadingRad);

        var v2 = vx * vx + vy * vy;
        double tcpa, cpa;
        if (v2 < 1e-12)
        {
            tcpa = 0.0;
            cpa = range;
        }
        else
        {
            tcpa = -(rx * vx + ry * vy) / v2;
            var cx = rx + vx * tcpa;
            var cy = ry + vy * tcpa;
            cpa = Math.Sqrt(cx * cx + cy * cy);
        }

        // 艏向線:h = (sinψ, cosψ);相對軌跡 r(t) 與艏向線交會時 cross(h, r(t)) = 0
        var hx = Math.Sin(ownHeadingRad);
        var hy = Math.Cos(ownHeadingRad);
        var crossHv = hx * vy - hy * vx;
        double? bcr = null, bct = null;
        if (Math.Abs(crossHv) > 1e-12)
        {
            var t = -(hx * ry - hy * rx) / crossHv;
            if (t >= 0.0)
            {
                bct = t;
                bcr = hx * (rx + vx * t) + hy * (ry + vy * t);
            }
        }
        return new EncounterGeometry(range, bearing, relBearing, cpa, tcpa, bcr, bct);
    }

    /// <summary>
    /// 船體外形(凸五邊形,船舯為原點、x 向艏、y 向右舷:艏尖、肩部在 0.3 L、方艉)轉成 ENU 多邊形(東、北)。
    /// 供碰撞偵測(規劃書第 9.1 節「擱淺/碰撞偵測」)與雷達目標外形。
    /// </summary>
    public static (double E, double N)[] ShipOutline(double xEast, double yNorth, double headingRad, double loa, double beam)
    {
        var half = loa / 2.0;
        var hb = beam / 2.0;
        var body = new (double X, double Y)[]
        {
            (half, 0.0),
            (0.3 * loa, hb),
            (-half, hb),
            (-half, -hb),
            (0.3 * loa, -hb),
        };
        var s = Math.Sin(headingRad);
        var c = Math.Cos(headingRad);
        var poly = new (double E, double N)[body.Length];
        for (var i = 0; i < body.Length; i++)
        {
            var (x, y) = body[i];
            // 船體 → ENU:E = x sinψ + y cosψ;N = x cosψ − y sinψ
            poly[i] = (xEast + x * s + y * c, yNorth + x * c - y * s);
        }
        return poly;
    }

    /// <summary>凸多邊形相交測試(分離軸定理;邊界接觸視為相交)。</summary>
    public static bool PolygonsIntersect((double E, double N)[] a, (double E, double N)[] b)
        => !HasSeparatingAxis(a, b) && !HasSeparatingAxis(b, a);

    private static bool HasSeparatingAxis((double E, double N)[] source, (double E, double N)[] other)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var p = source[i];
            var q = source[(i + 1) % source.Length];
            // 邊的法向量
            var nx = -(q.N - p.N);
            var ny = q.E - p.E;
            var (minA, maxA) = Project(source, nx, ny);
            var (minB, maxB) = Project(other, nx, ny);
            if (maxA < minB || maxB < minA) return true;
        }
        return false;
    }

    private static (double Min, double Max) Project((double E, double N)[] poly, double nx, double ny)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var (e, n) in poly)
        {
            var d = e * nx + n * ny;
            if (d < min) min = d;
            if (d > max) max = d;
        }
        return (min, max);
    }
}
