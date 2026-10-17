namespace SimosaBRM.SimCore.Geo;

/// <summary>平面多邊形(ENU,m)的純函數:內外判定(ray casting,偶奇規則,邊上視為內)、點到邊的最小距離、自交檢查。</summary>
public static class Polygon2D
{
    /// <summary>點是否在多邊形內(含邊上)。</summary>
    public static bool Contains(ReadOnlySpan<(double X, double Y)> poly, double px, double py)
    {
        var n = poly.Length;
        if (n < 3) return false;
        var inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var (xi, yi) = poly[i];
            var (xj, yj) = poly[j];
            if (DistanceToSegment(px, py, xi, yi, xj, yj) < 1e-9) return true;
            if ((yi > py) != (yj > py))
            {
                var xCross = xi + (py - yi) * (xj - xi) / (yj - yi);
                if (px < xCross) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>點到多邊形邊界的最小距離(m;點在內部時仍為正的邊距)。</summary>
    public static double DistanceToBoundary(ReadOnlySpan<(double X, double Y)> poly, double px, double py)
    {
        var best = double.PositiveInfinity;
        var n = poly.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var d = DistanceToSegment(px, py, poly[j].X, poly[j].Y, poly[i].X, poly[i].Y);
            if (d < best) best = d;
        }
        return best;
    }

    public static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var l2 = dx * dx + dy * dy;
        var t = l2 < 1e-12 ? 0.0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0.0, 1.0);
        var cx = ax + t * dx;
        var cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    /// <summary>非相鄰邊是否相交(自交多邊形在載入時為錯誤)。</summary>
    public static bool SelfIntersects(ReadOnlySpan<(double X, double Y)> poly)
    {
        var n = poly.Length;
        if (n < 4) return false;
        for (var i = 0; i < n; i++)
        {
            var a1 = poly[i];
            var a2 = poly[(i + 1) % n];
            for (var j = i + 1; j < n; j++)
            {
                if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue; // 相鄰邊共用頂點
                var b1 = poly[j];
                var b2 = poly[(j + 1) % n];
                if (SegmentsIntersect(a1, a2, b1, b2)) return true;
            }
        }
        return false;
    }

    private static bool SegmentsIntersect((double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3, (double X, double Y) p4)
    {
        var d1 = Cross(p3, p4, p1);
        var d2 = Cross(p3, p4, p2);
        var d3 = Cross(p1, p2, p3);
        var d4 = Cross(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) p)
        => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
}
