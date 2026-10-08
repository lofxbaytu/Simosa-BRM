using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Traffic;

/// <summary>交通事件種類(紀錄檔 <c>event</c> 行;規劃書第 5.3 節衍生事件、第 8.3 節避碰指標)。</summary>
public static class TrafficEventKinds
{
    /// <summary>自船與目標外形相交</summary>
    public const string Collision = "collision";
    /// <summary>與目標的 CPA 低於情境門檻(assessment.minCpa_nm)且 TCPA &gt; 0</summary>
    public const string CpaAlarm = "cpaAlarm";
    /// <summary>CPA 警報解除(已通過或 CPA 回到門檻以上)</summary>
    public const string CpaClear = "cpaClear";
    /// <summary>目標出現(觸發條件成立或情境開始)</summary>
    public const string Activated = "targetActivated";
    /// <summary>到達航點</summary>
    public const string Waypoint = "waypointReached";
    /// <summary>腳本步執行</summary>
    public const string Script = "scriptStep";
    /// <summary>COLREG 判定/行動變化</summary>
    public const string Colreg = "colreg";
    /// <summary>聲號</summary>
    public const string Sound = "soundSignal";
    /// <summary>目標新增/刪除/教官覆寫等控制</summary>
    public const string Control = "targetControl";
}

/// <summary>交通事件(紀錄與教官站用;JSON 名稱固定)。</summary>
public sealed record TrafficEvent
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    /// <summary>模擬時間(秒)</summary>
    [JsonPropertyName("t")] public required double T { get; init; }
    [JsonPropertyName("targetId")] public string? TargetId { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("range_nm")] public double? RangeNm { get; init; }
    [JsonPropertyName("cpa_nm")] public double? CpaNm { get; init; }
    [JsonPropertyName("tcpa_min")] public double? TcpaMin { get; init; }
}
