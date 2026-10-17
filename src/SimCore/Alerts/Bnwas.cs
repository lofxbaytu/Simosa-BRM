using System.Buffers.Binary;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.SimCore.Alerts;

/// <summary>BNWAS 的可變狀態(進快照與雜湊)。</summary>
public sealed class BnwasState
{
    public BnwasMode Mode { get; set; }
    /// <summary>0 = 休眠、1 = 駕駛台視覺指示、2 = 駕駛台聲響、3 = 後備值班官/船長</summary>
    public int Stage { get; set; }
    /// <summary>上次重設(或開始作動)的 tick</summary>
    public long LastResetTick { get; set; }
    /// <summary>進入目前階段的 tick</summary>
    public long StageStartTick { get; set; }
    /// <summary>上一步是否作動中(off→作動時以該 tick 起算休眠期)</summary>
    public bool WasActive { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("dormant_s")] public double DormantS { get; set; } = 720.0;
    [System.Text.Json.Serialization.JsonPropertyName("stage2_s")] public double Stage2S { get; set; } = 15.0;
    [System.Text.Json.Serialization.JsonPropertyName("stage3_s")] public double Stage3S { get; set; } = 90.0;
}

/// <summary>
/// 駕駛台航行當值警報系統(MSC.128(75);設計文件 docs/design/sensors-alerts.md 第 4 節):模式 off / auto(自動舵啟用時作動)/ on;
/// 作動中休眠期 <c>dormant_s</c>(3–12 分鐘)未重設 → 第一階段(駕駛台視覺指示)→ <c>stage2_s</c>(15 s)→ 第二階段(駕駛台聲響)
/// → <c>stage3_s</c>(90 s)→ 第三階段(後備值班官/船長處聲響)。重設:bnwasReset 指令或任何駕駛台操作(舵令/車令/側推/自動舵/操舵模式/警報確認或靜音)。
/// 事件:bnwasStage {stage, from, reason}、bnwasReset {fromStage, by}、bnwasModeChanged {from, to, dormant_s}。
/// </summary>
public sealed class Bnwas
{
    private readonly int _stepsPerSecond;
    private readonly double _dt;
    private readonly Action<string, string?, object> _raise;

    public BnwasState State { get; private set; } = new();

    public Bnwas(ScenarioBnwas config, double dt, int stepsPerSecond, Action<string, string?, object> raise)
    {
        _dt = dt;
        _stepsPerSecond = stepsPerSecond;
        _raise = raise;
        State = new BnwasState { Mode = config.Mode, DormantS = config.DormantS, Stage2S = config.Stage2S, Stage3S = config.Stage3S };
    }

    private long Ticks(double s) => (long)Math.Round(s * _stepsPerSecond);

    public bool IsActive(bool autopilotEnabled) => State.Mode == BnwasMode.On || (State.Mode == BnwasMode.Auto && autopilotEnabled);

    /// <summary>預設狀態(off、未作動):雜湊不追加 BNWAS 位元組。</summary>
    public bool IsDefault => State.Mode == BnwasMode.Off && State.Stage == 0 && !State.WasActive;

    /// <summary>每步更新(引擎在狀態推進後呼叫)。</summary>
    public void Update(long tick, bool autopilotEnabled)
    {
        var s = State;
        if (!IsActive(autopilotEnabled))
        {
            if (!s.WasActive) return;
            s.WasActive = false;
            if (s.Stage > 0) SetStage(0, tick, "inactive");
            return;
        }
        if (!s.WasActive)
        {
            s.WasActive = true;
            s.LastResetTick = tick;
            s.StageStartTick = tick;
            s.Stage = 0;
            return;
        }
        switch (s.Stage)
        {
            case 0 when tick - s.LastResetTick >= Ticks(s.DormantS): SetStage(1, tick, "dormantElapsed"); break;
            case 1 when tick - s.StageStartTick >= Ticks(s.Stage2S): SetStage(2, tick, "notReset"); break;
            case 2 when tick - s.StageStartTick >= Ticks(s.Stage3S): SetStage(3, tick, "notReset"); break;
        }
    }

    private void SetStage(int stage, long tick, string reason)
    {
        var from = State.Stage;
        State.Stage = stage;
        State.StageStartTick = tick;
        _raise(SimEventKinds.BnwasStage, $"BNWAS 第 {stage} 階段", new { stage, from, reason });
    }

    /// <summary>
    /// 重設:作動中時休眠期重新起算、階段歸零。<paramref name="explicitReset"/>(bnwasReset 指令)一律發 bnwasReset 事件;
    /// 駕駛台操作(<paramref name="by"/> = 指令名)只在階段 ≥ 1 時發事件(休眠中的一般操作只重設計時,不產生事件雜訊)。
    /// </summary>
    public void Reset(long tick, bool autopilotEnabled, string by, bool explicitReset)
    {
        var s = State;
        if (!IsActive(autopilotEnabled)) return;
        var from = s.Stage;
        s.LastResetTick = tick;
        s.WasActive = true;
        if (from > 0)
        {
            s.Stage = 0;
            s.StageStartTick = tick;
        }
        if (from > 0 || explicitReset) _raise(SimEventKinds.BnwasReset, from > 0 ? $"BNWAS 重設(第 {from} 階段)" : "BNWAS 重設", new { fromStage = from, by });
    }

    /// <summary>切換模式(bnwasMode 指令;保護的模式開關由教官站操作):階段歸零、休眠期於下一步重新起算。<paramref name="dormantS"/> 夾在 180–720 s。</summary>
    public void SetMode(BnwasMode mode, double? dormantS, double? stage3S, long tick)
    {
        var s = State;
        var from = s.Mode;
        var fromStage = s.Stage;
        s.Mode = mode;
        if (dormantS is { } d) s.DormantS = Units.Clamp(d, ScenarioBnwas.MinDormantS, ScenarioBnwas.MaxDormantS);
        if (stage3S is { } t3 && t3 > 0) s.Stage3S = t3;
        s.Stage = 0;
        s.WasActive = false;
        s.LastResetTick = tick;
        s.StageStartTick = tick;
        _raise(SimEventKinds.BnwasModeChanged, $"{ContractJson.EnumName(from)} → {ContractJson.EnumName(mode)}",
            new { from = ContractJson.EnumName(from), to = ContractJson.EnumName(mode), fromStage, dormant_s = s.DormantS, stage2_s = s.Stage2S, stage3_s = s.Stage3S });
    }

    /// <summary>距下一階段的秒數(作動中;第三階段或未作動時 null)。</summary>
    public double? RemainingS(long tick, bool autopilotEnabled)
    {
        var s = State;
        if (!IsActive(autopilotEnabled) || !s.WasActive) return IsActive(autopilotEnabled) ? s.DormantS : null;
        return s.Stage switch
        {
            0 => Math.Max(0.0, s.DormantS - (tick - s.LastResetTick) * _dt),
            1 => Math.Max(0.0, s.Stage2S - (tick - s.StageStartTick) * _dt),
            2 => Math.Max(0.0, s.Stage3S - (tick - s.StageStartTick) * _dt),
            _ => null,
        };
    }

    public BnwasStatus BuildStatus(long tick, bool autopilotEnabled) => new()
    {
        Mode = State.Mode, Active = IsActive(autopilotEnabled), Stage = State.Stage, RemainingS = RemainingS(tick, autopilotEnabled), DormantS = State.DormantS,
    };

    public const int HashLongs = 8;

    public int WriteHash(Span<byte> buf)
    {
        var s = State;
        long[] v =
        {
            (long)s.Mode, s.Stage, s.LastResetTick, s.StageStartTick, s.WasActive ? 1 : 0,
            BitConverter.DoubleToInt64Bits(s.DormantS), BitConverter.DoubleToInt64Bits(s.Stage2S), BitConverter.DoubleToInt64Bits(s.Stage3S),
        };
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(buf[(i * 8)..], v[i]);
        return v.Length * 8;
    }

    public BnwasState CreateSnapshot() => ContractJson.Deserialize<BnwasState>(ContractJson.Serialize(State))!;

    public void Restore(BnwasState? s, ScenarioBnwas config)
        => State = s is null
            ? new BnwasState { Mode = config.Mode, DormantS = config.DormantS, Stage2S = config.Stage2S, Stage3S = config.Stage3S }
            : ContractJson.Deserialize<BnwasState>(ContractJson.Serialize(s))!;
}
