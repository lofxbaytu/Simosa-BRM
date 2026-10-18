using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Geo;

namespace SimosaBRM.SimCore.Scenario;

// 情境雷達環境 environment.radar(選填;src/Contracts/scenario.schema.json;設計文件 docs/design/radar-arpa.md 第 2.1 節)。
// 核心不影響物理:載入時檢查結構,引擎以情境原點換算為本地 ENU,於 state.radarEnvironment 廣播與記錄(只在關鍵幀:載入/重設/倒帶後第一幀與每秒一次,其他幀省略 = 不變;情境未給時省略,舊情境狀態不變)。
// 內容只來自情境(紀錄 header 含完整定義),整個練習期間不變,故不進快照與雜湊(重播時由 header 重建即逐位元相同)。

/// <summary>情境雷達環境:雨區(多邊形或圓)與海況覆寫。</summary>
public sealed class ScenarioRadarEnvironment
{
    /// <summary>雨區</summary>
    public List<ScenarioRainArea> RainAreas { get; set; } = new();
    /// <summary>雷達海雜波用海況(Douglas 0–9;省略 = 雷達服務依真風速推估)</summary>
    public double? SeaState { get; set; }
}

/// <summary>一個雨區:多邊形(polygon)或圓(center + radius_nm)二擇一;強度 0–1。</summary>
public sealed class ScenarioRainArea
{
    public string? Id { get; set; }
    /// <summary>雨區多邊形({lat, lon} 或 {x, y};≥ 3 點)</summary>
    public List<ScenarioPosition>? Polygon { get; set; }
    /// <summary>圓形雨區中心</summary>
    public ScenarioPosition? Center { get; set; }
    /// <summary>圓形雨區半徑(nm)</summary>
    [JsonPropertyName("radius_nm")] public double? RadiusNm { get; set; }
    /// <summary>降雨強度 0–1(雷達服務以 <see cref="ScenarioRadarEnvironmentBuilder.MaxRainRateMmH"/> × intensity mm/h 計算)</summary>
    public double Intensity { get; set; }
}

/// <summary>結構檢查(Scenario.StructuralIssues 呼叫)與 state.radarEnvironment 建立(引擎初始化時呼叫一次)。</summary>
public static class ScenarioRadarEnvironmentBuilder
{
    /// <summary>intensity = 1 對應的降雨率(mm/h;大雨)。intensity 0.25 ≈ 中雨 4 mm/h。</summary>
    public const double MaxRainRateMmH = 16.0;

    public static void CheckStructure(Scenario sc, Action<string, string> error)
    {
        if (sc.Environment.Radar is not { } r) return;
        if (r.SeaState is { } ss && (double.IsNaN(ss) || ss < 0 || ss > 9)) error("environment.radar.seaState", "seaState 須在 0–9(Douglas)");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < r.RainAreas.Count; i++)
        {
            var a = r.RainAreas[i];
            var path = $"environment.radar.rainAreas[{i}]";
            if (a.Id is { Length: > 0 } id && !ids.Add(id)) error(path, $"雨區 id 重複:{id}");
            if (double.IsNaN(a.Intensity) || a.Intensity < 0 || a.Intensity > 1) error(path, "intensity 須在 0–1");
            var hasPolygon = a.Polygon is not null;
            var hasCircle = a.Center is not null || a.RadiusNm is not null;
            if (hasPolygon == hasCircle) { error(path, "雨區需為 polygon 或 center + radius_nm(二擇一)"); continue; }
            if (hasPolygon)
            {
                if (a.Polygon!.Count < 3) error(path, "polygon 至少需要 3 點");
                if (a.Polygon.Any(p => !Valid(p))) error(path, "polygon 點需為 {lat, lon} 或 {x, y}");
            }
            else
            {
                if (a.Center is null || !Valid(a.Center)) error(path, "center 需為 {lat, lon} 或 {x, y}");
                if (a.RadiusNm is not > 0) error(path, "radius_nm 必須為正");
            }
        }
    }

    private static bool Valid(ScenarioPosition p) => (p.Lat is not null && p.Lon is not null) || (p.X is not null && p.Y is not null);

    /// <summary>state.radarEnvironment(情境無 environment.radar 時 null):座標換算為本地 ENU(並附經緯度)、強度換算降雨率。</summary>
    public static RadarEnvironmentState? Build(Scenario sc, LocalTangentPlane proj)
    {
        if (sc.Environment.Radar is not { } r) return null;
        GeoPosition Pos(ScenarioPosition p)
        {
            var (x, y) = Scenario.Resolve(proj, p);
            var (lat, lon) = p.Lat is { } la && p.Lon is { } lo ? (la, lo) : proj.ToGeodetic(x, y);
            return new GeoPosition { Lat = lat, Lon = lon, X = x, Y = y };
        }
        var areas = r.RainAreas.Select((a, i) => new RadarRainAreaState
        {
            Id = string.IsNullOrEmpty(a.Id) ? $"rain{i + 1}" : a.Id,
            Intensity = a.Intensity,
            RateMmH = MaxRainRateMmH * a.Intensity,
            Polygon = a.Polygon?.Select(Pos).ToArray(),
            Center = a.Polygon is null && a.Center is { } c ? Pos(c) : null,
            RadiusM = a.Polygon is null && a.RadiusNm is { } rn ? rn * Units.MetresPerNauticalMile : null,
        }).ToArray();
        return new RadarEnvironmentState { RainAreas = areas, SeaState = r.SeaState };
    }
}

/// <summary>
/// state.radarEnvironment(state.schema.json;C# 產生,Python 不輸出):情境 environment.radar 換算後的雨區(本地 ENU m + 經緯度)與海況覆寫。
/// 雷達服務(Sensors.Radar RadarScene/RadarEnvironment)據以產生雨雜波與雨衰減。
/// </summary>
public sealed record RadarEnvironmentState
{
    [JsonPropertyName("rainAreas")] public required IReadOnlyList<RadarRainAreaState> RainAreas { get; init; }
    /// <summary>海況覆寫(Douglas 0–9;省略 = 依真風速推估)</summary>
    [JsonPropertyName("seaState")] public double? SeaState { get; init; }
}

/// <summary>一個雨區:polygon 或 center + radius_m;intensity 0–1 與換算的降雨率 rate_mmh。</summary>
public sealed record RadarRainAreaState
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("intensity")] public required double Intensity { get; init; }
    /// <summary>降雨率(mm/h)= 16 × intensity</summary>
    [JsonPropertyName("rate_mmh")] public required double RateMmH { get; init; }
    [JsonPropertyName("polygon")] public IReadOnlyList<GeoPosition>? Polygon { get; init; }
    [JsonPropertyName("center")] public GeoPosition? Center { get; init; }
    [JsonPropertyName("radius_m")] public double? RadiusM { get; init; }
}
