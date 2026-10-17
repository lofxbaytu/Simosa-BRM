using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Scenario;
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
    private readonly RampSet _ramps = new();
    private readonly List<PhaseSpan> _phaseHistory = new();
    private long _seq;
    private long _assessmentSeq;
    private bool _hashScenarioEngine;
    private List<string> _phaseNames = new();

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
    public bool Frozen { get; private set; }
    public double TimeScale { get; private set; } = 1.0;
    public bool Aground { get; private set; }
    public StateVector Motion { get; private set; }
    public TelegraphOrder Telegraph => _telegraph;
    public IReadOnlyList<string> Faults => _faults;
    public ActuatorModel Actuators => _act;
    public LocalTangentPlane Projection => _projection;
    public DeterministicRandom Random => _rng;
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

    /// <summary>載入新情境(船舶 particulars 由呼叫端提供;船舶不同時需給新的 particulars)。</summary>
    public void LoadScenario(Scenario.Scenario scenario, ShipParticulars? ship = null)
    {
        Scenario = scenario.Validate();
        if (ship is not null) Ship = ship;
        if (!string.Equals(Ship.Id, scenario.Ship.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"情境需要船舶 {scenario.Ship.Id},目前為 {Ship.Id};請一併提供 particulars");
        ScenarioLoading?.Invoke(this, scenario);
        _rescoreScenario = null;
        Initialize();
    }

    /// <summary>
    /// 啟用重新評分(重播 <c>--rescore</c>;情境引擎設計第 7.4 節):物理、交通、時間表與雜湊仍完全依目前(紀錄 header 的)情境執行,
    /// 另以 <paramref name="rescored"/>(同一情境、以新參數集重新代入門檻)建立影子評估模組;其事件只餵回自己(不對外發出、不進雜湊、不觸發時間表),
    /// 其 summary 由 <see cref="RescoreAssessment"/> 取得。只能在 tick 0 呼叫。
    /// </summary>
    public void EnableRescore(Scenario.Scenario rescored)
    {
        if (Tick != 0) throw new InvalidOperationException("重新評分須在推進前(tick 0)啟用");
        if (!string.Equals(rescored.Id, Scenario.Id, StringComparison.Ordinal) || !string.Equals(rescored.Ship.Id, Scenario.Ship.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"重新評分的情境 {rescored.Id}/{rescored.Ship.Id} 與目前情境 {Scenario.Id}/{Scenario.Ship.Id} 不符");
        _rescoreScenario = rescored.Validate();
        _rescore = new AssessmentEngine(_rescoreScenario, Ship, this, _projection, Options.Dt, RaiseRescore);
    }

    private void Initialize()
    {
        var sc = Scenario;
        Loading = sc.Ship.Loading;
        Dynamics = _dynamicsFactory(Ship, Loading);
        _projection = new LocalTangentPlane(sc.Origin!.Lat, sc.Origin.Lon);

        var pos = sc.Initial.Position!;
        double x, y;
        if (pos.X is not null && pos.Y is not null) (x, y) = (pos.X.Value, pos.Y.Value);
        else (x, y) = _projection.ToLocal(pos.Lat!.Value, pos.Lon!.Value);
        var psi = Units.NormalizeHeadingRad(Units.DegToRad(sc.Initial.Heading));
        var u = Units.KnToMps(sc.Initial.Speed);
        Motion = new StateVector(u, 0.0, 0.0, x, y, psi);

        var (telegraph, rpm) = ResolveInitialPropulsion(sc, u);
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
            VisibilityNm = sc.Environment.VisibilityNm,
        };

        _rng = new DeterministicRandom(sc.Seed);
        Tick = 0;
        Array.Clear(_hash);
        Frozen = false;
        TimeScale = Units.Clamp(sc.TimeScale <= 0 ? 1.0 : sc.TimeScale, 0.1, Options.MaxTimeScale);
        Aground = false;
        _faults.Clear();
        _snapshots.Clear();
        Autopilot.Enabled = false;
        Autopilot.HeadingRad = psi;
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
        _assessment = new AssessmentEngine(sc, Ship, this, _projection, Options.Dt, RaiseAssessment);
        _rescore = _rescoreScenario is { } rs ? new AssessmentEngine(rs, Ship, this, _projection, Options.Dt, RaiseRescore) : null;
        var depthZones = _assessment.Zones.DepthZones();
        if (depthZones.Count > 0) Environment.Depth = new ZoneDepth((ConstantDepth)Environment.Depth, depthZones);
    }

    // ------------------------------------------------------------------ 事件

    /// <summary>發出事件:補 seq 與 phase,轉給時間表/評估模組觀察,再通知訂閱者(紀錄器、主控程序)。</summary>
    private void Raise(SimEvent e)
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
    private (TelegraphOrder Telegraph, double Rpm) ResolveInitialPropulsion(Scenario.Scenario sc, double u)
    {
        var explicitOrder = !string.IsNullOrWhiteSpace(sc.Initial.Telegraph) &&
                            Enum.TryParse<TelegraphOrder>(sc.Initial.Telegraph, ignoreCase: true, out var parsed)
            ? parsed : (TelegraphOrder?)null;
        if (sc.Initial.Rpm is { } rpm0)
            return (explicitOrder ?? NearestTelegraphForRpm(rpm0), rpm0);
        if (explicitOrder is { } o)
            return (o, Ship.TelegraphRpm(o));
        if (u > 0.0 && Dynamics.SteadyRpmForSpeed(u, sc.Environment.WaterDepth) is { } steady)
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
        switch (cmd.Type)
        {
            case SimCommandType.Rudder:
                if (cmd.ValueAsDouble() is { } deg)
                {
                    var before = _act.RudderOrderDeg;
                    // 手動舵令解除自動舵:學員指令視為一次 autopilotOff(autopilotSwitches、必要事件 expect autopilotOff);時間表 ownShip 覆寫不觀察
                    if (fromInstructor && Autopilot.Enabled) Observe(ActionKinds.AutopilotOff, 1.0);
                    Autopilot.Enabled = false;
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
                var wasEnabled = Autopilot.Enabled;
                var oldHeading = Autopilot.HeadingRad;
                if (cmd.ArgAsBool("enabled") is { } enabled) Autopilot.Enabled = enabled;
                if (cmd.ArgAsDouble("heading") is { } hdg) Autopilot.HeadingRad = Units.DegToRad(Units.NormalizeHeadingDeg(hdg));
                if (cmd.ArgAsDouble("rotLimit") is { } lim) Autopilot.RotLimitDegPerMin = Math.Max(1.0, Math.Abs(lim));
                if (fromInstructor)
                {
                    if (Autopilot.Enabled != wasEnabled) Observe(Autopilot.Enabled ? ActionKinds.AutopilotOn : ActionKinds.AutopilotOff, 1.0);
                    var dh = Math.Abs(Units.RadToDeg(Units.WrapRadPi(Autopilot.HeadingRad - oldHeading)));
                    if (Autopilot.Enabled && dh >= 1.0) Observe(ActionKinds.AutopilotHeading, dh);
                }
                else Raise(NewEvent(SimEventKinds.AutopilotOverride, source, itemId, null, null, cmd.Args));
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
                if (cmd.ValueAsString() is { } path) ScenarioLoadRequested?.Invoke(this, path);
                else if (cmd.Arg("path") is { } p && p.ValueKind == System.Text.Json.JsonValueKind.String)
                    ScenarioLoadRequested?.Invoke(this, p.GetString()!);
                break;

            case SimCommandType.SetEnvironment:
                ApplyEnvironment(cmd, source, itemId);
                break;

            case SimCommandType.InjectFault:
                if (cmd.FaultName() is { } f && f.Length > 0)
                {
                    var already = _faults.Contains(f);
                    if (!already) _faults.Add(f);
                    Raise(NewEvent(SimEventKinds.FaultInjected, source, itemId, null, f, new { fault = f, args = cmd.Arg("args"), alreadyActive = already }));
                }
                break;

            case SimCommandType.ClearFault:
            {
                var cf = cmd.FaultName();
                if (cf is not null && !string.Equals(cf, "all", StringComparison.OrdinalIgnoreCase))
                {
                    if (_faults.Remove(cf)) Raise(NewEvent(SimEventKinds.FaultCleared, source, itemId, null, cf, new { fault = cf }));
                }
                else
                {
                    var all = _faults.ToArray();
                    _faults.Clear();
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
        }
    }

    /// <summary>學員指令觀察 → 評估模組(會遇首次行動、必要事件、故障應變、操縱指標)。</summary>
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
        if (cmd.Arg("current") is { } cur && cur.ValueKind == JsonValueKind.Object)
        {
            if (cur.TryGetProperty("set", out var cs) && SimCommand.AsDouble(cs) is { } csv) targets.Add((RampChannel.CurrentSet, Units.DegToRad(Units.NormalizeHeadingDeg(csv))));
            if (cur.TryGetProperty("drift", out var cd) && SimCommand.AsDouble(cd) is { } cdv) targets.Add((RampChannel.CurrentDrift, Units.KnToMps(Math.Max(0, cdv))));
        }
        if (cmd.ArgAsDouble("waterDepth") is { } depth && depth > 0) targets.Add((RampChannel.WaterDepth, depth));
        if (cmd.ArgAsDouble("visibility_nm") is { } vis && vis > 0) targets.Add((RampChannel.Visibility, vis));
        if (targets.Count == 0) return;

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
            ramp_s = rampS, changes,
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

        if (Autopilot.Enabled)
            _act.RudderOrderDeg = Autopilot.RudderOrderDeg(Motion.Psi, Motion.R, _act.Parameters.RudderMaxDeg);

        // 致動器(含主機換向狀態機)先推進;MMG 模式回傳 RK4 各階段的舵角/轉速/側推,與 Python 參考實作的耦合積分一致
        var controls = _act.Step(Options.Dt,
            rudderJammed: _faults.Contains(FaultNames.SteeringGear),
            engineFailed: _faults.Contains(FaultNames.MainEngine),
            thrusterFailed: _faults.Contains(FaultNames.BowThruster));

        var sample = Environment.SampleAt(Motion.X, Motion.Y);
        Motion = Rk4Integrator.Step(Dynamics, t, Motion, sample, controls, Options.Dt);
        Tick++;

        if (!Aground && UnderKeelClearance() <= 0.0)
        {
            Aground = true;
            Grounded?.Invoke(this);
            Raise(NewEvent(SimEventKinds.Grounding, SimEventSources.Engine, null, null, "擱淺:UKC ≤ 0", new { ukc_m = UnderKeelClearance() }));
        }

        // 目標船:與自船同 dt,依自船推進後的狀態更新(跟隨、COLREG、會遇指標、碰撞)
        if (_traffic.Targets.Count > 0)
            _traffic.Step(OwnKinematics(), sample, Tick);

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
            Broadcast.Invoke(this, BuildState());

        if (Options.AutoSnapshotIntervalTicks > 0 && Tick % Options.AutoSnapshotIntervalTicks == 0)
            TakeSnapshot();
    }

    private void UpdateHash()
    {
        // 既有欄位的位元組配置不變;目標船(若有)接在其後,因此無目標船的情境雜湊與舊版完全相同
        var size = 32 + 8 + 8 * 16 + _traffic.Targets.Count * TrafficManager.HashDoublesPerTarget * 8;
        if (_hashScenarioEngine)
            size += 16 + Scenario.Timeline.Count * TimelineEngine.HashLongsPerItem * 8 + _ramps.Count * 24 + 8;
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
            _act.EngineMode, _act.EngineTimerSec, _act.RpmTarget, _act.RpmTau),
        Telegraph = _telegraph,
        Environment = new EnvironmentSnapshot(
            Environment.WindTrueSpeedMps, Environment.WindTrueDirFromRad, Environment.Gustiness, Environment.GustFactor,
            Environment.CurrentSetRad, Environment.CurrentDriftMps,
            Environment.Depth is ConstantDepth cd ? cd.Depth : Environment.Depth is ZoneDepth zd ? zd.Base.Depth : null),
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

        Tick = s.Tick;
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
            // Version 2 快照含能見度(null = 無限制,直接取快照值);Version 1 快照沒有此欄位才沿用目前值
            VisibilityNm = s.Version >= 2 ? s.VisibilityNm : Environment.VisibilityNm,
        };
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
        _faults.Clear();
        _faults.AddRange(s.Faults);
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

        static IDepthProvider SetDepth(ConstantDepth existing, double value) { existing.Depth = value; return existing; }
    }

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
        var depth = Environment.Depth.DepthAt(Motion.X, Motion.Y);
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

    /// <summary>組出 state.schema.json 的自船狀態(引擎執行緒)。</summary>
    public OwnShipState BuildState()
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
        var engineState = _faults.Contains(FaultNames.MainEngine) ? EngineRunState.Failed
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
            ShipId = Ship.Id,
            Pos = new GeoPosition { Lat = lat, Lon = lon, X = m.X, Y = m.Y },
            Heading = Units.NormalizeHeadingDeg(Units.RadToDeg(m.Psi)),
            Cog = Units.NormalizeHeadingDeg(Units.RadToDeg(cogRad)),
            Sog = Units.MpsToKn(sogMps),
            Stw = stwKn,
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
            DepthBelowKeel = ukc,
            WaterDepth = sample.WaterDepthM,
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
            Loading = Loading,
            Draft = lc.DraftFore_m is { } df && lc.DraftAft_m is { } da ? new DraftState { Fore = df, Aft = da } : null,
            Engine = new EngineStatus { State = engineState, StartsRemaining = _startsRemaining, LoadPct = loadPct },
            Faults = _faults.Count == 0 ? Array.Empty<string>() : _faults.ToArray(),
            Flags = new StateFlags { Frozen = Frozen, Aground = Aground, Collision = _traffic.Collision },
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
        };
    }
}
