using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Assessment;

/// <summary>操縱量測的事件種類(source = assessment;itemId = 量測 id,例如 turning35.port、zigzag10.portFirst、crashStop;設計文件 ship-familiarisation.md 第 2 節)。</summary>
public static class ManoeuvreEventKinds
{
    /// <summary>量測開始(data {id, kind, side, approach, environment})</summary>
    public const string Started = "manoeuvreStarted";
    /// <summary>量測完成(data = 完整 <see cref="ManoeuvreRecord"/>;complete = false 表示提前結束但已有可用量測值)</summary>
    public const string Measured = "manoeuvreMeasured";
    /// <summary>量測中止且無可用量測值(data {id, kind, reason})</summary>
    public const string Aborted = "manoeuvreAborted";
}

/// <summary>量測種類(<see cref="ManoeuvreRecord.Kind"/>)。</summary>
public static class ManoeuvreKinds
{
    public const string Turning = "turning";
    public const string Zigzag = "zigzag";
    public const string CrashStop = "crashStop";
    public const string InertiaStop = "inertiaStop";
    public const string ThrusterTurn = "thrusterTurn";
    public const string RudderSwing = "rudderSwing";
    public const string ManOverboard = "manOverboard";
}

/// <summary>
/// 一次操縱量測的結果(summary.manoeuvres[]、manoeuvreMeasured 事件 data;規劃書第 8.1 節 F01–F03、第 3.5 節)。
/// 也是追蹤器的可變狀態(進快照);對外單位:度、節、m、s。數值放在 <see cref="Values"/>(鍵名含單位後綴,與 bands 指標
/// <c>manoeuvres.&lt;id&gt;.&lt;鍵&gt;</c> 相同),與 trial_targets.json 的比較放在 <see cref="Comparisons"/>。
/// </summary>
public sealed class ManoeuvreRecord
{
    /// <summary>量測 id:turning35.port、zigzag10.portFirst、crashStop、inertiaStop、thrusterTurn.starboard、rudderSwing.toStarboard、manOverboard</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    /// <summary>port | starboard(迴旋、Z 形首舵、側推、MOB 落水舷);停船為 null</summary>
    [JsonPropertyName("side")] public string? Side { get; set; }
    /// <summary>inProgress | measured | aborted</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "inProgress";
    /// <summary>量測是否完整(迴旋到 360°、Z 形 ≥ 2 個超越角、停船到 u ≤ 0、慣性停船到結束速度、側推到 90°、MOB 回到落水者附近)</summary>
    [JsonPropertyName("complete")] public bool Complete { get; set; }
    [JsonPropertyName("abortReason")] public string? AbortReason { get; set; }
    [JsonPropertyName("start_s")] public double StartS { get; set; }
    [JsonPropertyName("end_s")] public double? EndS { get; set; }
    [JsonPropertyName("shipId")] public string ShipId { get; set; } = "";
    [JsonPropertyName("loading")] public string Loading { get; set; } = "";
    /// <summary>動力學模型名稱(mmg/1;暫代模型時比較一律不可比)</summary>
    [JsonPropertyName("dynamics")] public string Dynamics { get; set; } = "";
    /// <summary>舵令(度,右正左負;迴旋/Z 形/舵機擺動)</summary>
    [JsonPropertyName("rudder_deg")] public double? RudderDeg { get; set; }
    /// <summary>Z 形的執行航向變化(度;= |首舵角|,10/10、20/20)</summary>
    [JsonPropertyName("checkHeading_deg")] public double? CheckHeadingDeg { get; set; }
    /// <summary>停船/慣性停船的車令(EFAS、FAS、STOP…;以轉速下令時為 rpm;先停俥再倒俥為 "STOP→EFAS")</summary>
    [JsonPropertyName("order")] public string? Order { get; set; }
    /// <summary>量測方式:drift = 停俥漂移(inertiaStop 進入速度 &lt; 7 kn:觀察 300 s 或減至 0.5 kn,不與試俥比較);其他省略</summary>
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    /// <summary>舵機泵("1"|"2"|"both";舵機擺動)</summary>
    [JsonPropertyName("pump")] public string? Pump { get; set; }
    /// <summary>MOB 目標 id</summary>
    [JsonPropertyName("targetId")] public string? TargetId { get; set; }
    [JsonPropertyName("approach")] public ManoeuvreApproach Approach { get; set; } = new();
    [JsonPropertyName("environment")] public ManoeuvreEnvironment Environment { get; set; } = new();
    /// <summary>量測值(鍵含單位後綴;插入順序固定)</summary>
    [JsonPropertyName("values")] public Dictionary<string, double> Values { get; set; } = new();
    /// <summary>迴旋 90/180/270/360° 標記、Z 形反舵/超越角、慣性停船每分鐘速度、MOB 迴旋段</summary>
    [JsonPropertyName("marks")] public List<ManoeuvreMark> Marks { get; set; } = new();
    [JsonPropertyName("comparisons")] public List<ManoeuvreComparison> Comparisons { get; set; } = new();
    /// <summary>比較所用目標值的最佳資料品質:trial(實測)| poster(海報圖面讀值)| estimate(估計值)| particulars(交船圖說)| none</summary>
    [JsonPropertyName("dataQuality")] public string DataQuality { get; set; } = "none";
    /// <summary>比較所用 data/ships/&lt;ID&gt;/trial_targets.json 的 sha256(小寫 hex;未比較時省略)</summary>
    [JsonPropertyName("targetsSha256")] public string? TargetsSha256 { get; set; }
    /// <summary>MOB 旋迴方式:williamson | anderson | scharnow | other(僅 manOverboard)</summary>
    [JsonPropertyName("method")] public string? Method { get; set; }
    /// <summary>說明(資料品質、不可比原因、模型驗證範圍;繁體中文)</summary>
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new();
}

public sealed class ManoeuvreApproach
{
    /// <summary>對水速度 u(節)</summary>
    [JsonPropertyName("speed_kn")] public double SpeedKn { get; set; }
    [JsonPropertyName("sog_kn")] public double SogKn { get; set; }
    [JsonPropertyName("heading_deg")] public double HeadingDeg { get; set; }
    [JsonPropertyName("rpm")] public double Rpm { get; set; }
    [JsonPropertyName("telegraph")] public string Telegraph { get; set; } = "";
    /// <summary>開始前是否穩定直航(MSC.137(76):定速直航;30 s 內 |ROT| ≤ 6°/min、|舵角| ≤ 5°、航速變化 ≤ 0.5 kn、車令未變)</summary>
    [JsonPropertyName("steady")] public bool Steady { get; set; }
    /// <summary>穩定直航持續秒數(上限 = 檢查視窗)</summary>
    [JsonPropertyName("steadyFor_s")] public double SteadyForS { get; set; }
    /// <summary>練習開始後尚無任何車/舵令:情境初始狀態即為穩態直航(初始條件以平衡轉速建立),視為穩定</summary>
    [JsonPropertyName("fromInitial")] public bool FromInitial { get; set; }
    /// <summary>開始前(最近一個每秒樣本)是否以自動舵保向;舵令會解除自動舵,故不取指令當下的狀態</summary>
    [JsonPropertyName("autopilot")] public bool Autopilot { get; set; }
}

public sealed class ManoeuvreEnvironment
{
    [JsonPropertyName("windSpeed_kn")] public double WindSpeedKn { get; set; }
    [JsonPropertyName("windDir_deg")] public double WindDirDeg { get; set; }
    [JsonPropertyName("currentDrift_kn")] public double CurrentDriftKn { get; set; }
    [JsonPropertyName("currentSet_deg")] public double CurrentSetDeg { get; set; }
    [JsonPropertyName("waterDepth_m")] public double WaterDepthM { get; set; }
    /// <summary>水深/最大吃水(h/T);試俥目標值為深水(h/T ≥ 4)</summary>
    [JsonPropertyName("depthRatio")] public double DepthRatio { get; set; }
}

/// <summary>量測過程的標記點。</summary>
public sealed class ManoeuvreMark
{
    /// <summary>heading90 | heading180 | heading270 | heading360 | execute | reversal | overshoot | minute | turn | reciprocal | closest</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("t_s")] public double TS { get; set; }
    /// <summary>自開始的航向變化(度,朝首舵方向為正)</summary>
    [JsonPropertyName("headingChange_deg")] public double? HeadingChangeDeg { get; set; }
    [JsonPropertyName("speed_kn")] public double? SpeedKn { get; set; }
    [JsonPropertyName("advance_m")] public double? AdvanceM { get; set; }
    [JsonPropertyName("transfer_m")] public double? TransferM { get; set; }
    /// <summary>Z 形超越角、MOB 迴旋段航向變化等(度)</summary>
    [JsonPropertyName("value")] public double? Value { get; set; }
    [JsonPropertyName("index")] public int? Index { get; set; }
}

/// <summary>量測值與 trial_targets.json 目標值的比較(規劃書第 6.4 節公差;第 3.5 節資料品質)。</summary>
public sealed class ManoeuvreComparison
{
    /// <summary>量測值鍵(<see cref="ManoeuvreRecord.Values"/>)</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("measured")] public double Measured { get; set; }
    [JsonPropertyName("target")] public double Target { get; set; }
    /// <summary>目標為範圍時(定常迴旋速度 4.6–4.9 kn)的上下限;target 為中點</summary>
    [JsonPropertyName("targetMin")] public double? TargetMin { get; set; }
    [JsonPropertyName("targetMax")] public double? TargetMax { get; set; }
    /// <summary>量測 − 目標(範圍目標時為超出範圍的量,範圍內為 0)</summary>
    [JsonPropertyName("deviation")] public double Deviation { get; set; }
    [JsonPropertyName("deviation_pct")] public double? DeviationPct { get; set; }
    [JsonPropertyName("tolerance")] public double Tolerance { get; set; }
    [JsonPropertyName("withinTolerance")] public bool WithinTolerance { get; set; }
    /// <summary>條件可比(同船同裝載、進入速度在公差內、深水、無風流、舵角/車令相同、穩定直航、MMG 模型)</summary>
    [JsonPropertyName("comparable")] public bool Comparable { get; set; }
    /// <summary>trial | poster | estimate | particulars</summary>
    [JsonPropertyName("dataQuality")] public string DataQuality { get; set; } = "";
    /// <summary>目標值在 trial_targets.json 的位置(identification.turningCircle35.port.advance_m…)</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "";
}

/// <summary>穩定直航判斷用的每秒樣本。</summary>
public sealed class SteadySample
{
    public long Tick { get; set; }
    public double RotDegPerMin { get; set; }
    public double RudderDeg { get; set; }
    public double StwKn { get; set; }
    public bool Autopilot { get; set; }
}

/// <summary>評估步(每秒)的自船樣本(進快照;開始序列未確定前暫存,確定後依序補入量測)。</summary>
public sealed class ManoeuvreSample
{
    public long Tick { get; set; }
    public double T { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Psi { get; set; }
    public double U { get; set; }
    public double V { get; set; }
    public double R { get; set; }
    public double Rpm { get; set; }
    public double RudderOrderDeg { get; set; }
    public double RotDegPerMin { get; set; }
}

/// <summary>進行中的量測(進快照;不輸出到 summary)。</summary>
public sealed class ManoeuvreProgress
{
    public ManoeuvreRecord Record { get; set; } = new();
    public long StartTick { get; set; }
    // 開始序列(舵輪拖曳/鍵盤逐步/NFU):Pending 時尚未確定量測種類、未發事件,樣本暫存於 Buffer
    public bool Pending { get; set; }
    public double SeqPrevRudder { get; set; }
    public double SeqRudder { get; set; }
    public long SeqLastTick { get; set; }
    public int SeqOrders { get; set; }
    public bool SeqPolled { get; set; }
    /// <summary>序列中有操舵模式 NFU 推桿放開(舵令由滿舵改為當時舵角)</summary>
    public bool SeqNfuLever { get; set; }
    public List<ManoeuvreSample> Buffer { get; set; } = new();
    /// <summary>同向加大舵令可改判量測的期限(tick;−1 = 不可)</summary>
    public long GrowUntilTick { get; set; } = -1;
    public double X0 { get; set; }
    public double Y0 { get; set; }
    public double Psi0 { get; set; }
    /// <summary>+1 右舷 / −1 左舷(迴旋與 Z 形首舵、側推方向)</summary>
    public double Sign { get; set; } = 1.0;
    public double RudderOrder0 { get; set; }
    public double RpmOrder0 { get; set; }
    public double Thruster0 { get; set; }
    public double LastPsi { get; set; }
    /// <summary>自開始的累計航向變化(度,朝 Sign 方向為正)</summary>
    public double ChangeDeg { get; set; }
    public double TrackM { get; set; }
    // 前一樣本(線性內插)
    public double PrevT { get; set; }
    public double PrevChange { get; set; }
    public double PrevAlong { get; set; }
    public double PrevAcross { get; set; }
    public double PrevSpeedKn { get; set; }
    public double PrevU { get; set; }
    public double PrevRpm { get; set; }
    public double PrevRudder { get; set; }
    public double PrevX { get; set; }
    public double PrevY { get; set; }
    // 迴旋
    public int NextMark { get; set; } = 90;
    public double SumV { get; set; }
    public double SumR { get; set; }
    public int SteadyCount { get; set; }
    // Z 形:Phase 0 = 首舵後等待執行航向,1 = 反舵後追蹤超越角極值;Side = 目前舵的方向
    public int Phase { get; set; }
    public double Side { get; set; }
    public double Extreme { get; set; }
    public double ExtremeT { get; set; }
    // 進快照(JSON):不可用 NaN/Infinity,未定以 null 表示
    public double? H1 { get; set; }
    public double? H2 { get; set; }
    public double T1 { get; set; }
    public int Reversals { get; set; }
    public int Overshoots { get; set; }
    public int MaxOvershoots { get; set; }
    public long LastReversalTick { get; set; } = -1;
    public int ExecuteCrossings { get; set; }
    /// <summary>逐步反舵進行中(已記錄反舵,舵令尚未到達對舷同舵角)</summary>
    public bool ReversalInProgress { get; set; }
    // 停船
    public bool AsternSeen { get; set; }
    public bool RudderUsed { get; set; }
    public double EndSpeedKn { get; set; }
    public int NextMinute { get; set; } = 1;
    /// <summary>停俥漂移(進入速度 &lt; 7 kn 的停俥):觀察 300 s 或減至 0.5 kn</summary>
    public bool Drift { get; set; }
    /// <summary>自開始的最大航向變化(帶號,度;停俥漂移)</summary>
    public double MaxChange { get; set; }
    // 側推 / MOB
    public double MaxRot { get; set; }
    public bool Reached30 { get; set; }
    public bool Mark60 { get; set; }
    public double? MinRange { get; set; }
    public double MinRangeT { get; set; }
    public double MinRangeSpeedKn { get; set; }
    public long FirstRudderTick { get; set; } = -1;
    public double FirstRudderSign { get; set; }
    public double SegmentStartChange { get; set; }
    public double CurrentRudderSign { get; set; }
    public List<double> Segments { get; set; } = new();
    public bool ReciprocalSeen { get; set; }
    public int OpeningSeconds { get; set; }
}

/// <summary>操縱量測追蹤器的可變狀態(進快照)。</summary>
public sealed class ManoeuvreState
{
    public List<ManoeuvreRecord> Records { get; set; } = new();
    public ManoeuvreProgress? Active { get; set; }
    public ManoeuvreProgress? Swing { get; set; }
    public ManoeuvreProgress? Mob { get; set; }
    public List<SteadySample> History { get; set; } = new();
    public long LastPropulsionChangeTick { get; set; } = -1;
    public long LastRudderChangeTick { get; set; } = -1;
    public double LastRudderOrder { get; set; }
    public double LastRpmOrder { get; set; }
    public double LastThruster { get; set; }
    /// <summary>最近一次指令檢查時的車鐘(開始量測時填 approach.telegraph = 下令前的車鐘)</summary>
    public string? LastTelegraph { get; set; }
    public bool Initialized { get; set; }
}
