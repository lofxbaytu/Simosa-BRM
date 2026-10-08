using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Traffic;

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
    /// <summary>交通事件(碰撞、CPA 低於門檻、目標出現、航點、COLREG、聲號、目標控制);目標船狀態在 state 行的 targets[]、目標指令在 input 行</summary>
    public const string Event = "event";
    public const string Footer = "footer";
}

public sealed record RecordHeader
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Header;
    [JsonPropertyName("version")] public int Version { get; init; } = 1;
    [JsonPropertyName("createdUtc")] public required DateTime CreatedUtc { get; init; }
    [JsonPropertyName("shipId")] public required string ShipId { get; init; }
    [JsonPropertyName("shipName")] public string? ShipName { get; init; }
    [JsonPropertyName("loading")] public required LoadingCondition Loading { get; init; }
    [JsonPropertyName("seed")] public required int Seed { get; init; }
    [JsonPropertyName("dt")] public required double Dt { get; init; }
    [JsonPropertyName("dynamics")] public required string Dynamics { get; init; }
    /// <summary>完整情境(重播不依賴原 YAML 檔)</summary>
    [JsonPropertyName("scenario")] public required Scenario.Scenario Scenario { get; init; }
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

/// <summary>衍生事件行(規劃書第 5.3 節「衍生事件(警報、CPA、UKC)」;目前為交通事件)。</summary>
public sealed record RecordEvent
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = RecordKinds.Event;
    [JsonPropertyName("tick")] public required long Tick { get; init; }
    [JsonPropertyName("event")] public required TrafficEvent Event { get; init; }
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
}
