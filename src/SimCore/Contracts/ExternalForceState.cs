using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Contracts;

// 拖船、錨、泊位的狀態廣播(state.schema.json tugs[] / anchors[] / berth;設計文件 docs/design/physics-tugs-anchors-mooring.md 第 5 節)。
// 力以噸力(t = 9.80665 kN)、方向以度(相對船艏、順時針)、碰墊反力以 kN 表示;C# 核心產生,Python 參考實作不輸出。

/// <summary>拖船狀態(state.tugs[])。</summary>
public sealed record TugStatusState
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    /// <summary>命令模式 push | pull | standby | free | unavailable</summary>
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    /// <summary>standby | connecting | repositioning | working | free | parted | unavailable(已離開,之後指令以 tugUnavailable 拒絕)</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }
    /// <summary>實際出力(t)</summary>
    [JsonPropertyName("force_t")] public required double ForceT { get; init; }
    /// <summary>實際力方向(相對船艏,度 0–360;0 = 向前、90 = 向右舷)</summary>
    [JsonPropertyName("direction_deg")] public required double DirectionDeg { get; init; }
    [JsonPropertyName("directionOrder_deg")] public double? DirectionOrderDeg { get; init; }
    [JsonPropertyName("power_pct")] public double? PowerPct { get; init; }
    /// <summary>自船速度造成的效能(0–1)</summary>
    [JsonPropertyName("efficiency")] public double? Efficiency { get; init; }
    [JsonPropertyName("bollardPull_t")] public double? BollardPullT { get; init; }
    [JsonPropertyName("lineParted")] public bool? LineParted { get; init; }
    /// <summary>示意位置(視景/教官站顯示;非物理)</summary>
    [JsonPropertyName("pos")] public GeoPosition? Pos { get; init; }
}

/// <summary>錨狀態(state.anchors[])。</summary>
public sealed record AnchorStatusState
{
    /// <summary>port | starboard</summary>
    [JsonPropertyName("side")] public required string Side { get; init; }
    /// <summary>stowed | running | holding | dragging | heaving | aweigh</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }
    /// <summary>送出鏈長(m)</summary>
    [JsonPropertyName("length_m")] public required double LengthM { get; init; }
    [JsonPropertyName("length_shackles")] public double? LengthShackles { get; init; }
    [JsonPropertyName("maxLength_m")] public double? MaxLengthM { get; init; }
    /// <summary>錨鏈孔處張力(t)</summary>
    [JsonPropertyName("tension_t")] public required double TensionT { get; init; }
    [JsonPropertyName("horizontalTension_t")] public double? HorizontalTensionT { get; init; }
    /// <summary>躺底鏈長(m)</summary>
    [JsonPropertyName("onBottom_m")] public required double OnBottomM { get; init; }
    /// <summary>目前抓地力(t;錨 + 躺底鏈摩擦)</summary>
    [JsonPropertyName("holding_t")] public double? HoldingT { get; init; }
    [JsonPropertyName("brake")] public bool? Brake { get; init; }
    [JsonPropertyName("fullyOut")] public bool? FullyOut { get; init; }
    /// <summary>錨位(收存時省略)</summary>
    [JsonPropertyName("position")] public GeoPosition? Position { get; init; }
}

/// <summary>泊位狀態(state.berth)。</summary>
public sealed record BerthStatusState
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    /// <summary>目前受壓的碰墊</summary>
    [JsonPropertyName("contacts")] public required IReadOnlyList<FenderContactState> Contacts { get; init; }
    /// <summary>船體與碼頭硬接觸中</summary>
    [JsonPropertyName("quayContact")] public bool QuayContact { get; init; }
    [JsonPropertyName("lines")] public required IReadOnlyList<MooringLineState> Lines { get; init; }
}

public sealed record FenderContactState
{
    [JsonPropertyName("fenderId")] public required string FenderId { get; init; }
    [JsonPropertyName("deflection_m")] public required double DeflectionM { get; init; }
    [JsonPropertyName("deflection_pct")] public double? DeflectionPct { get; init; }
    [JsonPropertyName("reaction_kN")] public required double ReactionKN { get; init; }
    [JsonPropertyName("overload")] public bool Overload { get; init; }
}

public sealed record MooringLineState
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    /// <summary>stowed | fast | heaving | slacking | letGo | parted</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("tension_t")] public required double TensionT { get; init; }
    [JsonPropertyName("mbl_t")] public double? MblT { get; init; }
    [JsonPropertyName("bollardId")] public string? BollardId { get; init; }
    /// <summary>未受力纜長(m;未繫時省略)</summary>
    [JsonPropertyName("length_m")] public double? LengthM { get; init; }
}
