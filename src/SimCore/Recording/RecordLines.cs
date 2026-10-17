using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Parameters;

namespace SimosaBRM.SimCore.Recording;

/// <summary>
/// JSON Lines 紀錄檔的各行型別(規劃書第 5.3 節:輸入事件逐 tick、狀態 25 Hz、快照每 10 s;日後改 MCAP 容器)。
/// 每行以 <c>kind</c> 區分;最後一行 footer 含前面所有位元組的 SHA-256,供完整性檢查。
/// </summary>
public static class RecordKinds
{
    public const string Header = "header";
    public const string Input = "input";
    public const string State = "state";
    public const string Snapshot = "snapshot";
    /// <summary>核心事件(交通、時間表、評估、教官、引擎;SimEvent);目標船狀態在 state 行的 targets[]、目標指令在 input 行</summary>
    public const string Event = "event";
    /// <summary>評估摘要(footer 之前;endExercise 時也寫一行;講評站取最後一行)</summary>
    public const string Summary = "summary";
    public const string Footer = "footer";
}

public sealed record RecordHeader
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Header;
    /// <summary>2:新增 parameterSet,scenario 為解析後的完整情境(含 timeline/route/zones/assessment/briefing)</summary>
    [JsonPropertyName("version")] public int Version { get; init; } = 2;
    [JsonPropertyName("createdUtc")] public required DateTime CreatedUtc { get; init; }
    [JsonPropertyName("shipId")] public required string ShipId { get; init; }
    [JsonPropertyName("shipName")] public string? ShipName { get; init; }
    [JsonPropertyName("loading")] public required LoadingCondition Loading { get; init; }
    [JsonPropertyName("seed")] public required int Seed { get; init; }
    [JsonPropertyName("dt")] public required double Dt { get; init; }
    [JsonPropertyName("dynamics")] public required string Dynamics { get; init; }
    /// <summary>完整情境(重播不依賴原 YAML 檔;參數引用已代入)</summary>
    [JsonPropertyName("scenario")] public required Scenario.Scenario Scenario { get; init; }
    /// <summary>參數集摘要與解析清單(無參數引用時省略;重播不需參數集檔案)</summary>
    [JsonPropertyName("parameterSet")] public ParameterSetInfo? ParameterSet { get; init; }
}

public sealed record RecordInput
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Input;
    /// <summary>套用指令時的 tick(重播時在該 tick 推進前送入)</summary>
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    [JsonPropertyName("command")] public required SimCommand Command { get; init; }
}

public sealed record RecordState
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.State;
    [JsonPropertyName("state")] public required OwnShipState State { get; init; }
}

public sealed record RecordSnapshot
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Snapshot;
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    [JsonPropertyName("snapshot")] public required EngineSnapshot Snapshot { get; init; }
}

/// <summary>衍生事件行(規劃書第 5.3 節「衍生事件(警報、CPA、UKC)」):交通、時間表、評估、教官與引擎事件,讀回一律為 SimEvent。</summary>
public sealed record RecordEvent
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Event;
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    [JsonPropertyName("event")] public required SimEvent Event { get; init; }
}

/// <summary>評估摘要行(情境引擎設計第 4.8 節);計入 footer 的 sha256。</summary>
public sealed record RecordSummary
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Summary;
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    [JsonPropertyName("summary")] public required AssessmentSummary Summary { get; init; }
}

public sealed record RecordFooter
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Footer;
    [JsonPropertyName("finalTick")] public required long FinalTick { get; init; }
    /// <summary>結束時的狀態鏈雜湊(hex)</summary>
    [JsonPropertyName("stateHash")] public required string StateHash { get; init; }
    [JsonPropertyName("lines")] public required long Lines { get; init; }
    [JsonPropertyName("inputs")] public required long Inputs { get; init; }
    /// <summary>footer 之前所有行(含換行)的 SHA-256(hex)</summary>
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    /// <summary>summary 行數(選填)</summary>
    [JsonPropertyName("summaryLines")] public long? SummaryLines { get; init; }
}
