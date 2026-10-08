using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Traffic;

/// <summary>COLREG 覆蓋層的階段。</summary>
public enum ColregPhase
{
    /// <summary>未介入</summary>
    None,
    /// <summary>讓路行動中(對遇右轉、橫越讓路、追越避讓)</summary>
    GiveWay,
    /// <summary>直航(保持航向航速)</summary>
    StandOn,
    /// <summary>直航船的最後手段行動(第 17 條)</summary>
    LastResort,
    /// <summary>行動結束後的冷卻期(不再觸發)</summary>
    Resumed,
}

/// <summary>教官強制:auto 依規則、giveWay 強制讓路、standOn 強制直航。</summary>
public enum ColregForce
{
    Auto,
    GiveWay,
    StandOn,
}

/// <summary>某船在 ENU 的運動學(目標船行為與會遇計算的輸入;自船與目標共用)。</summary>
public readonly record struct ShipKinematics(double X, double Y, double HeadingRad, double VelE, double VelN, double Loa, double Beam)
{
    public double SogMps => Math.Sqrt(VelE * VelE + VelN * VelN);
}

/// <summary>
/// 目標船(規劃書第 5.2 節 Traffic、第 9.1 節):識別/靜態資料、簡化運動模型(迴轉率一階響應 + 航速一階滯後,參數依船長估計)、
/// 行為模式狀態(航點、腳本、錨泊、跟隨、COLREG 覆蓋層)、AIS 設定、評估用的最小 CPA 追蹤。
/// 所有欄位都進入快照(<see cref="TargetSnapshot"/>),步進完全由 <see cref="TrafficManager"/> 驅動,不使用亂數。
/// </summary>
public sealed class TargetShip
{
    /// <summary>目前有效的定義(建構時深拷貝,指令可修改)</summary>
    public ScenarioTarget Spec { get; private set; }
    public string Id => Spec.Id;
    public string Name => string.IsNullOrWhiteSpace(Spec.Name) ? Spec.Id : Spec.Name!;
    public int Mmsi { get; private set; }

    // ---- 運動狀態(SI、弧度)
    public bool Active { get; set; }
    public long ActivatedTick { get; set; } = -1;
    /// <summary>本地 ENU 位置(m)</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>航向(弧度,0–2π)</summary>
    public double Psi { get; set; }
    /// <summary>對水速度(m/s,≥ 0)</summary>
    public double Speed { get; set; }
    /// <summary>迴轉率(rad/s,右轉正)</summary>
    public double R { get; set; }
    /// <summary>對地速度(m/s,ENU;錨泊時由位置差分)</summary>
    public double VelE { get; set; }
    public double VelN { get; set; }

    // ---- 行為輸出(指令)
    public double HeadingCmdRad { get; set; }
    public double SpeedCmdMps { get; set; }
    /// <summary>目前有效的迴轉率上限(rad/s;腳本可暫時覆寫)</summary>
    public double RotLimitRadps { get; set; }
    public bool Manual { get; set; }
    public double ManualHeadingRad { get; set; }
    public double ManualSpeedMps { get; set; }
    public int WaypointIndex { get; set; }
    public int ScriptIndex { get; set; }
    /// <summary>錨泊:錨位與基準艏向(迴盪疊加其上)</summary>
    public double AnchorX { get; set; }
    public double AnchorY { get; set; }
    public double AnchorBaseRad { get; set; }

    // ---- COLREG 覆蓋層
    public ColregPhase ColregPhase { get; set; }
    public ColregForce ColregForce { get; set; }
    public double ColregHeadingRad { get; set; }
    public double ColregSpeedMps { get; set; }
    public double ColregTurnAccumRad { get; set; }
    public long ColregActionTick { get; set; } = -1;
    public long ColregResumeTick { get; set; } = -1;
    public string? ColregSituation { get; set; }

    // ---- 評估與聲號
    public double MinCpaNm { get; set; } = double.PositiveInfinity;
    public double MinRangeNm { get; set; } = double.PositiveInfinity;
    public bool CpaAlarm { get; set; }
    public string? Sound { get; set; }
    public long SoundUntilTick { get; set; } = -1;

    // ---- 運動參數
    public double HeadingTauS { get; private set; }
    public double MaxRotRadps { get; private set; }
    public double SpeedTauS { get; private set; }
    public double MaxSpeedMps { get; private set; }

    public TargetShip(ScenarioTarget spec, int defaultMmsi)
    {
        Spec = spec.Clone();
        Mmsi = Spec.Mmsi ?? defaultMmsi;
        ApplyMotionParameters();
    }

    /// <summary>
    /// 依船長估計運動參數(未在 spec.motion 指定時):迴轉時間常數 L/10 s(2–30)、最大迴轉率 3000/L 度/分(15–120)、
    /// 航速時間常數 2 L s(10–400)、最大航速 20 kn(L &lt; 50 m 為 30 kn)。
    /// </summary>
    public void ApplyMotionParameters()
    {
        var l = Math.Max(5.0, Spec.Loa);
        var m = Spec.Motion;
        HeadingTauS = m?.HeadingTau ?? Units.Clamp(l / 10.0, 2.0, 30.0);
        MaxRotRadps = Units.DegPerMinToRadPerSec(m?.MaxRot ?? Units.Clamp(3000.0 / l, 15.0, 120.0));
        SpeedTauS = m?.SpeedTau ?? Units.Clamp(2.0 * l, 10.0, 400.0);
        MaxSpeedMps = Units.KnToMps(m?.MaxSpeed ?? (l < 50.0 ? 30.0 : 20.0));
        RotLimitRadps = MaxRotRadps;
    }

    /// <summary>替換定義(targetControl 的 behaviour/add 等);重算運動參數。</summary>
    public void ReplaceSpec(ScenarioTarget spec)
    {
        Spec = spec.Clone();
        Mmsi = Spec.Mmsi ?? Mmsi;
        ApplyMotionParameters();
    }

    /// <summary>設定初始運動狀態(情境載入或 add 指令;位置已換算為 ENU)。</summary>
    public void SetInitialState(double x, double y)
    {
        X = x;
        Y = y;
        Psi = Units.NormalizeHeadingRad(Units.DegToRad(Spec.Initial.Heading));
        Speed = Math.Max(0.0, Units.KnToMps(Spec.Initial.Speed));
        R = Units.DegPerMinToRadPerSec(Spec.Initial.Rot);
        VelE = Speed * Math.Sin(Psi);
        VelN = Speed * Math.Cos(Psi);
        HeadingCmdRad = Psi;
        SpeedCmdMps = Speed;
        AnchorX = x;
        AnchorY = y;
        AnchorBaseRad = Psi;
        WaypointIndex = 0;
        ScriptIndex = 0;
        ColregPhase = ColregPhase.None;
        ColregTurnAccumRad = 0.0;
        ColregSituation = null;
    }

    /// <summary>COLREG 覆蓋層是否啟用(mode = colreg,或其他模式加 colreg.enabled)。</summary>
    public bool ColregEnabled => Spec.Behaviour.Mode == TargetBehaviourMode.Colreg
        ? Spec.Behaviour.Colreg?.Enabled ?? true
        : Spec.Behaviour.Colreg?.Enabled ?? false;

    public TargetColreg ColregSettings => Spec.Behaviour.Colreg ?? DefaultColreg;
    private static readonly TargetColreg DefaultColreg = new();

    /// <summary>跟隨自船的目標(引水船、拖船):不列入自船的 CPA 警報。</summary>
    public bool EscortsOwnShip => Spec.Behaviour.Mode == TargetBehaviourMode.Follow
        && string.Equals(Spec.Behaviour.Follow?.Leader ?? "own", "own", StringComparison.OrdinalIgnoreCase);

    /// <summary>自出現起的秒數。</summary>
    public double TimeSinceActivation(long tick, double dt) => ActivatedTick < 0 ? 0.0 : (tick - ActivatedTick) * dt;

    /// <summary>目前運動學(供會遇計算與跟隨)。</summary>
    public ShipKinematics Kinematics => new(X, Y, Psi, VelE, VelN, Spec.Loa, Spec.Beam);

    /// <summary>燈號類別:指定者優先,否則依行為(錨泊)與船型(引水、漁船)推定。</summary>
    public string LightsClass
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Spec.Lights)) return Spec.Lights!;
            if (Spec.Behaviour.Mode == TargetBehaviourMode.Anchored) return "anchored";
            return Spec.ShipType.ToLowerInvariant() switch
            {
                "pilot" => "pilot",
                "fishing" => "fishing",
                _ => "powerDriven",
            };
        }
    }

    /// <summary>AIS 船型碼(ITU-R M.1371 表 53)。</summary>
    public static int AisShipTypeCode(string shipType) => shipType.ToLowerInvariant() switch
    {
        "cargo" => 70,
        "tanker" => 80,
        "fishing" => 30,
        "pilot" => 50,
        "tug" => 52,
        "passenger" => 60,
        "pleasure" => 37,
        "sar" => 51,
        "hsc" => 40,
        "sailing" => 36,
        _ => 90,
    };

    /// <summary>AIS 航行狀態:錨泊 1、停船且航速 0 為 0(機動航行);其餘 0。</summary>
    public int AisNavStatus => Spec.Behaviour.Mode == TargetBehaviourMode.Anchored ? 1 : 0;

    // ------------------------------------------------------------------ 運動模型

    /// <summary>
    /// 一步:航向以「航向誤差 → 目標迴轉率(限幅)→ 一階響應」、航速一階滯後,位置含均勻流;dt 為引擎步長。
    /// 指令為 <paramref name="headingCmd"/>/<paramref name="speedCmd"/>(已依優先序決定)。
    /// </summary>
    public void Integrate(double headingCmd, double speedCmd, double currentE, double currentN, double dt)
    {
        var err = Units.WrapRadPi(headingCmd - Psi);
        // 誤差 10° 以上即以最大迴轉率轉向
        var gain = RotLimitRadps / Units.DegToRad(10.0);
        var rotDesired = Units.Clamp(gain * err, -RotLimitRadps, RotLimitRadps);
        var kr = 1.0 - Math.Exp(-dt / HeadingTauS);
        R += (rotDesired - R) * kr;
        Psi = Units.NormalizeHeadingRad(Psi + R * dt);

        var kv = 1.0 - Math.Exp(-dt / SpeedTauS);
        var target = Units.Clamp(speedCmd, 0.0, MaxSpeedMps);
        Speed += (target - Speed) * kv;
        if (Speed < 0.0) Speed = 0.0;

        VelE = Speed * Math.Sin(Psi) + currentE;
        VelN = Speed * Math.Cos(Psi) + currentN;
        X += VelE * dt;
        Y += VelN * dt;
    }

    /// <summary>
    /// 錨泊運動學:基準艏向以 120 s 時間常數轉向風流合成來向,疊加 swing·sin(2πt/period) 的迴盪;
    /// 船舯位於錨位後方 scope 處;對地速度由位置差分(供 AIS SOG)。
    /// </summary>
    public void IntegrateAnchored(double headToRad, double tSinceActivation, double dt)
    {
        var a = Spec.Behaviour.Anchor ?? DefaultAnchor;
        var k = 1.0 - Math.Exp(-dt / 120.0);
        AnchorBaseRad = Units.NormalizeHeadingRad(AnchorBaseRad + Units.WrapRadPi(headToRad - AnchorBaseRad) * k);
        var swing = Units.DegToRad(a.Swing) * Math.Sin(Units.TwoPi * tSinceActivation / Math.Max(1.0, a.Period));
        var psiNew = Units.NormalizeHeadingRad(AnchorBaseRad + swing);
        var scope = a.Scope ?? Spec.Loa;
        var xNew = AnchorX - scope * Math.Sin(psiNew);
        var yNew = AnchorY - scope * Math.Cos(psiNew);
        R = Units.WrapRadPi(psiNew - Psi) / dt;
        VelE = (xNew - X) / dt;
        VelN = (yNew - Y) / dt;
        X = xNew;
        Y = yNew;
        Psi = psiNew;
        Speed = 0.0;
        HeadingCmdRad = psiNew;
        SpeedCmdMps = 0.0;
    }

    private static readonly TargetAnchor DefaultAnchor = new();
}
