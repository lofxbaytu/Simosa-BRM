using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.Sensors.Radar;

/// <summary>雷達 JSON 訊息(src/Contracts/radar.schema.json);欄位名以 JsonPropertyName 固定,null 省略。</summary>
public static class RadarJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static byte[] Utf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static string Text<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>四捨五入(輸出用;避免 JSON 數字過長)。</summary>
    internal static double R(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
    internal static double? R(double? v, int digits) => v is { } x ? Math.Round(x, digits, MidpointRounding.AwayFromZero) : null;
}

public sealed record OwnShipReport(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon,
    [property: JsonPropertyName("heading")] double Heading,
    [property: JsonPropertyName("cog")] double Cog,
    [property: JsonPropertyName("sog")] double Sog);

public sealed record PastPosition(
    [property: JsonPropertyName("t")] double T,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon);

/// <summary>追蹤目標的 AIS 關聯(tracks 訊息 targets[].ais;候選或已關聯時提供)。</summary>
public sealed record AisTrackReport(
    [property: JsonPropertyName("mmsi")] int Mmsi,
    [property: JsonPropertyName("name")] string Name,
    /// <summary>已關聯(連續 N 次通過門檻);false = 候選中或保持期(差異過大)</summary>
    [property: JsonPropertyName("associated")] bool Associated,
    /// <summary>ARPA 預測位置與 AIS 報告位置的距離(m;aisError 注入時可見差異)</summary>
    [property: JsonPropertyName("positionOffset_m")] double PositionOffsetM);

/// <summary>追蹤目標(tracks 訊息 targets[])。向量與 CPA 只在 state = tracking 時提供。</summary>
public sealed record TrackReport
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("acquisition")] public required string Acquisition { get; init; }
    [JsonPropertyName("age_s")] public required double AgeS { get; init; }
    [JsonPropertyName("stable")] public required bool Stable { get; init; }
    [JsonPropertyName("manoeuvring")] public required bool Manoeuvring { get; init; }
    [JsonPropertyName("dangerous")] public required bool Dangerous { get; init; }
    [JsonPropertyName("swapRisk")] public required bool SwapRisk { get; init; }
    [JsonPropertyName("misses")] public required int Misses { get; init; }
    [JsonPropertyName("x")] public required double X { get; init; }
    [JsonPropertyName("y")] public required double Y { get; init; }
    [JsonPropertyName("lat")] public required double Lat { get; init; }
    [JsonPropertyName("lon")] public required double Lon { get; init; }
    [JsonPropertyName("range_nm")] public required double RangeNm { get; init; }
    [JsonPropertyName("bearing_deg")] public required double BearingDeg { get; init; }
    [JsonPropertyName("relBearing_deg")] public required double RelBearingDeg { get; init; }
    [JsonPropertyName("course_deg")] public double? CourseDeg { get; init; }
    [JsonPropertyName("speed_kn")] public double? SpeedKn { get; init; }
    [JsonPropertyName("relCourse_deg")] public double? RelCourseDeg { get; init; }
    [JsonPropertyName("relSpeed_kn")] public double? RelSpeedKn { get; init; }
    [JsonPropertyName("cpa_nm")] public double? CpaNm { get; init; }
    [JsonPropertyName("tcpa_min")] public double? TcpaMin { get; init; }
    [JsonPropertyName("bcr_nm")] public double? BcrNm { get; init; }
    [JsonPropertyName("bct_min")] public double? BctMin { get; init; }
    [JsonPropertyName("trialCpa_nm")] public double? TrialCpaNm { get; init; }
    [JsonPropertyName("trialTcpa_min")] public double? TrialTcpaMin { get; init; }
    [JsonPropertyName("past")] public IReadOnlyList<PastPosition>? Past { get; init; }
    /// <summary>AIS 關聯(無候選時省略)</summary>
    [JsonPropertyName("ais")] public AisTrackReport? Ais { get; init; }
}

/// <summary>
/// 純 AIS 目標(tracks 訊息 aisTargets[];radar.schema.json aisTarget):本週期有 AIS 報告、未被任何追蹤中的 ARPA 目標關聯者
/// (IEC 62288 AIS 目標符號 △;PPI 以三角形顯示、可點選擷取)。位置/向量為 AIS 報告值(含注入的 aisError);CPA/TCPA 以自船指示 COG/SOG 計算。
/// </summary>
public sealed record AisTargetReport
{
    [JsonPropertyName("mmsi")] public required int Mmsi { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("x")] public required double X { get; init; }
    [JsonPropertyName("y")] public required double Y { get; init; }
    [JsonPropertyName("lat")] public required double Lat { get; init; }
    [JsonPropertyName("lon")] public required double Lon { get; init; }
    [JsonPropertyName("range_nm")] public required double RangeNm { get; init; }
    [JsonPropertyName("bearing_deg")] public required double BearingDeg { get; init; }
    [JsonPropertyName("relBearing_deg")] public required double RelBearingDeg { get; init; }
    [JsonPropertyName("cog_deg")] public required double CogDeg { get; init; }
    [JsonPropertyName("sog_kn")] public required double SogKn { get; init; }
    [JsonPropertyName("heading_deg")] public required double HeadingDeg { get; init; }
    [JsonPropertyName("cpa_nm")] public required double CpaNm { get; init; }
    [JsonPropertyName("tcpa_min")] public required double TcpaMin { get; init; }
    /// <summary>CPA < cpaLimit 且 0 ≤ TCPA ≤ tcpaLimit(與 ARPA 相同規則)</summary>
    [JsonPropertyName("dangerous")] public required bool Dangerous { get; init; }
    /// <summary>有 ARPA 目標以此 MMSI 為候選(未關聯)時其編號;純 AIS 無候選時省略</summary>
    [JsonPropertyName("candidateTrackId")] public int? CandidateTrackId { get; init; }
}

public sealed record TrialReport(
    [property: JsonPropertyName("course")] double Course,
    [property: JsonPropertyName("speed")] double Speed,
    [property: JsonPropertyName("delay_min")] double DelayMin);

/// <summary>追蹤清單(1 Hz 模擬時間)。</summary>
public sealed record TracksMessage
{
    [JsonPropertyName("type")] public string Type => "tracks";
    [JsonPropertyName("radar")] public required string Radar { get; init; }
    [JsonPropertyName("t")] public required double T { get; init; }
    [JsonPropertyName("scan")] public required long Scan { get; init; }
    [JsonPropertyName("transmitting")] public required bool Transmitting { get; init; }
    [JsonPropertyName("ownShip")] public required OwnShipReport OwnShip { get; init; }
    [JsonPropertyName("cpaLimit_nm")] public required double CpaLimitNm { get; init; }
    [JsonPropertyName("tcpaLimit_min")] public required double TcpaLimitMin { get; init; }
    [JsonPropertyName("trial")] public TrialReport? Trial { get; init; }
    [JsonPropertyName("targets")] public required IReadOnlyList<TrackReport> Targets { get; init; }
    /// <summary>純 AIS 目標(未被追蹤中的 ARPA 目標關聯;無則省略)</summary>
    [JsonPropertyName("aisTargets")] public IReadOnlyList<AisTargetReport>? AisTargets { get; init; }
}

public sealed record ShadowSectorReport(
    [property: JsonPropertyName("from")] double From,
    [property: JsonPropertyName("to")] double To,
    [property: JsonPropertyName("attenuation_dB")] double AttenuationDb);

public sealed record GuardZoneReport(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("inner_nm")] double InnerNm,
    [property: JsonPropertyName("outer_nm")] double OuterNm,
    [property: JsonPropertyName("from")] double From,
    [property: JsonPropertyName("to")] double To,
    [property: JsonPropertyName("relative")] bool Relative);

public sealed record FrameFormatReport(
    [property: JsonPropertyName("magic")] string Magic,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("headerBytes")] int HeaderBytes);

/// <summary>雷達狀態(連線時與設定變更時送出)。</summary>
public sealed record StatusMessage
{
    [JsonPropertyName("type")] public string Type => "status";
    [JsonPropertyName("radar")] public required string Radar { get; init; }
    [JsonPropertyName("band")] public required string Band { get; init; }
    [JsonPropertyName("frequency_GHz")] public required double FrequencyGHz { get; init; }
    [JsonPropertyName("transmitting")] public required bool Transmitting { get; init; }
    [JsonPropertyName("standby")] public required bool Standby { get; init; }
    [JsonPropertyName("failed")] public required bool Failed { get; init; }
    [JsonPropertyName("degraded")] public required bool Degraded { get; init; }
    [JsonPropertyName("headingValid")] public required bool HeadingValid { get; init; }
    [JsonPropertyName("range_nm")] public required double RangeNm { get; init; }
    [JsonPropertyName("rangeScales")] public required IReadOnlyList<double> RangeScales { get; init; }
    [JsonPropertyName("pulse")] public required string Pulse { get; init; }
    [JsonPropertyName("pulseLength_m")] public required double PulseLengthM { get; init; }
    [JsonPropertyName("gain")] public required double Gain { get; init; }
    [JsonPropertyName("sea")] public required double Sea { get; init; }
    [JsonPropertyName("rain")] public required double Rain { get; init; }
    [JsonPropertyName("tune")] public required double Tune { get; init; }
    [JsonPropertyName("autoTune")] public required bool AutoTune { get; init; }
    [JsonPropertyName("rpm")] public required double Rpm { get; init; }
    [JsonPropertyName("spokes")] public required int Spokes { get; init; }
    [JsonPropertyName("cells")] public required int Cells { get; init; }
    [JsonPropertyName("beamwidth_deg")] public required double BeamwidthDeg { get; init; }
    [JsonPropertyName("antennaHeight_m")] public required double AntennaHeightM { get; init; }
    [JsonPropertyName("antennaFwd_m")] public required double AntennaFwdM { get; init; }
    [JsonPropertyName("antennaStbd_m")] public required double AntennaStbdM { get; init; }
    [JsonPropertyName("shadowSectors")] public required IReadOnlyList<ShadowSectorReport> ShadowSectors { get; init; }
    [JsonPropertyName("guardZone")] public required GuardZoneReport GuardZone { get; init; }
    [JsonPropertyName("trial")] public TrialReport? Trial { get; init; }
    [JsonPropertyName("cpaLimit_nm")] public required double CpaLimitNm { get; init; }
    [JsonPropertyName("tcpaLimit_min")] public required double TcpaLimitMin { get; init; }
    [JsonPropertyName("arpaRange_nm")] public required double ArpaRangeNm { get; init; }
    [JsonPropertyName("maxTracks")] public required int MaxTracks { get; init; }
    [JsonPropertyName("seaState")] public required double SeaState { get; init; }
    [JsonPropertyName("rainCells")] public required int RainCells { get; init; }
    [JsonPropertyName("landPolygons")] public required int LandPolygons { get; init; }
    [JsonPropertyName("frame")] public required FrameFormatReport Frame { get; init; }
}

/// <summary>控制指令回應。</summary>
public sealed record AckMessage
{
    [JsonPropertyName("type")] public string Type => "ack";
    [JsonPropertyName("radar")] public required string Radar { get; init; }
    [JsonPropertyName("cmd")] public required string Cmd { get; init; }
    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("reqId")] public JsonElement? ReqId { get; init; }
    /// <summary>acquire 成功時的新目標編號</summary>
    [JsonPropertyName("id")] public int? Id { get; init; }
    /// <summary>實際採用的值(例如量程換成最近的標準量程)</summary>
    [JsonPropertyName("value")] public double? Value { get; init; }
}

public sealed record EventBody(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("t")] double T,
    [property: JsonPropertyName("targetId")] int? TargetId,
    [property: JsonPropertyName("message")] string Message);

public sealed record EventMessage
{
    [JsonPropertyName("type")] public string Type => "event";
    [JsonPropertyName("radar")] public required string Radar { get; init; }
    [JsonPropertyName("event")] public required EventBody Event { get; init; }
}
