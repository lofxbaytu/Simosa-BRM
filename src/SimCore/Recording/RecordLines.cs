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
    /// <summary>參與者與休息時間確認(紀錄已開啟後收到 sessionInfo 指令時追加;不進狀態雜湊鏈;RecordSession)</summary>
    public const string Session = "session";
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
    /// <summary>
    /// 操縱量測比較用的試俥/海報目標值(data/ships/&lt;ID&gt;/trial_targets.json 全文;只在情境使用操縱量測時寫入)。重播優先使用此內容,
    /// <c>--rescore</c> 才讀目前檔案(與參數集重新評分一致);舊紀錄無此欄位時重播退回讀目前檔案並警告。
    /// </summary>
    [JsonPropertyName("manoeuvreTargets")] public RecordManoeuvreTargets? ManoeuvreTargets { get; init; }
    /// <summary>
    /// 參與者(規劃書第 5.5 節;教官站 sessionInfo 指令在紀錄開啟前送達,或情境切換另開新檔時由主控程序帶入;version 2 選填欄位)。
    /// 紀錄開啟後才送達者寫成 session 行(<see cref="RecordSession"/>)。
    /// </summary>
    [JsonPropertyName("participants")] public IReadOnlyList<RecordParticipant>? Participants { get; init; }
    /// <summary>練習前休息時間核對確認(同上)</summary>
    [JsonPropertyName("restHoursConfirmation")] public RecordRestHoursConfirmation? RestHoursConfirmation { get; init; }
    /// <summary>課程資訊(同上;選填)</summary>
    [JsonPropertyName("course")] public RecordCourse? Course { get; init; }

    /// <summary>標頭內的參與者資訊(無 participants 與 restHoursConfirmation 時為 null)。</summary>
    [JsonIgnore] public RecordSessionInfo? SessionInfo => Participants is null && RestHoursConfirmation is null ? null
        : new RecordSessionInfo { Participants = Participants ?? Array.Empty<RecordParticipant>(), RestHoursConfirmation = RestHoursConfirmation, Course = Course };
}

/// <summary>紀錄標頭的試俥目標值(<see cref="RecordHeader.ManoeuvreTargets"/>)。</summary>
public sealed record RecordManoeuvreTargets
{
    [JsonPropertyName("shipId")] public required string ShipId { get; init; }
    /// <summary>情境裝載(目標值只與同船同裝載的量測比較)</summary>
    [JsonPropertyName("loading")] public required LoadingCondition Loading { get; init; }
    /// <summary>相對專案根目錄的路徑(例如 data/ships/FSB1/trial_targets.json)</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }
    /// <summary>檔案原始位元組的 SHA-256(小寫 hex;= summary.manoeuvres[].targetsSha256)</summary>
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    /// <summary>檔案內容(JSON)</summary>
    [JsonPropertyName("content")] public required System.Text.Json.JsonElement Content { get; init; }
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
    /// <summary>關檔時的牆鐘 UTC(選填;紀錄包以它為練習結束時間,倍速/凍結時與模擬時長不同;docs/design/packaging-sync.md 第 7 節)</summary>
    [JsonPropertyName("closedUtc")] public DateTime? ClosedUtc { get; init; }
}
