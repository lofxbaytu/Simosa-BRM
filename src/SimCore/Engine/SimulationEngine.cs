using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using SimosaBRM.SimCore.Alerts;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Sensors;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.SimCore.Traffic;

namespace SimosaBRM.SimCore.Engine;

/// <summary>引擎選項(規劃書第 5.3 節:核心 50 Hz、廣播 25 Hz、快照每 10 s)。</summary>
public sealed class EngineOptions
{
    /// <summary>積分步長(秒)</summary>
    public double Dt { get; init; } = 0.02;
    /// <summary>每幾步廣播一次狀態(2 → 25 Hz)</summary>
    public int BroadcastDivider { get; init; } = 2;
    /// <summary>自動快照間隔(步;500 → 10 s;0 = 關閉)</summary>
    public int AutoSnapshotIntervalTicks { get; init; } = 500;
    /// <summary>保留的自動快照數(倒帶用)</summary>
    public int SnapshotHistory { get; init; } = 60;
    /// <summary>時間倍率上限</summary>
    public double MaxTimeScale { get; init; } = 10.0;
    /// <summary>
    /// 操縱量測比較用的試俥目標值(null = 讀 data/ships/&lt;ID&gt;/trial_targets.json)。重播時由紀錄標頭 manoeuvreTargets 注入,
    /// 使目標值檔案日後更新也不改變舊紀錄的重播摘要(只影響 summary 比較,不影響物理與雜湊)。
    /// </summary>
    public Assessment.ManoeuvreTargets? ManoeuvreTargets { get; init; }
    /// <summary>
    /// 情境未給 <c>startTimeUtc</c> 時用來取「練習開始時刻」的實際時鐘(既有 NMEA 時間規則:主控程序傳 <see cref="TimeProvider.System"/>;測試可固定)。
    /// 解析出的開始時刻會寫回情境 <c>startTimeUtc</c>,因此紀錄標頭帶有它、重播沿用而不取現在時間。
    /// null(預設;單元測試與重播)= 不以實際時鐘代入:情境無 startTimeUtc 時 state 省略 utc,使同一情境分別建立的引擎狀態 JSON 逐位元相同。
    /// </summary>
    public TimeProvider? Clock { get; init; }
}

/// <summary>
/// 模擬核心(規劃書第 5 章):固定步長 50 Hz、確定性、指令佇列、快照/還原、狀態鏈雜湊。
/// 所有狀態變更都只在呼叫 <see cref="Step"/> / <see cref="ProcessPendingCommands"/> 的執行緒上發生;
/// 其他執行緒只透過 <see cref="Enqueue"/> 送指令,並由事件取得廣播狀態。
/// </summary>
public sealed class SimulationEngine
{
    private readonly ConcurrentQueue<SimCommand> _queue = new();
    private readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> _dynamicsFactory;
    private readonly List<EngineSnapshot> _snapshots = new();
    private readonly List<string> _faults = new();
    private readonly byte[] _hash = new byte[32];
    private readonly int _stepsPerSecond;

    private ActuatorModel _act;
    private DeterministicRandom _rng;
    private LocalTangentPlane _projection;
    private TelegraphOrder _telegraph;
    private int? _startsRemaining;
    private TrafficManager _traffic;
    private TimelineEngine? _timeline;
    private AssessmentEngine? _assessment;
    /// <summary>重新評分用的影子評估模組(以新參數集解析的情境建立;事件不對外發出、不進雜湊;情境引擎設計第 7.4 節)</summary>
    private AssessmentEngine? _rescore;
    private Scenario.Scenario? _rescoreScenario;
    private ManoeuvreTargets? _rescoreTargets;
    private readonly RampSet _ramps = new();
    private readonly List<PhaseSpan> _phaseHistory = new();
    private long _seq;
    /// <summary>loadScenario 指令處理中的結果追蹤:主控程序在事件處理者內呼叫 LoadScenario(已載入)或 RejectScenarioLoad(已拒絕);兩者皆未發生視為拒絕</summary>
    private ScenarioLoadOutcome _scenarioLoadOutcome = ScenarioLoadOutcome.None;
    private string _scenarioLoadSource = SimEventSources.Instructor;
    /// <summary>LoadScenario 預先建立(確認可建立)的動力學模型,Initialize 直接取用而不再呼叫工廠</summary>
    private IShipDynamics? _pendingDynamics;

    private enum ScenarioLoadOutcome { None, Pending, Loaded, Rejected }
    private long _assessmentSeq;
    private bool _hashScenarioEngine;
    private List<string> _phaseNames = new();
    /// <summary>事件發出中由觀察者(評估模組)再發出的事件:待外層事件送完後依序發出,使紀錄/seq 順序與因果一致</summary>
    private readonly Queue<SimEvent> _deferredEvents = new();
    private bool _raising;
    // 操舵模式(規劃書第 7.2 節;情境引擎設計 D32):autopilot 模式與 Autopilot.Enabled 同步;nfu 以 _nfuDirection(−1 左、0 停、+1 右)推動舵
    private SteeringMode _steeringMode = SteeringMode.Fu;
    private string _steeringPump = SteeringPumps.One;
    private int _nfuDirection;
    // 感測器層、警報管理(BAM)、BNWAS(設計文件 docs/design/sensors-alerts.md);Initialize 時依情境重建
    private SensorModel _sensors = null!;
    private AlertManager _alerts = null!;
    private Bnwas _bnwas = null!;
    /// <summary>BNWAS 事件的 source(指令觸發的重設/模式變更沿用指令來源;階段變更為 engine)</summary>
    private string _bnwasSource = SimEventSources.Engine;
    // 外力模組(拖船、錨、碰墊、繫纜;設計文件 docs/design/physics-tugs-anchors-mooring.md);Initialize 時依情境重建
    private ExternalForceModel _external = null!;
    private StateVector _initialMotion;
    private readonly List<ExternalEvent> _externalEvents = new();
    // 岸壁效應(設計文件 docs/design/physics-bank-effect.md);情境有 banks 區段時建立,Initialize/Restore 時依情境重建
    private BankEffectModel? _bank;
    // 平均波浪漂移力(設計文件 docs/design/physics-waves.md);情境有 environment.waves 時建立,Initialize/Restore 時依情境重建(純函數,無跨步狀態)
    private WaveDriftModel? _waves;
    // 船間交互作用(設計文件 docs/design/physics-ship-interaction.md);情境 environment.shipInteraction.enabled 時建立,Initialize/Restore 時依情境重建
    // (力為純函數;跨步狀態只有事件旗標,進快照)
    private ShipInteractionModel? _interaction;
    private RadarEnvironmentState? _radarEnvironment;
    // state.radarEnvironment 廣播關鍵幀(只影響廣播/紀錄內容,不進快照與雜湊):載入/重設/倒帶後第一幀與之後每秒一次帶出,
    // 其他幀省略(內容練習中不變;雷達服務視省略為不變,時間倒退或情境切換時清除)。避免 25 Hz 每幀重複整份雨區定義
    private bool _radarEnvironmentPending = true;
    private long _radarEnvironmentSentTick;
    /// <summary>外力模組 + 岸壁的和(兩者皆 Active 時用;_external/_bank 重建時一併重建)</summary>
    private SumForceField? _sumField;

    public EngineOptions Options { get; }
    public ShipParticulars Ship { get; private set; }
    public Scenario.Scenario Scenario { get; private set; }
    public LoadingCondition Loading { get; private set; }
    public IShipDynamics Dynamics { get; private set; }
    public EnvironmentConditions Environment { get; private set; } = new();
    public Autopilot Autopilot { get; } = new();

    public double Dt => Options.Dt;
    public long Tick { get; private set; }
    /// <summary>模擬時間(秒)= Tick × dt(以乘法計算,避免累加誤差)</summary>
    public double Time => Tick * Options.Dt;
    /// <summary>
    /// 練習開始的 UTC 時刻(state.utc = StartUtc + Time;NMEA 時間規則相同):情境 <c>startTimeUtc</c>,未給時於載入情境時取
    /// <see cref="EngineOptions.Clock"/> 的現在時刻並寫回情境(紀錄標頭因此帶有它,重播得到相同的 utc);兩者皆無為 null(state 省略 utc)。
    /// 不進快照與雜湊。
    /// </summary>
    public DateTime? StartUtc { get; private set; }
    public bool Frozen { get; private set; }
    public double TimeScale { get; private set; } = 1.0;
    public bool Aground { get; private set; }
    public StateVector Motion { get; private set; }
    public TelegraphOrder Telegraph => _telegraph;
    public IReadOnlyList<string> Faults => _faults;
    public ActuatorModel Actuators => _act;
    public LocalTangentPlane Projection => _projection;
    public DeterministicRandom Random => _rng;
    /// <summary>操舵模式(fu / nfu / autopilot / emergency;autopilot ⇔ <see cref="Autopilot"/>.Enabled)</summary>
    public SteeringMode SteeringMode => _steeringMode;
    /// <summary>使用中的舵機泵("1"|"2"|"both";速率依 particulars 的單/雙泵滿舵時間換算,無資料時不變)</summary>
    public string SteeringPump => _steeringPump;
    /// <summary>NFU 推動方向(−1 左、0 停、+1 右;非 nfu 模式為 0)</summary>
    public int NfuDirection => _nfuDirection;
    /// <summary>應急操舵的舵機速率(度/秒):particulars rudder.steeringGear.emergencyHardOverTime35_s,無資料時取隨動速率的一半</summary>
    public double EmergencyRudderRateDegPerSec => Ship.Rudder.SteeringGear?.EmergencyHardOverTime35_s is { } t && t > 0 ? 35.0 / t : _act.Parameters.RudderRateDegPerSec / 2.0;
    public IReadOnlyList<EngineSnapshot> Snapshots => _snapshots;
    public int PendingCommandCount => _queue.Count;
    /// <summary>交通模組(目標船;規劃書第 5.2 節)</summary>
    public TrafficManager Traffic => _traffic;
    /// <summary>自船與任一目標船外形曾相交(flags.collision)</summary>
    public bool Collision => _traffic.Collision;
    /// <summary>時間表執行期(情境無時間表時仍存在但無項目;情境引擎設計第 3 節)</summary>
    public TimelineEngine? Timeline => _timeline;
    /// <summary>評估模組(航線/區域/指標/會遇/故障應變/規則)</summary>
    public AssessmentEngine? Assessment => _assessment;
    /// <summary>重新評分的影子評估模組(<see cref="EnableRescore"/> 後存在;其 summary 為新參數集的評分)</summary>
    public AssessmentEngine? RescoreAssessment => _rescore;
    /// <summary>載入情境時解析的參數集摘要(無引用時 null)</summary>
    public ParameterSetInfo? ParameterSet => Scenario.ParameterSetInfo;
    /// <summary>情境階段(預設 normal)</summary>
    public string Phase { get; private set; } = "normal";
    /// <summary>能見度(nm;null = 無限制)</summary>
    public double? VisibilityNm => Environment.VisibilityNm;
    /// <summary>最新評估摘要(每秒更新;endExercise 後定稿)</summary>
    public AssessmentSummary? LatestSummary => _assessment?.LatestSummary;
    /// <summary>練習已結束(endExercise)</summary>
    public bool Ended { get; private set; }
    /// <summary>下一個事件的序號</summary>
    public long EventSeq => _seq;
    /// <summary>進行中的環境漸變</summary>
    public IReadOnlyList<EnvironmentRamp> Ramps => _ramps.Ramps;
    public int StepsPerSecond => _stepsPerSecond;
    /// <summary>感測器層(指示值 = 真值 + 故障;state 頂層 heading/pos/stw/depthBelowKeel 為指示值,truth 區塊為真值)</summary>
    public SensorModel Sensors => _sensors;
    /// <summary>警報管理(BAM,MSC.302(87))</summary>
    public AlertManager Alerts => _alerts;
    /// <summary>BNWAS(MSC.128(75))</summary>
    public Bnwas Bnwas => _bnwas;
    /// <summary>外力模組(拖船/錨/泊位;<see cref="ExternalForceModel.Active"/> 為 false 時不影響積分與雜湊)</summary>
    public ExternalForceModel External => _external;
    /// <summary>岸壁效應(情境無 banks 時 null;<see cref="BankEffectModel.Active"/> 為 false 時不影響積分與雜湊)</summary>
    public BankEffectModel? Bank => _bank;
    /// <summary>平均波浪漂移力(情境無 environment.waves 時 null;<see cref="WaveDriftModel.Active"/> 為 false 時不影響積分與雜湊)</summary>
    public WaveDriftModel? Waves => _waves;
    /// <summary>船間交互作用(情境未啟用 environment.shipInteraction 時 null;<see cref="ShipInteractionModel.Active"/> 為 false 時不影響積分與雜湊)</summary>
    public ShipInteractionModel? ShipInteraction => _interaction;
    /// <summary>自動舵可啟用:無 autopilotFailure,且電羅經未失效(指示航向有效)</summary>
    public bool AutopilotAvailable => !_faults.Contains(FaultNames.AutopilotFailure) && !_faults.Contains(FaultNames.GyroFailure);

    /// <summary>狀態鏈雜湊:h_n = SHA-256(h_{n−1} ‖ tick ‖ 狀態向量 ‖ 致動器 ‖ 陣風係數 ‖ 主機狀態機 ‖ 各目標船 x,y,ψ,V,r)。</summary>
    public string StateHash => Convert.ToHexString(_hash);
    public ReadOnlySpan<byte> StateHashBytes => _hash;

    /// <summary>指令套用後(參數:引擎、指令、套用時的 tick)— 紀錄器用</summary>
    public event Action<SimulationEngine, SimCommand, long>? CommandApplied;
    /// <summary>每步(狀態已推進)</summary>
    public event Action<SimulationEngine>? Stepped;
    /// <summary>每 BroadcastDivider 步(25 Hz)</summary>
    public event Action<SimulationEngine, OwnShipState>? Broadcast;
    /// <summary>快照產生(自動或指令)</summary>
    public event Action<SimulationEngine, EngineSnapshot>? SnapshotTaken;
    /// <summary>loadScenario 指令(引擎本身不讀檔,由主控程序載入後呼叫 <see cref="LoadScenario"/>)</summary>
    public event Action<SimulationEngine, string>? ScenarioLoadRequested;
    /// <summary>
    /// loadScenario 指令帶內嵌情境(<c>args.scenario</c> 完整情境物件,<c>args.id</c> 為紀錄用名稱;規劃書第 9.1 節靠離泊預演自動組題):
    /// 參數為(引擎、情境物件、id 或 null);由主控程序以 <c>ScenarioLoader.FromJsonElement</c> 解析後呼叫 <see cref="LoadScenario"/>。
    /// 有 <c>args.scenario</c> 時只發此事件(value/args.path 忽略)。
    /// </summary>
    public event Action<SimulationEngine, System.Text.Json.JsonElement, string?>? InlineScenarioLoadRequested;
    /// <summary>
    /// loadScenario 載入/驗證失敗、引擎狀態不變(主控程序呼叫 <see cref="RejectScenarioLoad"/> 或 <see cref="TryApplyScenarioLoad"/> 失敗時;
    /// 事件 kind <see cref="SimEventKinds.ScenarioLoadRejected"/>)。不走 <see cref="EventRaised"/>:不計事件序號、不進雜湊、紀錄器不寫
    /// (紀錄器本就不寫 loadScenario input 行,重播無法重現檔案載入失敗),只供主控程序印出並以 WebSocket 事件框轉給教官站。
    /// </summary>
    public event Action<SimulationEngine, SimEvent>? ScenarioLoadRejected;
    /// <summary>
    /// 即將以 <see cref="LoadScenario"/> 換成新情境(參數:新情境),在狀態重設前發出:紀錄器據此關閉目前紀錄(footer 為舊情境的最終 tick 與雜湊),
    /// 主控程序再為新情境開新檔(每份紀錄只有一個 header 情境,重播不需處理情境切換)。Reset 不發此事件(reset 為可重播的 input)。
    /// </summary>
    public event Action<SimulationEngine, Scenario.Scenario>? ScenarioLoading;
    /// <summary>擱淺(UKC ≤ 0)</summary>
    public event Action<SimulationEngine>? Grounded;
    /// <summary>交通事件(碰撞、CPA 低於門檻、目標出現、航點、COLREG 行動、聲號、目標控制)— 只發交通事件;紀錄器改用 <see cref="EventRaised"/></summary>
    public event Action<SimulationEngine, TrafficEvent>? TrafficEventRaised;
    /// <summary>所有核心事件(交通、時間表、評估、教官、引擎;情境引擎設計第 3.2 節)— 紀錄器與主控程序用</summary>
    public event Action<SimulationEngine, SimEvent>? EventRaised;

    public SimulationEngine(ShipParticulars ship, Scenario.Scenario scenario,
        Func<ShipParticulars, LoadingCondition, IShipDynamics>? dynamicsFactory = null, EngineOptions? options = null)
    {
        Options = options ?? new EngineOptions();
        if (Options.Dt <= 0) throw new ArgumentOutOfRangeException(nameof(options), "dt 必須為正");
        _stepsPerSecond = Math.Max(1, (int)Math.Round(1.0 / Options.Dt));
        _dynamicsFactory = dynamicsFactory ?? ((s, l) => new PlaceholderDynamics(s, l));
        Ship = ship;
        Scenario = scenario.Validate();
        Loading = scenario.Ship.Loading;
        Dynamics = _dynamicsFactory(ship, Loading);
        _act = new ActuatorModel(ActuatorParametersForDynamics());
        _rng = new DeterministicRandom(scenario.Seed);
        _projection = new LocalTangentPlane(scenario.Origin!.Lat, scenario.Origin.Lon);
        _traffic = new TrafficManager(_projection, Options.Dt);
        Initialize();
    }

    // ------------------------------------------------------------------ 初始化 / 情境

    /// <summary>依目前情境重設為初始狀態(tick 0、雜湊歸零、亂數重建)。</summary>
    public void Reset() => Initialize();

    /// <summary>
    /// 載入新情境(船舶 particulars 由呼叫端提供;船舶不同時需給新的 particulars)。
    /// 情境驗證、船舶比對與動力學模型建立(係數檔)都在改動任何狀態之前完成:任一失敗即丟例外而引擎狀態完全不變(不重設、不切換情境、
    /// 不發 <see cref="ScenarioLoading"/>,紀錄不關閉)。主控程序請改用 <see cref="TryApplyScenarioLoad"/>(失敗時發 scenarioLoadRejected 回報教官站)。
    /// </summary>
    public void LoadScenario(Scenario.Scenario scenario, ShipParticulars? ship = null)
    {
        scenario.Validate();
        var nextShip = ship ?? Ship;
        if (!string.Equals(nextShip.Id, scenario.Ship.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"情境需要船舶 {scenario.Ship.Id},目前為 {nextShip.Id};請一併提供 particulars");
        var dynamics = _dynamicsFactory(nextShip, scenario.Ship.Loading);   // 係數檔不存在等錯誤在此丟出,狀態尚未改動
        Scenario = scenario;
        Ship = nextShip;
        _pendingDynamics = dynamics;
        ScenarioLoading?.Invoke(this, scenario);
        _rescoreScenario = null;
        _rescoreTargets = null;
        Initialize();
        if (_scenarioLoadOutcome == ScenarioLoadOutcome.Pending) _scenarioLoadOutcome = ScenarioLoadOutcome.Loaded;
    }

    /// <summary>
    /// 主控程序的情境套用流程(<see cref="ScenarioLoadRequested"/> / <see cref="InlineScenarioLoadRequested"/> 處理者共用;可單元測試):
    /// 載入結果有問題 → <see cref="RejectScenarioLoad"/>、回傳 false、引擎狀態不變;船舶不同時以 <paramref name="shipLoader"/> 取 particulars
    /// (取得失敗同樣拒絕);最後 <see cref="LoadScenario"/>(其例外亦轉為拒絕)。成功回傳 true,之後主控程序列印檢查並為紀錄另開新檔。
    /// </summary>
    /// <param name="origin">載入來源(file | inline;事件 data.origin 與訊息用)</param>
    public bool TryApplyScenarioLoad(ScenarioLoadResult result, Func<string, ShipParticulars> shipLoader, string origin)
    {
        if (!result.Ok)
        {
            RejectScenarioLoad(result.ScenarioId, result.Issues, origin, result.Path);
            return false;
        }
        var sc = result.Scenario!;
        ShipParticulars? ship = null;
        if (!string.Equals(sc.Ship.Id, Ship.Id, StringComparison.OrdinalIgnoreCase))
        {
            try { ship = shipLoader(sc.Ship.Id); }
            catch (Exception ex)
            {
                RejectScenarioLoad(sc.Id, new[] { new ScenarioIssue(ScenarioIssueSeverity.Error, "ship.id", $"無法載入船舶 {sc.Ship.Id} 的 particulars:{ex.Message}") }, origin, result.Path);
                return false;
            }
        }
        try { LoadScenario(sc, ship); }
        catch (Exception ex)
        {
            RejectScenarioLoad(sc.Id, new[] { new ScenarioIssue(ScenarioIssueSeverity.Error, "scenario", ex.Message) }, origin, result.Path);
            return false;
        }
        return true;
    }

    /// <summary>問題訊息取前幾條(事件 message 用)。</summary>
    public const int RejectedIssuesInMessage = 3;

    /// <summary>
    /// 回報情境載入失敗(引擎狀態不變):發 <see cref="SimEventKinds.ScenarioLoadRejected"/> 事件給 <see cref="ScenarioLoadRejected"/> 訂閱者
    /// (source 為指令來源;在 loadScenario 指令處理之外呼叫時為 instructor)。message 為「情境 id:前 N 條問題(共 M 項)」,
    /// data {scenarioId, origin, path, issueCount, issues[]{path, message, severity}}。
    /// </summary>
    public void RejectScenarioLoad(string? scenarioId, IReadOnlyList<ScenarioIssue> issues, string origin, string? path = null)
    {
        var source = _scenarioLoadOutcome == ScenarioLoadOutcome.Pending ? _scenarioLoadSource : SimEventSources.Instructor;
        if (_scenarioLoadOutcome == ScenarioLoadOutcome.Pending) _scenarioLoadOutcome = ScenarioLoadOutcome.Rejected;
        var head = string.Join(";", issues.Take(RejectedIssuesInMessage).Select(i => $"{i.Path}:{i.Message}"));
        var message = $"情境 {scenarioId ?? "(未知)"} 載入被拒:{head}{(issues.Count > RejectedIssuesInMessage ? $"(共 {issues.Count} 項)" : "")}";
        var ev = NewEvent(SimEventKinds.ScenarioLoadRejected, source, scenarioId, null, message, new
        {
            scenarioId,
            origin,
            path,
            issueCount = issues.Count,
            issues = issues.Select(i => new { path = i.Path, message = i.Message, severity = ContractJson.EnumName(i.Severity) }).ToList(),
        }) with { Seq = -1, Phase = Phase };
        ScenarioLoadRejected?.Invoke(this, ev);
    }

    /// <summary>
    /// 啟用重新評分(重播 <c>--rescore</c>;情境引擎設計第 7.4 節):物理、交通、時間表與雜湊仍完全依目前(紀錄 header 的)情境執行,
    /// 另以 <paramref name="rescored"/>(同一情境、以新參數集重新代入門檻)建立影子評估模組;其事件只餵回自己(不對外發出、不進雜湊、不觸發時間表),
    /// 其 summary 由 <see cref="RescoreAssessment"/> 取得。只能在 tick 0 呼叫。
    /// </summary>
    /// <param name="targets">影子評估的試俥目標值(重播 --rescore 時為目前檔案;null = 與主評估相同的來源)</param>
    public void EnableRescore(Scenario.Scenario rescored, ManoeuvreTargets? targets = null)
    {
        if (Tick != 0) throw new InvalidOperationException("重新評分須在推進前(tick 0)啟用");
        if (!string.Equals(rescored.Id, Scenario.Id, StringComparison.Ordinal) || !string.Equals(rescored.Ship.Id, Scenario.Ship.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"重新評分的情境 {rescored.Id}/{rescored.Ship.Id} 與目前情境 {Scenario.Id}/{Scenario.Ship.Id} 不符");
        _rescoreScenario = rescored.Validate();
        _rescoreTargets = targets;
        _rescore = new AssessmentEngine(_rescoreScenario, Ship, this, _projection, Options.Dt, RaiseRescore, _rescoreTargets ?? Options.ManoeuvreTargets);
    }

    private void Initialize()
    {
        var sc = Scenario;
        Loading = sc.Ship.Loading;
        Dynamics = _pendingDynamics ?? _dynamicsFactory(Ship, Loading);
        _pendingDynamics = null;
        _projection = new LocalTangentPlane(sc.Origin!.Lat, sc.Origin.Lon);
        StartUtc = ResolveStartUtc(sc);
        // 潮汐(environment.tide):潮高為練習時間的純函數,不進快照/雜湊;無 tide 時 null(潮高 0,數值與舊版相同)
        var tide = TideModel.FromScenario(sc.Environment.Tide, StartUtc);

        var pos = sc.Initial.Position!;
        double x, y;
        if (pos.X is not null && pos.Y is not null) (x, y) = (pos.X.Value, pos.Y.Value);
        else (x, y) = _projection.ToLocal(pos.Lat!.Value, pos.Lon!.Value);
        var psi = Units.NormalizeHeadingRad(Units.DegToRad(sc.Initial.Heading));
        var u = Units.KnToMps(sc.Initial.Speed);
        Motion = new StateVector(u, 0.0, 0.0, x, y, psi);

        // 直航平衡轉速以初始有效水深(基底 + 潮高(0))計算;無 tide 時 + 0.0 與舊版位元相同
        var (telegraph, rpm) = ResolveInitialPropulsion(sc, u, sc.Environment.WaterDepth + (tide?.HeightAt(0.0) ?? 0.0));
        _telegraph = telegraph;
        _act = new ActuatorModel(ActuatorParametersForDynamics())
        {
            RudderOrderDeg = sc.Initial.Rudder,
            RudderDeg = sc.Initial.Rudder,
            RpmOrder = rpm,
            Rpm = rpm,
        };
        _act.InitializeEngineState();

        Environment = new EnvironmentConditions
        {
            WindTrueSpeedMps = Units.KnToMps(sc.Environment.Wind.TrueSpeed),
            WindTrueDirFromRad = Units.DegToRad(sc.Environment.Wind.TrueDir),
            Gustiness = Units.Clamp(sc.Environment.Wind.Gustiness, 0.0, 1.0),
            GustFactor = 1.0,
            CurrentSetRad = Units.DegToRad(sc.Environment.Current.Set),
            CurrentDriftMps = Units.KnToMps(sc.Environment.Current.Drift),
            Depth = new ConstantDepth(sc.Environment.WaterDepth),
            Tide = tide,
            // 時變流場(environment.current.table | tidal):流向/流速為練習時間的純函數,不進快照/雜湊;無模型時 null(常數流,數值與舊版相同)
            CurrentModel = CurrentModel.FromScenario(sc.Environment.Current, tide, StartUtc),
            CurrentOverridden = false,
            VisibilityNm = sc.Environment.VisibilityNm,
        };

        _rng = new DeterministicRandom(sc.Seed);
        Tick = 0;
        Environment.RefreshTide(Time);
        Environment.RefreshCurrent(Time);
        Array.Clear(_hash);
        Frozen = false;
        TimeScale = Units.Clamp(sc.TimeScale <= 0 ? 1.0 : sc.TimeScale, 0.1, Options.MaxTimeScale);
        Aground = false;
        _faults.Clear();
        _snapshots.Clear();
        Autopilot.Enabled = false;
        Autopilot.HeadingRad = psi;
        _steeringMode = SteeringMode.Fu;
        _steeringPump = SteeringPumps.One;
        _nfuDirection = 0;
        _deferredEvents.Clear();
        _raising = false;
        _sensors = new SensorModel(sc.Sensors, _faults, Options.Dt);
        _alerts = new AlertManager(sc.Alerts, Options.Dt, _stepsPerSecond, RaiseAlert);
        _bnwas = new Bnwas(sc.Bnwas, Options.Dt, _stepsPerSecond, RaiseBnwas);
        _bnwasSource = SimEventSources.Engine;
        _initialMotion = Motion;
        _external = CreateExternal();
        _bank = CreateBank();
        _sumField = _bank is null ? null : new SumForceField(_external, _bank);
        _waves = CreateWaves();
        _interaction = CreateInteraction();
        // 雷達環境:只來自情境、練習中不變(不進快照與雜湊;重播由紀錄 header 的情境重建;廣播只在關鍵幀帶出)
        _radarEnvironment = ScenarioRadarEnvironmentBuilder.Build(sc, _projection);
        _radarEnvironmentPending = true;
        _externalEvents.Clear();
        _startsRemaining = Ship.Engine.MaxConsecutiveStarts;
        AttachTraffic(TrafficManager.FromScenario(sc, _projection, Options.Dt));

        // 情境引擎:事件序號、階段、時間表、評估、漸變(無時間表/航線/區域/規則時不追加雜湊位元組)
        _seq = 0;
        _assessmentSeq = 0;
        Ended = false;
        Phase = "normal";
        _phaseHistory.Clear();
        _phaseHistory.Add(new PhaseSpan("normal", 0));
        _phaseNames = new List<string> { "normal" };
        foreach (var name in sc.Timeline.Select(t => t.Phase).Concat(sc.Timeline.SelectMany(t => t.Actions).Where(a => a.Kind == "phase").Select(a => a.AsString())))
            if (name is not null && !_phaseNames.Contains(name)) _phaseNames.Add(name);
        _ramps.Clear();
        _hashScenarioEngine = sc.HasScenarioEngine;
        _timeline = new TimelineEngine(sc, this, _stepsPerSecond);
        _assessment = new AssessmentEngine(sc, Ship, this, _projection, Options.Dt, RaiseAssessment, Options.ManoeuvreTargets);
        _rescore = _rescoreScenario is { } rs ? new AssessmentEngine(rs, Ship, this, _projection, Options.Dt, RaiseRescore, _rescoreTargets ?? Options.ManoeuvreTargets) : null;
        var depthZones = _assessment.Zones.DepthZones();
        if (depthZones.Count > 0) Environment.Depth = new ZoneDepth((ConstantDepth)Environment.Depth, depthZones);
        RefreshBank();
        RefreshWaves();
        RefreshInteraction();
    }

    /// <summary>依目前情境建立岸壁效應模組(情境無 banks 時 null)。</summary>
    private BankEffectModel? CreateBank()
        => BankEffectModel.Create(Ship, Loading, Scenario, _projection, Dynamics.SupportsExternalForces);

    /// <summary>依目前情境建立平均波浪漂移力模組(情境無 environment.waves 時 null)。</summary>
    private WaveDriftModel? CreateWaves()
        => WaveDriftModel.Create(Ship, Loading, Scenario, Dynamics.SupportsExternalForces);

    /// <summary>依目前情境建立船間交互作用模組(情境未啟用 environment.shipInteraction 時 null)。</summary>
    private ShipInteractionModel? CreateInteraction()
        => ShipInteractionModel.Create(Ship, Loading, Scenario, Dynamics.SupportsExternalForces);

    /// <summary>船間交互作用的步末結果(雜湊與 state.shipInteraction):以目前狀態、有效水深與目前的目標船重算(力為純函數;事件旗標不變)。</summary>
    private void RefreshInteraction()
    {
        if (_interaction is not { Active: true } si) return;
        si.Refresh(Motion, Environment.DepthAt(Motion.X, Motion.Y), _traffic.Targets);
    }

    /// <summary>船間交互作用力跨過門檻 → shipInteraction 事件(進入/離開各一次;source engine、targetId = 主要目標、學員站可見)。</summary>
    private void RaiseInteractionTransition(ShipInteractionTransition transition)
    {
        if (transition == ShipInteractionTransition.None || _interaction is not { } si) return;
        var r = si.Last;
        var enter = transition == ShipInteractionTransition.Enter;
        var targetId = enter ? r.TargetId : si.EventTargetId;
        var data = new
        {
            phase = enter ? "enter" : "exit", targetId, forceSurge_t = r.X / ShipInteractionModel.TonneForce, forceSway_t = r.Y / ShipInteractionModel.TonneForce,
            momentYaw_tm = r.N / ShipInteractionModel.TonneForce, xi = r.Xi, eta = r.Eta, threshold_t = si.EventThresholdN / ShipInteractionModel.TonneForce,
        };
        var message = enter
            ? $"船間交互作用:{targetId} 側向力 {r.Y / ShipInteractionModel.TonneForce:+0.0;-0.0} t、艏搖 {r.N / ShipInteractionModel.TonneForce:+0;-0} t·m(超過門檻)"
            : $"船間交互作用結束:{targetId}";
        Raise(NewEvent(SimEventKinds.ShipInteraction, SimEventSources.Engine, null, targetId, message, data, visible: true));
    }

    /// <summary>波浪漂移力的步末結果(雜湊與 state.waves):以目前狀態與所在有效水深重算(純函數,快照不需新欄位)。</summary>
    private void RefreshWaves()
    {
        if (_waves is not { Active: true } w) return;
        w.UpdateLast(Motion, Environment.DepthAt(Motion.X, Motion.Y));
    }

    /// <summary>岸壁效應的步末結果(雜湊與 state.bank):以目前狀態、所在水深與實際轉速重算(純函數,故快照不需新欄位)。</summary>
    private void RefreshBank()
    {
        if (_bank is not { Active: true } b) return;
        b.PreStep(_act.Rpm);
        b.UpdateLast(Motion, Environment.DepthAt(Motion.X, Motion.Y), _act.Rpm);
    }

    /// <summary>依目前情境、船舶與裝載建立外力模組(初始狀態:拖船 initial、錨收存或情境初始已錨泊、繫纜 initial)。</summary>
    private ExternalForceModel CreateExternal()
        => new(Ship, Loading, Scenario, _projection, Options.Dt, Dynamics.SupportsExternalForces, _initialMotion);

    // ------------------------------------------------------------------ 事件

    /// <summary>
    /// 發出事件:補 seq 與 phase,轉給時間表/評估模組觀察,再通知訂閱者(紀錄器、主控程序)。
    /// 觀察者在觀察途中再發出的事件(例如 faultInjected → requiredEventPending)先排隊,待本事件送完後依序發出,seq 與紀錄順序因此與因果一致。
    /// </summary>
    private void Raise(SimEvent e)
    {
        if (_raising)
        {
            _deferredEvents.Enqueue(e);
            return;
        }
        _raising = true;
        try
        {
            Dispatch(e);
            while (_deferredEvents.TryDequeue(out var d)) Dispatch(d);
        }
        finally { _raising = false; }
    }

    private void Dispatch(SimEvent e)
    {
        e = e with { Seq = _seq++, Phase = e.Phase ?? Phase };
        _timeline?.OnEvent(e);
        _assessment?.OnEvent(e);
        if (e.Source != SimEventSources.Assessment) _rescore?.OnEvent(e); // 影子評估只看非評估事件,評估事件由它自己產生
        EventRaised?.Invoke(this, e);
    }

    /// <summary>影子評估模組的事件:只餵回影子模組本身(必要事件 after/expect、故障應變),不對外發出、不計 seq、不進雜湊。</summary>
    private void RaiseRescore(string kind, string? itemId, string? targetId, string? message, object? data)
        => _rescore?.OnEvent(NewEvent(kind, SimEventSources.Assessment, itemId, targetId, message, data) with { Seq = -1 });

    private SimEvent NewEvent(string kind, string source, string? itemId = null, string? targetId = null, string? message = null, object? data = null, bool visible = false)
        => new()
        {
            Kind = kind, Tick = Tick, T = Time, Source = source, ItemId = itemId, TargetId = targetId, Message = message, Visible = visible,
            Data = data is null ? null : data is JsonElement je ? je : SimEvent.DataOf(data),
        };

    /// <summary>警報事件(source = engine、itemId = 警報 id、學員站可見)。</summary>
    private void RaiseAlert(string kind, AlertRecord rec, string? message, object data)
        => Raise(NewEvent(kind, SimEventSources.Engine, rec.Id, null, message, data, visible: true));

    /// <summary>BNWAS 事件(source 見 <see cref="_bnwasSource"/>、學員站可見)。</summary>
    private void RaiseBnwas(string kind, string? message, object data)
        => Raise(NewEvent(kind, _bnwasSource, null, null, message, data, visible: true));

    internal void RaiseTimelineEvent(string kind, string? itemId, string? message, JsonElement? data, bool visible)
        => Raise(NewEvent(kind, SimEventSources.Timeline, itemId, null, message, data, visible));

    private void RaiseAssessment(string kind, string? itemId, string? targetId, string? message, object? data)
    {
        _assessmentSeq++;
        Raise(NewEvent(kind, SimEventSources.Assessment, itemId, targetId, message, data));
    }

    /// <summary>設定情境階段(時間表或教官);相同階段不發事件。</summary>
    internal void SetPhase(string phase, string source, string? itemId)
    {
        if (string.Equals(Phase, phase, StringComparison.Ordinal)) return;
        var from = Phase;
        Phase = phase;
        if (!_phaseNames.Contains(phase)) _phaseNames.Add(phase);
        _phaseHistory.Add(new PhaseSpan(phase, Tick));
        Raise(NewEvent(SimEventKinds.PhaseChanged, source, itemId, null, $"{from} → {phase}", new { from, to = phase }));
    }

    /// <summary>階段歷程(summary.phases[]);最後一段到目前時間。</summary>
    internal IReadOnlyList<SummaryPhase> PhaseSpans(double time)
    {
        var list = new List<SummaryPhase>(_phaseHistory.Count);
        for (var i = 0; i < _phaseHistory.Count; i++)
        {
            var from = _phaseHistory[i].FromTick * Options.Dt;
            var to = i + 1 < _phaseHistory.Count ? _phaseHistory[i + 1].FromTick * Options.Dt : time;
            list.Add(new SummaryPhase { Phase = _phaseHistory[i].Phase, FromS = from, ToS = to });
        }
        return list;
    }

    private void AttachTraffic(TrafficManager traffic)
    {
        if (_traffic is not null) _traffic.EventRaised -= OnTrafficEvent;
        _traffic = traffic;
        _traffic.EventRaised += OnTrafficEvent;
    }

    private void OnTrafficEvent(TrafficEvent e)
    {
        var stamped = e with { Seq = _seq++, Phase = Phase };
        _timeline?.OnEvent(stamped);
        _assessment?.OnEvent(stamped);
        _rescore?.OnEvent(stamped);
        TrafficEventRaised?.Invoke(this, stamped);
        EventRaised?.Invoke(this, stamped);
    }

    /// <summary>自船運動學(ENU 位置、航向、對地速度、尺寸),供交通模組。</summary>
    private ShipKinematics OwnKinematics()
    {
        var m = Motion;
        var sample = Environment.SampleAt(m.X, m.Y);
        var (velE, velN) = EnvironmentMath.GroundVelocity(m.U, m.V, m.Psi, sample.CurrentEastMps, sample.CurrentNorthMps);
        return new ShipKinematics(m.X, m.Y, m.Psi, velE, velN, Ship.Hull.LengthOverall_m > 0 ? Ship.Hull.LengthOverall_m : Ship.Hull.LengthBetweenPerpendiculars_m, Ship.Hull.Breadth_m);
    }

    /// <summary>致動器參數:動力學模型提供者(MMG 由係數檔)優先,否則依 particulars(暫代模型)。</summary>
    private ActuatorParameters ActuatorParametersForDynamics()
        => Dynamics.ActuatorParameters ?? ActuatorParameters.FromParticulars(Ship);

    /// <summary>
    /// 初始車鐘與轉速:initial.rpm 指定時優先(車鐘未指定時取最接近者);否則車鐘指定時取其轉速;
    /// 否則(航速 &gt; 0 且模型能解直航穩態,如 MMG)取該航速的平衡轉速,與 Python 參考實作 reset() 相同;
    /// 最後退回「航速最接近的前進車令」(暫代模型)。
    /// </summary>
    private (TelegraphOrder Telegraph, double Rpm) ResolveInitialPropulsion(Scenario.Scenario sc, double u, double initialDepth)
    {
        var explicitOrder = !string.IsNullOrWhiteSpace(sc.Initial.Telegraph) &&
                            Enum.TryParse<TelegraphOrder>(sc.Initial.Telegraph, ignoreCase: true, out var parsed)
            ? parsed : (TelegraphOrder?)null;
        if (sc.Initial.Rpm is { } rpm0)
            return (explicitOrder ?? NearestTelegraphForRpm(rpm0), rpm0);
        if (explicitOrder is { } o)
            return (o, Ship.TelegraphRpm(o));
        if (u > 0.0 && Dynamics.SteadyRpmForSpeed(u, initialDepth) is { } steady)
            return (NearestTelegraphForRpm(steady), steady);
        var order = ResolveInitialTelegraph(sc);
        return (order, Ship.TelegraphRpm(order));
    }

    /// <summary>與轉速同向且最接近的車鐘(0 → STOP);與 Python _telegraph_for_rpm 相同。</summary>
    private TelegraphOrder NearestTelegraphForRpm(double rpm)
    {
        if (Math.Abs(rpm) < 1e-6) return TelegraphOrder.STOP;
        var best = TelegraphOrder.STOP;
        var bestErr = double.MaxValue;
        foreach (var o in Enum.GetValues<TelegraphOrder>())
        {
            if (o == TelegraphOrder.STOP) continue;
            double v;
            try { v = Ship.TelegraphRpm(o); } catch (KeyNotFoundException) { continue; }
            if (v == 0.0 || (v > 0) != (rpm > 0)) continue;
            var err = Math.Abs(v - rpm);
            if (err < bestErr) (best, bestErr) = (o, err);
        }
        return best;
    }

    private TelegraphOrder ResolveInitialTelegraph(Scenario.Scenario sc)
    {
        if (sc.Initial.Speed < 0.5) return TelegraphOrder.STOP;
        var best = TelegraphOrder.STOP;
        var bestErr = double.MaxValue;
        foreach (var o in new[] { TelegraphOrder.DSAH, TelegraphOrder.SAH, TelegraphOrder.HAH, TelegraphOrder.FAH, TelegraphOrder.NAVF })
        {
            if (Ship.TelegraphSpeedKn(o, Loading) is not { } kn) continue;
            var err = Math.Abs(kn - sc.Initial.Speed);
            if (err < bestErr) (best, bestErr) = (o, err);
        }
        return best;
    }

    // ------------------------------------------------------------------ 指令

    /// <summary>送入指令(任何執行緒);於下一次 Step/ProcessPendingCommands 套用。</summary>
    public void Enqueue(SimCommand command) => _queue.Enqueue(command);

    /// <summary>套用佇列中所有指令(引擎執行緒)。</summary>
    public void ProcessPendingCommands()
    {
        while (_queue.TryDequeue(out var cmd))
        {
            var tickAtApply = Tick;
            Apply(cmd);
            CommandApplied?.Invoke(this, cmd, tickAtApply);
        }
    }

    /// <summary>佇列指令(教官/學員):寫 input 行(由 CommandApplied 訂閱者),事件 source = instructor,並產生學員動作觀察。</summary>
    private void Apply(SimCommand cmd) => ApplyCore(cmd, SimEventSources.Instructor, null);

    /// <summary>時間表動作:與 Apply 共用邏輯,但不經佇列、不觸發 CommandApplied(不寫 input 行),事件 source = timeline。</summary>
    internal void ApplyInternal(SimCommand cmd, string source, string? itemId) => ApplyCore(cmd, source, itemId);

    private void ApplyCore(SimCommand cmd, string source, string? itemId)
    {
        var fromInstructor = source == SimEventSources.Instructor;
        // 駕駛台操作重設 BNWAS(MSC.128(75) 4.1.4:任何可辨識的值班官操作);時間表動作不是駕駛台上的人為操作,不重設。
        // 警報確認/靜音在各自分支處理(AlertBridgeOperation):只有駕駛台三站且未被核心拒絕者才重設,機控室 ecr 的操作與被拒絕的指令不重設
        if (fromInstructor && cmd.Type is SimCommandType.Rudder or SimCommandType.Telegraph or SimCommandType.Rpm or SimCommandType.Thruster
                or SimCommandType.Autopilot or SimCommandType.SteeringMode)
            ResetBnwas(ContractJson.EnumName(cmd.Type), source, explicitReset: false);
        switch (cmd.Type)
        {
            case SimCommandType.Rudder:
                if (cmd.ValueAsDouble() is { } deg)
                {
                    var before = _act.RudderOrderDeg;
                    // 手動舵令解除自動舵:學員指令視為一次 autopilotOff(autopilotSwitches、必要事件 expect autopilotOff);時間表 ownShip 覆寫不觀察
                    if (fromInstructor && Autopilot.Enabled) Observe(ActionKinds.AutopilotOff, 1.0);
                    Autopilot.Enabled = false;
                    // 隨動舵令 = 使用舵輪:autopilot / nfu 模式回到 fu(emergency 保持,電話下令仍以 rudder 指令表示)
                    if (_steeringMode is SteeringMode.Autopilot or SteeringMode.Nfu) SetSteeringMode(SteeringMode.Fu, null, source, itemId, observe: false);
                    _act.RudderOrderDeg = Units.Clamp(deg, -_act.Parameters.RudderMaxDeg, _act.Parameters.RudderMaxDeg);
                    if (fromInstructor && Math.Abs(_act.RudderOrderDeg - before) >= 1.0) Observe(ActionKinds.RudderOrder, Math.Abs(_act.RudderOrderDeg - before));
                }
                break;

            case SimCommandType.Telegraph:
                if (cmd.ValueAsString() is { } s && Enum.TryParse<TelegraphOrder>(s, ignoreCase: true, out var order))
                {
                    var changed = order != _telegraph;
                    SetTelegraph(order);
                    if (changed && fromInstructor) Observe(ActionKinds.TelegraphChange, 1.0);
                }
                break;

            case SimCommandType.Rpm:
                if (cmd.ValueAsDouble() is { } rpm)
                {
                    var limit = _act.Parameters.MaxRpm ?? Ship.Engine.McrRpm;
                    SetRpmOrder(Units.Clamp(rpm, -limit, limit));
                    if (fromInstructor) Observe(ActionKinds.RpmOrder, Math.Abs(rpm));
                }
                break;

            case SimCommandType.Thruster:
                if (cmd.ValueAsDouble() is { } thr)
                {
                    var before = _act.ThrusterOrder;
                    _act.ThrusterOrder = Units.Clamp(thr, -1.0, 1.0);
                    if (fromInstructor && Math.Abs(_act.ThrusterOrder - before) >= 0.05) Observe(ActionKinds.ThrusterOrder, Math.Abs(_act.ThrusterOrder - before));
                }
                break;

            case SimCommandType.Autopilot:
            {
                // 應急操舵(舵機房操舵)物理上接不到自動舵:emergency 模式下的啟用要求整個拒絕(狀態不變、發 commandRejected),需先 steeringMode fu;解除照常(D35)
                if (_steeringMode == SteeringMode.Emergency && cmd.ArgAsBool("enabled") == true)
                {
                    RejectCommand(cmd, source, itemId, CommandRejectReasons.EmergencySteering);
                    break;
                }
                // 自動舵失效或電羅經失效(無航向輸入)時不能啟用;狀態不變(設計文件 sensors-alerts.md 第 2.3 節)
                if (!AutopilotAvailable && cmd.ArgAsBool("enabled") == true)
                {
                    RejectCommand(cmd, source, itemId, CommandRejectReasons.AutopilotUnavailable);
                    break;
                }
                var wasEnabled = Autopilot.Enabled;
                var oldHeading = Autopilot.HeadingRad;
                if (cmd.ArgAsBool("enabled") is { } enabled) Autopilot.Enabled = enabled;
                if (cmd.ArgAsDouble("heading") is { } hdg) Autopilot.HeadingRad = Units.DegToRad(Units.NormalizeHeadingDeg(hdg));
                if (cmd.ArgAsDouble("rotLimit") is { } lim) Autopilot.RotLimitDegPerMin = Math.Max(1.0, Math.Abs(lim));
                if (fromInstructor)
                {
                    if (Autopilot.Enabled != wasEnabled) Observe(Autopilot.Enabled ? ActionKinds.AutopilotOn : ActionKinds.AutopilotOff, 1.0);
                    ObserveAutopilotHeadingChange(oldHeading);
                }
                else Raise(NewEvent(SimEventKinds.AutopilotOverride, source, itemId, null, null, cmd.Args));
                // 操舵模式與自動舵同步:啟用 → autopilot;解除 → fu(autopilotOff 已觀察,不再重複 steeringModeChange)
                if (Autopilot.Enabled) SetSteeringMode(SteeringMode.Autopilot, null, source, itemId, observe: false);
                else if (_steeringMode == SteeringMode.Autopilot) SetSteeringMode(SteeringMode.Fu, null, source, itemId, observe: false);
                break;
            }

            case SimCommandType.SteeringMode:
            {
                var modeName = cmd.ValueAsString() ?? cmd.ArgAsString("mode");
                if (modeName is null || !Enum.TryParse<SteeringMode>(modeName, ignoreCase: true, out var mode)) break;
                // emergency → autopilot 與 autopilot 指令同樣拒絕(D35)
                if (mode == SteeringMode.Autopilot && _steeringMode == SteeringMode.Emergency)
                {
                    RejectCommand(cmd, source, itemId, CommandRejectReasons.EmergencySteering);
                    break;
                }
                if (mode == SteeringMode.Autopilot && !AutopilotAvailable)
                {
                    RejectCommand(cmd, source, itemId, CommandRejectReasons.AutopilotUnavailable);
                    break;
                }
                var pump = SteeringPumps.Normalize(cmd.Arg("pump"));
                var wasAutopilot = Autopilot.Enabled;
                var oldHeading = Autopilot.HeadingRad;
                var headingGiven = false;
                if (mode == SteeringMode.Autopilot)
                {
                    if (cmd.ArgAsDouble("heading") is { } hdg) { Autopilot.HeadingRad = Units.DegToRad(Units.NormalizeHeadingDeg(hdg)); headingGiven = true; }
                    else if (!wasAutopilot) Autopilot.HeadingRad = IndicatedHeadingRad(); // 未給航向時保持目前羅經讀數(電羅經指示值,與自動舵/偏航警報同一來源;不視為改航向動作)
                    if (cmd.ArgAsDouble("rotLimit") is { } lim) Autopilot.RotLimitDegPerMin = Math.Max(1.0, Math.Abs(lim));
                }
                SetSteeringMode(mode, pump, source, itemId, observe: fromInstructor);
                if (fromInstructor && Autopilot.Enabled != wasAutopilot) Observe(Autopilot.Enabled ? ActionKinds.AutopilotOn : ActionKinds.AutopilotOff, 1.0);
                // 帶 heading 的 steeringMode autopilot 與 autopilot {heading} 等價:同樣觀察 autopilotHeading(會遇首次行動、必要事件)
                if (headingGiven && fromInstructor) ObserveAutopilotHeadingChange(oldHeading);
                if (mode == SteeringMode.Nfu) ApplyNfu(cmd.ArgAsString("direction"), fromInstructor);
                break;
            }

            case SimCommandType.Freeze:
                if (!Frozen) Raise(NewEvent(SimEventKinds.Frozen, source, itemId, null, cmd.ArgAsString("reason"), cmd.Args));
                Frozen = true;
                break;

            case SimCommandType.Resume:
                if (Frozen && !Ended) Raise(NewEvent(SimEventKinds.Resumed, source, itemId));
                if (!Ended) Frozen = false;
                break;

            case SimCommandType.Reset:
                Initialize();
                break;

            case SimCommandType.TimeScale:
                if (cmd.ValueAsDouble() is { } scale)
                {
                    if (scale <= 0)
                    {
                        // 倍率 ≤ 0 視同凍結:與 freeze 指令同路徑(發 frozen 事件、計入 general.freezes),之後 resume 才有對應的 resumed
                        if (!Frozen) Raise(NewEvent(SimEventKinds.Frozen, source, itemId, null, "timeScale ≤ 0", new { timeScale = scale }));
                        Frozen = true;
                    }
                    else TimeScale = Units.Clamp(scale, 0.1, Options.MaxTimeScale);
                    Raise(NewEvent(SimEventKinds.TimeScaleChanged, source, itemId, null, null, new { timeScale = TimeScale, frozen = Frozen }));
                }
                break;

            case SimCommandType.LoadScenario:
            {
                // 引擎不讀檔:由主控程序在事件處理者內載入並呼叫 LoadScenario(成功)或 RejectScenarioLoad(失敗);
                // 處理者回來後兩者皆未發生(無訂閱者、或處理者吞掉例外未回報)也視為拒絕,教官站才不會停在「已載入」。
                // 拒絕時引擎狀態不變;紀錄器不寫 loadScenario input 行(RecordWriter.OnCommand),拒絕事件也不進序號/雜湊,重播不受影響
                var argsId = cmd.Arg("id") is { ValueKind: System.Text.Json.JsonValueKind.String } idEl ? idEl.GetString() : null;
                var requestedId = argsId;   // 拒絕回報用的情境名稱(args.id、內嵌物件自帶 id 或路徑);處理者仍只收 args.id
                _scenarioLoadOutcome = ScenarioLoadOutcome.Pending;
                _scenarioLoadSource = source;
                try
                {
                    string? requestedPath = null;
                    if (cmd.Arg("scenario") is { ValueKind: System.Text.Json.JsonValueKind.Object } inline)
                    {
                        // 內嵌情境(靠離泊預演自動組題):args.id 為紀錄用名稱;value/args.path 忽略
                        if (requestedId is null && inline.TryGetProperty("id", out var scId) && scId.ValueKind == System.Text.Json.JsonValueKind.String) requestedId = scId.GetString();
                        InlineScenarioLoadRequested?.Invoke(this, inline, argsId);
                    }
                    else if ((cmd.ValueAsString() ?? (cmd.Arg("path") is { ValueKind: System.Text.Json.JsonValueKind.String } p ? p.GetString() : null)) is { } path)
                    {
                        requestedPath = path;
                        requestedId ??= path;
                        ScenarioLoadRequested?.Invoke(this, path);
                    }
                    else if (cmd.Arg("scenario") is { } notObject && notObject.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                        RejectScenarioLoad(requestedId, new[] { new ScenarioIssue(ScenarioIssueSeverity.Error, "args.scenario", "內嵌情境須為物件") }, "command");
                    else
                        RejectScenarioLoad(requestedId, new[] { new ScenarioIssue(ScenarioIssueSeverity.Error, "loadScenario", "缺少情境路徑(value / args.path)或內嵌情境物件(args.scenario)") }, "command");
                    if (_scenarioLoadOutcome == ScenarioLoadOutcome.Pending)
                        RejectScenarioLoad(requestedId, new[] { new ScenarioIssue(ScenarioIssueSeverity.Error, "loadScenario", "主控程序未處理情境載入(無載入處理者,或處理者未回報結果)") }, "command", requestedPath);
                }
                finally { _scenarioLoadOutcome = ScenarioLoadOutcome.None; }
                break;
            }

            case SimCommandType.SetEnvironment:
                ApplyEnvironment(cmd, source, itemId);
                break;

            case SimCommandType.InjectFault:
                if (cmd.FaultName() is { } f && f.Length > 0)
                {
                    // tugLineBreak 指定 args.id 時須為已定義且仍可用的拖船(且動力學支援外力);否則拒絕、不加入 faults[](避免故障亮起卻沒有作用):
                    // 已離開(tugControl mode unavailable)的拖船沒有拖纜可斷 → tugUnavailable(與 tugControl 對它的拒絕原因相同)
                    if (f == FaultNames.TugLineBreak && FaultArgString(cmd, "id") is { } tugId)
                    {
                        if (!_external.Supported) { RejectCommand(cmd, source, itemId, CommandRejectReasons.ExternalForcesUnsupported); break; }
                        if (_external.Tug(tugId) is not { } lineTug) { RejectCommand(cmd, source, itemId, CommandRejectReasons.UnknownTug); break; }
                        if (lineTug.Status == TugStatus.Unavailable) { RejectCommand(cmd, source, itemId, CommandRejectReasons.TugUnavailable); break; }
                    }
                    var already = _faults.Contains(f);
                    // 感測器層先記下注入時的指示值(凍結/保持值、漂移起點),再把名稱加入 faults[]
                    if (FaultNames.Modelled.Contains(f)) _sensors.OnFaultInjected(f, cmd.Arg("args"), Tick, TruthSample(), already);
                    if (!already) _faults.Add(f);
                    // 主機起動失敗/延遲:記下 args(delay_s、attempts)並重設計數(重複注入亦重設)
                    if (f == FaultNames.EngineStartFailure) _act.InjectStartFailure(FaultArgDouble(cmd, "delay_s"), FaultArgDouble(cmd, "attempts") is { } at ? (int)Math.Round(at) : null);
                    Raise(NewEvent(SimEventKinds.FaultInjected, source, itemId, null, f, new { fault = f, args = cmd.Arg("args"), alreadyActive = already }));
                    if (Autopilot.Enabled && !AutopilotAvailable) TripAutopilot(f);
                    // 拖纜斷裂(args.id 指定拖船;重複注入可斷另一艘)
                    if (f == FaultNames.TugLineBreak && _external.PartTugLine(FaultArgString(cmd, "id")) is { } parted)
                        Raise(NewEvent(parted.Kind, SimEventSources.Engine, parted.ItemId, null, parted.Message, parted.Data, visible: true));
                }
                break;

            case SimCommandType.ClearFault:
            {
                var cf = cmd.FaultName();
                if (cf is not null && !string.Equals(cf, "all", StringComparison.OrdinalIgnoreCase))
                {
                    if (_faults.Remove(cf))
                    {
                        _sensors.OnFaultCleared(cf);
                        if (cf == FaultNames.TugLineBreak) _external.RestoreTugLines();
                        if (cf == FaultNames.EngineStartFailure) _act.ClearStartFailure();
                        Raise(NewEvent(SimEventKinds.FaultCleared, source, itemId, null, cf, new { fault = cf }));
                    }
                }
                else
                {
                    var all = _faults.ToArray();
                    _faults.Clear();
                    foreach (var name in all) _sensors.OnFaultCleared(name);
                    if (all.Contains(FaultNames.TugLineBreak)) _external.RestoreTugLines();
                    if (all.Contains(FaultNames.EngineStartFailure)) _act.ClearStartFailure();
                    foreach (var name in all) Raise(NewEvent(SimEventKinds.FaultCleared, source, itemId, null, name, new { fault = name }));
                }
                break;
            }

            case SimCommandType.Snapshot:
                TakeSnapshot();
                break;

            case SimCommandType.Restore:
                RestoreFromHistory(cmd.ArgAsDouble("tick") is { } t ? (long)t : null);
                break;

            case SimCommandType.TargetControl:
                _traffic.ApplyControl(cmd, Tick);
                break;

            case SimCommandType.TriggerTimeline:
            {
                var id = cmd.ArgAsString("id") ?? cmd.ValueAsString() ?? "";
                var force = cmd.ArgAsBool("force") ?? false;
                var (ok, reason) = _timeline?.TriggerManual(id, force, Tick) ?? (false, "無時間表");
                Raise(NewEvent(SimEventKinds.TimelineManual, SimEventSources.Instructor, id, null, reason, new { ok, force, reason }));
                break;
            }

            case SimCommandType.Acknowledge:
            {
                var kind = cmd.ArgAsString("kind") ?? "other";
                var ackItem = cmd.ArgAsString("itemId");
                Raise(NewEvent(SimEventKinds.Acknowledge, SimEventSources.Instructor, ackItem, null, cmd.ArgAsString("note") ?? kind,
                    new { kind, role = cmd.ArgAsString("role"), note = cmd.ArgAsString("note"), itemId = ackItem }));
                break;
            }

            case SimCommandType.EndExercise:
                EndExercise(cmd.ArgAsString("reason"), source, itemId);
                break;

            case SimCommandType.GetAssessment:
                break; // 查詢由主控程序直接回覆;引擎不處理

            case SimCommandType.AlertAcknowledge:
            {
                // args.station(bridge|portWing|stbdWing|ecr)省略 = bridge:Category A 與已轉移責任的警報只能在負責站確認(MSC.302(87) 第 6 節)
                var id = AlertIdOf(cmd);
                var station = cmd.ArgAsString("station");
                var r = _alerts.Acknowledge(Tick, id, station);
                if (r < 0) RejectCommand(cmd, source, itemId, AlertRejectReason(r));
                else AlertBridgeOperation(cmd, station, fromInstructor, source);
                break;
            }

            case SimCommandType.AlertTransfer:
            {
                // 責任轉移(MSC.302(87) responsibility transferred):只有 AlertCatalogue.TransferTargets 允許的站;事件 alertTransferred(source engine,同其他警報事件)
                var r = _alerts.Transfer(Tick, AlertIdOf(cmd), cmd.ArgAsString("station"));
                if (r < 0) RejectCommand(cmd, source, itemId, AlertRejectReason(r));
                break;
            }

            case SimCommandType.RadarControl:
            {
                // 雷達服務轉送的學員雷達操作(規劃書第 8.3 節第 7 類):只寫 input 行(CommandApplied)並發事件供評估/講評;不影響物理;
                // 事件序號與 acknowledge 同類(重播時由 input 行重發,序列一致)
                var radar = cmd.ArgAsString("radar") ?? "x";
                var rc = cmd.ArgAsString("cmd") ?? "?";
                var ok = cmd.ArgAsBool("ok") ?? true;
                Raise(NewEvent(SimEventKinds.RadarControl, SimEventSources.Instructor, radar, null, $"雷達 {radar.ToUpperInvariant()} {rc}{(ok ? "" : "(失敗)")}", cmd.Args));
                break;
            }

            case SimCommandType.AlertSilence:
            {
                // 靜音不受站別限制;args.station 只決定是否視為駕駛台操作(機控室頁面的靜音不重設 BNWAS)
                var id = AlertIdOf(cmd);
                if (_alerts.Silence(Tick, id) < 0) RejectCommand(cmd, source, itemId, CommandRejectReasons.UnknownAlert);
                else AlertBridgeOperation(cmd, cmd.ArgAsString("station"), fromInstructor, source);
                break;
            }

            case SimCommandType.BnwasMode:
            {
                var name = cmd.Value is { ValueKind: JsonValueKind.String } ? cmd.ValueAsString() : cmd.ArgAsString("mode");
                if (name is null || !Enum.TryParse<BnwasMode>(name, ignoreCase: true, out var bm)) break;
                _bnwasSource = source;
                try { _bnwas.SetMode(bm, cmd.ArgAsDouble("dormant_s"), cmd.ArgAsDouble("stage3_s"), Tick); }
                finally { _bnwasSource = SimEventSources.Engine; }
                break;
            }

            case SimCommandType.BnwasReset:
                ResetBnwas("bnwasReset", source, explicitReset: true);
                break;

            // 外力指令(設計文件 physics-tugs-anchors-mooring.md 第 6 節):接受時發 tugCommand / anchorLetGo / anchorCommand / mooringCommand,否則 commandRejected
            case SimCommandType.TugControl:
                ApplyExternalOutcome(cmd, source, itemId, _external.TugControl(
                    cmd.ArgAsString("id") ?? cmd.ValueAsString(), cmd.ArgAsString("mode"),
                    cmd.ArgAsDouble("direction_deg") ?? cmd.ArgAsDouble("direction"), cmd.ArgAsDouble("power_pct") ?? cmd.ArgAsDouble("power")));
                break;

            case SimCommandType.Anchor:
                ApplyExternalOutcome(cmd, source, itemId, _external.AnchorCommand(
                    cmd.ArgAsString("side") ?? cmd.ValueAsString(), cmd.ArgAsString("action"), cmd.ArgAsDouble("targetLength_shackles"), cmd.ArgAsDouble("length_m"),
                    Motion, Environment.SampleAt(Motion.X, Motion.Y).WaterDepthM));
                break;

            case SimCommandType.Mooring:
                ApplyExternalOutcome(cmd, source, itemId, _external.MooringCommand(
                    cmd.ArgAsString("lineId") ?? cmd.ArgAsString("id"), cmd.ArgAsString("action"), cmd.ArgAsString("bollard"), cmd.ArgAsDouble("tension_t"), cmd.ArgAsDouble("length_m"),
                    Motion, Environment.SampleAt(Motion.X, Motion.Y)));
                break;
        }
    }

    /// <summary>外力指令結果:拒絕 → commandRejected;接受 → 對應事件(itemId = 拖船 id / 錨側 / 纜 id,source 同指令)。</summary>
    private void ApplyExternalOutcome(SimCommand cmd, string source, string? itemId, ExternalCommandOutcome r)
    {
        if (r.Reject is { } reason) RejectCommand(cmd, source, itemId, reason);
        else if (r.Kind is { } kind) Raise(NewEvent(kind, source, r.ItemId ?? itemId, null, r.Message, r.Data));
    }

    /// <summary>injectFault {name, args} 的 args 數值欄位(數值或數值字串)。</summary>
    private static double? FaultArgDouble(SimCommand cmd, string name)
        => cmd.Arg("args") is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v) ? SimCommand.AsDouble(v) : null;

    /// <summary>injectFault {name, args} 的 args 字串欄位。</summary>
    private static string? FaultArgString(SimCommand cmd, string name)
        => cmd.Arg("args") is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>警報指令的 id(value 字串或 args.id;省略 = 全部)。</summary>
    private static string? AlertIdOf(SimCommand cmd)
        => cmd.Value is { ValueKind: JsonValueKind.String } ? cmd.ValueAsString() : cmd.ArgAsString("id");

    /// <summary><see cref="AlertManager"/> 負值結果 → commandRejected 的 reason。</summary>
    private static string AlertRejectReason(int r) => r switch
    {
        AlertManager.RejectStationRequired => CommandRejectReasons.AlertStationRequired,
        AlertManager.RejectNotTransferable => CommandRejectReasons.AlertNotTransferable,
        AlertManager.RejectUnknownStation => CommandRejectReasons.UnknownStation,
        _ => CommandRejectReasons.UnknownAlert,
    };

    /// <summary>
    /// 核心接受的警報確認/靜音視為駕駛台操作重設 BNWAS(MSC.128(75) 4.1.4:駕駛台上值班官可辨識的操作):只限指令佇列(學員/教官)且 <paramref name="station"/>
    /// 屬駕駛台三站(省略 = bridge、兩翼);機控室(ecr)或未知站的操作不是駕駛台操作。被拒絕的指令(alertStationRequired/unknownAlert/unknownStation)由呼叫端不經此處,
    /// 因此誤按也不會解除值班警報(AlertStationTests.EcrAcknowledgeDoesNotResetBnwas / RejectedAcknowledgeDoesNotResetBnwas)。
    /// </summary>
    private void AlertBridgeOperation(SimCommand cmd, string? station, bool fromInstructor, string source)
    {
        if (fromInstructor && AlertStations.IsBridgeStation(station)) ResetBnwas(ContractJson.EnumName(cmd.Type), source, explicitReset: false);
    }

    private void ResetBnwas(string by, string source, bool explicitReset)
    {
        _bnwasSource = source;
        try { _bnwas.Reset(Tick, Autopilot.Enabled, by, explicitReset); }
        finally { _bnwasSource = SimEventSources.Engine; }
    }

    /// <summary>
    /// 自動舵因故障/航向無效脫開(autopilotFailure、gyroFailure):發 autopilotTripped {reason, fault},舵令同步到目前舵角(舵停在原處)並切回 fu
    /// (steeringModeChanged,source = engine;不視為學員動作)。
    /// </summary>
    private void TripAutopilot(string fault)
    {
        var reason = fault == FaultNames.AutopilotFailure ? "autopilotFailure" : "headingInvalid";
        Raise(NewEvent(SimEventKinds.AutopilotTripped, SimEventSources.Engine, null, null, reason, new { reason, fault }, visible: true));
        HoldRudder();
        SetSteeringMode(SteeringMode.Fu, null, SimEventSources.Engine, null, observe: false);
    }

    /// <summary>目前的真值取樣(對外單位;感測器故障注入時記錄保持值用)。</summary>
    private SensorTruth TruthSample()
    {
        var m = Motion;
        var sample = Environment.SampleAt(m.X, m.Y);
        var (velE, velN) = EnvironmentMath.GroundVelocity(m.U, m.V, m.Psi, sample.CurrentEastMps, sample.CurrentNorthMps);
        var (sogMps, cogRad) = EnvironmentMath.CogSog(velE, velN, m.Psi);
        var ukc = EnvironmentMath.UnderKeelClearance(sample.WaterDepthM, Ship.GetLoading(Loading).MaxDraft_m, SquatAt(sample.WaterDepthM));
        return new SensorTruth(Units.RadToDeg(m.Psi), m.X, m.Y, Units.NormalizeHeadingDeg(Units.RadToDeg(cogRad)), Units.MpsToKn(sogMps), Units.MpsToKn(m.U), ukc);
    }

    /// <summary>電羅經是否受故障影響(否則自動舵直接用 Motion.Psi,與舊版位元一致)。</summary>
    private bool GyroAffected => _sensors.State.GyroDriftStartTick >= 0 || _sensors.State.GyroDriftBaseDeg != 0
                                 || _faults.Contains(FaultNames.GyroFreeze) || _faults.Contains(FaultNames.GyroFailure);

    /// <summary>自動舵看到的航向(弧度):電羅經指示值(規劃書第 8.1 節 E08:漂移時自動舵跟隨錯誤航向)。</summary>
    private double IndicatedHeadingRad()
        => GyroAffected ? Units.DegToRad(_sensors.GyroHeading(Units.RadToDeg(Motion.Psi), Tick).Deg) : Motion.Psi;

    /// <summary>警報條件輸入(指示值)並更新警報狀態機。</summary>
    private void UpdateAlerts()
    {
        var truthDeg = Units.RadToDeg(Motion.Psi);
        var (hdg, hdgValid) = _sensors.GyroHeading(truthDeg, Tick);
        double dbk = 0;
        var depthValid = false;
        if (Scenario.Alerts.ShallowWaterM is not null)
        {
            depthValid = !_faults.Contains(FaultNames.EchoSounderFailure);
            dbk = depthValid ? UnderKeelClearance() : 0;
        }
        _alerts.Update(Tick, new AlertInputs(_faults, _sensors.LogInvalid, Autopilot.Enabled,
            Units.NormalizeHeadingDeg(Units.RadToDeg(Autopilot.HeadingRad)), hdg, hdgValid, _sensors.MagneticHeading(truthDeg), dbk, depthValid, _bnwas.State.Stage));
    }

    /// <summary>
    /// 切換操舵模式(規劃書第 7.2 節;情境引擎設計 D32、D35):autopilot ⇔ Autopilot.Enabled;進出 nfu 時先把舵令同步到目前舵角(舵停在原處:
    /// 進入 nfu 時先前的隨動舵令失效、由推桿決定;離開 nfu 時推桿的滿舵舵令失效,之後由 rudder 指令或自動舵給新舵令),離開 nfu 時推動方向歸零;
    /// 模式或泵改變時發 steeringModeChanged {from, to, pump},<paramref name="observe"/> 時並以 steeringModeChange 動作餵評估(必要事件、故障應變)。
    /// </summary>
    private void SetSteeringMode(SteeringMode to, string? pump, string source, string? itemId, bool observe)
    {
        var from = _steeringMode;
        var pumpFrom = _steeringPump;
        if (pump is not null) _steeringPump = pump;
        _steeringMode = to;
        if (from != to && (from == SteeringMode.Nfu || to == SteeringMode.Nfu)) HoldRudder();
        if (to != SteeringMode.Nfu) _nfuDirection = 0;
        Autopilot.Enabled = to == SteeringMode.Autopilot;
        if (from == to && pumpFrom == _steeringPump) return;
        Raise(NewEvent(SimEventKinds.SteeringModeChanged, source, itemId, null, $"{ContractJson.EnumName(from)} → {ContractJson.EnumName(to)}",
            new { from = ContractJson.EnumName(from), to = ContractJson.EnumName(to), pump = _steeringPump }));
        if (observe && from != to) Observe(ActionKinds.SteeringModeChange, 1.0);
    }

    /// <summary>
    /// NFU 推桿:port/starboard 時舵令設為該側滿舵(舵以舵機速率移動),stop 時舵令設為目前舵角(舵停在原處);未給方向則不變(首次進入 nfu 已由
    /// <see cref="SetSteeringMode"/> 同步舵令,等同 stop);不明方向視同 stop(推桿放開,舵不會沿用舊舵令繼續走;D35)。幅度 ≥ 1° 視為一次舵令。
    /// </summary>
    private void ApplyNfu(string? direction, bool fromInstructor)
    {
        var d = direction?.ToLowerInvariant() switch { "port" => -1, "starboard" => 1, "stop" => 0, null => _nfuDirection, _ => 0 };
        _nfuDirection = d;
        var before = _act.RudderOrderDeg;
        _act.RudderOrderDeg = d == 0 ? Units.Clamp(_act.RudderDeg, -_act.Parameters.RudderMaxDeg, _act.Parameters.RudderMaxDeg) : d * _act.Parameters.RudderMaxDeg;
        if (fromInstructor && Math.Abs(_act.RudderOrderDeg - before) >= 1.0) Observe(ActionKinds.RudderOrder, Math.Abs(_act.RudderOrderDeg - before));
    }

    /// <summary>
    /// 本步的舵機速率(度/秒):emergency 為 <see cref="EmergencyRudderRateDegPerSec"/>;其他模式為參數速率依泵數換算
    /// (particulars hardOverTime35_onePump_s / _twoPumps_s 相對 <see cref="ShipParticulars.RudderParticulars.HardOverTime35Seconds"/>;無資料時不變)。
    /// </summary>
    private double RudderRateForStep()
    {
        // 舵機泵失效:所有模式速率減半(fault-catalogue steeringPumpFailure)
        var pumpFactor = _faults.Contains(FaultNames.SteeringPumpFailure) ? 0.5 : 1.0;
        return pumpFactor * RudderRateNominal();
    }

    private double RudderRateNominal()
    {
        if (_steeringMode == SteeringMode.Emergency) return EmergencyRudderRateDegPerSec;
        var nominal = Ship.Rudder.HardOverTime35Seconds;
        var sg = Ship.Rudder.SteeringGear;
        var pumpTime = _steeringPump == SteeringPumps.Both ? sg?.HardOverTime35_twoPumps_s : sg?.HardOverTime35_onePump_s;
        var scale = pumpTime is { } t && t > 0 && nominal > 0 ? nominal / t : 1.0;
        return _act.Parameters.RudderRateDegPerSec * scale;
    }

    /// <summary>學員指令觀察 → 評估模組(會遇首次行動、必要事件、故障應變、操縱指標)。</summary>
    /// <summary>舵令同步到目前舵角(夾在滿舵範圍內):舵停在原處。</summary>
    private void HoldRudder() => _act.RudderOrderDeg = Units.Clamp(_act.RudderDeg, -_act.Parameters.RudderMaxDeg, _act.Parameters.RudderMaxDeg);

    /// <summary>自動舵啟用中且設定航向較 <paramref name="oldHeadingRad"/> 改變 ≥ 1° 時觀察一次 autopilotHeading(會遇首次行動以 ≥ 5° 判定);autopilot 與 steeringMode autopilot 共用。</summary>
    private void ObserveAutopilotHeadingChange(double oldHeadingRad)
    {
        if (!Autopilot.Enabled) return;
        var dh = Math.Abs(Units.RadToDeg(Units.WrapRadPi(Autopilot.HeadingRad - oldHeadingRad)));
        if (dh >= 1.0) Observe(ActionKinds.AutopilotHeading, dh);
    }

    /// <summary>拒絕指令(狀態不變):發 commandRejected {command, reason}(source 同指令來源),供教官站/紀錄說明為何無效。</summary>
    private void RejectCommand(SimCommand cmd, string source, string? itemId, string reason)
        => Raise(NewEvent(SimEventKinds.CommandRejected, source, itemId, null, $"{ContractJson.EnumName(cmd.Type)}:{reason}",
            new { command = ContractJson.EnumName(cmd.Type), reason, args = cmd.Args }));

    private void Observe(string kind, double magnitude)
    {
        var a = new ObservedAction(kind, Tick, magnitude);
        _assessment?.OnAction(a);
        _rescore?.OnAction(a);
    }

    /// <summary>結束練習:凍結、定稿 summary、發 exerciseEnded(之後 resume 無效)。</summary>
    private void EndExercise(string? reason, string source, string? itemId)
    {
        if (Ended) return;
        Ended = true;
        Frozen = true;
        _assessment?.Finalize(Tick);
        _rescore?.Finalize(Tick);
        Raise(NewEvent(SimEventKinds.ExerciseEnded, source, itemId, null, reason, new { reason }));
    }

    /// <summary>執行時間表動作(情境引擎設計第 1.1.2 節);不寫 input 行,事件 source = timeline。</summary>
    internal void ExecuteTimelineAction(TimelineItem item, TimelineAction action, int index)
    {
        var id = item.Id;
        var visible = item.Visibility == TimelineVisibility.All;
        switch (action.Kind)
        {
            case "injectFault":
                if (action.AsString() is { } fault)
                    ApplyInternal(action.FaultArgs() is { } fa ? SimCommand.InjectFault(fault, JsonSerializer.Deserialize<object>(fa.GetRawText())) : SimCommand.InjectFault(fault), SimEventSources.Timeline, id);
                break;
            case "clearFault":
                ApplyInternal(SimCommand.ClearFault(action.AsString() ?? "all"), SimEventSources.Timeline, id);
                break;
            case "setEnvironment":
                ApplyInternal(new SimCommand { Type = SimCommandType.SetEnvironment, Args = action.Payload }, SimEventSources.Timeline, id);
                break;
            case "targetControl":
                ApplyInternal(new SimCommand { Type = SimCommandType.TargetControl, Args = action.Payload }, SimEventSources.Timeline, id);
                break;
            case "tugControl":
                // 與指令相同的 args(拖船縮減:mode unavailable);拒絕時 commandRejected source timeline
                ApplyInternal(new SimCommand { Type = SimCommandType.TugControl, Args = action.Payload }, SimEventSources.Timeline, id);
                break;
            case "voiceScript":
                Raise(NewEvent(SimEventKinds.VoiceScript, SimEventSources.Timeline, id, null, action.AsVoiceScript()?.Text, action.Payload, visible));
                break;
            case "message":
            {
                var msg = action.AsMessage();
                var toAll = string.Equals(msg?.To, "all", StringComparison.OrdinalIgnoreCase);
                Raise(NewEvent(SimEventKinds.Message, SimEventSources.Timeline, id, null, msg?.Text, msg is null ? action.Payload : SimEvent.DataOf(msg), visible || toAll));
                break;
            }
            case "phase":
                if (action.AsString() is { } phase) SetPhase(phase, SimEventSources.Timeline, id);
                break;
            case "freeze":
                ApplyInternal(new SimCommand { Type = SimCommandType.Freeze, Args = action.Payload.ValueKind == JsonValueKind.Object ? action.Payload : null }, SimEventSources.Timeline, id);
                break;
            case "autopilot":
                ApplyInternal(new SimCommand { Type = SimCommandType.Autopilot, Args = action.Payload }, SimEventSources.Timeline, id);
                break;
            case "ownShip":
            {
                if (action.NumberField("rudder") is { } r) ApplyInternal(SimCommand.Rudder(r), SimEventSources.Timeline, id);
                if (action.Field("telegraph") is { } tg && Enum.TryParse<TelegraphOrder>(tg, ignoreCase: true, out var order)) ApplyInternal(SimCommand.Telegraph(order), SimEventSources.Timeline, id);
                if (action.NumberField("rpm") is { } rpm) ApplyInternal(SimCommand.Rpm(rpm), SimEventSources.Timeline, id);
                if (action.NumberField("thruster") is { } th) ApplyInternal(SimCommand.Thruster(th), SimEventSources.Timeline, id);
                Raise(NewEvent(SimEventKinds.OwnShipOverride, SimEventSources.Timeline, id, null, null, action.Payload));
                break;
            }
            case "timeScale":
                if (action.AsNumber() is { } ts) ApplyInternal(SimCommand.TimeScale(ts), SimEventSources.Timeline, id);
                break;
            case "arm" or "disarm":
            {
                var target = action.AsString() ?? "";
                var armed = action.Kind == "arm";
                _timeline?.SetArmed(target, armed);
                Raise(NewEvent(armed ? SimEventKinds.TimelineArmed : SimEventKinds.TimelineDisarmed, SimEventSources.Timeline, target, null, null, new { by = id }));
                break;
            }
            case "endExercise":
                EndExercise(action.Field("reason") ?? action.AsString(), SimEventSources.Timeline, id);
                break;
        }
    }

    private void SetTelegraph(TelegraphOrder order)
    {
        _telegraph = order;
        SetRpmOrder(Ship.TelegraphRpm(order));
        _assessment?.Metrics.OnTelegraph(order, Tick);
    }

    private void SetRpmOrder(double rpm)
    {
        // 由停俥起動或換向視為一次起動(啟動空氣次數,規劃書第 6.2 節)
        var wasStopped = _act.RpmOrder == 0.0 || Math.Sign(_act.RpmOrder) != Math.Sign(rpm);
        if (rpm != 0.0 && wasStopped && _startsRemaining is { } n && n > 0) _startsRemaining = n - 1;
        _act.RpmOrder = rpm;
    }

    /// <summary>
    /// 環境設定(指令與時間表動作共用):ramp_s &gt; 0 時各通道自目前值線性漸變(角度最短弧)到目標值(情境引擎設計第 3.5 節),
    /// 否則立即設定並移除該通道的漸變;發 environmentChange 事件(data 含 ramp_s 與各通道 from/to)。
    /// </summary>
    private void ApplyEnvironment(SimCommand cmd, string source, string? itemId)
    {
        var rampS = Math.Max(0.0, cmd.ArgAsDouble("ramp_s") ?? 0.0);
        var targets = new List<(RampChannel Channel, double To)>();
        if (cmd.Arg("wind") is { } wind && wind.ValueKind == JsonValueKind.Object)
        {
            if (wind.TryGetProperty("trueSpeed", out var ws) && SimCommand.AsDouble(ws) is { } wsv) targets.Add((RampChannel.WindSpeed, Units.KnToMps(Math.Max(0, wsv))));
            if (wind.TryGetProperty("trueDir", out var wd) && SimCommand.AsDouble(wd) is { } wdv) targets.Add((RampChannel.WindDir, Units.DegToRad(Units.NormalizeHeadingDeg(wdv))));
            if (wind.TryGetProperty("gustiness", out var wg) && SimCommand.AsDouble(wg) is { } wgv) targets.Add((RampChannel.Gustiness, Units.Clamp(wgv, 0, 1)));
        }
        var currentGiven = false;
        if (cmd.Arg("current") is { } cur && cur.ValueKind == JsonValueKind.Object)
        {
            if (cur.TryGetProperty("set", out var cs) && SimCommand.AsDouble(cs) is { } csv) { targets.Add((RampChannel.CurrentSet, Units.DegToRad(Units.NormalizeHeadingDeg(csv)))); currentGiven = true; }
            if (cur.TryGetProperty("drift", out var cd) && SimCommand.AsDouble(cd) is { } cdv) { targets.Add((RampChannel.CurrentDrift, Units.KnToMps(Math.Max(0, cdv)))); currentGiven = true; }
        }
        if (cmd.ArgAsDouble("waterDepth") is { } depth && depth > 0) targets.Add((RampChannel.WaterDepth, depth));
        if (cmd.ArgAsDouble("visibility_nm") is { } vis && vis > 0) targets.Add((RampChannel.Visibility, vis));
        if (targets.Count == 0) return;

        // 時變流場(environment.current.table | tidal)被常數覆寫:時變停止直到再載入情境(情境引擎設計第 1.5.2 節);漸變自目前模型值起算
        bool? currentModelStopped = null;
        if (currentGiven && Environment.CurrentModelActive)
        {
            Environment.CurrentOverridden = true;
            currentModelStopped = true;
        }

        var changes = new Dictionary<string, object>();
        var rampTicks = (long)Math.Round(rampS * _stepsPerSecond);
        foreach (var (ch, to) in targets)
        {
            // 能見度「無限制」(null)沒有可起算的數值:不建立漸變、直接寫入目標值(與 ramp_s = 0 相同),事件 from 記為 null
            if (ch == RampChannel.Visibility && Environment.VisibilityNm is null)
            {
                _ramps.Remove(ch);
                RampSet.Write(Environment, ch, to);
                changes[ContractJson.EnumName(ch)] = new { from = (double?)null, to = RampSet.External(ch, to) };
                continue;
            }
            var from = RampSet.Read(Environment, ch);
            if (rampTicks > 0) _ramps.Set(ch, Tick, Tick + rampTicks, from, to);
            else
            {
                _ramps.Remove(ch);
                RampSet.Write(Environment, ch, to);
            }
            changes[ContractJson.EnumName(ch)] = new { from = RampSet.External(ch, from), to = RampSet.External(ch, to) };
        }
        Raise(NewEvent(SimEventKinds.EnvironmentChange, source, itemId, null, null, new
        {
            wind = cmd.Arg("wind"), current = cmd.Arg("current"), waterDepth = cmd.ArgAsDouble("waterDepth"), visibility_nm = cmd.ArgAsDouble("visibility_nm"),
            ramp_s = rampS, changes, currentModelStopped,
        }));
    }

    // ------------------------------------------------------------------ 推進

    /// <summary>一步:先套用佇列指令,未凍結時推進 dt。</summary>
    public void Step()
    {
        ProcessPendingCommands();
        if (Frozen) return;
        Advance();
    }

    /// <summary>連續推進 n 步。</summary>
    public void Run(int steps)
    {
        for (var i = 0; i < steps; i++) Step();
    }

    private void Advance()
    {
        var t = Time;

        // 陣風:每秒以種子亂數更新一次(即使 gustiness = 0 也抽號,使亂數序列與快照的計數一致)
        if (Tick % _stepsPerSecond == 0)
            Environment.GustFactor = 1.0 + Environment.Gustiness * (2.0 * _rng.NextDouble() - 1.0);

        // 環境漸變(陣風之後、致動器之前;完成者發 environmentRampDone)
        if (_ramps.Count > 0 && _ramps.Apply(Tick, Environment) is { } done)
            foreach (var r in done)
                Raise(NewEvent(SimEventKinds.EnvironmentRampDone, SimEventSources.Timeline, null, null, ContractJson.EnumName(r.Channel),
                    new { channel = ContractJson.EnumName(r.Channel), to = RampSet.External(r.Channel, r.To) }));

        // 自動舵以電羅經「指示航向」計算舵令(gyroDrift 時跟隨錯誤航向);ROT 取迴轉率陀螺(真值)
        if (Autopilot.Enabled)
            _act.RudderOrderDeg = Autopilot.RudderOrderDeg(IndicatedHeadingRad(), Motion.R, _act.Parameters.RudderMaxDeg);

        // 致動器(含主機換向狀態機)先推進;MMG 模式回傳 RK4 各階段的舵角/轉速/側推,與 Python 參考實作的耦合積分一致
        // 舵機故障:舵卡住;切到 emergency(應急操舵)後舵以降低的速率再度可動(規劃書第 7.2 節操舵模式)
        _act.StartFailureActive = _faults.Contains(FaultNames.EngineStartFailure);
        var controls = _act.Step(Options.Dt,
            // FU 失效:隨動迴路(fu 與自動舵)無法動舵,NFU 與應急操舵可動
            rudderJammed: (_faults.Contains(FaultNames.SteeringGear) && _steeringMode != SteeringMode.Emergency)
                          || (_faults.Contains(FaultNames.SteeringFuOnly) && _steeringMode is SteeringMode.Fu or SteeringMode.Autopilot),
            engineFailed: _faults.Contains(FaultNames.MainEngine),
            thrusterFailed: _faults.Contains(FaultNames.BowThruster),
            rudderRateDegPerSec: RudderRateForStep());
        if (_act.LastStartOutcome != StartOutcome.None) OnStartOutcome(_act.LastStartOutcome);

        var sample = Environment.SampleAt(Motion.X, Motion.Y);
        // 外力(拖船/錨/碰墊/繫纜):只在情境使用時走含外力的積分(未使用時位元與舊版相同)
        // 岸壁效應:情境有 banks 時與外力模組相加(螺槳載荷取步首轉速,四階段共用);兩者皆未使用時走舊積分多載
        var external = _external.Active;
        var bank = _bank is { Active: true } bk ? bk : null;
        // 波浪漂移力:情境有 environment.waves 時再與前兩者相加(純函數;無 waves 的情境此處為 null、運算順序與舊版相同)
        var waves = _waves is { Active: true } wv ? wv : null;
        // 船間交互作用:情境啟用 environment.shipInteraction 時最後相加(目標船取步首狀態,四階段共用;未啟用的情境此處為 null、運算順序與舊版相同)
        var interaction = _interaction is { Active: true } si ? si : null;
        if (external || bank is not null || waves is not null || interaction is not null)
        {
            if (external) _external.PreStep(Motion, Options.Dt);
            bank?.PreStep(controls.C1.Rpm);
            interaction?.PreStep(_traffic.Targets);
            // 只有外力模組 → 與舊版相同的 _external;只有岸壁 → _bank;兩者 → 和;有浪 → (前者) + 浪,只有浪 → 浪;有船間交互作用 → (前者) + 交互作用
            IExternalForceField? baseField = bank is null ? (external ? _external : null) : external ? _sumField! : bank;
            IExternalForceField? field = waves is null ? baseField : baseField is null ? waves : new SumForceField(baseField, waves);
            if (interaction is not null) field = field is null ? interaction : new SumForceField(field, interaction);
            Motion = Rk4Integrator.Step(Dynamics, t, Motion, sample, controls, Options.Dt, field!);
        }
        else Motion = Rk4Integrator.Step(Dynamics, t, Motion, sample, controls, Options.Dt);
        Tick++;
        // 潮高隨練習時間更新(步末位置的 UKC/擱淺檢查與下一步的取樣用新時刻的潮高);時變流場同(state.current 與下一步取樣用新時刻的流)
        Environment.RefreshTide(Time);
        Environment.RefreshCurrent(Time);
        if (external)
        {
            _externalEvents.Clear();
            _external.PostStep(Motion, sample, Options.Dt, _externalEvents);
            foreach (var ev in _externalEvents)
                Raise(NewEvent(ev.Kind, SimEventSources.Engine, ev.ItemId, null, ev.Message, ev.Data, visible: true));
        }
        // 岸壁效應步末結果(雜湊與 state.bank;步末位置的水深、實際轉速)
        bank?.UpdateLast(Motion, Environment.DepthAt(Motion.X, Motion.Y), _act.Rpm);
        // 波浪漂移力步末結果(雜湊與 state.waves;步末位置的有效水深)
        waves?.UpdateLast(Motion, Environment.DepthAt(Motion.X, Motion.Y));

        if (!Aground && UnderKeelClearance() <= 0.0)
        {
            Aground = true;
            Grounded?.Invoke(this);
            Raise(NewEvent(SimEventKinds.Grounding, SimEventSources.Engine, null, null, "擱淺:UKC ≤ 0", new { ukc_m = UnderKeelClearance() }));
        }

        // 目標船:與自船同 dt,依自船推進後的狀態更新(跟隨、COLREG、會遇指標、碰撞)
        if (_traffic.Targets.Count > 0)
            _traffic.Step(OwnKinematics(), sample, Tick);
        // 船間交互作用步末結果(雜湊與 state.shipInteraction;自船與目標船皆已推進到本 tick 的幾何)與門檻事件
        if (interaction is not null)
            RaiseInteractionTransition(interaction.UpdateLast(Motion, Environment.DepthAt(Motion.X, Motion.Y), _traffic.Targets));

        // BNWAS 與警報管理(每步;評估步之前,使同一 tick 的評估看得到警報事件)
        _bnwas.Update(Tick, Autopilot.Enabled);
        UpdateAlerts();

        // 評估步(每秒):Route/Zone → Metrics → Encounter/FaultResponse → Timeline → Assessment
        if (Tick % _stepsPerSecond == 0 && _assessment is not null && _timeline is not null)
        {
            var sit = CurrentSituation();
            _assessment.Update(sit);
            _rescore?.Update(sit);
            _timeline.Evaluate(Tick, sit);
            _assessment.EvaluateRules(sit);
            _rescore?.EvaluateRules(sit);
        }

        UpdateHash();
        Stepped?.Invoke(this);

        if (Broadcast is not null && Tick % Options.BroadcastDivider == 0)
            Broadcast.Invoke(this, BuildState(includeRadarEnvironment: RadarEnvironmentKeyframe()));

        if (Options.AutoSnapshotIntervalTicks > 0 && Tick % Options.AutoSnapshotIntervalTicks == 0)
            TakeSnapshot();
    }

    /// <summary>
    /// engineStartFailure 起動結果 → 事件(source engine、學員站可見)。失敗的嘗試由遙控系統自動再試,每次再消耗一次起動空氣
    /// (下令本身已在 <see cref="SetRpmOrder"/> 計一次)。
    /// </summary>
    private void OnStartOutcome(StartOutcome outcome)
    {
        var telegraph = ContractJson.EnumName(_telegraph);
        if (outcome == StartOutcome.Started)
        {
            Raise(NewEvent(SimEventKinds.EngineStarted, SimEventSources.Engine, FaultNames.EngineStartFailure, null, $"主機起動(下令後 {_act.LastStartAfterSec:F0} s)",
                new { after_s = _act.LastStartAfterSec, failedAttempts = _act.StartFailedAttempts, rpmOrder = _act.RpmOrder, telegraph }, visible: true));
            return;
        }
        if (_startsRemaining is { } n && n > 0) _startsRemaining = n - 1;
        var blocked = outcome == StartOutcome.Blocked;
        Raise(NewEvent(blocked ? SimEventKinds.EngineStartBlocked : SimEventKinds.EngineStartFailed, SimEventSources.Engine, FaultNames.EngineStartFailure, null,
            blocked ? $"主機連續 {_act.StartConsecutiveFailures} 次起動失敗,起動封鎖(車令回 STOP 後再下令)" : $"主機起動失敗(第 {_act.StartConsecutiveFailures} 次),自動再試",
            new
            {
                attempt = _act.StartConsecutiveFailures, consecutive = _act.StartConsecutiveFailures, failedAttempts = _act.StartFailedAttempts,
                rpmOrder = _act.RpmOrder, telegraph, startsRemaining = _startsRemaining,
            }, visible: true));
    }

    private void UpdateHash()
    {
        // 既有欄位的位元組配置不變;目標船(若有)接在其後,因此無目標船的情境雜湊與舊版完全相同
        var size = 32 + 8 + 8 * 16 + _traffic.Targets.Count * TrafficManager.HashDoublesPerTarget * 8;
        // 操舵模式只在非預設(nfu / emergency / 泵非 1)時追加 3 個 int64:fu 與 autopilot(與既有 Autopilot.Enabled 同義,其舵令已在雜湊內)維持舊版配置
        var hashSteering = _steeringMode is SteeringMode.Nfu or SteeringMode.Emergency || _steeringPump != SteeringPumps.One;
        if (hashSteering) size += 24;
        if (_hashScenarioEngine)
            size += 16 + Scenario.Timeline.Count * TimelineEngine.HashLongsPerItem * 8 + _ramps.Count * 24 + 8;
        // 感測器/警報/BNWAS 只在非預設時追加(無感測器故障、無警報、BNWAS off 的情境雜湊與舊版相同)
        var hashBridge = !_sensors.State.IsNominal || !_alerts.State.IsEmpty || !_bnwas.IsDefault;
        if (hashBridge) size += 8 + SensorModel.HashLongs * 8 + _alerts.HashSize + Bnwas.HashLongs * 8;
        // 外力模組只在使用時追加(情境無拖船/泊位且錨未用過者雜湊與舊版相同)
        var hashExternal = _external.Active;
        if (hashExternal) size += _external.HashLongs * 8;
        // 主機起動故障(engineStartFailure)只在成立或留有狀態時追加(未使用者雜湊與舊版相同)
        var hashStart = _act.StartFailureActive || _act.HasStartFailureState;
        if (hashStart) size += 8 * 8;
        // 岸壁效應只在情境有 banks 且動力學接受外力時追加(未使用者雜湊與舊版相同)
        var hashBank = _bank is { Active: true };
        if (hashBank) size += BankEffectModel.HashLongs * 8;
        // 波浪漂移力只在情境有 environment.waves 且動力學接受外力時追加(未使用者雜湊與舊版相同)
        var hashWaves = _waves is { Active: true };
        if (hashWaves) size += WaveDriftModel.HashLongs * 8;
        // 船間交互作用只在情境啟用且動力學接受外力時追加(未使用者雜湊與舊版相同)
        var hashInteraction = _interaction is { Active: true };
        if (hashInteraction) size += ShipInteractionModel.HashLongs * 8;
        Span<byte> buf = size <= 2048 ? stackalloc byte[size] : new byte[size];
        _hash.CopyTo(buf);
        var o = 32;
        BinaryPrimitives.WriteInt64LittleEndian(buf[o..], Tick); o += 8;
        foreach (var d in new[]
                 {
                     Motion.U, Motion.V, Motion.R, Motion.X, Motion.Y, Motion.Psi,
                     _act.RudderDeg, _act.RudderOrderDeg, _act.Rpm, _act.RpmOrder, _act.ThrusterActual, Environment.GustFactor,
                     // 主機換向狀態機(MMG 模式;暫代模型為常數)
                     (double)_act.EngineMode, _act.EngineTimerSec, _act.RpmTarget, _act.RpmTau,
                 })
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], BitConverter.DoubleToInt64Bits(d));
            o += 8;
        }
        o += _traffic.WriteHash(buf[o..]);
        if (hashSteering)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], (long)_steeringMode); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], SteeringPumps.Index(_steeringPump)); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _nfuDirection); o += 8;
        }
        if (_hashScenarioEngine)
        {
            // 情境引擎(只在有時間表/航線/區域/規則時追加,E05 等情境雜湊不變):非評估事件數、階段索引、各項目狀態、進行中漸變、航段
            // (評估事件不計,使「以新參數集重新評分」的重播雜湊不變;規則衍生量以 summary 比對驗證)
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _seq - _assessmentSeq); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _phaseNames.IndexOf(Phase)); o += 8;
            o += _timeline!.WriteHash(buf[o..]);
            foreach (var r in _ramps.Ramps)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buf[o..], (long)r.Channel); o += 8;
                BinaryPrimitives.WriteInt64LittleEndian(buf[o..], r.EndTick); o += 8;
                BinaryPrimitives.WriteInt64LittleEndian(buf[o..], BitConverter.DoubleToInt64Bits(r.To)); o += 8;
            }
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _assessment?.Route?.State.LegIndex ?? -1); o += 8;
        }
        if (hashBridge)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], 0x53454E53_414C5254); o += 8; // 區塊標記("SENSALRT")
            o += _sensors.WriteHash(buf[o..]);
            o += _alerts.WriteHash(buf[o..]);
            o += _bnwas.WriteHash(buf[o..]);
        }
        if (hashExternal) o += _external.WriteHash(buf[o..]);
        if (hashStart)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], 0x454E4753_54415254); o += 8; // 區塊標記("ENGSTART")
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _act.StartFailureActive ? 1 : 0); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], BitConverter.DoubleToInt64Bits(_act.StartFailureDelayS ?? -1.0)); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _act.StartFailureAttempts ?? -1); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _act.StartFailedAttempts); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _act.StartConsecutiveFailures); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], _act.StartBlocked ? 1 : 0); o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], BitConverter.DoubleToInt64Bits(_act.StartAttemptTimerSec)); o += 8;
        }
        if (hashBank) o += _bank!.WriteHash(buf[o..]);
        if (hashWaves) o += _waves!.WriteHash(buf[o..]);
        if (hashInteraction) o += _interaction!.WriteHash(buf[o..]);
        SHA256.HashData(buf[..o], _hash);
    }

    // ------------------------------------------------------------------ 快照 / 還原

    public EngineSnapshot CreateSnapshot() => new()
    {
        ShipId = Ship.Id,
        Loading = Loading,
        ScenarioId = Scenario.Id,
        Dynamics = Dynamics.ModelName,
        Tick = Tick,
        Dt = Options.Dt,
        Motion = new MotionSnapshot(Motion.U, Motion.V, Motion.R, Motion.X, Motion.Y, Motion.Psi),
        Actuators = new ActuatorSnapshot(_act.RudderOrderDeg, _act.RudderDeg, _act.RpmOrder, _act.Rpm, _act.ThrusterOrder, _act.ThrusterActual,
            _act.EngineMode, _act.EngineTimerSec, _act.RpmTarget, _act.RpmTau,
            _act.StartFailureDelayS, _act.StartFailureAttempts, _act.StartFailedAttempts, _act.StartConsecutiveFailures, _act.StartBlocked, _act.StartAttemptTimerSec),
        Telegraph = _telegraph,
        Environment = new EnvironmentSnapshot(
            Environment.WindTrueSpeedMps, Environment.WindTrueDirFromRad, Environment.Gustiness, Environment.GustFactor,
            Environment.CurrentSetRad, Environment.CurrentDriftMps,
            Environment.Depth is ConstantDepth cd ? cd.Depth : Environment.Depth is ZoneDepth zd ? zd.Base.Depth : null,
            // 只在情境有時變流場時寫出覆寫旗標(常數流情境的快照位元不變)
            Environment.CurrentModel is null ? null : Environment.CurrentOverridden),
        RandomSeed = _rng.Seed,
        RandomCount = _rng.Count,
        Frozen = Frozen,
        TimeScale = TimeScale,
        Autopilot = new AutopilotSnapshot(Autopilot.Enabled, Autopilot.HeadingRad, Autopilot.RotLimitDegPerMin),
        Faults = _faults.ToArray(),
        Aground = Aground,
        StartsRemaining = _startsRemaining,
        Traffic = _traffic.CreateSnapshot(),
        Timeline = _timeline?.CreateSnapshot(Phase, _seq, _assessmentSeq, _ramps.Ramps, _phaseHistory, Ended),
        Assessment = _assessment?.CreateSnapshot(),
        RescoreAssessment = _rescore?.CreateSnapshot(),
        ParameterSetVersion = ParameterSet?.Version,
        Steering = new SteeringSnapshot(_steeringMode, _steeringPump, _nfuDirection),
        Sensors = _sensors.CreateSnapshot(),
        Alerts = _alerts.CreateSnapshot(),
        Bnwas = _bnwas.CreateSnapshot(),
        External = _external.Active ? _external.CreateSnapshot() : null,
        ShipInteraction = _interaction is { Active: true } ? _interaction.CreateSnapshot() : null,
        Phase = Phase,
        VisibilityNm = Environment.VisibilityNm,
        StateHash = StateHash,
    };

    /// <summary>產生快照並加入歷史(供倒帶);觸發 <see cref="SnapshotTaken"/>。</summary>
    public EngineSnapshot TakeSnapshot()
    {
        var snap = CreateSnapshot();
        _snapshots.Add(snap);
        while (_snapshots.Count > Math.Max(1, Options.SnapshotHistory)) _snapshots.RemoveAt(0);
        SnapshotTaken?.Invoke(this, snap);
        return snap;
    }

    /// <summary>
    /// 教官指令還原(restore):還原到歷史中 tick ≤ 指定值的最新快照(未指定時取最新)。
    /// 保留目前的凍結狀態與時間倍率(規劃書第 9.1 節「凍結、倒帶後講評再繼續」:凍結中倒帶不會自行恢復執行);
    /// 練習已結束(endExercise、summary 已定稿)後不能倒帶,回傳 false(請 reset 或 loadScenario)。
    /// </summary>
    public bool RestoreFromHistory(long? atOrBeforeTick = null)
    {
        if (Ended) return false;
        EngineSnapshot? pick = null;
        for (var i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (atOrBeforeTick is null || _snapshots[i].Tick <= atOrBeforeTick.Value) { pick = _snapshots[i]; break; }
        }
        if (pick is null) return false;
        Restore(pick, keepRunState: true);
        return true;
    }

    /// <summary>
    /// 從快照還原完整狀態(含亂數與雜湊鏈);快照歷史保留 tick ≤ 快照者。
    /// <paramref name="keepRunState"/> 為 true 時不覆寫目前的 Frozen/TimeScale(教官指令路徑);false(預設,直接呼叫)則完全依快照。
    /// </summary>
    public void Restore(EngineSnapshot s, bool keepRunState = false)
    {
        if (!string.Equals(s.ShipId, Ship.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"快照船舶 {s.ShipId} 與引擎 {Ship.Id} 不符");
        if (Math.Abs(s.Dt - Options.Dt) > 1e-12)
            throw new InvalidOperationException($"快照 dt {s.Dt} 與引擎 {Options.Dt} 不符");
        if (s.Loading != Loading)
        {
            Loading = s.Loading;
            Dynamics = _dynamicsFactory(Ship, Loading);
        }
        // 外力模組:重建為初始狀態再套用快照(舊快照或未使用外力者即為初始狀態)
        _external = CreateExternal();
        if (s.External is { } ext) _external.Restore(ext);
        // 岸壁效應:無跨步狀態,依情境重建幾何,於還原完成後以還原的狀態重算步末結果(RefreshBank)
        _bank = CreateBank();
        _sumField = _bank is null ? null : new SumForceField(_external, _bank);
        // 波浪漂移力:純函數,依情境重建,還原完成後以還原的狀態重算步末結果(RefreshWaves)
        _waves = CreateWaves();
        // 船間交互作用:力為純函數、事件旗標依快照(舊快照或未啟用者為未進入),目標船還原後重算步末結果(RefreshInteraction)
        _interaction = CreateInteraction();
        if (s.ShipInteraction is { } sis) _interaction?.RestoreEvent(sis.EventActive, sis.EventTargetId);

        Tick = s.Tick;
        _radarEnvironmentPending = true;
        Motion = new StateVector(s.Motion.U, s.Motion.V, s.Motion.R, s.Motion.X, s.Motion.Y, s.Motion.Psi);
        _act = new ActuatorModel(ActuatorParametersForDynamics())
        {
            RudderOrderDeg = s.Actuators.RudderOrderDeg,
            RudderDeg = s.Actuators.RudderDeg,
            RpmOrder = s.Actuators.RpmOrder,
            Rpm = s.Actuators.Rpm,
            ThrusterOrder = s.Actuators.ThrusterOrder,
            ThrusterActual = s.Actuators.ThrusterActual,
            EngineMode = s.Actuators.EngineMode,
            EngineTimerSec = s.Actuators.EngineTimerSec,
            RpmTarget = s.Actuators.RpmTarget,
            RpmTau = s.Actuators.RpmTau,
            StartFailureDelayS = s.Actuators.StartFailureDelayS,
            StartFailureAttempts = s.Actuators.StartFailureAttempts,
            StartFailedAttempts = s.Actuators.StartFailedAttempts,
            StartConsecutiveFailures = s.Actuators.StartConsecutiveFailures,
            StartBlocked = s.Actuators.StartBlocked,
            StartAttemptTimerSec = s.Actuators.StartAttemptTimerSec,
        };
        _telegraph = s.Telegraph;
        var depth = Environment.Depth;
        if (s.Environment.ConstantDepth is { } cd)
            depth = depth is ConstantDepth existing ? SetDepth(existing, cd) : depth is ZoneDepth zd ? SetDepth(zd.Base, cd) is var _ ? zd : zd : new ConstantDepth(cd);
        Environment = new EnvironmentConditions
        {
            WindTrueSpeedMps = s.Environment.WindTrueSpeedMps,
            WindTrueDirFromRad = s.Environment.WindTrueDirFromRad,
            Gustiness = s.Environment.Gustiness,
            GustFactor = s.Environment.GustFactor,
            CurrentSetRad = s.Environment.CurrentSetRad,
            CurrentDriftMps = s.Environment.CurrentDriftMps,
            Depth = depth,
            // 潮汐模型來自情境(不進快照),沿用並以還原的 Tick 重算潮高
            Tide = Environment.Tide,
            // 時變流場模型來自情境(不進快照);覆寫旗標由快照還原(倒帶到覆寫前即恢復時變),舊快照無此欄位視為未覆寫
            CurrentModel = Environment.CurrentModel,
            CurrentOverridden = s.Environment.CurrentOverridden ?? false,
            // Version 2 快照含能見度(null = 無限制,直接取快照值);Version 1 快照沒有此欄位才沿用目前值
            VisibilityNm = s.Version >= 2 ? s.VisibilityNm : Environment.VisibilityNm,
        };
        Environment.RefreshTide(Time);
        Environment.RefreshCurrent(Time);
        if (_rng.Seed != s.RandomSeed) _rng = new DeterministicRandom(s.RandomSeed, s.RandomCount);
        else _rng.ResetTo(s.RandomCount);
        if (!keepRunState)
        {
            Frozen = s.Frozen;
            TimeScale = s.TimeScale;
        }
        Autopilot.Enabled = s.Autopilot.Enabled;
        Autopilot.HeadingRad = s.Autopilot.HeadingRad;
        Autopilot.RotLimitDegPerMin = s.Autopilot.RotLimitDegPerMin;
        // 操舵模式:舊快照無此欄位 → fu(自動舵啟用時 autopilot)、泵 1
        _steeringMode = s.Steering?.Mode ?? (s.Autopilot.Enabled ? SteeringMode.Autopilot : SteeringMode.Fu);
        _steeringPump = SteeringPumps.Normalize(s.Steering?.Pump) ?? SteeringPumps.One;
        _nfuDirection = _steeringMode == SteeringMode.Nfu ? Math.Sign(s.Steering?.NfuDirection ?? 0) : 0;
        Autopilot.Enabled = _steeringMode == SteeringMode.Autopilot;
        _faults.Clear();
        _faults.AddRange(s.Faults);
        // Version 3:感測器/警報/BNWAS;舊快照沒有時還原為預設(無偏差、無警報、BNWAS 依情境設定)
        _sensors.Restore(s.Sensors);
        _alerts.Restore(s.Alerts);
        _bnwas.Restore(s.Bnwas, Scenario.Bnwas);
        Aground = s.Aground;
        _startsRemaining = s.StartsRemaining;
        if (s.Traffic is { } ts) _traffic.Restore(ts, _projection);
        if (s.Timeline is { } tl && _timeline is not null)
        {
            _timeline.Restore(tl);
            _seq = tl.Seq;
            _assessmentSeq = tl.AssessmentEvents;
            Phase = tl.Phase;
            if (!_phaseNames.Contains(Phase)) _phaseNames.Add(Phase);
            _ramps.Restore(tl.Ramps);
            _phaseHistory.Clear();
            _phaseHistory.AddRange(tl.PhaseHistory ?? new[] { new PhaseSpan(Phase, 0) });
            Ended = tl.Ended;
        }
        else if (s.Phase is { } ph) Phase = ph;
        if (s.Assessment is { } asn) _assessment?.Restore(asn);
        if (s.RescoreAssessment is { } rsn) _rescore?.Restore(rsn);
        var hash = Convert.FromHexString(s.StateHash);
        if (hash.Length != 32) throw new InvalidDataException("快照雜湊長度錯誤");
        hash.CopyTo(_hash, 0);
        _snapshots.RemoveAll(x => x.Tick > s.Tick);
        RefreshBank();
        RefreshWaves();
        RefreshInteraction();

        static IDepthProvider SetDepth(ConstantDepth existing, double value) { existing.Depth = value; return existing; }
    }

    // ------------------------------------------------------------------ UTC 時刻(state.utc)

    /// <summary>state.utc 的格式(ISO 8601,毫秒,Z;25 Hz 幀距 40 ms 故毫秒足夠)。</summary>
    public const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// 情境 startTimeUtc(ISO 8601)可解析者直接採用;否則有 Options.Clock 時取其現在時刻(截到毫秒)並寫回情境 startTimeUtc,
    /// 使紀錄標頭的情境帶有開始時刻、重播與 NMEA 閘道(主控程序在建立引擎後讀情境 startTimeUtc)都用同一個起點;無時鐘則 null。
    /// Reset 再次呼叫時已有值,不變。
    /// </summary>
    private DateTime? ResolveStartUtc(Scenario.Scenario sc)
    {
        if (TryParseUtc(sc.StartTimeUtc, out var given)) return given;
        if (Options.Clock is not { } clock) return null;
        var now = clock.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        sc.StartTimeUtc = now.ToString(UtcFormat, System.Globalization.CultureInfo.InvariantCulture);
        return now;
    }

    /// <summary>ISO 8601 → UTC DateTime(無時區者視為 UTC)。</summary>
    public static bool TryParseUtc(string? iso, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(iso)) return false;
        if (!DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)) return false;
        utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return true;
    }

    /// <summary>模擬時間 t(秒)對應的 UTC 字串(state.utc);無開始時刻為 null(state 省略 utc)。</summary>
    public string? UtcAt(double simSeconds) => StartUtc?.AddSeconds(simSeconds).ToString(UtcFormat, System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ 廣播狀態

    /// <summary>Squat:動力學模型提供者(MMG:ICORELS)優先,否則 Barrass 開闊水域簡式。</summary>
    private double SquatAt(double waterDepth)
    {
        if (Dynamics.Squat(Motion, waterDepth) is { } s) return s;
        var lc = Ship.GetLoading(Loading);
        return EnvironmentMath.SquatBarrass(lc.BlockCoefficient ?? 0.75, Units.MpsToKn(Math.Abs(Motion.U)));
    }

    private double UnderKeelClearance()
    {
        var depth = Environment.DepthAt(Motion.X, Motion.Y);
        return EnvironmentMath.UnderKeelClearance(depth, Ship.GetLoading(Loading).MaxDraft_m, SquatAt(depth));
    }

    /// <summary>評估步取樣的自船狀態(對外單位)。</summary>
    private Situation CurrentSituation()
    {
        var m = Motion;
        var sample = Environment.SampleAt(m.X, m.Y);
        var (velE, velN) = EnvironmentMath.GroundVelocity(m.U, m.V, m.Psi, sample.CurrentEastMps, sample.CurrentNorthMps);
        var (sogMps, _) = EnvironmentMath.CogSog(velE, velN, m.Psi);
        var squat = SquatAt(sample.WaterDepthM);
        var lc = Ship.GetLoading(Loading);
        return new Situation
        {
            Tick = Tick, Time = Time, X = m.X, Y = m.Y, HeadingRad = m.Psi,
            SogKn = Units.MpsToKn(sogMps), StwKn = Units.MpsToKn(m.U), RotDegPerMin = Units.RadPerSecToDegPerMin(m.R),
            RudderDeg = _act.RudderDeg, RudderOrderDeg = _act.RudderOrderDeg, Telegraph = _telegraph, Rpm = _act.Rpm, ThrusterOrder = _act.ThrusterOrder,
            AutopilotEnabled = Autopilot.Enabled,
            UkcM = EnvironmentMath.UnderKeelClearance(sample.WaterDepthM, lc.MaxDraft_m, squat), SquatM = squat, WaterDepthM = sample.WaterDepthM,
            MaxDraftM = lc.MaxDraft_m, LoaM = Ship.Hull.LengthOverall_m > 0 ? Ship.Hull.LengthOverall_m : Ship.Hull.LengthBetweenPerpendiculars_m,
            VelE = velE, VelN = velN,
        };
    }

    /// <summary>
    /// 廣播幀是否帶出 state.radarEnvironment:情境有 environment.radar 時,載入/重設/倒帶後第一幀與距上次帶出 ≥ 1 s(模擬時間)的幀;
    /// 以 tick 判定(與牆鐘無關),相同輸入紀錄重播得到相同的廣播內容。
    /// </summary>
    private bool RadarEnvironmentKeyframe()
    {
        if (_radarEnvironment is null) return false;
        if (!_radarEnvironmentPending && Tick - _radarEnvironmentSentTick < _stepsPerSecond) return false;
        _radarEnvironmentPending = false;
        _radarEnvironmentSentTick = Tick;
        return true;
    }

    /// <summary>組出 state.schema.json 的自船狀態(引擎執行緒;含完整 radarEnvironment,廣播則只在關鍵幀帶出)。</summary>
    public OwnShipState BuildState() => BuildState(includeRadarEnvironment: true);

    private OwnShipState BuildState(bool includeRadarEnvironment)
    {
        var m = Motion;
        var env = Environment;
        var sample = env.SampleAt(m.X, m.Y);
        var (velE, velN) = EnvironmentMath.GroundVelocity(m.U, m.V, m.Psi, sample.CurrentEastMps, sample.CurrentNorthMps);
        var (sogMps, cogRad) = EnvironmentMath.CogSog(velE, velN, m.Psi);
        var (relWindMps, relWindFromRad) = EnvironmentMath.RelativeWind(sample.WindEastMps, sample.WindNorthMps, velE, velN, m.Psi);
        var (lat, lon) = _projection.ToGeodetic(m.X, m.Y);
        var lc = Ship.GetLoading(Loading);
        var stwKn = Units.MpsToKn(m.U);
        var squat = SquatAt(sample.WaterDepthM);
        var ukc = EnvironmentMath.UnderKeelClearance(sample.WaterDepthM, lc.MaxDraft_m, squat);
        var drift = Math.Abs(m.U) > 0.05 ? Units.RadToDeg(Math.Atan2(-m.V, m.U)) : 0.0;
        // 感測器層:頂層 heading/pos/cog/sog/stw/depthBelowKeel 為指示值,truth 為真值(設計文件 sensors-alerts.md 第 2 節)
        var truthHeading = Units.NormalizeHeadingDeg(Units.RadToDeg(m.Psi));
        var truthCog = Units.NormalizeHeadingDeg(Units.RadToDeg(cogRad));
        var truthSog = Units.MpsToKn(sogMps);
        var ind = _sensors.Read(new SensorTruth(Units.RadToDeg(m.Psi), m.X, m.Y, truthCog, truthSog, stwKn, ukc), Tick);
        var (indLat, indLon) = ind.X == m.X && ind.Y == m.Y ? (lat, lon) : _projection.ToGeodetic(ind.X, ind.Y);
        // 起動封鎖(engineStartFailure 連續失敗)亦顯示 failed
        var engineState = _faults.Contains(FaultNames.MainEngine) || (_act.StartBlocked && _faults.Contains(FaultNames.EngineStartFailure)) ? EngineRunState.Failed
            : _act.Parameters.EngineStateMachine ? _act.EngineMode switch
            {
                EngineMode.Stopped => EngineRunState.Stopped,
                EngineMode.Starting or EngineMode.Reversing => EngineRunState.Starting,
                _ => EngineRunState.Running, // run 與停俥滑行(stopping)
            }
            : Math.Abs(_act.Rpm) > 1.0 ? EngineRunState.Running
            : EngineRunState.Stopped;
        var loadPct = Ship.Engine.McrRpm > 0 ? 100.0 * Math.Pow(Math.Abs(_act.Rpm) / Ship.Engine.McrRpm, 3) : 0.0;

        return new OwnShipState
        {
            T = Time,
            Tick = Tick,
            Utc = UtcAt(Time),
            ShipId = Ship.Id,
            Pos = new GeoPosition { Lat = indLat, Lon = indLon, X = ind.X, Y = ind.Y },
            Heading = ind.HeadingDeg,
            Cog = ind.CogDeg,
            Sog = ind.SogKn,
            Stw = ind.StwKn,
            Rot = Units.RadPerSecToDegPerMin(m.R),
            U = m.U,
            V = m.V,
            R = m.R,
            Drift = drift,
            Rudder = _act.RudderDeg,
            RudderOrder = _act.RudderOrderDeg,
            Rpm = _act.Rpm,
            RpmOrder = _act.RpmOrder,
            Telegraph = _telegraph,
            Thruster = _act.Parameters.HasBowThruster ? new ThrusterState { Order = _act.ThrusterOrder, Actual = _act.ThrusterActual } : null,
            DepthBelowKeel = ind.DepthBelowKeelM,
            WaterDepth = sample.WaterDepthM,
            TideHeightM = env.Tide is null ? null : env.TideHeightM,
            Squat = squat,
            Wind = new WindState
            {
                TrueSpeed = Units.MpsToKn(env.WindEffectiveSpeedMps),
                TrueDir = Units.NormalizeHeadingDeg(Units.RadToDeg(env.WindTrueDirFromRad)),
                RelSpeed = Units.MpsToKn(relWindMps),
                RelDir = Units.NormalizeHeadingDeg(Units.RadToDeg(relWindFromRad)),
            },
            Current = new CurrentState
            {
                Set = Units.NormalizeHeadingDeg(Units.RadToDeg(env.CurrentSetRad)),
                Drift = Units.MpsToKn(env.CurrentDriftMps),
            },
            CurrentSource = env.CurrentSource,
            Loading = Loading,
            Draft = lc.DraftFore_m is { } df && lc.DraftAft_m is { } da ? new DraftState { Fore = df, Aft = da } : null,
            Engine = new EngineStatus { State = engineState, StartsRemaining = _startsRemaining, LoadPct = loadPct },
            Faults = _faults.Count == 0 ? Array.Empty<string>() : _faults.ToArray(),
            Flags = new StateFlags { Frozen = Frozen, Aground = Aground, Collision = _traffic.Collision },
            Steering = new SteeringState { Mode = _steeringMode, Pump = _steeringPump, NfuActive = _steeringMode == SteeringMode.Nfu ? _nfuDirection != 0 : null },
            Targets = _traffic.BuildStates(new ShipKinematics(m.X, m.Y, m.Psi, velE, velN,
                Ship.Hull.LengthOverall_m > 0 ? Ship.Hull.LengthOverall_m : Ship.Hull.LengthBetweenPerpendiculars_m, Ship.Hull.Breadth_m)),
            VisibilityNm = env.VisibilityNm,
            Scenario = new ScenarioStatus
            {
                Id = Scenario.Id,
                Phase = Phase,
                ParameterSetVersion = ParameterSet?.Version,
                TimelineFired = _timeline?.TotalFired,
                TimelinePending = _timeline?.PendingActions.Count,
                HardFailed = _assessment?.Rules.HardFailed,
                Score = _assessment?.LatestSummary?.Assessment.Score,
                Ended = Ended,
            },
            Route = _assessment?.Route?.Status(),
            Truth = new TruthState
            {
                Heading = truthHeading,
                Pos = new GeoPosition { Lat = lat, Lon = lon, X = m.X, Y = m.Y },
                Cog = truthCog,
                Sog = truthSog,
                Stw = stwKn,
                DepthBelowKeel = ukc,
                HeadingErrorDeg = Units.WrapDeg180(ind.HeadingDeg - truthHeading),
                PositionErrorM = Math.Sqrt((ind.X - m.X) * (ind.X - m.X) + (ind.Y - m.Y) * (ind.Y - m.Y)),
            },
            Sensors = new SensorStatus
            {
                HeadingValid = ind.HeadingValid, PositionValid = ind.PositionValid, DepthValid = ind.DepthValid, StwValid = ind.StwValid,
                MagneticHeading = ind.MagneticHeadingDeg,
            },
            Autopilot = new AutopilotStatus
            {
                Enabled = Autopilot.Enabled, SetHeading = Units.NormalizeHeadingDeg(Units.RadToDeg(Autopilot.HeadingRad)),
                RotLimitDegPerMin = Autopilot.RotLimitDegPerMin, Available = AutopilotAvailable,
            },
            Alerts = _alerts.BuildStates(),
            Bnwas = _bnwas.BuildStatus(Tick, Autopilot.Enabled),
            Tugs = _external.TugStates(m, _projection),
            Anchors = _external.AnchorStates(_projection),
            Berth = _external.BerthState(),
            Bank = _bank is { Active: true } bank ? bank.State() : null,
            Waves = _waves is { Active: true } waves ? waves.State() : null,
            ShipInteraction = _interaction is { Active: true } inter ? inter.State() : null,
            RadarEnvironment = includeRadarEnvironment ? _radarEnvironment : null,
        };
    }
}

/// <summary>舵機泵選擇(指令 steeringMode args.pump、state.steering.pump):"1"、"2"、"both"。</summary>
public static class SteeringPumps
{
    public const string One = "1";
    public const string Two = "2";
    public const string Both = "both";

    /// <summary>雜湊用索引(1 → 0、2 → 1、both → 2)。</summary>
    public static long Index(string pump) => pump switch { Two => 1, Both => 2, _ => 0 };

    /// <summary>由指令/快照值正規化(數值 1/2、字串 "1"/"2"/"both"/"all";其他 → null = 不變)。</summary>
    public static string? Normalize(JsonElement? e) => e is { } v ? v.ValueKind switch
    {
        JsonValueKind.Number => Normalize(v.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)),
        JsonValueKind.String => Normalize(v.GetString()),
        _ => null,
    } : null;

    public static string? Normalize(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "1" => One,
        "2" => Two,
        "both" or "all" or "1+2" => Both,
        _ => null,
    };
}
