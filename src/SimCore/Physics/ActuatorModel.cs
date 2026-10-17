using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics;

/// <summary>主機換向狀態機的模式(Python mmg.py _engine_logic;JSON 以小寫輸出)。</summary>
public enum EngineMode
{
    /// <summary>停俥且軸已靜止</summary>
    Stopped,
    /// <summary>停俥令後軸轉速衰減中</summary>
    Stopping,
    /// <summary>已點火,朝指令轉速</summary>
    Run,
    /// <summary>換向:燃油切斷、軸轉速衰減,逾 reversalDelay 且 |n| 低於起動門檻後反向點火</summary>
    Reversing,
    /// <summary>軸靜止後重新起動(startDelay)</summary>
    Starting,
}

/// <summary>起動嘗試的本步結果(engineStartFailure;引擎據以發 engineStartFailed / engineStartBlocked / engineStarted 事件)。</summary>
public enum StartOutcome
{
    None,
    /// <summary>故障成立中點火成功(延遲或先前失敗後)</summary>
    Started,
    /// <summary>起動失敗(將自動再試)</summary>
    Failed,
    /// <summary>連續失敗達上限,起動封鎖(須車令回 STOP)</summary>
    Blocked,
}

/// <summary>致動器參數(由 particulars 或 MMG 係數檔推得)。</summary>
public sealed record ActuatorParameters
{
    /// <summary>舵機速率(度/秒)。particulars:35 / hardOverTime35(規劃書第 6.2 節);係數檔:rudder.rate_degps(70° 行程 / hardOverTime35)</summary>
    public required double RudderRateDegPerSec { get; init; }
    /// <summary>舵令上限(度)。particulars:一般操舵最大舵角;係數檔:rudder.maxAngle_deg(Schilling 舵 70°)</summary>
    public required double RudderMaxDeg { get; init; }
    /// <summary>主機轉速一階滯後時間常數(秒)</summary>
    public double RpmTimeConstantSec { get; init; } = 12.0;
    /// <summary>最低轉速(rpm);null 表示不限制</summary>
    public double? MinimumRpm { get; init; }
    /// <summary>側推由 0 到全推力所需時間(秒)</summary>
    public double ThrusterFullDelaySec { get; init; } = 35.0;
    public bool HasBowThruster { get; init; }

    // ---- 以下為 MMG 模式(與 Python 參考實作 mmg.py 一致);暫代模型用預設值即為原本的顯式更新 ----

    /// <summary>
    /// true:舵角、軸轉速、側推為一階 ODE,與船體用同一組 RK4 階段積分(<see cref="ControlStages"/>);
    /// false:每步顯式更新一次、整步內視為常數(暫代模型原行為)。
    /// </summary>
    public bool CoupledRk4 { get; init; }
    /// <summary>舵機:dδ/dt = clamp((δ_cmd − δ)/τ, ±rate);null = 純速率限制(到位即停)</summary>
    public double? RudderLagSec { get; init; }
    /// <summary>轉速令幅度上限(rpm);null = 不限制</summary>
    public double? MaxRpm { get; init; }
    /// <summary>軸轉速變化率上限(rpm/s);null = 不限制</summary>
    public double? RpmRateLimitRpmPerSec { get; init; }
    /// <summary>true:主機以換向狀態機(停俥滑行 / 起動延遲 / 換向延遲 + 5 % MCR 反向點火門檻)決定本步的轉速目標與時間常數</summary>
    public bool EngineStateMachine { get; init; }
    public double StartDelaySec { get; init; } = 5.0;
    public double ReversalDelaySec { get; init; } = 90.0;
    /// <summary>燃油切斷後軸轉速衰減的時間常數(秒)</summary>
    public double ShaftStopTimeConstantSec { get; init; } = 20.0;
    /// <summary>反向點火門檻(rpm,5 % MCR)</summary>
    public double StartThresholdRpm { get; init; }
    /// <summary>軸視為靜止的轉速(rpm,2 % maxRpm)</summary>
    public double ShaftStillRpm { get; init; } = 1.0;
    /// <summary>側推:dT/dt = clamp((cmd − T)/τ, ±1/delay);null = 固定斜率</summary>
    public double? ThrusterLagSec { get; init; }

    public static ActuatorParameters FromParticulars(ShipParticulars p) => new()
    {
        RudderRateDegPerSec = 35.0 / Math.Max(1.0, p.Rudder.HardOverTime35Seconds),
        RudderMaxDeg = p.Rudder.NormalMaxAngleDeg,
        MinimumRpm = p.Engine.MinimumRpm,
        ThrusterFullDelaySec = p.BowThruster?.FullThrustDelay_s ?? 35.0,
        HasBowThruster = p.BowThruster is not null,
    };

    /// <summary>由 MMG 係數檔建立(rudder / engine / thruster 區段),行為與 Python mmg.py 的致動器與 _engine_logic 一致。</summary>
    public static ActuatorParameters FromCoefficients(MmgCoefficients c, ShipParticulars? ship = null)
    {
        var e = c.Engine;
        return new ActuatorParameters
        {
            RudderRateDegPerSec = c.Rudder.Rate_degps,
            RudderMaxDeg = c.Rudder.MaxAngle_deg,
            RpmTimeConstantSec = e.RpmTimeConstant_s,
            MinimumRpm = e.MinRpm > 0 ? e.MinRpm : null,
            MaxRpm = e.MaxRpm,
            ThrusterFullDelaySec = Math.Max(c.Thruster.FullThrustDelay_s, 1.0),
            HasBowThruster = c.Thruster.Installed && (ship?.BowThruster is not null || c.Thruster.NominalThrust_kN > 0),
            CoupledRk4 = true,
            RudderLagSec = 1.0,
            RpmRateLimitRpmPerSec = e.RpmRateLimit_rpmps > 0 ? e.RpmRateLimit_rpmps : null,
            EngineStateMachine = true,
            StartDelaySec = e.StartDelay_s,
            ReversalDelaySec = e.ReversalDelay_s,
            ShaftStopTimeConstantSec = e.ShaftStopTimeConstant_s,
            StartThresholdRpm = 0.05 * e.Mcr_rpm,
            ShaftStillRpm = 0.02 * e.MaxRpm,
            ThrusterLagSec = 2.0,
        };
    }
}

/// <summary>
/// 舵機、主機、側推的致動器動態(含狀態,故不放在純函數的 <see cref="IShipDynamics"/> 內)。
/// 暫代模式:舵角以速率限制追隨舵令、轉速一階滯後、側推固定斜率,每步顯式更新。
/// MMG 模式(<see cref="ActuatorParameters.CoupledRk4"/>):三者為一階 ODE(含速率限制),以與船體相同的 RK4 階段積分並回傳各階段控制量;
/// 主機先經換向狀態機(<see cref="EngineMode"/>)決定本步的轉速目標與時間常數。
/// 所有欄位(含狀態機模式、計時器、目標)皆為快照與狀態雜湊內容。
/// </summary>
public sealed class ActuatorModel
{
    public ActuatorParameters Parameters { get; }

    /// <summary>舵令(度,右正)</summary>
    public double RudderOrderDeg { get; set; }
    /// <summary>實際舵角(度)</summary>
    public double RudderDeg { get; set; }
    /// <summary>轉速令(rpm,倒車負)</summary>
    public double RpmOrder { get; set; }
    /// <summary>實際轉速</summary>
    public double Rpm { get; set; }
    /// <summary>側推令(−1 至 +1)</summary>
    public double ThrusterOrder { get; set; }
    /// <summary>側推實際推力比例</summary>
    public double ThrusterActual { get; set; }

    /// <summary>主機狀態機模式</summary>
    public EngineMode EngineMode { get; set; }
    /// <summary>起動/換向計時(秒,自下令起算)</summary>
    public double EngineTimerSec { get; set; }
    /// <summary>本步的轉速目標(rpm;換向/停俥中為 0)</summary>
    public double RpmTarget { get; set; }
    /// <summary>本步的轉速時間常數(秒;燃油切斷時為軸停止時間常數)</summary>
    public double RpmTau { get; set; }

    // ---- engineStartFailure 故障(fault-catalogue;規劃書第 8.1 節 E14「主機起動延遲」、第 6.2 節起動空氣次數)----
    // 由停俥起動或換向後反向點火時視為一次「起動嘗試」:故障成立時依 args 延遲或失敗;以下欄位進快照與雜湊(非預設時)

    /// <summary>遙控系統自動連續起動的上限:連續失敗達此次數即「起動封鎖」,須車令回 STOP 後再下令(Nabtesco M-800 類遙控系統的 3 次自動起動)</summary>
    public const int MaxAutoStartAttempts = 3;
    /// <summary>engineStartFailure 是否成立(引擎每步依 faults[] 設定;非狀態)</summary>
    public bool StartFailureActive { get; set; }
    /// <summary>故障 args.delay_s:每次點火再延後的秒數(null = 不延遲)</summary>
    public double? StartFailureDelayS { get; set; }
    /// <summary>故障 args.attempts:自注入起失敗的起動次數(之後一次成功);delay_s 與 attempts 皆省略 = 每次都失敗直到清除</summary>
    public int? StartFailureAttempts { get; set; }
    /// <summary>自注入起已失敗的起動次數</summary>
    public int StartFailedAttempts { get; set; }
    /// <summary>本次車令的連續失敗次數(車令歸零時清除)</summary>
    public int StartConsecutiveFailures { get; set; }
    /// <summary>起動封鎖(連續失敗達 <see cref="MaxAutoStartAttempts"/>;車令歸零或故障清除時解除)</summary>
    public bool StartBlocked { get; set; }
    /// <summary>起動嘗試計時(秒;到達點火條件後起算,≥ delay_s 時嘗試;失敗後設為 −startDelay 以等待下一次嘗試)</summary>
    public double StartAttemptTimerSec { get; set; }
    /// <summary>本步的起動結果(引擎據以發事件;不進快照)</summary>
    public StartOutcome LastStartOutcome { get; private set; }
    /// <summary>本步點火(故障成立中)時距下令的秒數(EngineTimerSec;事件用)</summary>
    public double LastStartAfterSec { get; private set; }

    /// <summary>起動故障相關狀態是否非預設(決定雜湊是否追加;未使用此故障者雜湊與舊版相同)</summary>
    public bool HasStartFailureState => StartFailureDelayS is not null || StartFailureAttempts is not null || StartFailedAttempts != 0
                                        || StartConsecutiveFailures != 0 || StartBlocked || StartAttemptTimerSec != 0.0;

    /// <summary>
    /// 注入 engineStartFailure:記下 args 並重設計數(重複注入亦重設)。暫代模型(顯式更新、無換向狀態機)在故障未成立期間不更新
    /// <see cref="EngineMode"/>/<see cref="RpmTarget"/>(未使用者雜湊與舊版相同),注入時依目前軸轉速與車令重新設定,
    /// 避免沿用陳舊的「運轉中」而跳過起動閘(例如情境開始即進車 → 故障未成立時停俥、軸已靜止 → 注入 → 再進車)。
    /// </summary>
    public void InjectStartFailure(double? delayS, int? attempts)
    {
        StartFailureDelayS = delayS is { } d && d > 0 ? d : null;
        StartFailureAttempts = attempts is { } n && n >= 0 ? n : null;
        StartFailedAttempts = 0;
        StartConsecutiveFailures = 0;
        StartBlocked = false;
        StartAttemptTimerSec = 0.0;
        if (!Parameters.CoupledRk4) SyncExplicitEngineState();
    }

    /// <summary>
    /// 暫代模型:依目前軸轉速與車令設定狀態機模式(起動閘的「運轉中」判斷依據)。軸未靜止且與車令同向 = 運轉中(Run、目標 = 車令);
    /// 否則 Stopped(下一個非零車令須經起動閘;反向轉動者先滑行到軸靜止)。
    /// </summary>
    private void SyncExplicitEngineState()
    {
        var running = RpmOrder != 0.0 && Math.Abs(Rpm) >= Parameters.ShaftStillRpm && Math.Sign(Rpm) == Math.Sign(RpmOrder);
        EngineMode = running ? EngineMode.Run : EngineMode.Stopped;
        RpmTarget = running ? RpmOrder : 0.0;
        EngineTimerSec = 0.0;
    }

    /// <summary>清除 engineStartFailure:解除封鎖與嘗試計時(失敗次數保留供講評;args 清除)。</summary>
    public void ClearStartFailure()
    {
        StartFailureDelayS = null;
        StartFailureAttempts = null;
        StartConsecutiveFailures = 0;
        StartBlocked = false;
        StartAttemptTimerSec = 0.0;
    }

    /// <summary>
    /// 點火閘(到達正常點火條件時呼叫;回傳 true = 本步點火)。故障未成立時一律 true。成立時:封鎖中不點火;
    /// 嘗試計時 ≥ delay_s 時嘗試一次:依 attempts 決定成敗(無 attempts 且無 delay_s = 失敗);失敗後等 startDelay 再自動嘗試,
    /// 連續失敗達 <see cref="MaxAutoStartAttempts"/> 次封鎖。
    /// </summary>
    private bool StartGate(double dt)
    {
        if (!StartFailureActive) return true;
        if (StartBlocked) return false;
        StartAttemptTimerSec += dt;
        if (StartAttemptTimerSec < (StartFailureDelayS ?? 0.0) - 1e-9) return false;
        var fails = StartFailureAttempts is { } n ? StartFailedAttempts < n : StartFailureDelayS is null;
        if (!fails)
        {
            StartAttemptTimerSec = 0.0;
            StartConsecutiveFailures = 0;
            LastStartOutcome = StartOutcome.Started;
            LastStartAfterSec = EngineTimerSec;
            return true;
        }
        StartFailedAttempts++;
        StartConsecutiveFailures++;
        StartAttemptTimerSec = -Parameters.StartDelaySec;
        if (StartConsecutiveFailures >= MaxAutoStartAttempts)
        {
            StartBlocked = true;
            LastStartOutcome = StartOutcome.Blocked;
        }
        else LastStartOutcome = StartOutcome.Failed;
        return false;
    }

    /// <summary>
    /// 車令歸零:解除封鎖、重設連續失敗與嘗試計時。只在車令(轉速令)本身為 0 時呼叫;主機故障(mainEngine)使目標為 0 時不呼叫,
    /// 封鎖與連續失敗次數保留(契約:連續 3 次失敗起動封鎖,須車令回 STOP 再下令)。
    /// </summary>
    private void ResetStartAttempt()
    {
        StartConsecutiveFailures = 0;
        StartBlocked = false;
        StartAttemptTimerSec = 0.0;
    }

    public ActuatorModel(ActuatorParameters parameters)
    {
        Parameters = parameters;
        RpmTau = parameters.RpmTimeConstantSec;
    }

    public ControlInput ToControlInput() => new(Units.DegToRad(RudderDeg), Rpm, ThrusterActual);

    /// <summary>依目前轉速設定狀態機初值(初始化/重設用):轉速非零視為已點火運轉,否則停俥。</summary>
    public void InitializeEngineState()
    {
        EngineMode = Math.Abs(Rpm) > 1e-6 ? EngineMode.Run : EngineMode.Stopped;
        EngineTimerSec = 0.0;
        RpmTarget = Rpm;
        RpmTau = Parameters.RpmTimeConstantSec;
    }

    /// <summary>
    /// 推進一步,回傳本步 RK4 各階段的控制量。<paramref name="rudderJammed"/>:舵機故障(舵角不動);
    /// <paramref name="engineFailed"/>:主機故障(轉速衰減到 0);<paramref name="thrusterFailed"/>:側推不可用;
    /// <paramref name="rudderRateDegPerSec"/>:本步的舵機速率(null = 參數值;應急操舵降速、泵數換算用,規劃書第 7.2 節操舵模式)。
    /// </summary>
    public ControlStages Step(double dt, bool rudderJammed = false, bool engineFailed = false, bool thrusterFailed = false, double? rudderRateDegPerSec = null)
    {
        var p = Parameters;
        RudderOrderDeg = Units.Clamp(RudderOrderDeg, -p.RudderMaxDeg, p.RudderMaxDeg);
        ThrusterOrder = p.HasBowThruster ? Units.Clamp(ThrusterOrder, -1.0, 1.0) : 0.0;
        var rudderRate = rudderJammed ? 0.0 : Math.Max(0.0, rudderRateDegPerSec ?? p.RudderRateDegPerSec);
        LastStartOutcome = StartOutcome.None;
        return p.CoupledRk4
            ? StepCoupled(dt, rudderRate, engineFailed, thrusterFailed)
            : StepExplicit(dt, rudderRate, engineFailed, thrusterFailed);
    }

    // ------------------------------------------------------------------ 暫代模式:顯式更新

    private ControlStages StepExplicit(double dt, double rudderRate, bool engineFailed, bool thrusterFailed)
    {
        var p = Parameters;

        // 舵機:速率限制(速率 0 = 卡舵)
        if (rudderRate > 0.0)
        {
            var maxDelta = rudderRate * dt;
            var delta = RudderOrderDeg - RudderDeg;
            RudderDeg += Units.Clamp(delta, -maxDelta, maxDelta);
        }

        // 主機:一階滯後;最低轉速以下的非零令視為最低轉速
        var target = RpmOrder;
        if (engineFailed) target = 0.0;
        else if (p.MinimumRpm is { } min && target != 0.0 && Math.Abs(target) < min) target = Math.Sign(target) * min;
        if (StartFailureActive) target = ExplicitStartGate(dt, target, orderZero: RpmOrder == 0.0);
        Rpm += (target - Rpm) * (dt / p.RpmTimeConstantSec);
        if (Math.Abs(Rpm) < 1e-6 && target == 0.0) Rpm = 0.0;

        // 側推:固定斜率
        var thrTarget = thrusterFailed ? 0.0 : ThrusterOrder;
        var thrRate = dt / Math.Max(0.1, p.ThrusterFullDelaySec);
        ThrusterActual += Units.Clamp(thrTarget - ThrusterActual, -thrRate, thrRate);

        return ControlStages.Constant(ToControlInput());
    }

    /// <summary>
    /// 暫代模型(無換向狀態機)在 engineStartFailure 成立時的起動閘:主機未運轉(或反向)時須點火,點火前目標轉速為 0
    /// (反向者先滑行到軸靜止);已在同向運轉者不受影響。故障未成立時不呼叫(與舊版位元一致;注入時以 <see cref="SyncExplicitEngineState"/> 同步)。
    /// <paramref name="orderZero"/>:車令本身為 0(STOP)才解除封鎖;主機故障使目標為 0 時保留封鎖。
    /// </summary>
    private double ExplicitStartGate(double dt, double target, bool orderZero)
    {
        var p = Parameters;
        if (target == 0.0)
        {
            EngineMode = EngineMode.Stopped;
            EngineTimerSec = 0.0;
            if (orderZero) ResetStartAttempt();
            return 0.0;
        }
        var dir = Math.Sign(target);
        var running = (EngineMode == EngineMode.Run && Math.Sign(RpmTarget) == dir)
                      || (EngineMode is not (EngineMode.Starting or EngineMode.Reversing) && Math.Abs(Rpm) >= p.ShaftStillRpm && Math.Sign(Rpm) == dir);
        if (running)
        {
            EngineMode = EngineMode.Run;
            RpmTarget = target;
            return target;
        }
        if (EngineMode is not (EngineMode.Starting or EngineMode.Reversing))
        {
            EngineTimerSec = 0.0;
            StartAttemptTimerSec = 0.0;
        }
        EngineTimerSec += dt;
        var still = Math.Abs(Rpm) < p.ShaftStillRpm;
        if (still && StartGate(dt))
        {
            EngineMode = EngineMode.Run;
            RpmTarget = target;
            return target;
        }
        EngineMode = still ? EngineMode.Starting : EngineMode.Reversing;
        RpmTarget = 0.0;
        return 0.0;
    }

    // ------------------------------------------------------------------ MMG 模式:狀態機 + 耦合 RK4

    /// <summary>轉速令經幅度限制(Python set_rpm:|rpm| 夾到 [minRpm, maxRpm];0 保持 0)。</summary>
    private double EffectiveRpmOrder(bool engineFailed)
    {
        var p = Parameters;
        var rpm = engineFailed ? 0.0 : RpmOrder;
        if (Math.Abs(rpm) < 1e-6) return 0.0;
        var mag = Math.Abs(rpm);
        if (p.MinimumRpm is { } min) mag = Math.Max(mag, min);
        if (p.MaxRpm is { } max) mag = Math.Min(mag, max);
        return Math.CopySign(mag, rpm);
    }

    /// <summary>
    /// 車鐘/轉速指令 → 本步的轉速目標與時間常數(規劃書 6.2「主機/推進控制」;Python _engine_logic 逐行移植,單位改 rpm)。
    /// 模式:stopped / stopping(停俥滑行)/ run(已點火朝指令轉速)/ reversing(換向:燃油切斷、等軸轉速降到起動門檻且逾
    /// reversalDelay)/ starting(停俥後重新起動,startDelay)。一旦進入 run,直到指令改變方向或歸零才離開。
    /// </summary>
    private void EngineLogic(double dt, bool engineFailed)
    {
        var p = Parameters;
        var cmd = EffectiveRpmOrder(engineFailed);
        var n = Rpm;
        var small = p.ShaftStillRpm;
        if (Math.Abs(cmd) < 1e-9)
        {
            EngineMode = Math.Abs(n) < small ? EngineMode.Stopped : EngineMode.Stopping;
            RpmTarget = 0.0;
            RpmTau = p.ShaftStopTimeConstantSec;
            EngineTimerSec = 0.0;
            // 車令歸零才解除起動封鎖;主機故障(engineFailed)使 cmd = 0 時保留封鎖與連續失敗次數
            if (!engineFailed || Math.Abs(EffectiveRpmOrder(false)) < 1e-9) ResetStartAttempt();
            return;
        }
        var cmdDir = cmd > 0 ? 1.0 : -1.0;
        if (EngineMode == EngineMode.Run && RpmTarget * cmdDir > 0)
        {
            RpmTarget = cmd;
            RpmTau = p.RpmTimeConstantSec;
            return;
        }
        var shaftDir = Math.Abs(n) < small ? 0.0 : (n > 0 ? 1.0 : -1.0);
        if (shaftDir == cmdDir)
        {
            EngineMode = EngineMode.Run;
            RpmTarget = cmd;
            RpmTau = p.RpmTimeConstantSec;
            EngineTimerSec = 0.0;
            return;
        }
        if (shaftDir != 0.0 || EngineMode == EngineMode.Reversing)
        {
            // 換向:燃油切斷、軸轉速衰減;計時自下令起算;逾時且 |n| 低於起動門檻才反向點火
            if (EngineMode != EngineMode.Reversing)
            {
                EngineMode = EngineMode.Reversing;
                EngineTimerSec = 0.0;
                StartAttemptTimerSec = 0.0;
            }
            EngineTimerSec += dt;
            // 反向點火即一次起動嘗試(engineStartFailure 時經點火閘延遲或失敗)
            if (EngineTimerSec >= p.ReversalDelaySec && Math.Abs(n) <= p.StartThresholdRpm && StartGate(dt))
            {
                EngineMode = EngineMode.Run;
                RpmTarget = cmd;
                RpmTau = p.RpmTimeConstantSec;
            }
            else
            {
                RpmTarget = 0.0;
                RpmTau = p.ShaftStopTimeConstantSec;
            }
            return;
        }
        // 軸靜止、未在換向:起動延遲
        if (EngineMode != EngineMode.Starting)
        {
            EngineMode = EngineMode.Starting;
            EngineTimerSec = 0.0;
            StartAttemptTimerSec = 0.0;
        }
        EngineTimerSec += dt;
        if (EngineTimerSec >= p.StartDelaySec && StartGate(dt))
        {
            EngineMode = EngineMode.Run;
            RpmTarget = cmd;
            RpmTau = p.RpmTimeConstantSec;
        }
        else
        {
            RpmTarget = 0.0;
            RpmTau = p.ShaftStopTimeConstantSec;
        }
    }

    /// <summary>致動器狀態(度、rpm、比例)的導數;與 Python _derivs 的致動器部分對應(舵角以度計,速率限制同比例換算)。</summary>
    private (double DDelta, double DRpm, double DThr) Rates(double delta, double rpm, double thr,
        double deltaCmd, double rudderRate, double thrCmd)
    {
        var p = Parameters;
        // 舵機:dδ/dt = clamp((δ_cmd − δ)/τ, ±rate)
        var dd = (deltaCmd - delta) / (p.RudderLagSec ?? 1.0);
        if (dd > rudderRate) dd = rudderRate;
        else if (dd < -rudderRate) dd = -rudderRate;
        // 主機:一階滯後 + 速率限制
        var dn = (RpmTarget - rpm) / RpmTau;
        if (p.RpmRateLimitRpmPerSec is { } nRate)
        {
            if (dn > nRate) dn = nRate;
            else if (dn < -nRate) dn = -nRate;
        }
        // 側推:dT/dt = clamp((cmd − T)/τ, ±1/delay)
        var thrRate = 1.0 / Math.Max(p.ThrusterFullDelaySec, 1.0);
        var dth = (thrCmd - thr) / (p.ThrusterLagSec ?? 1.0);
        if (dth > thrRate) dth = thrRate;
        else if (dth < -thrRate) dth = -thrRate;
        return (dd, dn, dth);
    }

    private ControlStages StepCoupled(double dt, double rudderRate, bool engineFailed, bool thrusterFailed)
    {
        var p = Parameters;
        if (p.EngineStateMachine) EngineLogic(dt, engineFailed);
        else
        {
            RpmTarget = EffectiveRpmOrder(engineFailed);
            RpmTau = p.RpmTimeConstantSec;
        }
        var deltaCmd = RudderOrderDeg;
        var thrCmd = thrusterFailed ? 0.0 : ThrusterOrder;

        var d0 = RudderDeg; var n0 = Rpm; var t0 = ThrusterActual;
        var k1 = Rates(d0, n0, t0, deltaCmd, rudderRate, thrCmd);
        var d1 = d0 + 0.5 * dt * k1.DDelta; var n1 = n0 + 0.5 * dt * k1.DRpm; var t1 = t0 + 0.5 * dt * k1.DThr;
        var k2 = Rates(d1, n1, t1, deltaCmd, rudderRate, thrCmd);
        var d2 = d0 + 0.5 * dt * k2.DDelta; var n2 = n0 + 0.5 * dt * k2.DRpm; var t2 = t0 + 0.5 * dt * k2.DThr;
        var k3 = Rates(d2, n2, t2, deltaCmd, rudderRate, thrCmd);
        var d3 = d0 + dt * k3.DDelta; var n3 = n0 + dt * k3.DRpm; var t3 = t0 + dt * k3.DThr;
        var k4 = Rates(d3, n3, t3, deltaCmd, rudderRate, thrCmd);

        RudderDeg = d0 + dt / 6.0 * (k1.DDelta + 2.0 * k2.DDelta + 2.0 * k3.DDelta + k4.DDelta);
        Rpm = n0 + dt / 6.0 * (k1.DRpm + 2.0 * k2.DRpm + 2.0 * k3.DRpm + k4.DRpm);
        ThrusterActual = t0 + dt / 6.0 * (k1.DThr + 2.0 * k2.DThr + 2.0 * k3.DThr + k4.DThr);
        // 舵角硬限制(Python step:積分後夾到 ±delta_max)
        RudderDeg = Units.Clamp(RudderDeg, -p.RudderMaxDeg, p.RudderMaxDeg);

        return new ControlStages(
            new ControlInput(Units.DegToRad(d0), n0, t0),
            new ControlInput(Units.DegToRad(d1), n1, t1),
            new ControlInput(Units.DegToRad(d2), n2, t2),
            new ControlInput(Units.DegToRad(d3), n3, t3));
    }
}
