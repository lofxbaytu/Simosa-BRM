using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.SimCore.Traffic;

namespace SimosaBRM.SimCore.Assessment;

/// <summary>
/// 操縱量測(規劃書第 8.1 節 F01–F03、E11;設計文件 docs/design/ship-familiarisation.md 第 2 節):自動辨識並量測標準操縱試驗,
/// 量測定義與 MSC.137(76) / ITTC 7.5-02-06-03 及 tests/SimCore.Tests/MmgTestSupport.cs(Python simosa_brm.manoeuvres 移植)相同,結果與
/// trial_targets.json 同船同裝載的目標值比較(<see cref="ManoeuvreTargets"/>)。
/// <list type="bullet">
/// <item>35° 迴旋(|舵令| ≥ 25°,自 ≥ 3 kn 的穩定直航):90° 前進距離/橫距、180° 戰術直徑、90/180/270/360° 時間與速度、360–450° 的定常迴旋直徑/速度/ROT(舵令維持到 450°;360° 後變更舵令則取至變更時)。</item>
/// <item>δ/ψ Z 形(5° ≤ |舵令| &lt; 25°,自穩定直航;執行航向 ψ = |δ|):執行航向到達時間、各次反舵(與 ψ 的差 reversalLate{n}_deg,超過 1° 時之後的超越角不可比)、第 1/2(/3)超越角。</item>
/// <item>開始序列:舵輪拖曳、鍵盤逐步與 NFU 的逐步舵令,以第一道離開正中的舵令為開始,舵令穩定 2 s 後依最終舵角確定迴旋或 Z 形;開始後 3 s 內同向加大即改判。</item>
/// <item>緊急停船(前進中改倒俥;停俥後 15 s 內倒俥時開始取停俥,order "STOP→EFAS"):倒車啟動、停船時間(u ≤ 0)、航跡距離、head reach、橫距、航向變化(單俥倒俥艉偏)。</item>
/// <item>慣性停船(前進中停俥):進入速度 ≥ 7 kn 減至 5 kn 的時間、距離、head/side reach,每分鐘速度與航向;低於 7 kn 為停俥漂移(mode drift,觀察 300 s 或減至 0.5 kn)。</item>
/// <item>艏側推迴轉(側推 ≥ 50%、舵在正中附近):30°/90° 時間、60 s 後最大 ROT;舵機擺動(|舵令| ≥ 30° 換到對舷 ≥ 30°):到 30°/目標舵角的時間(依泵)。</item>
/// <item>人員落水(id 以 MOB 開頭的目標船出現):首次舵令反應時間、迴旋段與方式(Williamson/Anderson/Scharnow)、回到對向航向的時刻與偏離原航跡、
/// 最接近落水者的距離/時間/速度。</item>
/// </list>
/// 取樣:評估步(每秒)讀引擎自船狀態(與 MMG 黃金測試相同的量:x、y、ψ、u、v、r、舵角、轉速),開始時刻與開始狀態取指令套用當下(學員指令的
/// <see cref="ObservedAction"/>、時間表 ownShipOverride;NFU 等 &lt; 1° 的舵令增量取每秒輪詢);標記點以相鄰樣本線性內插、Z 形超越角以三點拋物線修正極值。
/// 只讀引擎狀態、不改物理、不用亂數;狀態進快照,事件為 assessment 來源(不進雜湊鏈,與其他評估事件相同)。
/// </summary>
public sealed class ManoeuvreTracker
{
    public const double SteadyWindowS = 30.0;
    public const double SteadyRotDegPerMin = 6.0;
    public const double SteadyRudderDeg = 5.0;
    public const double SteadySpeedBandKn = 0.5;
    public const double TurningMinRudderDeg = 25.0;
    public const double ZigzagMinRudderDeg = 5.0;
    public const double SwingMinRudderDeg = 30.0;
    /// <summary>迴旋/Z 形的最低進入速度(kn):低於此值的滿舵屬低速操船,不視為標準迴旋試驗</summary>
    public const double MinTurningSpeedKn = 3.0;
    /// <summary>開始序列(舵輪拖曳、鍵盤逐步、NFU):最後一道同向加大的舵令後此秒數無變化即確定開始(ship-familiarisation.md 第 2.2 節)</summary>
    public const double StartSettleS = 2.0;
    /// <summary>開始序列最長秒數(NFU 以舵機速率推到 35° 約 15–20 s)</summary>
    public const double StartSequenceMaxS = 30.0;
    /// <summary>開始後(或開始序列最後一道舵令後)此秒數內同向加大舵令:改判量測(35° → 70°、10/10 → 迴旋),開始時刻與狀態不變</summary>
    public const double GrowWindowS = 3.0;
    /// <summary>Z 形同舵角容許(反舵到位、NFU 放開或舵輪抖動的微調不視為新舵令)</summary>
    public const double RudderBandDeg = 2.0;
    /// <summary>Z 形反舵時航向變化與執行航向 ψ 的差超過此值(晚或早):該次及之後的超越角不與試俥比較(MSC/Circ.1053:於 ψ 反舵)</summary>
    public const double ReversalToleranceDeg = 1.0;
    /// <summary>逐步反舵(舵輪拖曳/NFU)須於此秒數內到達對舷同舵角</summary>
    public const double ReversalSequenceMaxS = 15.0;
    /// <summary>停俥後此秒數內改倒俥:慣性停船改判為緊急停船(開始取停俥時刻)</summary>
    public const double StopToAsternWindowS = 15.0;
    /// <summary>進入速度低於此值的停俥為「停俥漂移」量測(不以速度減半結束)</summary>
    public const double DriftEntrySpeedKn = 7.0;
    /// <summary>停俥漂移的觀察時間(s)與結束速度(kn)</summary>
    public const double DriftObserveS = 300.0;
    public const double DriftEndSpeedKn = 0.5;
    private const int HistoryLength = 45;

    private readonly SimulationEngine _engine;
    private readonly ShipParticulars _ship;
    private readonly string _loading;
    private readonly double _dt;
    private readonly double _lpp;
    private readonly double _loa;
    private readonly double _maxDraft;
    private readonly ManoeuvreTargets? _targets;
    private readonly Action<string, string?, string?, string?, object?> _raise;

    public bool Enabled { get; }
    public ManoeuvreState State { get; private set; } = new();
    /// <summary>比較用的目標值(未啟用或找不到檔案時 null;紀錄器寫入標頭 manoeuvreTargets)</summary>
    public ManoeuvreTargets? Targets => _targets;

    /// <param name="raise">(kind, itemId, targetId, message, data) → 評估事件</param>
    /// <param name="targets">試俥/海報目標值(null = 讀 data/ships/&lt;ID&gt;/trial_targets.json;重播時為紀錄標頭的目標值)</param>
    public ManoeuvreTracker(SimulationEngine engine, ShipParticulars ship, LoadingCondition loading, double dt, bool enabled,
        Action<string, string?, string?, string?, object?> raise, ManoeuvreTargets? targets = null)
    {
        _engine = engine;
        _ship = ship;
        _loading = ContractJson.EnumName(loading);
        _dt = dt;
        _lpp = ship.Hull.LengthBetweenPerpendiculars_m > 0 ? ship.Hull.LengthBetweenPerpendiculars_m : ship.Hull.LengthOverall_m;
        _loa = ship.Hull.LengthOverall_m > 0 ? ship.Hull.LengthOverall_m : _lpp;
        _maxDraft = ship.GetLoading(loading).MaxDraft_m;
        _raise = raise;
        Enabled = enabled;
        _targets = enabled ? targets ?? ManoeuvreTargets.For(ship.Id) : null;
        // 引擎在 Initialize 中先設定致動器初始值再建立評估模組:此時的舵令/轉速令/側推即為情境初始值(指令觀察發生在指令套用之後,不能延後讀取)
        if (enabled) InitLast();
    }

    /// <summary>
    /// 情境是否使用操縱量測(未使用者不建立量測、不發事件、summary 無 manoeuvres 段,舊情境紀錄與事件序列不變):
    /// 容許帶引用 manoeuvres.* 指標、必要事件/硬性失敗/時間表條件引用 manoeuvre* 事件,或有 id 以 MOB 開頭的目標船(人員落水)。
    /// </summary>
    public static bool IsReferenced(Scenario.Scenario sc)
    {
        static bool M(string? kind) => kind is not null && kind.StartsWith("manoeuvre", StringComparison.Ordinal);
        var a = sc.Assessment;
        if (a.Bands.Any(b => b.Metric.StartsWith("manoeuvres.", StringComparison.Ordinal))) return true;
        if (a.HardFail.Any(h => M(h.Kind))) return true;
        if (a.Required.Any(r => M(r.After.Event) || r.Expect.Leaves().Any(l => M(l.Event?.Kind)))) return true;
        if (sc.Timeline.Any(t => t.When is { } w && w.Leaves().Any(l => M(l.Kind) || M(l.Since?.Event)))) return true;
        return sc.Targets.Any(t => IsMobTarget(t.Id));
    }

    public static bool IsMobTarget(string? id) => id is not null && id.StartsWith("MOB", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ 觀察

    public void OnAction(ObservedAction a)
    {
        if (!Enabled) return;
        var explicitOrder = a.Kind is ActionKinds.RudderOrder or ActionKinds.TelegraphChange or ActionKinds.RpmOrder or ActionKinds.ThrusterOrder;
        CheckOrders(a.Tick, explicitOrder);
    }

    public void OnEvent(SimEvent e)
    {
        if (!Enabled) return;
        if (e.Kind == SimEventKinds.OwnShipOverride) CheckOrders(e.Tick, explicitOrder: true);
        else if (e.Kind == TrafficEventKinds.Activated && IsMobTarget(e.TargetId) && State.Mob is null && !State.Records.Any(r => r.Kind == ManoeuvreKinds.ManOverboard && r.TargetId == e.TargetId))
            StartMob(e.TargetId!, e.Tick);
    }

    /// <summary>評估步(每秒):穩定直航樣本、輪詢舵令/車令(自動舵、NFU)、推進各進行中的量測(開始序列未確定時暫存樣本)。</summary>
    public void Update(Situation sit)
    {
        if (!Enabled) return;
        var st = State;
        if (!st.Initialized) InitLast();
        if (st.Active is { } act)
        {
            var sample = Capture(sit);
            if (act.Pending)
            {
                act.Buffer.Add(sample);
                // NFU 推桿中(舵以舵機速率移動、舵令不變)不以「舵令穩定」確定,待放開
                var leverPushed = _engine.SteeringMode == SteeringMode.Nfu && _engine.NfuDirection != 0;
                if ((!leverPushed && (sit.Tick - act.SeqLastTick) * _dt >= StartSettleS - 1e-9) || (sit.Tick - act.StartTick) * _dt >= StartSequenceMaxS - 1e-9)
                    FinalizeSequence(act, sit.Tick);
            }
            else Sample(act, sample);
        }
        if (st.Swing is { } sw) SampleSwing(sw, sit);
        if (st.Mob is { } mob) SampleMob(mob, sit);
        CheckOrders(sit.Tick, explicitOrder: false);
        st.History.Add(new SteadySample { Tick = sit.Tick, RotDegPerMin = sit.RotDegPerMin, RudderDeg = sit.RudderDeg, StwKn = sit.StwKn, Autopilot = sit.AutopilotEnabled });
        if (st.History.Count > HistoryLength) st.History.RemoveRange(0, st.History.Count - HistoryLength);
    }

    private ManoeuvreSample Capture(Situation sit)
    {
        var m = _engine.Motion;
        return new ManoeuvreSample
        {
            Tick = sit.Tick, T = sit.Time, X = m.X, Y = m.Y, Psi = m.Psi, U = m.U, V = m.V, R = m.R,
            Rpm = sit.Rpm, RudderOrderDeg = sit.RudderOrderDeg, RotDegPerMin = sit.RotDegPerMin,
        };
    }

    private void InitLast()
    {
        var act = _engine.Actuators;
        State.LastRudderOrder = act.RudderOrderDeg;
        State.LastRpmOrder = act.RpmOrder;
        State.LastThruster = act.ThrusterOrder;
        State.LastTelegraph = _engine.Telegraph.ToString();
        State.Initialized = true;
    }

    // ------------------------------------------------------------------ 指令變化

    /// <summary>
    /// 指令變化(學員指令觀察、時間表 ownShipOverride、每秒輪詢):舵令 ≥ 1°、轉速令或側推 ≥ 0.05 的變化。
    /// 輪詢到的舵令變化(NFU 推桿、&lt; 1° 的舵輪增量累計)在自動舵未啟用時也可開始量測(開始序列,ship-familiarisation.md 第 2.2 節)。
    /// </summary>
    private void CheckOrders(long tick, bool explicitOrder)
    {
        var st = State;
        if (!st.Initialized) InitLast();
        var act = _engine.Actuators;
        var rud = act.RudderOrderDeg;
        var rpm = act.RpmOrder;
        var thr = act.ThrusterOrder;
        var prevRud = st.LastRudderOrder;
        var prevRpm = st.LastRpmOrder;
        var prevThr = st.LastThruster;
        var rudChanged = Math.Abs(rud - prevRud) >= 1.0;
        var propChanged = rpm != prevRpm;
        var thrChanged = Math.Abs(thr - prevThr) >= 0.05;

        // 開始序列進行中:同向加大的舵令(含 < 1° 的增量)延續序列;改車令/側推即放棄(未發事件);減舵或反向先確定開始,再依一般規則處理
        if (st.Active is { Pending: true } seq)
        {
            if (propChanged || thrChanged) st.Active = null;
            else if (rud != prevRud && Math.Sign(rud) == Math.Sign(seq.SeqRudder) && Math.Abs(rud) > Math.Abs(seq.SeqRudder)
                     && (tick - seq.StartTick) * _dt <= StartSequenceMaxS)
            {
                seq.SeqRudder = rud;
                seq.SeqLastTick = tick;
                seq.SeqOrders++;
                if (!explicitOrder) seq.SeqPolled = true;
                if (st.Mob is { } m0 && rudChanged && explicitOrder) MobRudder(m0, rud, tick);
                if (rudChanged) st.LastRudderChangeTick = tick;
                st.LastRudderOrder = rud;
                st.LastTelegraph = _engine.Telegraph.ToString();
                return;
            }
            else if (rud != prevRud && _engine.SteeringMode == SteeringMode.Nfu && Math.Sign(rud) == Math.Sign(seq.SeqRudder))
            {
                // 操舵模式 NFU 推桿:推桿時舵令為滿舵(舵以舵機速率移動),放開時舵令 = 當時舵角:以放開時的舵角為最終舵角,延續序列
                seq.SeqRudder = rud;
                seq.SeqLastTick = tick;
                seq.SeqOrders++;
                seq.SeqNfuLever = true;
                if (rudChanged) st.LastRudderChangeTick = tick;
                st.LastRudderOrder = rud;
                st.LastTelegraph = _engine.Telegraph.ToString();
                return;
            }
            else if (rudChanged) FinalizeSequence(seq, tick);
        }

        if (!rudChanged && !propChanged && !thrChanged)
        {
            // 自動舵/NFU 的小幅舵令變化:只更新最近舵令(不視為一道舵令;累計 ≥ 1° 時由下一次檢查辨識)
            if (rud != prevRud) st.LastRudderOrder = rud;
            st.LastTelegraph = _engine.Telegraph.ToString();
            return;
        }

        var steady = Steadiness(tick);
        if (st.Mob is { } mob && rudChanged && explicitOrder) MobRudder(mob, rud, tick);   // 只計明確舵令(自動舵的保向舵角不算迴旋段)
        if (st.Active is { Pending: false } a) HandleActive(a, tick, rud, prevRud, rpm, thr, rudChanged, propChanged, thrChanged);
        if (st.Swing is null && explicitOrder && rudChanged && Math.Abs(prevRud) >= SwingMinRudderDeg && Math.Abs(rud) >= SwingMinRudderDeg
            && Math.Sign(rud) != Math.Sign(prevRud) && Math.Abs(act.RudderDeg - prevRud) <= 2.0)
            StartSwing(tick, prevRud, rud);
        // 輪詢到的舵令變化(自動舵未啟用):NFU 或 < 1° 的舵輪增量累計,只可開始舵令類量測(開始序列)
        var polledRudder = !explicitOrder && rudChanged && !propChanged && !thrChanged && !_engine.Autopilot.Enabled;
        if (st.Active is null && (explicitOrder || polledRudder)) TryStart(tick, prevRud, rud, prevRpm, rpm, prevThr, thr, rudChanged, propChanged, thrChanged, steady, explicitOrder);

        if (rudChanged) st.LastRudderChangeTick = tick;
        if (propChanged) st.LastPropulsionChangeTick = tick;
        st.LastRudderOrder = rud;
        st.LastRpmOrder = rpm;
        st.LastThruster = thr;
        st.LastTelegraph = _engine.Telegraph.ToString();
    }

    private void TryStart(long tick, double prevRud, double rud, double prevRpm, double rpm, double prevThr, double thr,
        bool rudChanged, bool propChanged, bool thrChanged, (bool Steady, double For, bool FromInitial) steady, bool explicitOrder)
    {
        var m = _engine.Motion;
        if (explicitOrder && propChanged && prevRpm >= 0 && m.U > 0.5)
        {
            if (rpm < 0) Start(ManoeuvreKinds.CrashStop, "crashStop", null, tick, steady, p =>
            {
                p.Record.Order = OrderLabel(rpm);
                if (prevRpm == 0) AddNote(p.Record, "停俥滑行中改倒俥(試俥為由全速進直接倒俥)");
            });
            else if (rpm == 0 && prevRpm > 0) Start(ManoeuvreKinds.InertiaStop, "inertiaStop", null, tick, steady, p =>
            {
                p.Record.Order = OrderLabel(rpm);
                var v0 = Units.MpsToKn(Math.Sqrt(m.U * m.U + m.V * m.V));
                if (v0 >= DriftEntrySpeedKn) p.EndSpeedKn = 5.0;
                else
                {
                    // 低速停俥:觀察漂移與艏偏(bow blow-off),不以速度減半結束
                    p.Drift = true;
                    p.Record.Mode = "drift";
                    p.EndSpeedKn = DriftEndSpeedKn;
                    AddNote(p.Record, $"停俥漂移(進入 {v0.ToString("F1", CultureInfo.InvariantCulture)} kn < {DriftEntrySpeedKn:F0} kn):觀察 {DriftObserveS:F0} s 或減至 {DriftEndSpeedKn:F1} kn,記錄每分鐘航向變化");
                }
            });
            return;
        }
        if (propChanged) return;
        // 舵令自正中附近(≤ 5°)離開、穩定直航、≥ 3 kn:一次到 ≥ 25° 的明確舵令立即開始迴旋;其餘(逐步下舵、Z 形舵角、NFU)開始序列,
        // 待舵令穩定(StartSettleS)後依最終舵角確定迴旋或 Z 形,開始時刻與狀態取第一道舵令
        if (rudChanged && !thrChanged && steady.Steady && Math.Abs(prevRud) <= SteadyRudderDeg && Math.Abs(rud) > Math.Abs(prevRud) && Units.MpsToKn(m.U) >= MinTurningSpeedKn)
        {
            // 操舵模式 NFU 推桿的滿舵令不是目標舵角(放開時才確定):一律走開始序列
            if (explicitOrder && Math.Abs(rud) >= TurningMinRudderDeg && _engine.SteeringMode != SteeringMode.Nfu)
            {
                var side = rud > 0 ? "starboard" : "port";
                Start(ManoeuvreKinds.Turning, $"turning{Math.Round(Math.Abs(rud)):F0}.{side}", side, tick, steady, p =>
                {
                    p.Sign = Math.Sign(rud);
                    p.Record.RudderDeg = rud;
                    p.GrowUntilTick = tick + (long)Math.Round(GrowWindowS / _dt);
                });
            }
            else
            {
                var p = NewProgress("", "", null, tick, steady);
                p.Pending = true;
                p.SeqPrevRudder = prevRud;
                p.SeqRudder = rud;
                p.SeqLastTick = tick;
                p.SeqOrders = 1;
                p.SeqPolled = !explicitOrder;
                State.Active = p;
            }
            return;
        }
        if (explicitOrder && thrChanged && Math.Abs(prevThr) < 0.05 && Math.Abs(thr) >= 0.5 && Math.Abs(rud) <= SteadyRudderDeg && Math.Abs(Units.RadPerSecToDegPerMin(m.R)) <= 10.0)
        {
            var side = thr > 0 ? "starboard" : "port";
            Start(ManoeuvreKinds.ThrusterTurn, $"thrusterTurn.{side}", side, tick, steady, p => p.Sign = Math.Sign(thr));
        }
    }

    /// <summary>確定開始序列:最終舵角 ≥ 25° 為迴旋、5°–25°(原舵角 ≤ 2.5°)為 Z 形,否則放棄(未發事件);確定後依序補入暫存樣本。</summary>
    private void FinalizeSequence(ManoeuvreProgress p, long tick)
    {
        var r = p.Record;
        var rud = p.SeqRudder;
        var mag = Math.Abs(rud);
        var side = rud > 0 ? "starboard" : "port";
        p.Pending = false;
        if (mag >= TurningMinRudderDeg) SetTurning(p, rud, side);
        else if (mag >= ZigzagMinRudderDeg && Math.Abs(p.SeqPrevRudder) <= 2.5) SetZigzag(p, rud, side);
        else
        {
            p.Buffer.Clear();
            if (State.Active == p) State.Active = null;
            return;
        }
        p.GrowUntilTick = p.SeqLastTick + (long)Math.Round(GrowWindowS / _dt);
        if (p.SeqOrders > 1 || p.SeqPolled)
            AddNote(r, $"舵令於 {((p.SeqLastTick - p.StartTick) * _dt).ToString("F1", CultureInfo.InvariantCulture)} s 內逐步下到 {rud.ToString("F1", CultureInfo.InvariantCulture)}°"
                + $"(共 {p.SeqOrders} 次變化;舵輪拖曳/鍵盤/NFU),開始時刻與狀態取第一道離開正中的舵令"
                + (p.SeqPolled ? ";小於 1° 的舵令增量(NFU)以每秒輪詢辨識,開始時刻誤差 ≤ 1 s" : "")
                + (p.SeqNfuLever ? ";操舵模式 NFU 推桿,以放開時的舵角為量測舵角" : ""));
        RaiseStarted(p);
        var buffer = p.Buffer;
        p.Buffer = new List<ManoeuvreSample>();
        foreach (var s in buffer)
        {
            if (State.Active != p) break;
            Sample(p, s);
        }
    }

    private static void SetTurning(ManoeuvreProgress p, double rud, string side)
    {
        var r = p.Record;
        r.Kind = ManoeuvreKinds.Turning;
        r.Id = $"turning{Math.Round(Math.Abs(rud)):F0}.{side}";
        r.Side = side;
        r.RudderDeg = rud;
        r.CheckHeadingDeg = null;
        p.Sign = Math.Sign(rud);
        p.RudderOrder0 = rud;
    }

    private static void SetZigzag(ManoeuvreProgress p, double rud, string side)
    {
        var r = p.Record;
        var psi = Math.Round(Math.Abs(rud));
        r.Kind = ManoeuvreKinds.Zigzag;
        r.Id = $"zigzag{psi:F0}.{side}First";
        r.Side = side;
        r.RudderDeg = rud;
        r.CheckHeadingDeg = psi;
        p.Sign = Math.Sign(rud);
        p.Side = Math.Sign(rud);
        p.RudderOrder0 = rud;
        p.MaxOvershoots = psi <= 15 ? 3 : 2;
    }

    /// <summary>開始後短時間內同向加大舵令(35° → 70°、10° → 35°):改判量測 id,開始時刻與狀態不變,重發 manoeuvreStarted(supersedes = 原 id)。</summary>
    private void Regrade(ManoeuvreProgress p, long tick, double rud)
    {
        var r = p.Record;
        var oldId = r.Id;
        var side = rud > 0 ? "starboard" : "port";
        if (Math.Abs(rud) >= TurningMinRudderDeg)
        {
            // Z 形的前一樣本航向變化為未帶號值;迴旋為朝舵方向的累計值
            if (r.Kind == ManoeuvreKinds.Zigzag) p.PrevChange = p.Sign * p.PrevChange;
            SetTurning(p, rud, side);
        }
        else SetZigzag(p, rud, side);
        p.GrowUntilTick = tick + (long)Math.Round(GrowWindowS / _dt);
        AddNote(r, $"開始後 {(tick * _dt - r.StartS).ToString("F1", CultureInfo.InvariantCulture)} s 內同向加大舵令至 {rud.ToString("F1", CultureInfo.InvariantCulture)}°:由 {oldId} 改判為 {r.Id}(開始時刻取第一道舵令)");
        RaiseStarted(p, oldId);
    }

    /// <summary>車令名稱:車鐘轉速與轉速令相同時為車鐘名(EFAS、STOP…),以 rpm 指令下令時為「n rpm」(0 為 STOP)。</summary>
    private string OrderLabel(double rpm)
    {
        var tg = _engine.Telegraph;
        double tgRpm;
        try { tgRpm = _ship.TelegraphRpm(tg); } catch (KeyNotFoundException) { tgRpm = double.NaN; }
        if (tgRpm == rpm) return tg.ToString();
        return rpm == 0 ? "STOP" : rpm.ToString("F0", CultureInfo.InvariantCulture) + " rpm";
    }

    private void HandleActive(ManoeuvreProgress p, long tick, double rud, double prevRud, double rpm, double thr, bool rudChanged, bool propChanged, bool thrChanged)
    {
        var r = p.Record;
        var t = tick * _dt;
        switch (r.Kind)
        {
            case ManoeuvreKinds.Turning:
                if (rudChanged && !propChanged && !thrChanged && tick <= p.GrowUntilTick && Math.Sign(rud) == p.Sign && Math.Abs(rud) > Math.Abs(p.RudderOrder0) + 1.0)
                {
                    Regrade(p, tick, rud);
                    break;
                }
                if (Math.Abs(rud - p.RudderOrder0) > 1.0 || propChanged || thrChanged)
                {
                    var why = propChanged ? "車令變更" : thrChanged ? "側推指令" : "舵令變更";
                    if (p.ChangeDeg >= 360.0)
                    {
                        // 已過 360°:主要量測完整,定常值取 360° 至指令變更間的樣本
                        SteadyTurn(p);
                        AddNote(r, $"定常迴旋值取 360° 至{why}({p.ChangeDeg:F0}°)間的樣本");
                        Finish(p, t, complete: true);
                    }
                    else EndEarly(p, t, p.ChangeDeg >= 90.0, why);
                }
                break;
            case ManoeuvreKinds.Zigzag:
                if (propChanged || thrChanged) { EndZigzagEarly(p, t, propChanged ? "車令變更" : "側推指令"); break; }
                if (rudChanged) ZigzagRudder(p, tick, rud, prevRud);
                break;
            case ManoeuvreKinds.CrashStop:
                if (propChanged && rpm >= 0) Abort(p, t, "停船前車令變更");
                else if (rudChanged && Math.Abs(rud) > SteadyRudderDeg) p.RudderUsed = true;
                break;
            case ManoeuvreKinds.InertiaStop:
                if (propChanged && rpm < 0 && t - r.StartS <= StopToAsternWindowS + 1e-9) ConvertToCrashStop(p, tick, rpm);
                else if (propChanged) EndEarly(p, t, t - r.StartS >= 60.0, "減至結束速度前車令變更");
                else if (rudChanged && Math.Abs(rud) > SteadyRudderDeg) p.RudderUsed = true;
                break;
            case ManoeuvreKinds.ThrusterTurn:
                if (thrChanged || propChanged || (rudChanged && Math.Abs(rud) > SteadyRudderDeg))
                    EndEarly(p, t, t - r.StartS >= 60.0, thrChanged ? "側推指令變更" : propChanged ? "車令變更" : "舵令變更");
                break;
        }
    }

    /// <summary>
    /// Z 形的舵令變化:同舵角 ±2° 內的微調不理會;首次反舵前、開始後 3 s 內同向加大則改判;離開本舷同舵角朝對舷即為反舵(記錄於第一道舵令),
    /// 舵輪拖曳/NFU 的逐步反舵在到達對舷同舵角前持續朝新舷的舵令不中止;其他舵令提前結束。
    /// </summary>
    private void ZigzagRudder(ManoeuvreProgress p, long tick, double rud, double prevRud)
    {
        var t = tick * _dt;
        var mag = Math.Abs(p.RudderOrder0);
        var onSide = p.Side * rud;   // 朝目前舵方向(反舵後為新舷)的舵令
        if (p.ReversalInProgress)
        {
            if (Math.Abs(onSide - mag) <= RudderBandDeg) { p.ReversalInProgress = false; return; }
            if (onSide < mag && p.Side * (rud - prevRud) > 0) return;
            EndZigzagEarly(p, t, "反舵未到對舷同舵角即變更舵令");
            return;
        }
        if (Math.Abs(onSide - mag) <= RudderBandDeg) return;
        if (onSide > mag && p.Phase == 0 && p.Reversals == 0 && p.ExecuteCrossings == 0 && tick <= p.GrowUntilTick)
        {
            Regrade(p, tick, rud);
            return;
        }
        if (onSide < mag - RudderBandDeg && p.Phase == 0 && -onSide <= mag + RudderBandDeg)
        {
            StartReversal(p, tick, rud);
            return;
        }
        EndZigzagEarly(p, t, "舵令不是對舷同舵角的反舵");
    }

    /// <summary>反舵:記錄執行航向到達、反舵當下相對執行航向的差(reversalLate{n}_deg,+ 晚 / − 早)與標記,轉入追蹤超越角。</summary>
    private void StartReversal(ManoeuvreProgress p, long tick, double rud)
    {
        var r = p.Record;
        var t = tick * _dt;
        var m = _engine.Motion;
        var mag = Math.Abs(p.RudderOrder0);
        var psiC = r.CheckHeadingDeg ?? 0;
        var h = Units.RadToDeg(Units.WrapRadPi(m.Psi - p.Psi0));
        // 反舵常在兩個樣本之間(到達執行航向的那一步):以上一樣本與反舵當下的航向內插執行航向到達時刻
        RecordExecute(p, p.PrevT, p.PrevChange, t, h);
        p.Reversals++;
        var n = p.Reversals;
        var excess = p.Side * h - psiC;
        Set(r, $"reversalLate{n}_deg", excess);
        var ex = Math.Abs(excess).ToString("F1", CultureInfo.InvariantCulture);
        var hc = Math.Abs(h).ToString("F1", CultureInfo.InvariantCulture);
        if (excess > ReversalToleranceDeg) AddNote(r, $"第 {n} 次反舵晚於執行航向 {ex}°(航向變化 {hc}°);第 {n} 個及之後的超越角不與試俥比較");
        else if (excess < -ReversalToleranceDeg) AddNote(r, $"第 {n} 次反舵早於執行航向 {ex}°(航向變化 {hc}°);第 {n} 個及之後的超越角不與試俥比較");
        r.Marks.Add(new ManoeuvreMark { Type = "reversal", TS = t - r.StartS, HeadingChangeDeg = p.Sign * h, Value = Round(excess), Index = n });
        p.Side = -p.Side;
        p.Phase = 1;
        p.Extreme = -p.Side * h;
        p.ExtremeT = t;
        p.H1 = null;
        p.H2 = null;
        p.LastReversalTick = tick;
        p.ReversalInProgress = Math.Abs(p.Side * rud - mag) > RudderBandDeg;
    }

    /// <summary>先停俥再於 15 s 內倒俥:慣性停船改判為緊急停船,開始時刻與狀態取停俥(order = "STOP→EFAS",stopToAstern_s),重發 manoeuvreStarted。</summary>
    private void ConvertToCrashStop(ManoeuvreProgress p, long tick, double rpm)
    {
        var r = p.Record;
        var delay = tick * _dt - r.StartS;
        var oldId = r.Id;
        r.Kind = ManoeuvreKinds.CrashStop;
        r.Id = "crashStop";
        r.Order = $"{r.Order}→{OrderLabel(rpm)}";
        r.Values.Clear();
        r.Marks.Clear();
        r.Notes.RemoveAll(x => x.StartsWith("停俥漂移", StringComparison.Ordinal));
        Set(r, "stopToAstern_s", delay);
        p.Drift = false;
        r.Mode = null;
        p.EndSpeedKn = 0;
        p.NextMinute = 1;
        p.AsternSeen = false;
        p.MaxRot = 0;
        AddNote(r, $"先停俥再倒俥:停俥後 {delay.ToString("F1", CultureInfo.InvariantCulture)} s 改 {OrderLabel(rpm)},開始時刻取停俥(試俥為由全速進直接倒俥)");
        RaiseStarted(p, oldId);
    }

    // ------------------------------------------------------------------ 穩定直航

    /// <summary>
    /// 開始前的穩定直航(MSC.137(76) 定速直航):最近 <see cref="SteadyWindowS"/> 秒的每秒樣本 |ROT| ≤ 6°/min、|舵角| ≤ 5°、對水速度與目前差 ≤ 0.5 kn,
    /// 且車令在視窗內未變。練習開始後尚無任何車/舵令且樣本皆穩定時,以情境初始狀態(平衡轉速建立的直航穩態)視為穩定。
    /// </summary>
    public (bool Steady, double For, bool FromInitial) Steadiness(long tick)
    {
        var st = State;
        var u = Units.MpsToKn(_engine.Motion.U);
        bool Ok(SteadySample s) => Math.Abs(s.RotDegPerMin) <= SteadyRotDegPerMin && Math.Abs(s.RudderDeg) <= SteadyRudderDeg && Math.Abs(s.StwKn - u) <= SteadySpeedBandKn;
        var samples = st.History.Where(s => s.Tick <= tick).ToList();
        var noOrders = st.LastPropulsionChangeTick < 0 && st.LastRudderChangeTick < 0;
        if (noOrders && samples.All(Ok)) return (true, Math.Min(tick * _dt, SteadyWindowS), true);
        long firstOk = tick;
        for (var i = samples.Count - 1; i >= 0 && Ok(samples[i]); i--) firstOk = samples[i].Tick;
        var steadyFor = (tick - firstOk) * _dt;
        if (st.LastPropulsionChangeTick >= 0) steadyFor = Math.Min(steadyFor, (tick - st.LastPropulsionChangeTick) * _dt);
        steadyFor = Math.Min(steadyFor, SteadyWindowS);
        return (steadyFor >= SteadyWindowS - 1e-9, steadyFor, false);
    }

    // ------------------------------------------------------------------ 開始

    private void Start(string kind, string id, string? side, long tick, (bool Steady, double For, bool FromInitial) steady, Action<ManoeuvreProgress> init)
    {
        var p = NewProgress(kind, id, side, tick, steady);
        init(p);
        State.Active = p;
        RaiseStarted(p);
    }

    private ManoeuvreProgress NewProgress(string kind, string id, string? side, long tick, (bool Steady, double For, bool FromInitial) steady)
    {
        var m = _engine.Motion;
        var act = _engine.Actuators;
        var env = _engine.Environment;
        var t = tick * _dt;
        var depth = env.DepthAt(m.X, m.Y); // 有效水深(基底 + 潮高),與 state.waterDepth、UKC 同源;h/T 可比較性判斷據此
        var rec = new ManoeuvreRecord
        {
            Id = id, Kind = kind, Side = side, StartS = t, ShipId = _ship.Id, Loading = _loading, Dynamics = _engine.Dynamics.ModelName,
            Approach = new ManoeuvreApproach
            {
                SpeedKn = Units.MpsToKn(m.U), SogKn = Units.MpsToKn(Math.Sqrt(m.U * m.U + m.V * m.V)), HeadingDeg = Units.NormalizeHeadingDeg(Units.RadToDeg(m.Psi)),
                Rpm = act.Rpm, Telegraph = State.LastTelegraph ?? _engine.Telegraph.ToString(), Steady = steady.Steady, SteadyForS = steady.For, FromInitial = steady.FromInitial,
                Autopilot = State.History.Count > 0 ? State.History[^1].Autopilot : _engine.Autopilot.Enabled,
            },
            Environment = new ManoeuvreEnvironment
            {
                WindSpeedKn = Units.MpsToKn(env.WindTrueSpeedMps), WindDirDeg = Units.NormalizeHeadingDeg(Units.RadToDeg(env.WindTrueDirFromRad)),
                CurrentDriftKn = Units.MpsToKn(env.CurrentDriftMps), CurrentSetDeg = Units.NormalizeHeadingDeg(Units.RadToDeg(env.CurrentSetRad)),
                WaterDepthM = depth, DepthRatio = _maxDraft > 0 ? depth / _maxDraft : 0,
            },
        };
        return new ManoeuvreProgress
        {
            Record = rec, StartTick = tick, X0 = m.X, Y0 = m.Y, Psi0 = m.Psi, LastPsi = m.Psi,
            RudderOrder0 = act.RudderOrderDeg, RpmOrder0 = act.RpmOrder, Thruster0 = act.ThrusterOrder,
            PrevT = t, PrevSpeedKn = Units.MpsToKn(Math.Sqrt(m.U * m.U + m.V * m.V)), PrevU = Units.MpsToKn(m.U), PrevRpm = act.Rpm, PrevRudder = act.RudderDeg,
            PrevX = m.X, PrevY = m.Y,
        };
    }

    /// <param name="supersedes">改判前的量測 id(開始序列延續、STOP → 倒俥);data.start_s 為量測開始時刻(可早於事件時刻)</param>
    private void RaiseStarted(ManoeuvreProgress p, string? supersedes = null)
    {
        var r = p.Record;
        var msg = $"{Label(r)} 量測開始" + (r.Approach.Steady || r.Kind is ManoeuvreKinds.ManOverboard or ManoeuvreKinds.RudderSwing ? "" : "(開始前未穩定直航)")
            + (supersedes is not null ? $"(由 {supersedes} 改判)" : "");
        _raise(ManoeuvreEventKinds.Started, r.Id, r.TargetId, msg,
            new { id = r.Id, kind = r.Kind, side = r.Side, start_s = r.StartS, rudder_deg = r.RudderDeg, order = r.Order, supersedes, approach = r.Approach, environment = r.Environment });
    }

    private void StartSwing(long tick, double from, double to)
    {
        var p = NewProgress(ManoeuvreKinds.RudderSwing, to > 0 ? "rudderSwing.toStarboard" : "rudderSwing.toPort", to > 0 ? "starboard" : "port", tick, (true, 0, false));
        p.Side = Math.Sign(to);
        p.Record.RudderDeg = to;
        p.Record.Pump = _engine.SteeringPump;
        p.Record.Values["from_deg"] = Round(from);
        p.Record.Values["to_deg"] = Round(to);
        if (_engine.SteeringMode != SteeringMode.Fu && _engine.SteeringMode != SteeringMode.Autopilot)
            AddNote(p.Record, $"操舵模式 {ContractJson.EnumName(_engine.SteeringMode)}");
        State.Swing = p;
        RaiseStarted(p);
    }

    private void StartMob(string targetId, long tick)
    {
        var target = _engine.Traffic.Find(targetId);
        var m = _engine.Motion;
        var p = NewProgress(ManoeuvreKinds.ManOverboard, "manOverboard", null, tick, (true, 0, false));
        p.Record.TargetId = targetId;
        if (target is not null)
        {
            var rel = Units.WrapRadPi(Math.Atan2(target.X - m.X, target.Y - m.Y) - m.Psi);
            p.Record.Side = rel < 0 ? "port" : "starboard";
            p.Record.Values["initialRange_m"] = Round(Math.Sqrt((target.X - m.X) * (target.X - m.X) + (target.Y - m.Y) * (target.Y - m.Y)));
        }
        p.Sign = 1.0;
        p.CurrentRudderSign = Math.Abs(_engine.Actuators.RudderOrderDeg) >= 10.0 ? Math.Sign(_engine.Actuators.RudderOrderDeg) : 0;
        State.Mob = p;
        RaiseStarted(p);
    }

    // ------------------------------------------------------------------ 取樣

    private void Sample(ManoeuvreProgress p, ManoeuvreSample m)
    {
        var r = p.Record;
        var t = m.T;
        var dpsi = Units.RadToDeg(Units.WrapRadPi(m.Psi - p.LastPsi));
        p.LastPsi = m.Psi;
        p.ChangeDeg += p.Sign * dpsi;
        var dx = m.X - p.X0;
        var dy = m.Y - p.Y0;
        var along = dx * Math.Sin(p.Psi0) + dy * Math.Cos(p.Psi0);
        var acrossStbd = dx * Math.Cos(p.Psi0) - dy * Math.Sin(p.Psi0);
        var chord = Math.Sqrt((m.X - p.PrevX) * (m.X - p.PrevX) + (m.Y - p.PrevY) * (m.Y - p.PrevY));
        var speedKn = Units.MpsToKn(Math.Sqrt(m.U * m.U + m.V * m.V));
        var uKn = Units.MpsToKn(m.U);
        var elapsed = t - r.StartS;
        double F(double a, double b, double target) => b != a ? Math.Clamp((target - a) / (b - a), 0.0, 1.0) : 1.0;
        double L(double a, double b, double f) => a + f * (b - a);

        switch (r.Kind)
        {
            case ManoeuvreKinds.Turning:
            {
                var across = p.Sign * acrossStbd;
                while (p.NextMark <= 360 && p.ChangeDeg >= p.NextMark)
                {
                    var f = F(p.PrevChange, p.ChangeDeg, p.NextMark);
                    var tm = L(p.PrevT, t, f) - r.StartS;
                    var adv = L(p.PrevAlong, along, f);
                    var tr = L(p.PrevAcross, across, f);
                    var v = L(p.PrevSpeedKn, speedKn, f);
                    var h = p.NextMark.ToString(CultureInfo.InvariantCulture);
                    r.Marks.Add(new ManoeuvreMark { Type = $"heading{h}", TS = Round(tm), HeadingChangeDeg = p.NextMark, SpeedKn = Round(v), AdvanceM = Round(adv), TransferM = Round(tr) });
                    if (p.NextMark == 90) { Set(r, "advance_m", adv); Set(r, "transfer_m", tr); }
                    if (p.NextMark == 180) Set(r, "tacticalDiameter_m", tr);
                    Set(r, $"timeTo{h}_s", tm);
                    Set(r, $"speedAt{h}_kn", v);
                    p.NextMark += 90;
                }
                // 定常迴旋:360° 至 450° 的樣本平均(黃金測試取 360° 後的後半段;ship-familiarisation.md 第 2.2 節)
                if (p.ChangeDeg >= 360.0)
                {
                    p.SumV += Math.Sqrt(m.U * m.U + m.V * m.V);
                    p.SumR += p.Sign * m.R;
                    p.SteadyCount++;
                }
                p.PrevChange = p.ChangeDeg;
                p.PrevAlong = along;
                p.PrevAcross = across;
                if (p.ChangeDeg >= 450.0)
                {
                    SteadyTurn(p);
                    Finish(p, t, complete: true);
                }
                else if (elapsed > 1800.0) EndEarly(p, t, p.ChangeDeg >= 90.0, "逾時(1,800 s 內未迴轉 450°)");
                break;
            }
            case ManoeuvreKinds.Zigzag:
            {
                var h = Units.RadToDeg(Units.WrapRadPi(m.Psi - p.Psi0));
                var psiC = r.CheckHeadingDeg ?? 0;
                if (p.Phase == 0)
                {
                    RecordExecute(p, p.PrevT, p.PrevChange, t, h);
                    if (p.Side * h > psiC + 90.0) { EndZigzagEarly(p, t, "航向變化超過執行航向 90° 仍未反舵"); break; }
                }
                else
                {
                    var g = -p.Side * h;   // 朝前一舵方向的航向變化
                    if (p.H1 is { } h1 && g < h1)
                    {
                        // 極值在前一樣本:三點拋物線修正(樣本等間距 1 s;樣本不足時取前一樣本)
                        var peak = h1;
                        var peakT = p.T1;
                        if (p.H2 is { } h2)
                        {
                            var den = h2 - 2.0 * h1 + g;
                            if (den < 0)
                            {
                                var off = (h2 - g) / (2.0 * den);
                                peak = h1 - (h2 - g) * off / 4.0;
                                peakT = p.T1 + off * (t - p.T1);
                            }
                        }
                        p.Overshoots++;
                        var os = peak - psiC;
                        r.Marks.Add(new ManoeuvreMark { Type = "overshoot", TS = Round(peakT - r.StartS), Value = Round(os), HeadingChangeDeg = Round(-p.Side * p.Sign * peak), Index = p.Overshoots });
                        Set(r, $"overshoot{p.Overshoots}_deg", os);
                        p.Phase = 0;
                        if (p.Overshoots >= p.MaxOvershoots)
                        {
                            Finish(p, t, complete: true);
                            break;
                        }
                    }
                    else
                    {
                        p.H2 = p.H1;
                        p.H1 = g;
                        p.T1 = t;
                    }
                }
                p.PrevChange = h;
                if (p.ReversalInProgress && (m.Tick - p.LastReversalTick) * _dt > ReversalSequenceMaxS)
                {
                    EndZigzagEarly(p, t, $"反舵 {ReversalSequenceMaxS:F0} s 內未到達對舷同舵角");
                    break;
                }
                if (p.LastReversalTick >= 0 ? (m.Tick - p.LastReversalTick) * _dt > 300.0 : elapsed > 600.0)
                    EndZigzagEarly(p, t, "逾時(未依時反舵)");
                break;
            }
            case ManoeuvreKinds.CrashStop:
            {
                if (!p.AsternSeen && m.Rpm < 0)
                {
                    var f = F(p.PrevRpm, m.Rpm, 0.0);
                    Set(r, "asternStart_s", L(p.PrevT, t, f) - r.StartS);
                    p.AsternSeen = true;
                }
                if (Math.Abs(m.RudderOrderDeg) > SteadyRudderDeg) p.RudderUsed = true;
                p.MaxRot = Math.Max(p.MaxRot, Math.Abs(m.RotDegPerMin));
                if (uKn <= 0.0)
                {
                    var f = F(p.PrevU, uKn, 0.0);
                    var x = L(p.PrevX, m.X, f) - p.X0;
                    var y = L(p.PrevY, m.Y, f) - p.Y0;
                    Set(r, "stopTime_s", L(p.PrevT, t, f) - r.StartS);
                    Set(r, "trackReach_m", p.TrackM + f * chord);
                    Set(r, "headReach_m", x * Math.Sin(p.Psi0) + y * Math.Cos(p.Psi0));
                    var lat = x * Math.Cos(p.Psi0) - y * Math.Sin(p.Psi0);
                    Set(r, "sideReach_m", Math.Abs(lat));
                    Set(r, "lateralOffset_m", lat);
                    Set(r, "headingChange_deg", p.ChangeDeg - (1 - f) * p.Sign * dpsi);
                    Set(r, "maxRot_degPerMin", p.MaxRot);
                    if (p.RudderUsed) AddNote(r, "停船過程使用舵(試俥為舵正中)");
                    Finish(p, t, complete: true);
                    break;
                }
                p.TrackM += chord;
                if (elapsed > 1500.0) Abort(p, t, "逾時(1,500 s 內未停船)");
                break;
            }
            case ManoeuvreKinds.InertiaStop:
            {
                while (elapsed >= p.NextMinute * 60.0 && p.NextMinute <= 30)
                {
                    var tm = r.StartS + p.NextMinute * 60.0;
                    var f = F(p.PrevT, t, tm);
                    var v = L(p.PrevSpeedKn, speedKn, f);
                    var hc = L(p.PrevChange, p.ChangeDeg, f);
                    r.Marks.Add(new ManoeuvreMark { Type = "minute", TS = p.NextMinute * 60.0, SpeedKn = Round(v), HeadingChangeDeg = Round(hc), Index = p.NextMinute });
                    Set(r, $"speedAt{p.NextMinute}min_kn", v);
                    if (p.Drift) Set(r, $"headingChangeAt{p.NextMinute}min_deg", hc);
                    p.NextMinute++;
                }
                if (Math.Abs(p.ChangeDeg) > Math.Abs(p.MaxChange)) p.MaxChange = p.ChangeDeg;
                if (Math.Abs(m.RudderOrderDeg) > SteadyRudderDeg) p.RudderUsed = true;
                var byTime = p.Drift && elapsed >= DriftObserveS;
                if (speedKn <= p.EndSpeedKn || byTime)
                {
                    // 停俥漂移:先到觀察時間或減至 0.5 kn 者結束;其他:減至結束速度
                    var f = speedKn <= p.EndSpeedKn ? F(p.PrevSpeedKn, speedKn, p.EndSpeedKn) : F(p.PrevT - r.StartS, elapsed, DriftObserveS);
                    var x = L(p.PrevX, m.X, f) - p.X0;
                    var y = L(p.PrevY, m.Y, f) - p.Y0;
                    Set(r, "endSpeed_kn", p.Drift ? L(p.PrevSpeedKn, speedKn, f) : p.EndSpeedKn);
                    Set(r, "time_s", L(p.PrevT, t, f) - r.StartS);
                    Set(r, "distance_m", p.TrackM + f * chord);
                    Set(r, "headReach_m", x * Math.Sin(p.Psi0) + y * Math.Cos(p.Psi0));
                    var lat = x * Math.Cos(p.Psi0) - y * Math.Sin(p.Psi0);
                    Set(r, "sideReach_m", Math.Abs(lat));
                    Set(r, "lateralOffset_m", lat);
                    var change = L(p.PrevChange, p.ChangeDeg, f);
                    Set(r, "headingChange_deg", change);
                    if (p.Drift) Set(r, "maxHeadingChange_deg", Math.Abs(change) > Math.Abs(p.MaxChange) ? change : p.MaxChange);
                    if (p.RudderUsed) AddNote(r, "滑行過程使用舵(試俥為舵正中)");
                    Finish(p, t, complete: true);
                    break;
                }
                p.TrackM += chord;
                p.PrevChange = p.ChangeDeg;
                if (elapsed > 1800.0)
                {
                    Set(r, "endSpeed_kn", speedKn);
                    Set(r, "time_s", elapsed);
                    Set(r, "distance_m", p.TrackM);
                    EndEarly(p, t, true, $"逾時(1,800 s 內未減至 {p.EndSpeedKn:F1} kn)");
                }
                break;
            }
            case ManoeuvreKinds.ThrusterTurn:
            {
                if (elapsed > 60.0) p.MaxRot = Math.Max(p.MaxRot, p.Sign * Units.RadPerSecToDegPerMin(m.R));
                if (!p.Mark60 && elapsed >= 60.0)
                {
                    var f = F(p.PrevT - r.StartS, elapsed, 60.0);
                    Set(r, "headingChange60_deg", L(p.PrevChange, p.ChangeDeg, f));
                    p.Mark60 = true;
                }
                if (!p.Reached30 && p.ChangeDeg >= 30.0)
                {
                    Set(r, "time30_s", L(p.PrevT, t, F(p.PrevChange, p.ChangeDeg, 30.0)) - r.StartS);
                    p.Reached30 = true;
                }
                if (p.ChangeDeg >= 90.0)
                {
                    Set(r, "time90_s", L(p.PrevT, t, F(p.PrevChange, p.ChangeDeg, 90.0)) - r.StartS);
                    if (p.MaxRot > 0) Set(r, "maxRot_degPerMin", p.MaxRot);
                    Finish(p, t, complete: true);
                    break;
                }
                p.PrevChange = p.ChangeDeg;
                if (elapsed > 900.0) EndEarly(p, t, true, "逾時(900 s 內未迴轉 90°)");
                break;
            }
        }
        if (State.Active == p)
        {
            p.PrevT = t;
            p.PrevSpeedKn = speedKn;
            p.PrevU = uKn;
            p.PrevRpm = m.Rpm;
            p.PrevX = m.X;
            p.PrevY = m.Y;
        }
    }

    /// <summary>Z 形:航向變化(相對開始航向)由 h0 到 h1 越過執行航向 ±ψ 時記錄到達時刻(每個半週期一次)。</summary>
    private static void RecordExecute(ManoeuvreProgress p, double t0, double h0, double t1, double h1)
    {
        var r = p.Record;
        var psiC = r.CheckHeadingDeg ?? 0;
        if (p.ExecuteCrossings != p.Reversals || p.Side * h1 < psiC || p.Side * h0 >= psiC) return;
        var f = h1 != h0 ? Math.Clamp((p.Side * psiC - h0) / (h1 - h0), 0.0, 1.0) : 1.0;
        var tm = t0 + f * (t1 - t0) - r.StartS;
        p.ExecuteCrossings++;
        r.Marks.Add(new ManoeuvreMark { Type = "execute", TS = Round(tm), HeadingChangeDeg = p.Sign * p.Side * psiC, Index = p.ExecuteCrossings });
        if (p.ExecuteCrossings == 1) Set(r, "timeToExecute_s", tm);
        if (p.ExecuteCrossings == 2) Set(r, "timeToSecondExecute_s", tm);
    }

    private static void SteadyTurn(ManoeuvreProgress p)
    {
        if (p.SteadyCount == 0) return;
        var v = p.SumV / p.SteadyCount;
        var rr = p.SumR / p.SteadyCount;
        Set(p.Record, "steadySpeed_kn", Units.MpsToKn(v));
        Set(p.Record, "steadyRot_degPerMin", Units.RadPerSecToDegPerMin(rr));
        if (Math.Abs(rr) > 1e-9) Set(p.Record, "steadyDiameter_m", 2.0 * v / Math.Abs(rr));
    }

    private void SampleSwing(ManoeuvreProgress p, Situation sit)
    {
        var r = p.Record;
        var t = sit.Time;
        var a = p.Side * sit.RudderDeg;       // 朝新舵令方向的舵角
        var prev = p.Side * p.PrevRudder;
        var target = Math.Abs(r.RudderDeg ?? 0);
        double At(double level) => p.PrevT + (a != prev ? Math.Clamp((level - prev) / (a - prev), 0, 1) : 1) * (t - p.PrevT) - r.StartS;
        if (!r.Values.ContainsKey("timeToMidships_s") && a >= 0 && prev < 0) Set(r, "timeToMidships_s", At(0));
        if (!r.Values.ContainsKey("timeTo30_s") && a >= 30 && prev < 30) Set(r, "timeTo30_s", At(30));
        if (a >= target - 0.5)
        {
            var tt = At(target - 0.5);
            Set(r, "timeToOrder_s", tt);
            if (Math.Abs(target - 35) <= 1 && Math.Abs(Math.Abs(r.Values.GetValueOrDefault("from_deg")) - 35) <= 1) Set(r, "time35to35_s", tt);
            if (r.Values.TryGetValue("timeTo30_s", out var t30))
            {
                Set(r, "solas35to30Within28s", t30 <= 28.0 ? 1 : 0);
                AddNote(r, $"35° → 對舷 30° {t30:F1} s(SOLAS II-1/29.3.2 要求 ≤ 28 s)");
            }
            FinishSlot(p, t, complete: true, slot: 1);
            return;
        }
        if (t - r.StartS > 120.0) { EndSlotEarly(p, t, "逾時(120 s 內舵未到令)", slot: 1); return; }
        p.PrevRudder = sit.RudderDeg;
        p.PrevT = t;
    }

    private void MobRudder(ManoeuvreProgress p, double rud, long tick)
    {
        var s = Math.Abs(rud) >= 10.0 ? Math.Sign(rud) : 0;
        if (s == 0) return;
        if (p.FirstRudderTick < 0)
        {
            p.FirstRudderTick = tick;
            p.FirstRudderSign = s;
            Set(p.Record, "reactionTime_s", tick * _dt - p.Record.StartS);
            p.Record.Values["firstRudder_deg"] = Round(rud);
            p.SegmentStartChange = p.ChangeDeg;
        }
        else if (p.CurrentRudderSign != 0 && s != p.CurrentRudderSign)
        {
            var seg = p.ChangeDeg - p.SegmentStartChange;
            p.Segments.Add(Round(seg));
            p.Record.Marks.Add(new ManoeuvreMark { Type = "turn", TS = Round(tick * _dt - p.Record.StartS), Value = Round(seg), HeadingChangeDeg = Round(p.ChangeDeg), Index = p.Segments.Count });
            p.SegmentStartChange = p.ChangeDeg;
        }
        p.CurrentRudderSign = s;
    }

    private void SampleMob(ManoeuvreProgress p, Situation sit)
    {
        var r = p.Record;
        var m = _engine.Motion;
        var t = sit.Time;
        var target = _engine.Traffic.Find(r.TargetId ?? "");
        if (target is null || !target.Active) { EndSlotEarly(p, t, "落水者目標已移除", slot: 2); return; }
        p.ChangeDeg += Units.RadToDeg(Units.WrapRadPi(m.Psi - p.LastPsi));
        p.LastPsi = m.Psi;
        var dx = m.X - target.X;
        var dy = m.Y - target.Y;
        var d = Math.Sqrt(dx * dx + dy * dy);
        var sog = sit.SogKn;
        var turned = Math.Abs(p.ChangeDeg) >= 150.0;
        var headingDeg = Units.RadToDeg(m.Psi);
        if (!p.ReciprocalSeen && turned && Math.Abs(Units.WrapDeg180(headingDeg - (Units.RadToDeg(p.Psi0) + 180.0))) <= 20.0)
        {
            p.ReciprocalSeen = true;
            var lat = dx * Math.Cos(p.Psi0) - dy * Math.Sin(p.Psi0);
            Set(r, "reciprocalAt_s", t - r.StartS);
            Set(r, "returnTrackOffset_m", Math.Abs(lat));
            Set(r, "returnLateralOffset_m", lat);
            Set(r, "rangeAtReciprocal_m", d);
            r.Marks.Add(new ManoeuvreMark { Type = "reciprocal", TS = Round(t - r.StartS), HeadingChangeDeg = Round(p.ChangeDeg), Value = Round(d) });
        }
        var returnRadius = Math.Max(2.0 * _loa, 100.0);
        if (turned)
        {
            if (p.MinRange is not { } minR || d < minR)
            {
                p.MinRange = d;
                p.MinRangeT = t;
                p.MinRangeSpeedKn = sog;
                p.OpeningSeconds = 0;
            }
            else if (d > p.MinRange + 20.0) p.OpeningSeconds++;   // MinRange 此時必有值
            if (!r.Values.ContainsKey("returnTime_s") && d <= returnRadius)
            {
                Set(r, "returnTime_s", t - r.StartS);
                Set(r, "returnSpeed_kn", sog);
            }
            var stoppedNear = d <= returnRadius && sog <= 1.0;
            if (stoppedNear || (p.OpeningSeconds >= 30 && p.MinRange is <= Units.MetresPerNauticalMile))
            {
                FinishMob(p, t, returnRadius);
                return;
            }
        }
        if (t - r.StartS > 2400.0) FinishMob(p, t, returnRadius, "逾時(2,400 s)");
    }

    private void FinishMob(ManoeuvreProgress p, double t, double returnRadius, string? reason = null)
    {
        var r = p.Record;
        if (p.MinRange is { } minRange)
        {
            Set(r, "minDistance_m", minRange);
            Set(r, "minDistanceAt_s", p.MinRangeT - r.StartS);
            Set(r, "speedAtMin_kn", p.MinRangeSpeedKn);
            r.Marks.Add(new ManoeuvreMark { Type = "closest", TS = Round(p.MinRangeT - r.StartS), SpeedKn = Round(p.MinRangeSpeedKn), Value = Round(minRange) });
        }
        Set(r, "totalHeadingChange_deg", p.ChangeDeg);
        Set(r, "returnRadius_m", returnRadius);
        var segs = new List<double>(p.Segments);
        if (p.FirstRudderTick >= 0) segs.Add(Round(p.ChangeDeg - p.SegmentStartChange));
        r.Method = ClassifyMob(segs);
        if (p.FirstRudderTick < 0) AddNote(r, "落水後未下 ≥ 10° 舵令");
        else
        {
            var toward = r.Side is not null && (p.FirstRudderSign > 0) == (r.Side == "starboard");
            r.Values["initialTurnTowardMob"] = toward ? 1 : 0;
            AddNote(r, toward ? "首次轉舵朝落水舷(使船艉離開落水者)" : "首次轉舵未朝落水舷");
        }
        AddNote(r, r.Method switch
        {
            "williamson" => "Williamson 迴旋:先轉約 60° 再滿舵反轉至對向航向,回到原航跡",
            "anderson" => "Anderson(單旋)迴旋:同向滿舵約 250° 直接回到落水者",
            "scharnow" => "Scharnow 迴旋:先轉約 240° 再反轉至對向航向",
            _ => "旋迴方式不屬 Williamson/Anderson/Scharnow",
        });
        var complete = p.MinRange is { } mr && mr <= returnRadius;
        if (reason is not null) AddNote(r, reason);
        FinishSlot(p, t, complete, slot: 2);
    }

    /// <summary>MOB 旋迴方式:依舵向分段的航向變化(度,+ 右轉)分類。</summary>
    public static string ClassifyMob(IReadOnlyList<double> segments)
    {
        if (segments.Count == 0) return "other";
        var first = segments[0];
        var net = segments.Sum();
        if (segments.Count == 1 || segments.Skip(1).All(s => Math.Abs(s) < 15.0))
            return Math.Abs(first) >= 230.0 ? "anderson" : "other";
        var second = segments[1];
        var reversed = Math.Sign(second) == -Math.Sign(first);
        var reciprocal = Math.Abs(Math.Abs(net) - 180.0) <= 30.0;
        if (reversed && reciprocal && Math.Abs(first) >= 40.0 && Math.Abs(first) <= 90.0) return "williamson";
        if (reversed && reciprocal && Math.Abs(first) >= 200.0 && Math.Abs(first) <= 280.0) return "scharnow";
        return "other";
    }

    // ------------------------------------------------------------------ 結束

    private void EndEarly(ManoeuvreProgress p, double t, bool usable, string reason)
    {
        if (!usable) { Abort(p, t, reason); return; }
        AddNote(p.Record, $"提前結束:{reason}");
        Finish(p, t, complete: false);
    }

    private void EndZigzagEarly(ManoeuvreProgress p, double t, string reason)
    {
        if (p.Overshoots == 0) { Abort(p, t, reason); return; }
        AddNote(p.Record, $"提前結束:{reason}");
        Finish(p, t, complete: p.Overshoots >= 2);
    }

    private void EndSlotEarly(ManoeuvreProgress p, double t, string reason, int slot)
    {
        p.Record.State = "aborted";
        p.Record.AbortReason = reason;
        p.Record.EndS = t;
        Clear(slot);
        State.Records.Add(p.Record);
        _raise(ManoeuvreEventKinds.Aborted, p.Record.Id, p.Record.TargetId, $"{Label(p.Record)} 量測中止:{reason}", new { id = p.Record.Id, kind = p.Record.Kind, reason });
    }

    private void Abort(ManoeuvreProgress p, double t, string reason) => EndSlotEarly(p, t, reason, slot: 0);

    private void Finish(ManoeuvreProgress p, double t, bool complete) => FinishSlot(p, t, complete, slot: 0);

    private void FinishSlot(ManoeuvreProgress p, double t, bool complete, int slot)
    {
        var r = p.Record;
        r.State = "measured";
        r.Complete = complete;
        r.EndS = t;
        if (r.Kind == ManoeuvreKinds.RudderSwing) ManoeuvreTargets.CompareSteering(r, _ship, 15.0);
        else if (r.Kind != ManoeuvreKinds.ManOverboard)
        {
            if (_targets is not null)
            {
                _targets.Compare(r, _lpp);
                r.TargetsSha256 = _targets.Sha256;
            }
            else AddNote(r, $"找不到 data/ships/{r.ShipId}/trial_targets.json,未比較");
        }
        if (string.Equals(r.ShipId, "FSB1", StringComparison.OrdinalIgnoreCase) && Math.Abs(r.RudderDeg ?? 0) > 35.5)
            AddNote(r, "Schilling 舵 35°–70° 區間的舵力模型無試俥資料,僅經船長驗證(規劃書第 3.5 節)");
        Clear(slot);
        State.Records.Add(r);
        _raise(ManoeuvreEventKinds.Measured, r.Id, r.TargetId, Message(r), r);
    }

    private void Clear(int slot)
    {
        if (slot == 0) State.Active = null;
        else if (slot == 1) State.Swing = null;
        else State.Mob = null;
    }

    // ------------------------------------------------------------------ 輸出

    /// <summary>summary.manoeuvres[]:已結束(measured/aborted)的量測依序,進行中者附於最後(state = inProgress)。</summary>
    public IReadOnlyList<ManoeuvreRecord> Summary()
    {
        var list = new List<ManoeuvreRecord>(State.Records);
        foreach (var p in new[] { State.Active, State.Swing, State.Mob })
            if (p is { Pending: false }) list.Add(p.Record);   // 未確定的開始序列不輸出
        return list;
    }

    /// <summary>容許帶指標 manoeuvres.*(<see cref="MetricNames"/>);無值時 null。</summary>
    public double? Metric(string name)
    {
        if (!Enabled || !name.StartsWith("manoeuvres.", StringComparison.Ordinal)) return null;
        var measured = State.Records.Where(r => r.State == "measured").ToList();
        var comps = measured.SelectMany(r => r.Comparisons).Where(c => c.Comparable).ToList();
        switch (name)
        {
            case "manoeuvres.count": return measured.Count;
            case "manoeuvres.compared": return comps.Count;
            case "manoeuvres.outsideTolerance": return comps.Count(c => !c.WithinTolerance);
            case "manoeuvres.maxAbsDeviation_pct":
            {
                var pcts = comps.Where(c => c.DeviationPct is not null).Select(c => Math.Abs(c.DeviationPct!.Value)).ToList();
                return pcts.Count > 0 ? pcts.Max() : null;
            }
        }
        var body = name["manoeuvres.".Length..];
        var dot = body.LastIndexOf('.');
        if (dot <= 0) return null;
        var (id, key) = (body[..dot], body[(dot + 1)..]);
        // 同 id 多筆(例:F01 標準 35° 迴旋與低速 Schilling 段的 35° 迴旋):取最近一次「完整且有可比比較」者,其次最近一次完整者,再其次最近一次 measured
        var same = measured.Where(r => r.Id == id && r.Values.ContainsKey(key)).ToList();
        var rec = same.LastOrDefault(r => r.Complete && r.Comparisons.Any(c => c.Comparable)) ?? same.LastOrDefault(r => r.Complete) ?? same.LastOrDefault();
        return rec is not null ? rec.Values[key] : null;
    }

    /// <summary>可被 bands 引用的 manoeuvres.* 指標名稱。</summary>
    public static IEnumerable<string> MetricNameList()
    {
        yield return "manoeuvres.count";
        yield return "manoeuvres.compared";
        yield return "manoeuvres.outsideTolerance";
        yield return "manoeuvres.maxAbsDeviation_pct";
        foreach (var side in new[] { "port", "starboard" })
            foreach (var k in new[] { "advance_m", "transfer_m", "tacticalDiameter_m", "steadyDiameter_m", "steadySpeed_kn", "timeTo90_s", "timeTo180_s" })
                yield return $"manoeuvres.turning35.{side}.{k}";
        foreach (var psi in new[] { 10, 20 })
            foreach (var first in new[] { "portFirst", "starboardFirst" })
                foreach (var k in new[] { "overshoot1_deg", "overshoot2_deg", "timeToExecute_s" })
                    yield return $"manoeuvres.zigzag{psi}.{first}.{k}";
        foreach (var k in new[] { "stopTime_s", "trackReach_m", "headReach_m", "sideReach_m", "headingChange_deg" })
            yield return $"manoeuvres.crashStop.{k}";
        foreach (var k in new[] { "time_s", "distance_m", "headReach_m", "sideReach_m" })
            yield return $"manoeuvres.inertiaStop.{k}";
        foreach (var side in new[] { "port", "starboard" })
            foreach (var k in new[] { "time90_s", "maxRot_degPerMin" })
                yield return $"manoeuvres.thrusterTurn.{side}.{k}";
        foreach (var to in new[] { "toPort", "toStarboard" })
            foreach (var k in new[] { "timeTo30_s", "timeToOrder_s" })
                yield return $"manoeuvres.rudderSwing.{to}.{k}";
        foreach (var k in new[] { "reactionTime_s", "returnTime_s", "minDistance_m", "returnTrackOffset_m", "reciprocalAt_s" })
            yield return $"manoeuvres.manOverboard.{k}";
    }

    public void Restore(ManoeuvreState s) => State = s;

    private static string Label(ManoeuvreRecord r)
    {
        var side = r.Side == "starboard" ? "右" : r.Side == "port" ? "左" : "";
        return r.Kind switch
        {
            ManoeuvreKinds.Turning => $"{Math.Abs(r.RudderDeg ?? 0):F0}° {side}迴旋",
            ManoeuvreKinds.Zigzag => $"{r.CheckHeadingDeg:F0}/{r.CheckHeadingDeg:F0} Z 形({side}舷先)",
            ManoeuvreKinds.CrashStop => $"緊急停船({r.Order})",
            ManoeuvreKinds.InertiaStop => r.Mode == "drift" ? "停俥漂移" : "停俥慣性停船",
            ManoeuvreKinds.ThrusterTurn => $"艏側推{side}迴轉",
            ManoeuvreKinds.RudderSwing => $"舵機擺動(至{side}舷,泵 {r.Pump})",
            ManoeuvreKinds.ManOverboard => "人員落水回航",
            _ => r.Kind,
        };
    }

    private static string Message(ManoeuvreRecord r)
    {
        string V(string key, string unit) => r.Values.TryGetValue(key, out var v) ? $"{v.ToString("F1", CultureInfo.InvariantCulture)} {unit}" : "—";
        var main = r.Kind switch
        {
            ManoeuvreKinds.Turning => $"前進距離 {V("advance_m", "m")}、橫距 {V("transfer_m", "m")}、戰術直徑 {V("tacticalDiameter_m", "m")}、定常直徑 {V("steadyDiameter_m", "m")}",
            ManoeuvreKinds.Zigzag => $"超越角 {V("overshoot1_deg", "°")} / {V("overshoot2_deg", "°")}" + (r.Values.ContainsKey("overshoot3_deg") ? $" / {V("overshoot3_deg", "°")}" : ""),
            ManoeuvreKinds.CrashStop => $"停船 {V("stopTime_s", "s")}、航跡 {V("trackReach_m", "m")}、head reach {V("headReach_m", "m")}、航向變化 {V("headingChange_deg", "°")}",
            ManoeuvreKinds.InertiaStop => r.Mode == "drift"
                ? $"{V("time_s", "s")}、{V("distance_m", "m")}、艏向變化 {V("headingChange_deg", "°")}(最大 {V("maxHeadingChange_deg", "°")})、末速 {V("endSpeed_kn", "kn")}"
                : $"減至 {V("endSpeed_kn", "kn")}:{V("time_s", "s")}、{V("distance_m", "m")}",
            ManoeuvreKinds.ThrusterTurn => $"90° {V("time90_s", "s")}、60 s 航向變化 {V("headingChange60_deg", "°")}",
            ManoeuvreKinds.RudderSwing => $"到 30° {V("timeTo30_s", "s")}、到令 {V("timeToOrder_s", "s")}",
            ManoeuvreKinds.ManOverboard => $"{r.Method};最近 {V("minDistance_m", "m")}(T+{V("minDistanceAt_s", "s")})",
            _ => "",
        };
        var comps = r.Comparisons.Where(c => c.Comparable).ToList();
        var cmp = comps.Count > 0 ? $";比較 {comps.Count} 項、公差內 {comps.Count(c => c.WithinTolerance)} 項({r.DataQuality})" : r.Comparisons.Count > 0 ? ";條件不可比,僅供參考" : "";
        return $"{Label(r)}{(r.Complete ? "" : "(未完整)")}:{main}{cmp}";
    }

    private static void Set(ManoeuvreRecord r, string key, double value)
    {
        if (double.IsFinite(value)) r.Values[key] = Round(value);
    }

    private static double Round(double v) => Math.Round(v, 3);

    private static void AddNote(ManoeuvreRecord r, string note)
    {
        if (!r.Notes.Contains(note)) r.Notes.Add(note);
    }
}
