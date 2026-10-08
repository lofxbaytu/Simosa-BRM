using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Contracts;

/// <summary>
/// 狀態廣播中的目標船(state.schema.json <c>targets[]</c>,每幀含所有已出現的目標;規劃書第 5.2 節 Traffic、第 8.3 節避碰指標)。
/// 位置/航向/速度為真值(供教官站、視景、雷達);AIS 報告值(含注入的錯誤)另在 <see cref="Ais"/>,AIS 關閉或不發送時為 null。
/// 距離與 CPA 用浬、TCPA 用分、方位為真方位(度);欄位名依 schema(含 <c>range_nm</c> 等非 camelCase 名稱)。
/// </summary>
public sealed record TargetState
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("mmsi")] public required int Mmsi { get; init; }
    [JsonPropertyName("shipType")] public required string ShipType { get; init; }
    /// <summary>總長(m)</summary>
    [JsonPropertyName("loa")] public required double Loa { get; init; }
    /// <summary>船寬(m)</summary>
    [JsonPropertyName("beam")] public required double Beam { get; init; }
    [JsonPropertyName("draft")] public double? Draft { get; init; }
    [JsonPropertyName("pos")] public required GeoPosition Pos { get; init; }
    /// <summary>航向(度,0–360)</summary>
    [JsonPropertyName("heading")] public required double Heading { get; init; }
    /// <summary>對地航向(度)</summary>
    [JsonPropertyName("cog")] public required double Cog { get; init; }
    /// <summary>對地速度(kn)</summary>
    [JsonPropertyName("sog")] public required double Sog { get; init; }
    /// <summary>對水速度(kn)</summary>
    [JsonPropertyName("stw")] public double? Stw { get; init; }
    /// <summary>迴轉率(度/分,右轉正)</summary>
    [JsonPropertyName("rot")] public required double Rot { get; init; }
    /// <summary>自船至目標的距離(nm)</summary>
    [JsonPropertyName("range_nm")] public required double RangeNm { get; init; }
    /// <summary>自船看目標的真方位(度)</summary>
    [JsonPropertyName("bearing_deg")] public required double BearingDeg { get; init; }
    /// <summary>相對方位(相對自船艏向,度,0–360)</summary>
    [JsonPropertyName("relBearing_deg")] public double? RelBearingDeg { get; init; }
    /// <summary>最近會遇距離(nm)</summary>
    [JsonPropertyName("cpa_nm")] public required double CpaNm { get; init; }
    /// <summary>到達 CPA 的時間(分;負值表示已通過)</summary>
    [JsonPropertyName("tcpa_min")] public required double TcpaMin { get; init; }
    /// <summary>艏越距離(nm;正 = 由艏前通過、負 = 由艉後通過;無交會時 null)</summary>
    [JsonPropertyName("bcr_nm")] public double? BcrNm { get; init; }
    /// <summary>艏越時間(分)</summary>
    [JsonPropertyName("bct_min")] public double? BctMin { get; init; }
    [JsonPropertyName("aisOn")] public required bool AisOn { get; init; }
    /// <summary>燈號類別(powerDriven、anchored、notUnderCommand、…)</summary>
    [JsonPropertyName("lights")] public required string Lights { get; init; }
    /// <summary>目前鳴放中的聲號(oneShort、twoShort、threeShort、fiveShort、prolonged;無時省略)</summary>
    [JsonPropertyName("sound")] public string? Sound { get; init; }
    /// <summary>行為模式(hold、waypoints、scripted、anchored、follow、colreg;manual = 教官覆寫中)</summary>
    [JsonPropertyName("behaviour")] public required string Behaviour { get; init; }
    /// <summary>COLREG 判定與行動(例如 "crossing/giveWay"、"headOn/avoiding"、"crossing/giveWay(ignored)");無時省略</summary>
    [JsonPropertyName("colreg")] public string? Colreg { get; init; }
    /// <summary>AIS 報告(含注入錯誤);AIS 關閉或不發送時 null</summary>
    [JsonPropertyName("ais")] public AisReport? Ais { get; init; }
}

/// <summary>AIS 報告內容(ITU-R M.1371 類型 1 位置報告與類型 5 靜態資料所需欄位;NMEA 閘道據此產生 !AIVDM)。</summary>
public sealed record AisReport
{
    [JsonPropertyName("mmsi")] public required int Mmsi { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("callSign")] public string? CallSign { get; init; }
    [JsonPropertyName("imo")] public int? Imo { get; init; }
    /// <summary>AIS 船型碼(70 貨船、80 油輪、30 漁船、50 引水船、52 拖船…)</summary>
    [JsonPropertyName("shipType")] public required int ShipType { get; init; }
    /// <summary>航行狀態(0 機動航行中、1 錨泊、5 繫泊…)</summary>
    [JsonPropertyName("navStatus")] public required int NavStatus { get; init; }
    [JsonPropertyName("lat")] public required double Lat { get; init; }
    [JsonPropertyName("lon")] public required double Lon { get; init; }
    [JsonPropertyName("cog")] public required double Cog { get; init; }
    [JsonPropertyName("sog")] public required double Sog { get; init; }
    [JsonPropertyName("heading")] public required double Heading { get; init; }
    /// <summary>迴轉率(度/分)</summary>
    [JsonPropertyName("rot")] public required double Rot { get; init; }
    /// <summary>天線至艏、艉、左舷、右舷的距離(m)</summary>
    [JsonPropertyName("dimToBow")] public required double DimToBow { get; init; }
    [JsonPropertyName("dimToStern")] public required double DimToStern { get; init; }
    [JsonPropertyName("dimToPort")] public required double DimToPort { get; init; }
    [JsonPropertyName("dimToStarboard")] public required double DimToStarboard { get; init; }
    [JsonPropertyName("draught")] public required double Draught { get; init; }
    [JsonPropertyName("destination")] public string? Destination { get; init; }
}
