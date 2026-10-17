using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Engine;

/// <summary>
/// 引擎完整狀態快照(規劃書第 5.3 節:每 10 s 一次,含亂數狀態與 tick,使重播可跳到任意時間接續)。
/// 以 JSON 序列化;double 以 System.Text.Json 的最短往返格式輸出,還原後位元一致。
/// </summary>
public sealed record EngineSnapshot
{
    /// <summary>
    /// 2:新增 Timeline/Assessment/ParameterSetVersion/Phase/VisibilityNm(皆可 null;Version 1 快照還原時沿用引擎目前狀態);
    /// 3:新增 Sensors/Alerts/Bnwas(感測器層、警報管理、BNWAS;舊快照無此欄位時還原為預設:無偏差、無警報、BNWAS 依情境設定)
    /// </summary>
    public int Version { get; init; } = 3;
    public required string ShipId { get; init; }
    public required LoadingCondition Loading { get; init; }
    public required string ScenarioId { get; init; }
    public required string Dynamics { get; init; }
    public required long Tick { get; init; }
    public required double Dt { get; init; }
    public required MotionSnapshot Motion { get; init; }
    public required ActuatorSnapshot Actuators { get; init; }
    public required TelegraphOrder Telegraph { get; init; }
    public required EnvironmentSnapshot Environment { get; init; }
    public required int RandomSeed { get; init; }
    public required long RandomCount { get; init; }
    public required bool Frozen { get; init; }
    public required double TimeScale { get; init; }
    public required AutopilotSnapshot Autopilot { get; init; }
    public required IReadOnlyList<string> Faults { get; init; }
    public required bool Aground { get; init; }
    public int? StartsRemaining { get; init; }
    /// <summary>目標船與碰撞旗標(交通模組;舊快照無此欄位時沿用引擎目前的目標船)</summary>
    public Traffic.TrafficSnapshot? Traffic { get; init; }
    /// <summary>時間表執行狀態(情境引擎;舊快照無此欄位)</summary>
    public Scenario.TimelineSnapshot? Timeline { get; init; }
    /// <summary>評估模組狀態(情境引擎;舊快照無此欄位)</summary>
    public Assessment.AssessmentSnapshot? Assessment { get; init; }
    /// <summary>重新評分的影子評估模組狀態(只在 --rescore 重播中存在;紀錄的快照行不含)</summary>
    public Assessment.AssessmentSnapshot? RescoreAssessment { get; init; }
    public string? ParameterSetVersion { get; init; }
    /// <summary>操舵模式(舊快照無此欄位:fu、泵 1;自動舵啟用時為 autopilot)</summary>
    public SteeringSnapshot? Steering { get; init; }
    public string? Phase { get; init; }
    public double? VisibilityNm { get; init; }
    /// <summary>感測器層(漂移起點、凍結/保持值、GNSS 偏移;Version 3)</summary>
    public Sensors.SensorState? Sensors { get; init; }
    /// <summary>警報管理(Version 3)</summary>
    public Alerts.AlertManagerState? Alerts { get; init; }
    /// <summary>BNWAS(Version 3)</summary>
    public Alerts.BnwasState? Bnwas { get; init; }
    /// <summary>
    /// 外力模組(拖船出力/方向、錨鏈長/錨位/走錨、碰墊接觸事件、繫纜;設計文件 physics-tugs-anchors-mooring.md)。選填:Version 不變(仍為 3),
    /// 只在情境使用外力時寫入;缺此欄位時還原為情境初始外力狀態(拖船依 initial、錨收存或初始已錨泊、繫纜依 initial)。
    /// </summary>
    public Physics.External.ExternalForceSnapshot? External { get; init; }
    /// <summary>狀態鏈雜湊(hex)</summary>
    public required string StateHash { get; init; }

    public string ToJson() => ContractJson.Serialize(this);
    public static EngineSnapshot FromJson(string json)
        => ContractJson.Deserialize<EngineSnapshot>(json) ?? throw new InvalidDataException("無法解析快照 JSON");
}

public sealed record MotionSnapshot(double U, double V, double R, double X, double Y, double Psi);

/// <summary>
/// 致動器狀態;EngineMode–RpmTau 為主機換向狀態機(MMG 模式;規劃書第 6.2 節「主機/推進控制」),
/// 舊快照未含時取預設值(暫代模型不使用)。StartFailure* 為 engineStartFailure 故障狀態(args 與起動嘗試計數;
/// 預設值時不寫入 JSON,未使用此故障者快照與舊版相同)。
/// </summary>
public sealed record ActuatorSnapshot(
    double RudderOrderDeg, double RudderDeg, double RpmOrder, double Rpm, double ThrusterOrder, double ThrusterActual,
    Physics.EngineMode EngineMode = Physics.EngineMode.Stopped, double EngineTimerSec = 0.0, double RpmTarget = 0.0, double RpmTau = 0.0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] double? StartFailureDelayS = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int? StartFailureAttempts = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int StartFailedAttempts = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int StartConsecutiveFailures = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool StartBlocked = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] double StartAttemptTimerSec = 0.0);

public sealed record EnvironmentSnapshot(
    double WindTrueSpeedMps, double WindTrueDirFromRad, double Gustiness, double GustFactor,
    double CurrentSetRad, double CurrentDriftMps,
    /// <summary>常數水深;null 表示使用 callback 水深(還原時沿用現有提供者)</summary>
    double? ConstantDepth);

public sealed record AutopilotSnapshot(bool Enabled, double HeadingRad, double RotLimitDegPerMin);

/// <summary>操舵模式:mode、泵("1"|"2"|"both")、NFU 推動方向(−1 左、0 停、+1 右)。</summary>
public sealed record SteeringSnapshot(SteeringMode Mode, string Pump, int NfuDirection);
