using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.Visual.Shared.View;

namespace SimosaBRM.Visual.Shared.Env;

/// <summary>某顯示幀的環境呈現參數(純函數自狀態推得;Godot 端只套用)。</summary>
public sealed record EnvironmentLook
{
    /// <summary>太陽高度角(度;負 = 地平線下)</summary>
    public required double SunElevationDeg { get; init; }
    public required double SunAzimuthDeg { get; init; }
    /// <summary>0 = 全夜、1 = 全日</summary>
    public required double Daylight { get; init; }
    public required bool NavigationLightsOn { get; init; }
    /// <summary>霧:指數霧密度(1/m)</summary>
    public required double FogDensity { get; init; }
    public required double VisibilityM { get; init; }
    /// <summary>降雨強度 0–1(自船位置落在雨區內時)</summary>
    public required double Rain { get; init; }
    /// <summary>海況 0–9(Douglas)</summary>
    public required double SeaState { get; init; }
    /// <summary>波高(m,有義波高近似)</summary>
    public required double WaveHeightM { get; init; }
    /// <summary>浪向(去向,度)</summary>
    public required double WaveDirectionDeg { get; init; }
    public required double WindSpeedKn { get; init; }
}

/// <summary>
/// 狀態 → 環境呈現:能見度 → 霧密度(距離 visibility 處透射率 5 %:density = −ln(0.05)/visibility);
/// 真風 → 海況(Douglas,依 Beaufort 近似)與波高;radarEnvironment 雨區(多邊形或圓)含自船位置 → 雨;
/// 日夜:state 無時間,依啟動參數 daynight 或 --start-utc + 模擬秒數 + 自船經緯度的太陽高度(簡化天文公式,誤差約 1°)。
/// 無物理效果,只影響畫面(規劃書第 10.1 節)。
/// </summary>
public static class EnvironmentMapper
{
    public const double DefaultVisibilityNm = 10.0;

    public static EnvironmentLook Build(OwnShipState s, RadarEnvironmentState? radar, DayNightMode mode, DateTimeOffset? startUtc)
    {
        var visNm = s.VisibilityNm is { } v && v > 0 ? v : DefaultVisibilityNm;
        var visM = visNm * Units.MetresPerNauticalMile;
        var fog = -Math.Log(0.05) / visM;
        var wind = s.Wind.TrueSpeed;
        var sea = radar?.SeaState ?? SeaStateFromWind(wind);
        var (elev, az) = SunPosition(mode, startUtc, s.T, s.Pos.Lat, s.Pos.Lon);
        var daylight = Math.Clamp((elev + 6.0) / 12.0, 0.0, 1.0); // −6°(民用曙暮光)→ 0,+6° → 1
        return new EnvironmentLook
        {
            SunElevationDeg = elev,
            SunAzimuthDeg = az,
            Daylight = daylight,
            NavigationLightsOn = elev < 0.0,
            FogDensity = fog,
            VisibilityM = visM,
            Rain = RainAt(radar, s.Pos.X, s.Pos.Y),
            SeaState = sea,
            WaveHeightM = WaveHeightFromSeaState(sea),
            WaveDirectionDeg = Units.NormalizeHeadingDeg(s.Wind.TrueDir + 180.0),
            WindSpeedKn = wind,
        };
    }

    /// <summary>真風速(kn)→ Douglas 海況(近似:Beaufort 對應)。</summary>
    public static double SeaStateFromWind(double windKn)
    {
        if (windKn < 1) return 0;
        if (windKn < 4) return 1;
        if (windKn < 7) return 2;
        if (windKn < 11) return 2.5;
        if (windKn < 17) return 3;
        if (windKn < 22) return 4;
        if (windKn < 28) return 5;
        if (windKn < 34) return 6;
        if (windKn < 41) return 7;
        return 8;
    }

    /// <summary>Douglas 海況 → 有義波高(m;表的代表值)。</summary>
    public static double WaveHeightFromSeaState(double sea) => sea switch
    {
        < 0.5 => 0.0,
        < 1.5 => 0.05,
        < 2.5 => 0.3,
        < 3.5 => 0.9,
        < 4.5 => 1.9,
        < 5.5 => 3.3,
        < 6.5 => 5.0,
        < 7.5 => 7.5,
        < 8.5 => 11.5,
        _ => 14.0,
    };

    /// <summary>自船 ENU 位置落在雨區內的最大強度(多邊形或圓);無雨區為 0。</summary>
    public static double RainAt(RadarEnvironmentState? radar, double x, double y)
    {
        if (radar is null) return 0;
        var best = 0.0;
        foreach (var r in radar.RainAreas)
        {
            var inside = false;
            if (r.Polygon is { Count: >= 3 } poly)
            {
                var pts = new (double X, double Y)[poly.Count];
                for (var i = 0; i < poly.Count; i++) pts[i] = (poly[i].X, poly[i].Y);
                inside = Polygon2D.Contains(pts, x, y);
            }
            else if (r.Center is { } c && r.RadiusM is { } rad)
            {
                var dx = x - c.X; var dy = y - c.Y;
                inside = dx * dx + dy * dy <= rad * rad;
            }
            if (inside) best = Math.Max(best, Math.Clamp(r.Intensity, 0, 1));
        }
        return best;
    }

    /// <summary>太陽高度與方位(度)。手動模式回固定值:day 45°、dusk −3°、night −30°。</summary>
    public static (double ElevationDeg, double AzimuthDeg) SunPosition(DayNightMode mode, DateTimeOffset? startUtc, double simSeconds, double latDeg, double lonDeg)
    {
        switch (mode)
        {
            case DayNightMode.Day: return (45.0, 180.0);
            case DayNightMode.Dusk: return (-3.0, 260.0);
            case DayNightMode.Night: return (-30.0, 0.0);
        }
        if (startUtc is null) return (45.0, 180.0);
        var t = startUtc.Value.ToUniversalTime().AddSeconds(simSeconds);
        return SolarPosition(t, latDeg, lonDeg);
    }

    /// <summary>簡化太陽位置(NOAA 近似;高度角誤差約 1°,足夠日夜/黃昏判定與光照方向)。</summary>
    public static (double ElevationDeg, double AzimuthDeg) SolarPosition(DateTimeOffset utc, double latDeg, double lonDeg)
    {
        var jd = utc.ToUniversalTime().DateTime.ToOADate() + 2415018.5;
        var d = jd - 2451545.0;
        var g = Units.DegToRad(Units.NormalizeHeadingDeg(357.529 + 0.98560028 * d));
        var q = Units.NormalizeHeadingDeg(280.459 + 0.98564736 * d);
        var l = Units.DegToRad(Units.NormalizeHeadingDeg(q + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)));
        var e = Units.DegToRad(23.439 - 0.00000036 * d);
        var ra = Math.Atan2(Math.Cos(e) * Math.Sin(l), Math.Cos(l));
        var dec = Math.Asin(Math.Sin(e) * Math.Sin(l));
        var gmst = 18.697374558 + 24.06570982441908 * d;
        var lst = ((gmst % 24 + 24) % 24) * 15.0 + lonDeg;
        var ha = Units.DegToRad(Units.NormalizeHeadingDeg(lst - Units.RadToDeg(ra)));
        var lat = Units.DegToRad(latDeg);
        var sinEl = Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(ha);
        var el = Math.Asin(Math.Clamp(sinEl, -1, 1));
        var az = Math.Atan2(-Math.Sin(ha), Math.Tan(dec) * Math.Cos(lat) - Math.Sin(lat) * Math.Cos(ha));
        return (Units.RadToDeg(el), Units.NormalizeHeadingDeg(Units.RadToDeg(az)));
    }
}
