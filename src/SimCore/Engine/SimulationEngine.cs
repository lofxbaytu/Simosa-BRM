using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Ship;

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

    /// <summary>狀態鏈雜湊:h_n = SHA-256(h_{n−1} ‖ tick ‖ 狀態向量 ‖ 致動器 ‖ 陣風係數)。</summary>
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
    /// <summary>擱淺(UKC ≤ 0)</summary>
    public event Action<SimulationEngine>? Grounded;

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
        _act = new ActuatorModel(ActuatorParameters.FromParticulars(ship));
        _rng = new DeterministicRandom(scenario.Seed);
        _projection = new LocalTangentPlane(scenario.Origin!.Lat, scenario.Origin.Lon);
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
        Initialize();
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

        _telegraph = ResolveInitialTelegraph(sc);
        var rpm = Ship.TelegraphRpm(_telegraph);
        _act = new ActuatorModel(ActuatorParameters.FromParticulars(Ship))
        {
            RudderOrderDeg = sc.Initial.Rudder,
            RudderDeg = sc.Initial.Rudder,
            RpmOrder = rpm,
            Rpm = rpm,
        };

        Environment = new EnvironmentConditions
        {
            WindTrueSpeedMps = Units.KnToMps(sc.Environment.Wind.TrueSpeed),
            WindTrueDirFromRad = Units.DegToRad(sc.Environment.Wind.TrueDir),
            Gustiness = Units.Clamp(sc.Environment.Wind.Gustiness, 0.0, 1.0),
            GustFactor = 1.0,
            CurrentSetRad = Units.DegToRad(sc.Environment.Current.Set),
            CurrentDriftMps = Units.KnToMps(sc.Environment.Current.Drift),
            Depth = new ConstantDepth(sc.Environment.WaterDepth),
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
    }

    private TelegraphOrder ResolveInitialTelegraph(Scenario.Scenario sc)
    {
        if (!string.IsNullOrWhiteSpace(sc.Initial.Telegraph) &&
            Enum.TryParse<TelegraphOrder>(sc.Initial.Telegraph, ignoreCase: true, out var explicitOrder))
            return explicitOrder;
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

    private void Apply(SimCommand cmd)
    {
        switch (cmd.Type)
        {
            case SimCommandType.Rudder:
                if (cmd.ValueAsDouble() is { } deg)
                {
                    Autopilot.Enabled = false; // 手動舵令解除自動舵
                    _act.RudderOrderDeg = Units.Clamp(deg, -_act.Parameters.RudderMaxDeg, _act.Parameters.RudderMaxDeg);
                }
                break;

            case SimCommandType.Telegraph:
                if (cmd.ValueAsString() is { } s && Enum.TryParse<TelegraphOrder>(s, ignoreCase: true, out var order))
                    SetTelegraph(order);
                break;

            case SimCommandType.Rpm:
                if (cmd.ValueAsDouble() is { } rpm)
                    SetRpmOrder(Units.Clamp(rpm, -Ship.Engine.McrRpm, Ship.Engine.McrRpm));
                break;

            case SimCommandType.Thruster:
                if (cmd.ValueAsDouble() is { } thr) _act.ThrusterOrder = Units.Clamp(thr, -1.0, 1.0);
                break;

            case SimCommandType.Autopilot:
                if (cmd.ArgAsBool("enabled") is { } enabled) Autopilot.Enabled = enabled;
                if (cmd.ArgAsDouble("heading") is { } hdg) Autopilot.HeadingRad = Units.DegToRad(Units.NormalizeHeadingDeg(hdg));
                if (cmd.ArgAsDouble("rotLimit") is { } lim) Autopilot.RotLimitDegPerMin = Math.Max(1.0, Math.Abs(lim));
                break;

            case SimCommandType.Freeze:
                Frozen = true;
                break;

            case SimCommandType.Resume:
                Frozen = false;
                break;

            case SimCommandType.Reset:
                Initialize();
                break;

            case SimCommandType.TimeScale:
                if (cmd.ValueAsDouble() is { } scale)
                {
                    if (scale <= 0) Frozen = true;
                    else TimeScale = Units.Clamp(scale, 0.1, Options.MaxTimeScale);
                }
                break;

            case SimCommandType.LoadScenario:
                if (cmd.ValueAsString() is { } path) ScenarioLoadRequested?.Invoke(this, path);
                else if (cmd.Arg("path") is { } p && p.ValueKind == System.Text.Json.JsonValueKind.String)
                    ScenarioLoadRequested?.Invoke(this, p.GetString()!);
                break;

            case SimCommandType.SetEnvironment:
                ApplyEnvironment(cmd);
                break;

            case SimCommandType.InjectFault:
                if (cmd.ValueAsString() is { } f && !_faults.Contains(f)) _faults.Add(f);
                break;

            case SimCommandType.ClearFault:
                if (cmd.ValueAsString() is { } cf) _faults.Remove(cf);
                else _faults.Clear();
                break;

            case SimCommandType.Snapshot:
                TakeSnapshot();
                break;

            case SimCommandType.Restore:
                RestoreFromHistory(cmd.ArgAsDouble("tick") is { } t ? (long)t : null);
                break;
        }
    }

    private void SetTelegraph(TelegraphOrder order)
    {
        _telegraph = order;
        SetRpmOrder(Ship.TelegraphRpm(order));
    }

    private void SetRpmOrder(double rpm)
    {
        // 由停俥起動或換向視為一次起動(啟動空氣次數,規劃書第 6.2 節)
        var wasStopped = _act.RpmOrder == 0.0 || Math.Sign(_act.RpmOrder) != Math.Sign(rpm);
        if (rpm != 0.0 && wasStopped && _startsRemaining is { } n && n > 0) _startsRemaining = n - 1;
        _act.RpmOrder = rpm;
    }

    private void ApplyEnvironment(SimCommand cmd)
    {
        if (cmd.Arg("wind") is { } wind && wind.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (wind.TryGetProperty("trueSpeed", out var ws) && SimCommand.AsDouble(ws) is { } wsv) Environment.WindTrueSpeedMps = Units.KnToMps(Math.Max(0, wsv));
            if (wind.TryGetProperty("trueDir", out var wd) && SimCommand.AsDouble(wd) is { } wdv) Environment.WindTrueDirFromRad = Units.DegToRad(Units.NormalizeHeadingDeg(wdv));
            if (wind.TryGetProperty("gustiness", out var wg) && SimCommand.AsDouble(wg) is { } wgv) Environment.Gustiness = Units.Clamp(wgv, 0, 1);
        }
        if (cmd.Arg("current") is { } cur && cur.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (cur.TryGetProperty("set", out var cs) && SimCommand.AsDouble(cs) is { } csv) Environment.CurrentSetRad = Units.DegToRad(Units.NormalizeHeadingDeg(csv));
            if (cur.TryGetProperty("drift", out var cd) && SimCommand.AsDouble(cd) is { } cdv) Environment.CurrentDriftMps = Units.KnToMps(Math.Max(0, cdv));
        }
        if (cmd.ArgAsDouble("waterDepth") is { } depth && depth > 0)
        {
            if (Environment.Depth is ConstantDepth cdp) cdp.Depth = depth;
            else Environment.Depth = new ConstantDepth(depth);
        }
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

        if (Autopilot.Enabled)
            _act.RudderOrderDeg = Autopilot.RudderOrderDeg(Motion.Psi, Motion.R, _act.Parameters.RudderMaxDeg);

        _act.Step(Options.Dt,
            rudderJammed: _faults.Contains(FaultNames.SteeringGear),
            engineFailed: _faults.Contains(FaultNames.MainEngine),
            thrusterFailed: _faults.Contains(FaultNames.BowThruster));

        var sample = Environment.SampleAt(Motion.X, Motion.Y);
        Motion = Rk4Integrator.Step(Dynamics, t, Motion, sample, _act.ToControlInput(), Options.Dt);
        Tick++;

        if (!Aground && UnderKeelClearance() <= 0.0)
        {
            Aground = true;
            Grounded?.Invoke(this);
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
        Span<byte> buf = stackalloc byte[32 + 8 + 8 * 12];
        _hash.CopyTo(buf);
        var o = 32;
        BinaryPrimitives.WriteInt64LittleEndian(buf[o..], Tick); o += 8;
        foreach (var d in new[]
                 {
                     Motion.U, Motion.V, Motion.R, Motion.X, Motion.Y, Motion.Psi,
                     _act.RudderDeg, _act.RudderOrderDeg, _act.Rpm, _act.RpmOrder, _act.ThrusterActual, Environment.GustFactor,
                 })
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf[o..], BitConverter.DoubleToInt64Bits(d));
            o += 8;
        }
        SHA256.HashData(buf, _hash);
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
        Actuators = new ActuatorSnapshot(_act.RudderOrderDeg, _act.RudderDeg, _act.RpmOrder, _act.Rpm, _act.ThrusterOrder, _act.ThrusterActual),
        Telegraph = _telegraph,
        Environment = new EnvironmentSnapshot(
            Environment.WindTrueSpeedMps, Environment.WindTrueDirFromRad, Environment.Gustiness, Environment.GustFactor,
            Environment.CurrentSetRad, Environment.CurrentDriftMps,
            Environment.Depth is ConstantDepth cd ? cd.Depth : null),
        RandomSeed = _rng.Seed,
        RandomCount = _rng.Count,
        Frozen = Frozen,
        TimeScale = TimeScale,
        Autopilot = new AutopilotSnapshot(Autopilot.Enabled, Autopilot.HeadingRad, Autopilot.RotLimitDegPerMin),
        Faults = _faults.ToArray(),
        Aground = Aground,
        StartsRemaining = _startsRemaining,
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

    /// <summary>還原到歷史中 tick ≤ 指定值的最新快照(未指定時取最新)。</summary>
    public bool RestoreFromHistory(long? atOrBeforeTick = null)
    {
        EngineSnapshot? pick = null;
        for (var i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (atOrBeforeTick is null || _snapshots[i].Tick <= atOrBeforeTick.Value) { pick = _snapshots[i]; break; }
        }
        if (pick is null) return false;
        Restore(pick);
        return true;
    }

    /// <summary>從快照還原完整狀態(含亂數與雜湊鏈);快照歷史保留 tick ≤ 快照者。</summary>
    public void Restore(EngineSnapshot s)
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
        _act = new ActuatorModel(ActuatorParameters.FromParticulars(Ship))
        {
            RudderOrderDeg = s.Actuators.RudderOrderDeg,
            RudderDeg = s.Actuators.RudderDeg,
            RpmOrder = s.Actuators.RpmOrder,
            Rpm = s.Actuators.Rpm,
            ThrusterOrder = s.Actuators.ThrusterOrder,
            ThrusterActual = s.Actuators.ThrusterActual,
        };
        _telegraph = s.Telegraph;
        var depth = Environment.Depth;
        if (s.Environment.ConstantDepth is { } cd) depth = depth is ConstantDepth existing ? SetDepth(existing, cd) : new ConstantDepth(cd);
        Environment = new EnvironmentConditions
        {
            WindTrueSpeedMps = s.Environment.WindTrueSpeedMps,
            WindTrueDirFromRad = s.Environment.WindTrueDirFromRad,
            Gustiness = s.Environment.Gustiness,
            GustFactor = s.Environment.GustFactor,
            CurrentSetRad = s.Environment.CurrentSetRad,
            CurrentDriftMps = s.Environment.CurrentDriftMps,
            Depth = depth,
        };
        if (_rng.Seed != s.RandomSeed) _rng = new DeterministicRandom(s.RandomSeed, s.RandomCount);
        else _rng.ResetTo(s.RandomCount);
        Frozen = s.Frozen;
        TimeScale = s.TimeScale;
        Autopilot.Enabled = s.Autopilot.Enabled;
        Autopilot.HeadingRad = s.Autopilot.HeadingRad;
        Autopilot.RotLimitDegPerMin = s.Autopilot.RotLimitDegPerMin;
        _faults.Clear();
        _faults.AddRange(s.Faults);
        Aground = s.Aground;
        _startsRemaining = s.StartsRemaining;
        var hash = Convert.FromHexString(s.StateHash);
        if (hash.Length != 32) throw new InvalidDataException("快照雜湊長度錯誤");
        hash.CopyTo(_hash, 0);
        _snapshots.RemoveAll(x => x.Tick > s.Tick);

        static IDepthProvider SetDepth(ConstantDepth existing, double value) { existing.Depth = value; return existing; }
    }

    // ------------------------------------------------------------------ 廣播狀態

    private double UnderKeelClearance()
    {
        var depth = Environment.Depth.DepthAt(Motion.X, Motion.Y);
        var lc = Ship.GetLoading(Loading);
        var squat = EnvironmentMath.SquatBarrass(lc.BlockCoefficient ?? 0.75, Units.MpsToKn(Math.Abs(Motion.U)));
        return EnvironmentMath.UnderKeelClearance(depth, lc.MaxDraft_m, squat);
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
        var squat = EnvironmentMath.SquatBarrass(lc.BlockCoefficient ?? 0.75, Math.Abs(stwKn));
        var ukc = EnvironmentMath.UnderKeelClearance(sample.WaterDepthM, lc.MaxDraft_m, squat);
        var drift = Math.Abs(m.U) > 0.05 ? Units.RadToDeg(Math.Atan2(-m.V, m.U)) : 0.0;
        var engineState = _faults.Contains(FaultNames.MainEngine) ? EngineRunState.Failed
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
            Flags = new StateFlags { Frozen = Frozen, Aground = Aground, Collision = false },
        };
    }
}
