namespace SimosaBRM.SimCore.Physics.External;

/// <summary>錨鏈形狀區域。</summary>
public enum CatenaryRegime
{
    /// <summary>錨未著底(鏈長 ≤ 錨鏈孔至海床高度):無水平張力</summary>
    Hanging,
    /// <summary>鏈長有餘(水平距離 ≤ L − h):水平張力 0,鏈堆在海床</summary>
    Slack,
    /// <summary>部分鏈躺底(標準錨泊懸鏈線,錨端水平)</summary>
    Touchdown,
    /// <summary>全鏈懸空,錨受上拔角</summary>
    Suspended,
    /// <summary>拉直(水平距離 ≥ √(L² − h²)):張力取上限</summary>
    Taut,
}

/// <summary>懸鏈線解:水平張力 H(N)、錨鏈孔處張力、躺底長度(m)、懸空長度(m)、錨端上拔角(弧度)。</summary>
public readonly record struct CatenaryResult(double H, double TopTension, double OnBottom, double Suspended, double UpliftRad, CatenaryRegime Regime);

/// <summary>
/// 準靜態非彈性懸鏈線(設計文件 physics-tugs-anchors-mooring.md 第 3.2 節):鏈單位水中重 w(N/m)、錨鏈孔至海床垂直距離 h、
/// 放出鏈長 L、錨鏈孔至錨的水平距離 X。參數 a = H / w。
/// <list type="bullet">
/// <item>躺底區(錨端水平):懸空長 s = √(h² + 2ha)、懸空水平跨距 x_s = a·asinh(s/a)、X = (L − s) + x_s;a 上限 a_max = (L² − h²)/(2h)(s = L)。</item>
/// <item>全懸空區:√(L² − h²) = 2a·sinh(X/(2a));錨端座標 x₁ = a·atanh(h/L) − X/2,上拔角 θ = atan(sinh(x₁/a))。</item>
/// <item>X ≥ √(L² − h²):拉直,H 取上限。兩區在 a = a_max 時連續(錨端恰為最低點)。</item>
/// </list>
/// X(a) 在兩區皆隨 a 單調遞增,以對數二分法求解(固定迭代次數,確定性)。
/// </summary>
public static class Catenary
{
    private const int Iterations = 80;

    /// <summary>躺底區給定 a 的水平距離(解析式;測試與初始錨位用)。</summary>
    public static double TouchdownDistance(double a, double length, double h)
    {
        var s = Math.Sqrt(h * h + 2.0 * h * a);
        if (s > length) s = length;
        return (length - s) + a * Asinh(s / a);
    }

    /// <summary>a 的躺底區上限(s = L)。</summary>
    public static double MaxTouchdownA(double length, double h) => (length * length - h * h) / (2.0 * h);

    /// <summary>求解懸鏈線;<paramref name="hCap"/> 為水平張力上限(N,數值安全;拉直區取此值)。</summary>
    public static CatenaryResult Solve(double x, double length, double h, double w, double hCap)
    {
        if (!(h > 0.0)) h = 1e-3;
        if (length <= h || w <= 0.0)
            return new CatenaryResult(0.0, w * Math.Max(0.0, length), 0.0, Math.Max(0.0, length), 0.0, CatenaryRegime.Hanging);
        if (x <= length - h)
            return new CatenaryResult(0.0, w * h, length - h, h, 0.0, CatenaryRegime.Slack);

        var aMax = MaxTouchdownA(length, h);
        var xTd = aMax * Asinh(length / aMax);
        if (x <= xTd)
        {
            // 對數二分:X(a) 單調遞增,a ∈ (0, a_max]
            var lo = Math.Log(Math.Max(1e-9, 1e-9 * h));
            var hi = Math.Log(aMax);
            for (var i = 0; i < Iterations; i++)
            {
                var mid = 0.5 * (lo + hi);
                if (TouchdownDistance(Math.Exp(mid), length, h) < x) lo = mid; else hi = mid;
            }
            var a = Math.Exp(0.5 * (lo + hi));
            var hT = w * a;
            var s = Math.Min(length, Math.Sqrt(h * h + 2.0 * h * a));
            if (hT > hCap) return Capped(hCap, length, h, w, CatenaryRegime.Touchdown, length - s, s);
            return new CatenaryResult(hT, hT + w * h, length - s, s, 0.0, CatenaryRegime.Touchdown);
        }

        var chord = Math.Sqrt(length * length - h * h);
        if (x >= chord) return Capped(hCap, length, h, w, CatenaryRegime.Taut, 0.0, length);

        // 全懸空:g(a) = 2a·sinh(X/2a) 隨 a 遞減至 X;找上界後二分
        var aLo = aMax;
        var aHi = aMax;
        for (var i = 0; i < 200 && 2.0 * aHi * Math.Sinh(x / (2.0 * aHi)) > chord; i++) aHi *= 2.0;
        var lLo = Math.Log(aLo);
        var lHi = Math.Log(aHi);
        for (var i = 0; i < Iterations; i++)
        {
            var mid = 0.5 * (lLo + lHi);
            var am = Math.Exp(mid);
            if (2.0 * am * Math.Sinh(x / (2.0 * am)) > chord) lLo = mid; else lHi = mid;
        }
        var aS = Math.Exp(0.5 * (lLo + lHi));
        var hS = w * aS;
        if (hS > hCap) return Capped(hCap, length, h, w, CatenaryRegime.Suspended, 0.0, length);
        var x1 = aS * Atanh(h / length) - 0.5 * x;
        var uplift = x1 > 0.0 ? Math.Atan(Math.Sinh(x1 / aS)) : 0.0;
        var top = hS * Math.Cosh((x1 + x) / aS);
        return new CatenaryResult(hS, top, 0.0, length, uplift, CatenaryRegime.Suspended);
    }

    /// <summary>水平張力達上限:錨鏈孔張力以直線近似(H / cos θ,θ = 弦線俯角)。</summary>
    private static CatenaryResult Capped(double hCap, double length, double h, double w, CatenaryRegime regime, double onBottom, double suspended)
    {
        var cos = Math.Sqrt(Math.Max(1e-12, 1.0 - (h / length) * (h / length)));
        return new CatenaryResult(hCap, Math.Max(hCap / cos, hCap + w * h), onBottom, suspended, onBottom > 0 ? 0.0 : Math.Asin(Math.Min(1.0, h / length)), regime);
    }

    private static double Asinh(double z) => Math.Log(z + Math.Sqrt(z * z + 1.0));
    private static double Atanh(double z) => 0.5 * Math.Log((1.0 + z) / (1.0 - z));
}
