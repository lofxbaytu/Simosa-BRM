using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics.External;

/// <summary>拖船命令模式(指令 tugControl.mode)。</summary>
public enum TugMode
{
    Standby,
    Push,
    Pull,
    Free,
    /// <summary>拖船離開、不再可用(教官或時間表 tugControl mode unavailable;之後的指令以 tugUnavailable 拒絕)。列在最後:既有值與雜湊不變。</summary>
    Unavailable,
}

/// <summary>拖船狀態(state.tugs[].status)。</summary>
public enum TugStatus
{
    /// <summary>已帶纜/靠妥待命,無出力</summary>
    Standby,
    /// <summary>自 free 帶纜/靠上中(connect_s)</summary>
    Connecting,
    /// <summary>推 ↔ 拉換位中(modeChange_s),無出力</summary>
    Repositioning,
    /// <summary>推或拉出力中</summary>
    Working,
    /// <summary>已解纜離開</summary>
    Free,
    /// <summary>拖纜斷裂(tugLineBreak);故障清除前不能拉</summary>
    Parted,
    /// <summary>已離開、本練習不再可用(拖船縮減;無出力,之後的指令以 tugUnavailable 拒絕)</summary>
    Unavailable,
}

/// <summary>
/// 單艘拖船(設計文件 physics-tugs-anchors-mooring.md 第 2 節):出力一階上升(時間常數 riseTime_s)、
/// 方向以有限速率變更(拖船繞行)、自船對水速度高於門檻時效能遞減、推拉換位與帶纜時間、拖纜斷裂。
/// 力為作用於固定連接點的水平向量(方向相對船艏),跨步狀態在本類別欄位(快照、雜湊)。
/// </summary>
public sealed class TugUnit
{
    public const double TonneForce = 9.80665 * 1000.0;

    public string Id { get; }
    public string? Name { get; }
    public double BollardPullN { get; }
    public double AttachX { get; }
    public double AttachY { get; }
    public double LineLengthM { get; }
    public bool AllowPush { get; }
    public bool AllowPull { get; }
    public double RiseTauS { get; }
    public double TurnRateRadPerSec { get; }
    public double PushFullMps { get; }
    public double PushHalfMps { get; }
    public double PullFullMps { get; }
    public double PullHalfMps { get; }
    public long ModeChangeTicks { get; }
    public long ConnectTicks { get; }
    private readonly double _alpha;

    // ---- 跨步狀態 ----
    public TugMode Mode { get; set; } = TugMode.Standby;
    public TugStatus Status { get; set; } = TugStatus.Standby;
    /// <summary>實際出力(N)</summary>
    public double ForceN { get; set; }
    /// <summary>實際力方向(相對船艏,弧度,(−π, π])</summary>
    public double DirectionRad { get; set; }
    public double DirectionOrderRad { get; set; }
    /// <summary>出力令(0–1)</summary>
    public double PowerOrder { get; set; }
    /// <summary>連接/換位剩餘步數</summary>
    public long TimerTicks { get; set; }
    /// <summary>拖纜已斷(故障清除前不能拉)</summary>
    public bool LineParted { get; set; }
    /// <summary>
    /// 拖船目前就位的工作側(Push = 推頂側靠船、Pull = 拖纜已帶上在纜端;Standby = 尚未就位)。push ↔ pull 換位依此判定,
    /// 中間插入 standby 不會改變它(審查發現:推 → standby → 拉不可繞過 modeChange_s)。
    /// </summary>
    public TugMode LastWorkingMode { get; set; } = TugMode.Standby;
    /// <summary>最近一步的速度效能(0–1;狀態廣播用,由狀態重算)</summary>
    public double Efficiency { get; private set; } = 1.0;

    public TugUnit(ScenarioTug t, ShipParticulars ship, double dt)
    {
        Id = t.Id;
        Name = t.Name;
        BollardPullN = t.BollardPullT * TonneForce;
        (AttachX, AttachY) = (t.Attach ?? new ScenarioBodyPoint { Named = "fwd" }).Resolve(ship);
        LineLengthM = t.LineLengthM;
        var modes = t.Modes is { Count: > 0 } m ? m : new List<string> { "push", "pull" };
        AllowPush = modes.Contains("push");
        AllowPull = modes.Contains("pull");
        RiseTauS = t.RiseTimeS;
        TurnRateRadPerSec = Units.DegToRad(t.TurnRateDegPerSec ?? Units.RadToDeg(Units.KnToMps(t.RepositionSpeedKn) / Math.Max(1.0, t.LineLengthM)));
        PushFullMps = Units.KnToMps(t.PushFullUpToKn);
        PushHalfMps = Units.KnToMps(t.PushHalfAtKn);
        PullFullMps = Units.KnToMps(t.PullFullUpToKn);
        PullHalfMps = Units.KnToMps(t.PullHalfAtKn);
        var sps = 1.0 / dt;
        ModeChangeTicks = (long)Math.Round(Math.Max(0.0, t.ModeChangeS) * sps);
        ConnectTicks = (long)Math.Round(Math.Max(0.0, t.ConnectS) * sps);
        _alpha = 1.0 - Math.Exp(-dt / RiseTauS);

        var init = t.Initial;
        var mode = init?.Mode switch { "push" => TugMode.Push, "pull" => TugMode.Pull, "free" => TugMode.Free, _ => TugMode.Standby };
        DirectionRad = DirectionOrderRad = Units.WrapRadPi(Units.DegToRad(init?.DirectionDeg ?? 90.0));
        PowerOrder = Units.Clamp((init?.PowerPct ?? 0.0) / 100.0, 0.0, 1.0);
        Mode = mode;
        Status = mode switch { TugMode.Push or TugMode.Pull => TugStatus.Working, TugMode.Free => TugStatus.Free, _ => TugStatus.Standby };
        if (mode is TugMode.Push or TugMode.Pull) LastWorkingMode = mode;
        // 初始已在出力中:出力自穩態開始(情境「拖船已推頂中」)
        if (Status == TugStatus.Working) ForceN = BollardPullN * PowerOrder;
    }

    /// <summary>速度效能 e(V) = 1(V ≤ V_full);否則 1 / (1 + ((V − V_full)/(V_half − V_full))²)。</summary>
    public double SpeedEfficiency(TugMode mode, double speedMps)
    {
        var (full, half) = mode == TugMode.Push ? (PushFullMps, PushHalfMps) : (PullFullMps, PullHalfMps);
        if (speedMps <= full) return 1.0;
        var z = (speedMps - full) / Math.Max(1e-6, half - full);
        return 1.0 / (1.0 + z * z);
    }

    /// <summary>
    /// 指令;回傳 null = 接受,否則拒絕原因(tugUnavailable、tugModeUnavailable、tugLineParted)。<paramref name="directionDeg"/>、<paramref name="powerPct"/> 未給時不變。
    /// mode unavailable:拖船立即失去出力並離開(status unavailable),之後任何指令(含再下 unavailable)以 tugUnavailable 拒絕。
    /// </summary>
    public string? Command(TugMode mode, double? directionDeg, double? powerPct)
    {
        if (Status == TugStatus.Unavailable) return Engine.CommandRejectReasons.TugUnavailable;
        if (mode == TugMode.Unavailable)
        {
            Mode = TugMode.Unavailable;
            Status = TugStatus.Unavailable;
            ForceN = 0.0;
            TimerTicks = 0;
            LastWorkingMode = TugMode.Standby;
            return null;
        }
        if (mode == TugMode.Push && !AllowPush || mode == TugMode.Pull && !AllowPull) return "tugModeUnavailable";
        if (mode == TugMode.Pull && LineParted) return "tugLineParted";
        if (directionDeg is { } d) DirectionOrderRad = Units.WrapRadPi(Units.DegToRad(d));
        if (powerPct is { } p) PowerOrder = Units.Clamp(p / 100.0, 0.0, 1.0);
        // 換位依「就位工作側」判定,而非上一個命令模式:推 → standby → 拉 仍需 modeChange_s
        var side = LastWorkingMode;
        Mode = mode;
        switch (mode)
        {
            case TugMode.Free:
                // 斷纜者保持 parted(故障清除前不能拉;LineRestored 才轉 free)
                if (Status != TugStatus.Parted) Status = TugStatus.Free;
                ForceN = 0.0;
                TimerTicks = 0;
                LastWorkingMode = TugMode.Standby;
                break;
            case TugMode.Standby:
                if (Status is TugStatus.Free or TugStatus.Parted) { TimerTicks = ConnectTicks; Status = TimerTicks > 0 ? TugStatus.Connecting : TugStatus.Standby; }
                // 帶纜/換位中改待命:計時照走(結束時成為 standby),不能用 standby 縮短換位
                else if (Status is not (TugStatus.Connecting or TugStatus.Repositioning)) Status = TugStatus.Standby;
                break;
            default:
                if (Status is TugStatus.Free or TugStatus.Parted) { TimerTicks = ConnectTicks; Status = TimerTicks > 0 ? TugStatus.Connecting : TugStatus.Working; }
                else if (Status is TugStatus.Connecting or TugStatus.Repositioning) { }
                else if (side is TugMode.Push or TugMode.Pull && side != mode)
                {
                    TimerTicks = ModeChangeTicks;
                    Status = TimerTicks > 0 ? TugStatus.Repositioning : TugStatus.Working;
                }
                else Status = TugStatus.Working;
                LastWorkingMode = mode;
                break;
        }
        return null;
    }

    /// <summary>拖纜斷裂(故障注入);回傳斷裂前的出力(N)。拉中者失去出力並成為 parted;推頂中者繼續推(推頂不需拖纜)但之後不能拉。</summary>
    public double PartLine()
    {
        if (Status == TugStatus.Unavailable) return 0.0;
        var before = ForceN;
        LineParted = true;
        if (Mode is TugMode.Pull or TugMode.Standby)
        {
            ForceN = 0.0;
            Mode = TugMode.Free;
            Status = TugStatus.Parted;
            TimerTicks = 0;
            LastWorkingMode = TugMode.Standby;
        }
        return before;
    }

    /// <summary>故障清除:可重新帶纜(parted → free,需 connect_s)。</summary>
    public void LineRestored()
    {
        LineParted = false;
        if (Status == TugStatus.Parted) Status = TugStatus.Free;
    }

    /// <summary>步首更新(出力一階上升、方向迴轉、計時);<paramref name="speedMps"/> 為自船對水速度大小。</summary>
    public void PreStep(double dt, double speedMps)
    {
        if (TimerTicks > 0 && --TimerTicks == 0)
            Status = Mode is TugMode.Push or TugMode.Pull ? TugStatus.Working : TugStatus.Standby;
        Efficiency = Mode is TugMode.Push or TugMode.Pull ? SpeedEfficiency(Mode, speedMps) : 1.0;
        var target = Status == TugStatus.Working ? BollardPullN * PowerOrder * Efficiency : 0.0;
        if (Status is TugStatus.Free or TugStatus.Parted or TugStatus.Unavailable) ForceN = 0.0;
        else ForceN += (target - ForceN) * _alpha;
        if (Status is not (TugStatus.Free or TugStatus.Parted or TugStatus.Unavailable))
        {
            var diff = Units.WrapRadPi(DirectionOrderRad - DirectionRad);
            var step = TurnRateRadPerSec * dt;
            DirectionRad = Units.WrapRadPi(DirectionRad + (diff > step ? step : diff < -step ? -step : diff));
        }
    }

    /// <summary>作用於船的力(船體座標;方向相對船艏、順時針)。</summary>
    public BodyForce Force()
    {
        if (ForceN == 0.0) return BodyForce.Zero;
        return BodyForce.AtPoint(ForceN * Math.Cos(DirectionRad), ForceN * Math.Sin(DirectionRad), AttachX, AttachY);
    }

    public static string ModeName(TugMode m) => ContractJson.EnumName(m);
    public static string StatusName(TugStatus s) => ContractJson.EnumName(s);
}
