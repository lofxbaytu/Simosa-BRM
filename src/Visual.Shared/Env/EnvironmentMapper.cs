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
    /// <summary>月球位置、月相與月光強度(夜間光照;規劃書第 7.3 節)</summary>
    public required MoonLook Moon { get; init; }
    /// <summary>
    /// 夜空亮度 0–1(0 = 無月、濃霧;1 = 晴空滿月高掛):夜間(太陽高度 &lt; −6°)的環境光/天空底色依此;
    /// = 星光底值 0.08 + 0.92 × 衰減後月光;白天/曙暮光時以 (1 − Daylight) 加權,Godot 端再與日光環境光混合。
    /// </summary>
    public required double NightSkyBrightness { get; init; }
}

/// <summary>月球呈現參數(<see cref="MoonModel"/> 的結果經日夜模式與天候整理;Godot 端月光 DirectionalLight3D 只套用)。</summary>
public sealed record MoonLook
{
    /// <summary>月球高度角(度;負 = 地平線下)</summary>
    public required double ElevationDeg { get; init; }
    public required double AzimuthDeg { get; init; }
    /// <summary>照明比例 0(新月)–1(滿月)</summary>
    public required double Phase { get; init; }
    /// <summary>晴空月光相對強度 0–1(滿月天頂 = 1 ≈ 0.25 lux;地平線下 0;已含高度角與大氣消光)</summary>
    public required double Intensity { get; init; }
    /// <summary>經霧/雨衰減後的月光 0–1(月光 DirectionalLight 的強度依此)</summary>
    public required double Light { get; init; }
    /// <summary>月球是否在地平線上</summary>
    public bool AboveHorizon => ElevationDeg > 0.0;
}

/// <summary>
/// 狀態 → 環境呈現:能見度 → 霧密度(距離 visibility 處透射率 5 %:density = −ln(0.05)/visibility);
/// 真風 → 海況(Douglas,依 Beaufort 近似)與波高;radarEnvironment 雨區(多邊形或圓)含自船位置 → 雨;
/// 日夜:啟動參數 daynight 為 day/dusk/night 時固定;auto 時依 state.utc(核心:情境 startTimeUtc 或載入時的實際時鐘 + t)
/// 與自船經緯度算太陽高度(簡化天文公式,誤差約 1°);state 無 utc(舊核心/Python 未設)時退回 --start-utc + 模擬秒數,兩者皆無則白天。
/// 月光:auto 時依同一 UTC 以 <see cref="MoonModel"/> 算月球高度/方位/月相與相對照度;night 固定模式月球固定在高度 45°、方位 135°,
/// 月相由顯示幀 UTC(state.utc 或 --start-utc + t)推得、皆無時 0.5;day/dusk 固定模式月球在地平線下(無月光)。
/// 月光經霧(能見度)與雨衰減(<see cref="MoonlightWeatherFactor"/>),夜空亮度 = 星光底值 + 月光。
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
        var (elev, az) = SunPosition(mode, s.Utc, startUtc, s.T, s.Pos.Lat, s.Pos.Lon);
        var daylight = Math.Clamp((elev + 6.0) / 12.0, 0.0, 1.0); // −6°(民用曙暮光)→ 0,+6° → 1
        var rain = RainAt(radar, s.Pos.X, s.Pos.Y);
        var moon = MoonLookFor(mode, s.Utc, startUtc, s.T, s.Pos.Lat, s.Pos.Lon, visNm, rain);
        // 情境有浪(state.waves)時波高與浪向取核心的 Hs 與來向(去向 = 來向 + 180°);否則維持由風速推得的海況波高與風向推得的浪向
        var waveHeight = s.Waves is { } w ? Math.Max(0.0, w.SignificantHeightM) : WaveHeightFromSeaState(sea);
        var waveDir = s.Waves is { } wd ? Units.NormalizeHeadingDeg(wd.DirectionDeg + 180.0) : Units.NormalizeHeadingDeg(s.Wind.TrueDir + 180.0);
        return new EnvironmentLook
        {
            SunElevationDeg = elev,
            SunAzimuthDeg = az,
            Daylight = daylight,
            NavigationLightsOn = elev < 0.0,
            FogDensity = fog,
            VisibilityM = visM,
            Rain = rain,
            SeaState = sea,
            WaveHeightM = waveHeight,
            WaveDirectionDeg = waveDir,
            WindSpeedKn = wind,
            Moon = moon,
            NightSkyBrightness = NightSkyBrightnessFor(daylight, moon.Light),
        };
    }

    /// <summary>night 固定模式的月球高度/方位(度):固定高掛東南方,讓夜間情境有月光與陰影方向。</summary>
    public const double FixedNightMoonElevationDeg = 45.0;
    public const double FixedNightMoonAzimuthDeg = 135.0;
    /// <summary>無法推得月相時(night 固定模式且無任何 UTC)的預設照明比例</summary>
    public const double DefaultMoonPhase = 0.5;
    /// <summary>夜空亮度的星光/氣輝底值(無月時仍可辨海天線)</summary>
    public const double StarlightSkyBrightness = 0.08;

    /// <summary>
    /// 月球呈現參數:auto 模式依顯示幀 UTC(state.utc → --start-utc + t)算位置與月相,無 UTC 時(白天)月球在地平線下;
    /// day/dusk 固定模式月球在地平線下;night 固定模式月球固定於 45°/135°,月相由 UTC 推得、皆無時 <see cref="DefaultMoonPhase"/>。
    /// </summary>
    public static MoonLook MoonLookFor(DayNightMode mode, string? stateUtc, DateTimeOffset? startUtc, double simSeconds, double latDeg, double lonDeg, double visibilityNm, double rain)
    {
        var utc = ResolveUtc(stateUtc, startUtc, simSeconds);
        double elev, az, phase;
        switch (mode)
        {
            case DayNightMode.Day:
                (elev, az, phase) = (-45.0, 0.0, utc is { } du ? MoonModel.Compute(du, latDeg, lonDeg).IlluminatedFraction : DefaultMoonPhase);
                break;
            case DayNightMode.Dusk:
                (elev, az, phase) = (-10.0, 80.0, utc is { } ku ? MoonModel.Compute(ku, latDeg, lonDeg).IlluminatedFraction : DefaultMoonPhase);
                break;
            case DayNightMode.Night:
                (elev, az, phase) = (FixedNightMoonElevationDeg, FixedNightMoonAzimuthDeg, utc is { } nu ? MoonModel.Compute(nu, latDeg, lonDeg).IlluminatedFraction : DefaultMoonPhase);
                break;
            default:
                if (utc is { } au)
                {
                    var m = MoonModel.Compute(au, latDeg, lonDeg);
                    (elev, az, phase) = (m.ElevationDeg, m.AzimuthDeg, m.IlluminatedFraction);
                }
                else (elev, az, phase) = (-45.0, 0.0, DefaultMoonPhase); // 無 UTC → 白天,無月光
                break;
        }
        var intensity = MoonModel.RelativeIlluminance(elev, phase);
        return new MoonLook
        {
            ElevationDeg = elev,
            AzimuthDeg = az,
            Phase = phase,
            Intensity = intensity,
            Light = intensity * MoonlightWeatherFactor(visibilityNm, rain),
        };
    }

    /// <summary>
    /// 霧/雨對月光的衰減 0–1(示意,非輻射傳輸):能見度 ≥ 5 nm 不衰減,線性降到 0.5 nm 以下的 0.1(霧中月光散成微弱天光);
    /// 雨強度 1 再乘 0.4(雲層遮蔽)。
    /// </summary>
    public static double MoonlightWeatherFactor(double visibilityNm, double rain)
    {
        var fog = Math.Clamp((visibilityNm - 0.5) / 4.5, 0.0, 1.0);
        var fogFactor = 0.1 + 0.9 * fog;
        var rainFactor = 1.0 - 0.6 * Math.Clamp(rain, 0.0, 1.0);
        return fogFactor * rainFactor;
    }

    /// <summary>夜空亮度:(1 − 日光) × (星光底值 + (1 − 底值) × 衰減後月光)。</summary>
    public static double NightSkyBrightnessFor(double daylight, double moonLight)
    {
        var night = 1.0 - Math.Clamp(daylight, 0.0, 1.0);
        var sky = StarlightSkyBrightness + (1.0 - StarlightSkyBrightness) * Math.Clamp(moonLight, 0.0, 1.0);
        return night * sky;
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

    /// <summary>太陽高度與方位(度)。手動模式回固定值:day 45°、dusk −3°、night −30°;auto 無 --start-utc 時為白天。</summary>
    public static (double ElevationDeg, double AzimuthDeg) SunPosition(DayNightMode mode, DateTimeOffset? startUtc, double simSeconds, double latDeg, double lonDeg)
        => SunPosition(mode, null, startUtc, simSeconds, latDeg, lonDeg);

    /// <summary>
    /// 太陽高度與方位(度):auto 時優先用 state.utc(<paramref name="stateUtc"/>,ISO 8601),無或不可解析時退回 --start-utc + 模擬秒數,
    /// 兩者皆無則白天(45°)。
    /// </summary>
    public static (double ElevationDeg, double AzimuthDeg) SunPosition(DayNightMode mode, string? stateUtc, DateTimeOffset? startUtc, double simSeconds, double latDeg, double lonDeg)
    {
        switch (mode)
        {
            case DayNightMode.Day: return (45.0, 180.0);
            case DayNightMode.Dusk: return (-3.0, 260.0);
            case DayNightMode.Night: return (-30.0, 0.0);
        }
        var t = ResolveUtc(stateUtc, startUtc, simSeconds);
        return t is { } utc ? SolarPosition(utc, latDeg, lonDeg) : (45.0, 180.0);
    }

    /// <summary>顯示幀的 UTC:state.utc 可解析者優先;否則 --start-utc + 模擬秒數;皆無為 null。</summary>
    public static DateTimeOffset? ResolveUtc(string? stateUtc, DateTimeOffset? startUtc, double simSeconds)
    {
        if (TryParseUtc(stateUtc, out var fromState)) return fromState;
        return startUtc?.ToUniversalTime().AddSeconds(simSeconds);
    }

    /// <summary>ISO 8601(無時區視為 UTC)→ DateTimeOffset(UTC)。</summary>
    public static bool TryParseUtc(string? iso, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(iso)) return false;
        if (!DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto)) return false;
        utc = dto.ToUniversalTime();
        return true;
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
