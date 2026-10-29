using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.Visual.Shared.Env;

/// <summary>某時刻、某觀測點的月球位置與月相(MoonModel 的輸出;純資料)。</summary>
public sealed record MoonState
{
    /// <summary>地心座標月球高度角(度;已含視差修正,負 = 地平線下)</summary>
    public required double ElevationDeg { get; init; }
    /// <summary>方位(度,0–360,北 0 順時針)</summary>
    public required double AzimuthDeg { get; init; }
    /// <summary>照明比例 0(新月)–1(滿月)</summary>
    public required double IlluminatedFraction { get; init; }
    /// <summary>相位角(度;0 = 滿月、180 = 新月)</summary>
    public required double PhaseAngleDeg { get; init; }
    /// <summary>漸盈(新月 → 滿月)為 true,漸虧為 false</summary>
    public required bool Waxing { get; init; }
    /// <summary>月齡(自新月起算的天數,0–29.53;由真黃經差推得)</summary>
    public required double AgeDays { get; init; }
    /// <summary>地心距離(地球半徑)</summary>
    public required double DistanceEarthRadii { get; init; }
}

/// <summary>
/// 月球位置與月相(低精度天文演算法;規劃書第 7.3 節夜間光照用,不作航海天文計算):
/// 位置採 Paul Schlyter「How to compute planetary positions」的月球軌道根數 + 主要攝動項(黃經 12 項、黃緯 5 項、距離 2 項),
/// 地心黃道 → 赤道 → 地平座標,再以視差 asin(1/r) 修正高度角;典型誤差高度/方位 ±0.5°(需求 ±1°)。
/// 月相以日、月真黃經差(離角)算照明比例 k = (1 − cos ε)/2(忽略黃緯與距離的二階影響);月齡由黃經差 × 朔望月長度推得。
/// 月光照度 <see cref="RelativeIlluminance"/>:Krisciunas &amp; Schaefer (1991) 的月球視星等相位項 0.026·α + 4×10⁻⁹·α⁴ 換成相對照度,
/// 乘以 sin(高度角)(水平面照度)與大氣消光(氣團 ≈ 1/sin h,0.2 mag/氣團);滿月天頂 = 1(約 0.25 lux),地平線下 = 0。
/// 純函數、確定性(只依 UTC 與經緯度),C# 與 Godot 端共用。
/// </summary>
public static class MoonModel
{
    /// <summary>滿月天頂的水平面照度(lux;相對強度 1 的比例基準,Krisciunas &amp; Schaefer 1991 約 0.2–0.3 lux)</summary>
    public const double FullMoonZenithLux = 0.25;
    /// <summary>朔望月平均長度(天)</summary>
    public const double SynodicMonthDays = 29.530589;

    private const double D2R = Math.PI / 180.0;
    private const double R2D = 180.0 / Math.PI;

    /// <summary>月球位置與月相(高度角/方位對 <paramref name="latDeg"/>、<paramref name="lonDeg"/> 的觀測點;東經正)。</summary>
    public static MoonState Compute(DateTimeOffset utc, double latDeg, double lonDeg)
    {
        var u = utc.ToUniversalTime();
        var jd = u.DateTime.ToOADate() + 2415018.5;
        var d = jd - 2451543.5; // Schlyter 的日數起點:2000-01-00 0:00 UT
        var ecl = 23.4393 - 3.563e-7 * d;

        // 太陽(月相與恆星時用)
        var ws = 282.9404 + 4.70935e-5 * d;
        var es = 0.016709 - 1.151e-9 * d;
        var ms = Rev(356.0470 + 0.9856002585 * d);
        var esun = ms + es * R2D * Math.Sin(ms * D2R) * (1 + es * Math.Cos(ms * D2R));
        var xvs = Math.Cos(esun * D2R) - es;
        var yvs = Math.Sqrt(1 - es * es) * Math.Sin(esun * D2R);
        var lonSun = Rev(Math.Atan2(yvs, xvs) * R2D + ws);
        var meanLonSun = Rev(ms + ws);

        // 月球軌道根數
        var n = Rev(125.1228 - 0.0529538083 * d);
        const double inc = 5.1454;
        var w = Rev(318.0634 + 0.1643573223 * d);
        const double a = 60.2666;
        const double e = 0.054900;
        var m = Rev(115.3654 + 13.0649929509 * d);

        // Kepler 方程(牛頓迭代)
        var e0 = m + e * R2D * Math.Sin(m * D2R) * (1 + e * Math.Cos(m * D2R));
        for (var k = 0; k < 20; k++)
        {
            var e1 = e0 - (e0 - e * R2D * Math.Sin(e0 * D2R) - m) / (1 - e * Math.Cos(e0 * D2R));
            var done = Math.Abs(e1 - e0) < 1e-5;
            e0 = e1;
            if (done) break;
        }
        var xv = a * (Math.Cos(e0 * D2R) - e);
        var yv = a * Math.Sqrt(1 - e * e) * Math.Sin(e0 * D2R);
        var v = Math.Atan2(yv, xv) * R2D;
        var r = Math.Sqrt(xv * xv + yv * yv);
        var vw = (v + w) * D2R;
        var xh = r * (Math.Cos(n * D2R) * Math.Cos(vw) - Math.Sin(n * D2R) * Math.Sin(vw) * Math.Cos(inc * D2R));
        var yh = r * (Math.Sin(n * D2R) * Math.Cos(vw) + Math.Cos(n * D2R) * Math.Sin(vw) * Math.Cos(inc * D2R));
        var zh = r * Math.Sin(vw) * Math.Sin(inc * D2R);
        var lonMoon = Math.Atan2(yh, xh) * R2D;
        var latMoon = Math.Atan2(zh, Math.Sqrt(xh * xh + yh * yh)) * R2D;

        // 主要攝動(度;Schlyter)
        var lm = Rev(m + w + n);      // 月球平黃經
        var dm = Rev(lm - meanLonSun); // 平離角
        var f = Rev(lm - n);           // 緯度幅角
        lonMoon += -1.274 * Sin(m - 2 * dm) + 0.658 * Sin(2 * dm) - 0.186 * Sin(ms) - 0.059 * Sin(2 * m - 2 * dm)
                   - 0.057 * Sin(m - 2 * dm + ms) + 0.053 * Sin(m + 2 * dm) + 0.046 * Sin(2 * dm - ms) + 0.041 * Sin(m - ms)
                   - 0.035 * Sin(dm) - 0.031 * Sin(m + ms) - 0.015 * Sin(2 * f - 2 * dm) + 0.011 * Sin(m - 4 * dm);
        latMoon += -0.173 * Sin(f - 2 * dm) - 0.055 * Sin(m - f - 2 * dm) - 0.046 * Sin(m + f - 2 * dm) + 0.033 * Sin(f + 2 * dm) + 0.017 * Sin(2 * m + f);
        r += -0.58 * Cos(m - 2 * dm) - 0.46 * Cos(2 * dm);
        lonMoon = Rev(lonMoon);

        // 黃道 → 赤道
        var xg = Cos(lonMoon) * Cos(latMoon);
        var yg = Sin(lonMoon) * Cos(latMoon);
        var zg = Sin(latMoon);
        var xe = xg;
        var ye = yg * Cos(ecl) - zg * Sin(ecl);
        var ze = yg * Sin(ecl) + zg * Cos(ecl);
        var ra = Math.Atan2(ye, xe) * R2D;
        var dec = Math.Atan2(ze, Math.Sqrt(xe * xe + ye * ye)) * R2D;

        // 赤道 → 地平(恆星時:GMST0 = 太陽平黃經 + 180°)
        var utHours = u.TimeOfDay.TotalHours;
        var lst = Rev(meanLonSun + 180.0 + utHours * 15.0 + lonDeg);
        var ha = Rev(lst - ra);
        var x = Cos(ha) * Cos(dec);
        var y = Sin(ha) * Cos(dec);
        var z = Sin(dec);
        var xhor = x * Sin(latDeg) - z * Cos(latDeg);
        var yhor = y;
        var zhor = x * Cos(latDeg) + z * Sin(latDeg);
        var az = Rev(Math.Atan2(yhor, xhor) * R2D + 180.0);
        var alt = Math.Asin(Math.Clamp(zhor, -1, 1)) * R2D;
        var parallax = Math.Asin(1.0 / r) * R2D; // 地平視差(約 0.95°)
        var altTopo = alt - parallax * Cos(alt);

        // 月相:日月真黃經差(離角)
        var cosElong = Math.Clamp(Cos(latMoon) * Cos(lonMoon - lonSun), -1, 1);
        var elong = Math.Acos(cosElong) * R2D;
        var phaseAngle = 180.0 - elong;
        var fraction = (1 - Math.Cos(elong * D2R)) / 2;
        var lonDiff = Rev(lonMoon - lonSun);
        return new MoonState
        {
            ElevationDeg = altTopo,
            AzimuthDeg = az,
            IlluminatedFraction = Math.Clamp(fraction, 0, 1),
            PhaseAngleDeg = phaseAngle,
            Waxing = lonDiff < 180.0,
            AgeDays = lonDiff / 360.0 * SynodicMonthDays,
            DistanceEarthRadii = r,
        };
    }

    /// <summary>
    /// 月光相對照度 0–1(滿月天頂晴空 = 1 ≈ <see cref="FullMoonZenithLux"/> lux):高度角 ≤ 0 → 0;
    /// 相位項依 Krisciunas &amp; Schaefer (1991) 視星等 Δm = 0.026·α + 4×10⁻⁹·α⁴(α = 相位角 = acos(2k − 1),度;上弦 ≈ 0.09、新月 ≈ 0),
    /// 水平面投影 sin h,大氣消光 10^(−0.4 × 0.2 × (X − 1))(X = 1/sin h,上限 40)。
    /// </summary>
    public static double RelativeIlluminance(double elevationDeg, double illuminatedFraction)
    {
        if (elevationDeg <= 0.0) return 0.0;
        var sinH = Math.Sin(elevationDeg * D2R);
        var airMass = Math.Min(1.0 / Math.Max(sinH, 1e-3), 40.0);
        var extinction = Math.Pow(10.0, -0.4 * 0.2 * (airMass - 1.0));
        return Math.Clamp(sinH * extinction * PhaseFactor(illuminatedFraction), 0.0, 1.0);
    }

    /// <summary>相位對月光亮度的比例(滿月 1、上弦/下弦約 0.09、新月約 0;Krisciunas &amp; Schaefer 1991)。</summary>
    public static double PhaseFactor(double illuminatedFraction)
    {
        var k = Math.Clamp(illuminatedFraction, 0.0, 1.0);
        var alpha = Math.Acos(Math.Clamp(2 * k - 1, -1, 1)) * R2D; // 相位角(度)
        var deltaMag = 0.026 * alpha + 4e-9 * Math.Pow(alpha, 4);
        return Math.Pow(10.0, -0.4 * deltaMag);
    }

    private static double Rev(double deg) => Units.NormalizeHeadingDeg(deg);
    private static double Sin(double deg) => Math.Sin(deg * D2R);
    private static double Cos(double deg) => Math.Cos(deg * D2R);
}
