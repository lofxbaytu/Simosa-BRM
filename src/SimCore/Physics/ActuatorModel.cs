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
    /// <paramref name="engineFailed"/>:主機故障(轉速衰減到 0);<paramref name="thrusterFailed"/>:側推不可用。
    /// </summary>
    public ControlStages Step(double dt, bool rudderJammed = false, bool engineFailed = false, bool thrusterFailed = false)
    {
        var p = Parameters;
        RudderOrderDeg = Units.Clamp(RudderOrderDeg, -p.RudderMaxDeg, p.RudderMaxDeg);
        ThrusterOrder = p.HasBowThruster ? Units.Clamp(ThrusterOrder, -1.0, 1.0) : 0.0;
        return p.CoupledRk4
            ? StepCoupled(dt, rudderJammed, engineFailed, thrusterFailed)
            : StepExplicit(dt, rudderJammed, engineFailed, thrusterFailed);
    }

    // ------------------------------------------------------------------ 暫代模式:顯式更新

    private ControlStages StepExplicit(double dt, bool rudderJammed, bool engineFailed, bool thrusterFailed)
    {
        var p = Parameters;

        // 舵機:速率限制
        if (!rudderJammed)
        {
            var maxDelta = p.RudderRateDegPerSec * dt;
            var delta = RudderOrderDeg - RudderDeg;
            RudderDeg += Units.Clamp(delta, -maxDelta, maxDelta);
        }

        // 主機:一階滯後;最低轉速以下的非零令視為最低轉速
        var target = RpmOrder;
        if (engineFailed) target = 0.0;
        else if (p.MinimumRpm is { } min && target != 0.0 && Math.Abs(target) < min) target = Math.Sign(target) * min;
        Rpm += (target - Rpm) * (dt / p.RpmTimeConstantSec);
        if (Math.Abs(Rpm) < 1e-6 && target == 0.0) Rpm = 0.0;

        // 側推:固定斜率
        var thrTarget = thrusterFailed ? 0.0 : ThrusterOrder;
        var thrRate = dt / Math.Max(0.1, p.ThrusterFullDelaySec);
        ThrusterActual += Units.Clamp(thrTarget - ThrusterActual, -thrRate, thrRate);

        return ControlStages.Constant(ToControlInput());
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
            }
            EngineTimerSec += dt;
            if (EngineTimerSec >= p.ReversalDelaySec && Math.Abs(n) <= p.StartThresholdRpm)
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
        }
        EngineTimerSec += dt;
        if (EngineTimerSec >= p.StartDelaySec)
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

    private ControlStages StepCoupled(double dt, bool rudderJammed, bool engineFailed, bool thrusterFailed)
    {
        var p = Parameters;
        if (p.EngineStateMachine) EngineLogic(dt, engineFailed);
        else
        {
            RpmTarget = EffectiveRpmOrder(engineFailed);
            RpmTau = p.RpmTimeConstantSec;
        }
        var deltaCmd = RudderOrderDeg;
        var rudderRate = rudderJammed ? 0.0 : p.RudderRateDegPerSec;
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
