using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.External;

namespace SimosaBRM.SimCore.Assessment;

/// <summary>
/// 靠泊與錨泊指標(規劃書第 8.3 節第 4 類「靠泊橫向接近速度、靠泊接觸能量」、第 8.1 節 E12–E14;物理事件見
/// docs/design/physics-tugs-anchors-mooring.md 第 6.2 節)。只讀引擎狀態與事件、不改物理、不用亂數;狀態進評估快照(不進雜湊鏈,與其他評估累計值相同)。
/// <list type="bullet">
/// <item>靠上(landing):一次靠泊接近 — 第一次接觸,或「未繫妥」且所有碰墊都已離開碰墊面 ≥ <see cref="LandingClearanceM"/> 之後再接觸的接觸段
/// (靠妥後在碰墊上的反覆輕碰、繫泊中纜繩上的擺盪不是新的靠上)。艏(+LOA/2)、舯(0)、艉(−LOA/2)朝碼頭面的對地速度取接觸開始前最後一個
/// 評估步(每秒)取樣,僅供講評(端點含偏擺分量 r·x,不作為容許帶指標);<c>contactSpeed</c> 為該次靠上的接近接觸點法向速度最大值。</item>
/// <item>碰墊接觸(contacts[]):每個 fenderContact 事件一筆(接觸點法向接近速度、靜態曲線能量與額定能量比、過載);<c>approach</c> = 接近接觸:
/// 未繫妥,且該碰墊自上次接觸(或情境開始)以來曾離開 ≥ <see cref="LandingClearanceM"/>。</item>
/// <item>繫妥(allFast):所有已設定的纜繩皆受力(fast / heaving / slacking)且本次靠泊船舯前、後的碰墊皆已接觸(每秒評估);記下時刻、船舯里程與泊位中心(碰墊列中點)的偏差。
/// 繫妥期間的接觸不是接近接觸;全部纜繩解除(無纜受力)後解除。</item>
/// <item>已離開泊位(departed;E14 離泊後再碰回):曾接觸過泊位後,於評估步(每秒)所有碰墊壓縮為 0、無碼頭硬接觸、未繫妥,且船體外形距碼頭線(有限線段)
/// &gt; <see cref="DepartedDistanceM"/>(情境 <c>berth.departedDistance_m</c>,預設 <see cref="DefaultDepartedDistanceM"/>)時成立並鎖定(再次繫妥時解除)。
/// 已離開泊位後的碰墊/碼頭接觸為再碰回(contacts[].reContact),其接觸點法向速度最大值為 <c>berthing.maxReContactSpeed_mps</c>
/// (與 maxLateralSpeed 分開;離泊過程中艉擺碰墊不算再碰回)。</item>
/// <item>碼頭硬接觸次數、斷纜次數、各纜最大張力 / MBL(每秒取樣與 mooringLineParted 事件張力)、各拖船工作時間(status working 的秒數)與最大出力、拖纜斷裂次數。</item>
/// <item>錨泊:每次 letGo 時的對地速度、帶上(anchorBroughtUp)時鏈長與水深比(scope)、走錨次數與累計時間(每秒取樣 dragging)、
/// 錨鏈孔最大張力、首次走錨到學員第一個反應(車令/舵令/側推/錨指令)的時間。</item>
/// </list>
/// 速度單位 m/s 並附 kn;接近速度正 = 朝碼頭。
/// </summary>
public sealed class BerthingTracker
{
    /// <summary>新的靠上 / 接近接觸所需的離開距離(m;船體與碰墊面,每秒取樣):小於此值的分離視為同一次靠泊的反覆輕碰</summary>
    public const double LandingClearanceM = 0.5;
    /// <summary>「已離開泊位」的船體距碼頭線門檻預設值(m;情境 berth.departedDistance_m 可覆寫;約 1 倍船寬)</summary>
    public const double DefaultDepartedDistanceM = 20.0;

    private readonly SimulationEngine _engine;
    private readonly Scenario.Scenario _scenario;
    private readonly double _halfLoa;

    /// <summary>情境有泊位或拖船(summary.berthing 輸出)</summary>
    public bool BerthingEnabled { get; }
    /// <summary>情境有 anchors 區段(summary.anchoring 一律輸出;否則錨曾被使用後才輸出)</summary>
    public bool AnchoringDeclared { get; }
    /// <summary>「已離開泊位」的船體距碼頭線門檻(m)</summary>
    public double DepartedDistanceM { get; }
    public BerthingState State { get; private set; } = new();

    public BerthingTracker(SimulationEngine engine, Scenario.Scenario scenario, double loa)
    {
        _engine = engine;
        _scenario = scenario;
        _halfLoa = 0.5 * loa;
        BerthingEnabled = scenario.Berth is not null || scenario.Tugs is { Count: > 0 };
        AnchoringDeclared = scenario.Anchors is not null;
        DepartedDistanceM = scenario.Berth?.DepartedDistanceM ?? DefaultDepartedDistanceM;
    }

    /// <summary>summary.anchoring 是否輸出(情境宣告 anchors,或錨曾被使用)。</summary>
    public bool AnchoringEnabled => AnchoringDeclared || State.AnchorsUsed || _engine.External.AnchorsUsed;

    public void Restore(BerthingState s) => State = s;

    // ------------------------------------------------------------------ 觀察

    public void OnEvent(SimEvent e)
    {
        var st = State;
        switch (e.Kind)
        {
            case SimEventKinds.FenderContact:
            {
                if (!BerthingEnabled) return;
                EnsureFenderState();
                var fid = e.DataString("fenderId") ?? e.ItemId ?? "";
                // 接近接觸:未繫妥且此碰墊自上次接觸以來曾離開 ≥ LandingClearanceM(判斷用接觸前的狀態)
                var approach = !st.AllFast && !st.HeldFenders.Contains(fid);
                StartEpisode(e.T);
                if (!st.HeldFenders.Contains(fid)) st.HeldFenders.Add(fid);
                MarkLanded(fid);
                var speed = e.DataNumber("normalSpeed_mps") ?? 0.0;
                var energy = e.DataNumber("energy_kNm") ?? 0.0;
                var rated = e.DataNumber("ratedEnergy_kNm");
                st.Contacts.Add(new FenderContactMetric
                {
                    FenderId = fid, TS = e.T, Approach = approach, NormalSpeedMps = speed, NormalSpeedKn = Units.MpsToKn(speed),
                    EnergyKNm = energy, RatedEnergyKNm = rated, EnergyRatio = rated is > 0 ? energy / rated.Value : null,
                    MaxReactionKN = e.DataNumber("maxReaction_kN"), DeflectionPct = e.DataNumber("deflection_pct"), Overload = e.DataBool("overload") == true,
                });
                if (approach) Approached(speed);
                if (st.Departed)
                {
                    st.Contacts[^1] = st.Contacts[^1] with { ReContact = true };
                    ReContacted(speed);
                }
                break;
            }
            case SimEventKinds.QuayContact:
            {
                if (!BerthingEnabled) return;
                EnsureFenderState();
                var approach = !st.AllFast;
                StartEpisode(e.T);
                st.QuayContacts++;
                st.QuayContactTimes.Add(e.T);
                if (e.DataNumber("penetration_m") is { } pen && pen > st.QuayMaxPenetrationM) st.QuayMaxPenetrationM = pen;
                if (e.DataNumber("normalSpeed_mps") is { } qv)
                {
                    if (st.QuayMaxSpeedMps is null || qv > st.QuayMaxSpeedMps) st.QuayMaxSpeedMps = qv;
                    if (approach) Approached(qv);
                    if (st.Departed) ReContacted(qv);
                }
                break;
            }
            case SimEventKinds.MooringLineParted:
            {
                if (!BerthingEnabled) return;
                st.LineBreaks++;
                var line = Line(e.DataString("lineId") ?? e.ItemId ?? "");
                line.Parted = true;
                line.PartedAtS ??= e.T;
                if (e.DataNumber("mbl_t") is { } mbl && mbl > 0) line.MblT = mbl;
                if (e.DataNumber("tension_t") is { } t && t > line.MaxTensionT) line.MaxTensionT = t;
                break;
            }
            case SimEventKinds.TugLineParted:
            {
                if (!BerthingEnabled) return;
                st.TugLineBreaks++;
                var tug = Tug(e.DataString("id") ?? e.ItemId ?? "");
                tug.LineParted = true;
                tug.LinePartedAtS ??= e.T;
                break;
            }
            case SimEventKinds.AnchorLetGo:
            {
                st.AnchorsUsed = true;
                var a = Anchor(e.DataString("side") ?? e.ItemId ?? "");
                var sog = GroundSpeedMps();
                a.LetGoCount++;
                a.LetGoAtS ??= e.T;
                a.LetGoSogMps = sog;
                a.LetGoSogKn = Units.MpsToKn(sog);
                if (a.MaxLetGoSogKn is null || a.LetGoSogKn > a.MaxLetGoSogKn) a.MaxLetGoSogKn = a.LetGoSogKn;
                a.TargetLengthM = e.DataNumber("targetLength_m");
                a.WaterDepthM = WaterDepthM();
                a.PlannedScopeRatio = a.TargetLengthM is { } tl && a.WaterDepthM > 0 ? tl / a.WaterDepthM : null;
                if (e.Source == SimEventSources.Instructor) Respond(e.T, $"event:{e.Kind}");
                break;
            }
            case SimEventKinds.AnchorCommand:
                st.AnchorsUsed = true;
                if (e.Source == SimEventSources.Instructor) Respond(e.T, $"event:{e.Kind}:{e.DataString("action")}");
                break;
            case SimEventKinds.AnchorBroughtUp:
            {
                st.AnchorsUsed = true;
                var a = Anchor(e.DataString("side") ?? e.ItemId ?? "");
                a.BroughtUpAtS = e.T;
                a.BroughtUpLengthM = e.DataNumber("length_m");
                a.BroughtUpShackles = a.BroughtUpLengthM is { } bl && _engine.External.AnchorSpec is { } spec ? bl / spec.ShackleLengthM : null;
                var depth = WaterDepthM();
                a.WaterDepthM = depth;
                a.ScopeRatio = a.BroughtUpLengthM is { } l && depth > 0 ? l / depth : null;
                if (e.DataNumber("tension_t") is { } t && t > a.MaxTensionT) a.MaxTensionT = t;
                break;
            }
            case SimEventKinds.AnchorDragging:
            {
                st.AnchorsUsed = true;
                var a = Anchor(e.DataString("side") ?? e.ItemId ?? "");
                a.DragEvents++;
                a.FirstDragAtS ??= e.T;
                st.FirstDragAtS ??= e.T;
                if (e.DataNumber("tension_t") is { } t && t > a.MaxTensionT) a.MaxTensionT = t;
                break;
            }
            case SimEventKinds.AnchorBrakeSlipping:
                st.AnchorsUsed = true;
                Anchor(e.DataString("side") ?? e.ItemId ?? "").BrakeSlips++;
                break;
            case SimEventKinds.Acknowledge:
                // 走錨偵測:首次走錨後第一次確認/回報(任一 kind;例如 anchorWatch、vhf)
                if (st.FirstDragAtS is { } fd && st.DragDetectedAtS is null && e.T >= fd) st.DragDetectedAtS = e.T;
                break;
        }
    }

    /// <summary>學員動作(舵令、車令、轉速、側推、操舵模式):走錨後的第一個反應。</summary>
    public void OnAction(ObservedAction a, double t)
    {
        if (a.Kind is ActionKinds.RudderOrder or ActionKinds.TelegraphChange or ActionKinds.RpmOrder or ActionKinds.ThrusterOrder or ActionKinds.SteeringModeChange)
            Respond(t, a.Kind);
    }

    private void Respond(double t, string by)
    {
        var st = State;
        if (st.FirstDragAtS is not { } fd || st.DragResponseAtS is not null || t < fd) return;
        st.DragResponseAtS = t;
        st.DragResponseBy = by;
    }

    /// <summary>評估步(每秒):艏/舯/艉接近速度取樣與接觸段、繫纜張力、拖船工作時間與出力、錨鏈張力與走錨時間。</summary>
    public void Update(Situation sit)
    {
        var st = State;
        var ext = _engine.External;
        if (BerthingEnabled)
        {
            if (ext.Berth is { } berth)
            {
                EnsureFenderState();
                var contact = berth.QuayInContact || berth.Fenders.Any(f => f.InContact);
                var (bow, mid, stern) = ApproachSpeeds(berth);
                if (contact) StartEpisode(sit.Time);
                else
                {
                    st.InEpisode = false;
                    st.PreSample = new ApproachSample { TS = sit.Time, BowMps = bow, MidshipMps = mid, SternMps = stern };
                }
                // 已接觸過的碰墊:未接觸且離開 ≥ LandingClearanceM 後,下一次接觸才是接近接觸
                if (st.HeldFenders.Count > 0)
                {
                    var clear = FenderClearances(berth);
                    for (var i = 0; i < berth.Fenders.Count; i++)
                        if (!berth.Fenders[i].InContact && clear[i] >= LandingClearanceM) st.HeldFenders.Remove(berth.Fenders[i].Id);
                }
                // 已離開所有碰墊(皆 ≥ LandingClearanceM)、未繫妥且纜繩未全部受力:下一次靠泊重新判定艏艉靠上
                // (纜繩已全部受力時離開再靠回,視為同一次以纜繩/拖船靠攏)
                if (st.HeldFenders.Count == 0 && !contact && !st.AllFast && !(berth.Lines.Count > 0 && berth.Lines.All(l => l.Loaded)))
                    st.FwdLanded = st.AftLanded = false;
                UpdateAllFast(sit.Time);
                // 已離開泊位:曾接觸過泊位、所有碰墊壓縮為 0 且無碼頭接觸、未繫妥、船體距碼頭線 > DepartedDistanceM(鎖定;再次繫妥時解除)
                if (st.AllFast) st.Departed = false;
                else if (!st.Departed && !contact && st.Landings.Count > 0 && HullDistanceFromQuay(berth) > DepartedDistanceM)
                {
                    st.Departed = true;
                    st.DepartedAtS ??= sit.Time;
                }
                foreach (var l in berth.Lines)
                {
                    var line = Line(l.Id);
                    line.MblT = l.MblN / MooringLine.TonneForce;
                    var t = l.LastTensionN / MooringLine.TonneForce;
                    if (t > line.MaxTensionT) line.MaxTensionT = t;
                }
            }
            foreach (var t in ext.Tugs)
            {
                var tug = Tug(t.Id);
                tug.BollardPullT = t.BollardPullN / TugUnit.TonneForce;
                if (t.Status == TugStatus.Working) tug.WorkingTimeS += 1.0;
                var f = t.ForceN / TugUnit.TonneForce;
                if (f > tug.MaxForceT) tug.MaxForceT = f;
            }
        }
        if (ext.AnchorsUsed)
        {
            st.AnchorsUsed = true;
            foreach (var a in ext.Anchors)
            {
                if (a.Mode == AnchorMode.Stowed && a.Length <= 0) continue;
                var m = Anchor(a.Side);
                var t = AnchorUnit.ToT(a.Last.TopTension);
                if (t > m.MaxTensionT) m.MaxTensionT = t;
                if (a.Length > m.MaxLengthM) m.MaxLengthM = a.Length;
                if (a.Dragging) { m.DragTimeS += 1.0; }
            }
        }
    }

    /// <summary>
    /// 接觸段開始(無接觸 → 接觸)。第一次接觸,或未繫妥且所有碰墊都已離開 ≥ <see cref="LandingClearanceM"/> 時記為新的靠上;
    /// 否則(靠妥後輕碰、繫泊中擺盪、反彈未離開)只是同一次靠泊的再接觸,不新增 landing。
    /// </summary>
    private void StartEpisode(double t)
    {
        var st = State;
        if (st.InEpisode) return;
        st.InEpisode = true;
        var pre = st.PreSample;
        st.PreSample = null;
        if (st.Landings.Count > 0 && (st.AllFast || st.HeldFenders.Count > 0)) return;
        // 情境開始即接觸(尚無無接觸取樣)= initial;接觸中的碰墊已由 EnsureFenderState 列入 HeldFenders
        var initial = pre is null;
        double? Kn(double? v) => v is { } x ? Units.MpsToKn(x) : null;
        double? max = pre is null ? null : Math.Max(pre.BowMps, Math.Max(pre.MidshipMps, pre.SternMps));
        st.Landings.Add(new BerthingLanding
        {
            TS = t, SampleAtS = pre?.TS, Initial = initial,
            BowMps = pre?.BowMps, BowKn = Kn(pre?.BowMps), MidshipMps = pre?.MidshipMps, MidshipKn = Kn(pre?.MidshipMps),
            SternMps = pre?.SternMps, SternKn = Kn(pre?.SternMps), MaxMps = max, MaxKn = Kn(max),
        });
    }

    /// <summary>接近接觸(碰墊或碼頭)的接觸點法向速度:計入 maxLateralSpeed 與最近一次(非 initial)靠上的 contactSpeed。</summary>
    private void Approached(double speedMps)
    {
        var st = State;
        if (st.MaxApproachSpeedMps is null || speedMps > st.MaxApproachSpeedMps) st.MaxApproachSpeedMps = speedMps;
        if (st.Landings.Count == 0) return;
        var i = st.Landings.Count - 1;
        var l = st.Landings[i];
        if (l.Initial || (l.ContactSpeedMps is { } c && c >= speedMps)) return;
        st.Landings[i] = l with { ContactSpeedMps = speedMps, ContactSpeedKn = Units.MpsToKn(speedMps) };
    }

    /// <summary>已離開泊位後的再碰回(碰墊或碼頭):接觸點法向速度計入 maxReContactSpeed。</summary>
    private void ReContacted(double speedMps)
    {
        var st = State;
        st.ReContacts++;
        if (st.MaxReContactSpeedMps is null || speedMps > st.MaxReContactSpeedMps) st.MaxReContactSpeedMps = speedMps;
    }

    /// <summary>
    /// 船體外形(BerthModel.Hull 多邊形各邊)至碼頭線(有限線段)的最近距離(m;只讀)。以船體而非船舯判定:沿碼頭向前/向後駛離時,
    /// 船舯已越過碼頭端但艉/艏仍在碰墊旁,不應視為已離開泊位。
    /// </summary>
    private double HullDistanceFromQuay(BerthModel berth)
    {
        Span<(double E, double N)> hull = stackalloc (double, double)[berth.Hull.Length];
        berth.HullEnu(_engine.Motion, hull);
        var best = double.PositiveInfinity;
        foreach (var q in berth.Segments)
        {
            var (bx, by) = (q.Ax + q.Length * q.Tx, q.Ay + q.Length * q.Ty);
            for (var i = 0; i < hull.Length; i++)
            {
                var (p, r) = (hull[i], hull[(i + 1) % hull.Length]);
                best = Math.Min(best, SegmentDistance(p.E, p.N, r.E, r.N, q.Ax, q.Ay, bx, by));
            }
        }
        return best;
    }

    /// <summary>兩線段距離(相交時 0)。</summary>
    private static double SegmentDistance(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        static double Cross(double ox, double oy, double px, double py, double qx, double qy) => (px - ox) * (qy - oy) - (py - oy) * (qx - ox);
        var d1 = Cross(cx, cy, dx, dy, ax, ay);
        var d2 = Cross(cx, cy, dx, dy, bx, by);
        var d3 = Cross(ax, ay, bx, by, cx, cy);
        var d4 = Cross(ax, ay, bx, by, dx, dy);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return 0.0;
        static double PointSeg(double px, double py, double ux, double uy, double vx, double vy)
        {
            var (ex, ey) = (vx - ux, vy - uy);
            var l2 = ex * ex + ey * ey;
            var t = l2 > 0 ? Math.Clamp(((px - ux) * ex + (py - uy) * ey) / l2, 0.0, 1.0) : 0.0;
            var (fx, fy) = (ux + t * ex - px, uy + t * ey - py);
            return Math.Sqrt(fx * fx + fy * fy);
        }
        return Math.Min(Math.Min(PointSeg(ax, ay, cx, cy, dx, dy), PointSeg(bx, by, cx, cy, dx, dy)),
                        Math.Min(PointSeg(cx, cy, ax, ay, bx, by), PointSeg(dx, dy, ax, ay, bx, by)));
    }

    /// <summary>碰墊接觸在船舯之前或之後:記下本次靠泊艏端 / 艉端已靠上(繫妥判定用)。</summary>
    private void MarkLanded(string fenderId)
    {
        if (_engine.External.Berth is not { } berth) return;
        foreach (var f in berth.Fenders)
        {
            if (f.Id != fenderId) continue;
            if (berth.Segments[f.Segment].Chainage + f.S >= MidshipChainage(berth)) State.FwdLanded = true;
            else State.AftLanded = true;
            return;
        }
    }

    /// <summary>
    /// 第一次觀察泊位時(第一個泊位事件或評估步):接觸中的碰墊視為「已接觸、未離開」(情境開始即靠在碰墊上),
    /// 須先離開 ≥ <see cref="LandingClearanceM"/> 其下一次接觸才是接近接觸。
    /// </summary>
    private void EnsureFenderState()
    {
        var st = State;
        if (st.FendersObserved || _engine.External.Berth is not { } berth) return;
        st.FendersObserved = true;
        foreach (var f in berth.Fenders)
            if (f.InContact)
            {
                if (!st.HeldFenders.Contains(f.Id)) st.HeldFenders.Add(f.Id);
                MarkLanded(f.Id);
            }
    }

    /// <summary>各碰墊與船體的距離(m;碰墊面到船體,負 = 壓縮;船體不在碰墊帶內時為 +∞)。只讀,不改物理狀態。</summary>
    private double[] FenderClearances(BerthModel berth)
    {
        Span<(double E, double N)> hull = stackalloc (double, double)[berth.Hull.Length];
        berth.HullEnu(_engine.Motion, hull);
        var clear = new double[berth.Fenders.Count];
        for (var i = 0; i < clear.Length; i++)
        {
            berth.FenderGeometry(berth.Fenders[i], hull, out var delta, out _, out _);
            clear[i] = double.IsNaN(delta) ? double.PositiveInfinity : -delta;
        }
        return clear;
    }

    /// <summary>
    /// 繫妥狀態(每秒評估步):所有已設定的纜繩皆受力,且本次靠泊船舯前、後的碰墊都已有接觸事件(艏艉皆已靠上;不需同時接觸)
    /// → 繫妥(記下時刻、船舯里程與泊位中心偏差;每次由未繫妥轉為繫妥時更新);無任何纜繩受力 → 解除。
    /// 先繫纜再絞靠、或靠上一端後另一端才靠上時,兩端靠上的接觸仍是接近接觸;之後在纜繩上的擺盪不是。
    /// </summary>
    private void UpdateAllFast(double t)
    {
        var st = State;
        if (_engine.External.Berth is not { Lines.Count: > 0 } berth) return;
        if (st.AllFast)
        {
            if (!berth.Lines.Any(l => l.Loaded)) st.AllFast = false;
            return;
        }
        if (!st.FwdLanded || !st.AftLanded || !berth.Lines.All(l => l.Loaded)) return;
        var chain = MidshipChainage(berth);
        st.AllFast = true;
        var centre = BerthCentreChainage(berth);
        st.AllFastAtS = t;
        st.AllFastMidshipChainageM = chain;
        st.AllFastPositionErrorM = chain - centre;
    }

    /// <summary>船舯沿碼頭的里程(m;最近碼頭線段的里程起點 + 投影,不夾限:泊位端外為負或大於全長)。</summary>
    private double MidshipChainage(BerthModel berth)
    {
        var s = _engine.Motion;
        var q = NearestSegment(berth, s.X, s.Y);
        return q.Chainage + (s.X - q.Ax) * q.Tx + (s.Y - q.Ay) * q.Ty;
    }

    /// <summary>泊位中心里程(m):碰墊列首尾的中點(無碰墊時為碼頭線中點)。</summary>
    public static double BerthCentreChainage(BerthModel berth)
    {
        if (berth.Fenders.Count == 0)
        {
            var last = berth.Segments[^1];
            return 0.5 * (last.Chainage + last.Length);
        }
        var lo = double.PositiveInfinity;
        var hi = double.NegativeInfinity;
        foreach (var f in berth.Fenders)
        {
            var c = berth.Segments[f.Segment].Chainage + f.S;
            lo = Math.Min(lo, c);
            hi = Math.Max(hi, c);
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>艏(+LOA/2)、舯、艉(−LOA/2)朝最近碼頭線段的對地速度(m/s;正 = 朝碼頭)。</summary>
    private (double Bow, double Mid, double Stern) ApproachSpeeds(BerthModel berth)
    {
        var s = _engine.Motion;
        var env = _engine.Environment.SampleAt(s.X, s.Y);
        var q = NearestSegment(berth, s.X, s.Y);
        double Toward(double xb)
        {
            var (ve, vn) = BodyFrame.PointGroundVelocity(s, env, xb, 0.0);
            return -(ve * q.Nx + vn * q.Ny);
        }
        return (Toward(_halfLoa), Toward(0.0), Toward(-_halfLoa));
    }

    private static QuaySegment NearestSegment(BerthModel berth, double x, double y)
    {
        var best = berth.Segments[0];
        var bestD = double.PositiveInfinity;
        foreach (var q in berth.Segments)
        {
            var sa = Math.Clamp((x - q.Ax) * q.Tx + (y - q.Ay) * q.Ty, 0.0, q.Length);
            var dx = x - (q.Ax + sa * q.Tx);
            var dy = y - (q.Ay + sa * q.Ty);
            var d = dx * dx + dy * dy;
            if (d < bestD) (best, bestD) = (q, d);
        }
        return best;
    }

    private double GroundSpeedMps()
    {
        var m = _engine.Motion;
        var env = _engine.Environment.SampleAt(m.X, m.Y);
        var (ve, vn) = EnvironmentMath.GroundVelocity(m.U, m.V, m.Psi, env.CurrentEastMps, env.CurrentNorthMps);
        return Math.Sqrt(ve * ve + vn * vn);
    }

    private double WaterDepthM()
    {
        var m = _engine.Motion;
        return _engine.Environment.SampleAt(m.X, m.Y).WaterDepthM;
    }

    private MooringLineMetric Line(string id)
    {
        var l = State.Lines.FirstOrDefault(x => x.LineId == id);
        if (l is null) State.Lines.Add(l = new MooringLineMetric { LineId = id });
        return l;
    }

    private TugMetric Tug(string id)
    {
        var t = State.Tugs.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (t is null) State.Tugs.Add(t = new TugMetric { Id = id });
        return t;
    }

    private AnchorMetric Anchor(string side)
    {
        var a = State.Anchors.FirstOrDefault(x => string.Equals(x.Side, side, StringComparison.OrdinalIgnoreCase));
        if (a is null) State.Anchors.Add(a = new AnchorMetric { Side = side.ToLowerInvariant() });
        return a;
    }

    // ------------------------------------------------------------------ 指標與摘要

    /// <summary>
    /// 靠泊橫向接近速度(規劃書第 8.3 節第 4 類):所有接近接觸(未繫妥、離開 ≥ <see cref="LandingClearanceM"/> 後的碰墊接觸與碼頭硬接觸)
    /// 接觸點法向速度的最大值;無接近接觸時為 null。艏/舯/艉端點速度(landings[])不併入。
    /// </summary>
    public double? MaxLateralSpeedMps() => State.MaxApproachSpeedMps;

    private double? MaxOf<T>(IEnumerable<T> items, Func<T, double?> f)
    {
        double? best = null;
        foreach (var i in items) if (f(i) is { } v && (best is null || v > best)) best = v;
        return best;
    }

    private double? MinOf<T>(IEnumerable<T> items, Func<T, double?> f)
    {
        double? best = null;
        foreach (var i in items) if (f(i) is { } v && (best is null || v < best)) best = v;
        return best;
    }

    /// <summary>berthing.* / anchoring.* 指標的目前值(名稱見 <see cref="MetricNameList"/>)。</summary>
    public double? Metric(string name, double time)
    {
        var st = State;
        switch (name)
        {
            case "berthing.maxLateralSpeed_mps": return MaxLateralSpeedMps();
            case "berthing.maxLateralSpeed_kn": return MaxLateralSpeedMps() is { } v ? Units.MpsToKn(v) : null;
            case "berthing.maxContactSpeed_mps": return MaxOf(st.Contacts, c => c.NormalSpeedMps);
            // 已離開泊位後再碰回(碰墊/碼頭)的接觸點法向速度最大值;未再碰回時 null
            case "berthing.maxReContactSpeed_mps": return st.MaxReContactSpeedMps;
            case "berthing.maxEnergy_kNm": return MaxOf(st.Contacts, c => c.EnergyKNm);
            case "berthing.maxEnergyRatio": return MaxOf(st.Contacts, c => c.EnergyRatio);
            case "berthing.fenderContacts": return st.Contacts.Count;
            case "berthing.fenderOverloads": return st.Contacts.Count(c => c.Overload);
            case "berthing.quayContacts": return st.QuayContacts;
            case "berthing.lineBreaks": return st.LineBreaks;
            case "berthing.maxLineTensionRatio": return MaxOf(st.Lines, l => l.MblT > 0 ? l.MaxTensionT / l.MblT : null);
            case "berthing.tugLineBreaks": return st.TugLineBreaks;
            case "berthing.maxTugForce_t": return MaxOf(st.Tugs, t => t.MaxForceT);
            case "berthing.tugWorkingTime_s": return st.Tugs.Sum(t => t.WorkingTimeS);
            // 繫妥時船舯與泊位中心(碰墊列中點)的距離;尚未繫妥時為 null
            case "berthing.allFastPositionError_m": return st.AllFastPositionErrorM is { } pe ? Math.Abs(pe) : null;
            case "anchoring.letGoCount": return st.Anchors.Sum(a => a.LetGoCount);
            case "anchoring.maxLetGoSog_kn": return MaxOf(st.Anchors, a => a.MaxLetGoSogKn);
            case "anchoring.dragEvents": return st.Anchors.Sum(a => a.DragEvents);
            case "anchoring.dragTime_s": return st.Anchors.Sum(a => a.DragTimeS);
            case "anchoring.maxChainTension_t": return MaxOf(st.Anchors, a => a.MaxTensionT > 0 ? a.MaxTensionT : null);
            case "anchoring.minScopeRatio": return MinOf(st.Anchors, a => a.ScopeRatio);
            // 尚未反應時回傳自首次走錨起的經過時間(容許帶可在無反應時逾時判定)
            case "anchoring.dragResponseTime_s":
                return st.FirstDragAtS is { } fd ? (st.DragResponseAtS ?? time) - fd : null;
            default: return null;
        }
    }

    /// <summary>容許帶可引用的靠泊/錨泊指標(MetricNames)。</summary>
    public static readonly string[] MetricNameList =
    {
        "berthing.maxLateralSpeed_mps", "berthing.maxLateralSpeed_kn", "berthing.maxContactSpeed_mps", "berthing.maxEnergy_kNm", "berthing.maxEnergyRatio",
        "berthing.fenderContacts", "berthing.fenderOverloads", "berthing.quayContacts", "berthing.lineBreaks", "berthing.maxLineTensionRatio",
        "berthing.tugLineBreaks", "berthing.maxTugForce_t", "berthing.tugWorkingTime_s", "berthing.allFastPositionError_m",
        "berthing.maxReContactSpeed_mps",
        "anchoring.letGoCount", "anchoring.maxLetGoSog_kn", "anchoring.dragEvents", "anchoring.dragTime_s", "anchoring.maxChainTension_t",
        "anchoring.minScopeRatio", "anchoring.dragResponseTime_s",
    };

    public SummaryBerthing? BerthingSummary()
    {
        if (!BerthingEnabled) return null;
        var st = State;
        var max = MaxLateralSpeedMps();
        return new SummaryBerthing
        {
            BerthId = _scenario.Berth?.Definition?.Id ?? _scenario.Berth?.Id,
            MaxLateralSpeedMps = max, MaxLateralSpeedKn = max is { } m ? Units.MpsToKn(m) : null,
            MaxContactSpeedMps = MaxOf(st.Contacts, c => c.NormalSpeedMps),
            DepartedAtS = st.DepartedAtS, ReContacts = st.ReContacts > 0 ? st.ReContacts : null, MaxReContactSpeedMps = st.MaxReContactSpeedMps,
            MaxEnergyKNm = MaxOf(st.Contacts, c => c.EnergyKNm), MaxEnergyRatio = MaxOf(st.Contacts, c => c.EnergyRatio),
            FenderContacts = st.Contacts.Count, FenderOverloads = st.Contacts.Count(c => c.Overload),
            QuayContacts = st.QuayContacts, QuayMaxPenetrationM = st.QuayContacts > 0 ? st.QuayMaxPenetrationM : null, QuayMaxSpeedMps = st.QuayMaxSpeedMps,
            LineBreaks = st.LineBreaks, MaxLineTensionRatio = MaxOf(st.Lines, l => l.MblT > 0 ? l.MaxTensionT / l.MblT : null),
            TugLineBreaks = st.TugLineBreaks, TugWorkingTimeS = st.Tugs.Sum(t => t.WorkingTimeS), MaxTugForceT = MaxOf(st.Tugs, t => t.MaxForceT),
            BerthCentreChainageM = _engine.External.Berth is { } bm ? BerthCentreChainage(bm) : null,
            AllFastAtS = st.AllFastAtS, AllFastMidshipChainageM = st.AllFastMidshipChainageM, AllFastPositionErrorM = st.AllFastPositionErrorM,
            Landings = st.Landings.Select(l => l with { }).ToArray(),
            Contacts = st.Contacts.Select(c => c with { }).ToArray(),
            Lines = st.Lines.Select(l => new MooringLineMetric
            {
                LineId = l.LineId, MaxTensionT = l.MaxTensionT, MblT = l.MblT, Parted = l.Parted, PartedAtS = l.PartedAtS,
                MaxTensionRatio = l.MblT > 0 ? l.MaxTensionT / l.MblT : null,
            }).ToArray(),
            Tugs = st.Tugs.Select(t => new TugMetric
            {
                Id = t.Id, WorkingTimeS = t.WorkingTimeS, MaxForceT = t.MaxForceT, BollardPullT = t.BollardPullT, LineParted = t.LineParted, LinePartedAtS = t.LinePartedAtS,
            }).ToArray(),
        };
    }

    public SummaryAnchoring? AnchoringSummary(double time)
    {
        if (!AnchoringEnabled) return null;
        var st = State;
        return new SummaryAnchoring
        {
            LetGoCount = st.Anchors.Sum(a => a.LetGoCount),
            MaxLetGoSogKn = MaxOf(st.Anchors, a => a.MaxLetGoSogKn),
            DragEvents = st.Anchors.Sum(a => a.DragEvents),
            DragTimeS = st.Anchors.Sum(a => a.DragTimeS),
            MaxChainTensionT = MaxOf(st.Anchors, a => a.MaxTensionT > 0 ? a.MaxTensionT : null),
            MinScopeRatio = MinOf(st.Anchors, a => a.ScopeRatio),
            FirstDragAtS = st.FirstDragAtS,
            DragDetectedAtS = st.DragDetectedAtS,
            DragResponseAtS = st.DragResponseAtS,
            DragResponseTimeS = st.FirstDragAtS is { } fd && st.DragResponseAtS is { } r ? r - fd : null,
            DragResponseBy = st.DragResponseBy,
            Anchors = st.Anchors.Select(a => a with { }).ToArray(),
        };
    }
}

// ------------------------------------------------------------------ 狀態(進評估快照)與摘要記錄

/// <summary>靠泊/錨泊追蹤器的可變狀態(評估快照 <see cref="AssessmentSnapshot.Berthing"/>)。</summary>
public sealed class BerthingState
{
    /// <summary>接觸段進行中(上一取樣或事件時船體與碰墊/碼頭接觸)</summary>
    public bool InEpisode { get; set; }
    /// <summary>最近一次無接觸時的艏/舯/艉接近速度取樣(接觸段開始時取用後清除)</summary>
    public ApproachSample? PreSample { get; set; }
    /// <summary>已觀察過泊位(首次觀察時接觸中的碰墊列入 HeldFenders)</summary>
    public bool FendersObserved { get; set; }
    /// <summary>已接觸、尚未離開 ≥ LandingClearanceM 的碰墊 id(其下一次接觸不是接近接觸;依接觸順序)</summary>
    public List<string> HeldFenders { get; set; } = new();
    /// <summary>接近接觸的接觸點法向速度最大值(berthing.maxLateralSpeed)</summary>
    public double? MaxApproachSpeedMps { get; set; }
    /// <summary>本次靠泊船舯前 / 後的碰墊已有接觸(繫妥判定;離開所有碰墊 ≥ LandingClearanceM、未繫妥且纜繩未全部受力時清除)</summary>
    public bool FwdLanded { get; set; }
    public bool AftLanded { get; set; }
    /// <summary>繫妥中(所有纜繩受力、艏艉皆已靠上碰墊);無纜受力時解除</summary>
    public bool AllFast { get; set; }
    public double? AllFastAtS { get; set; }
    public double? AllFastMidshipChainageM { get; set; }
    /// <summary>繫妥時船舯里程 − 泊位中心里程(m;正 = 沿碼頭線方向超前)</summary>
    public double? AllFastPositionErrorM { get; set; }
    public List<BerthingLanding> Landings { get; set; } = new();
    public List<FenderContactMetric> Contacts { get; set; } = new();
    public int QuayContacts { get; set; }
    public List<double> QuayContactTimes { get; set; } = new();
    public double QuayMaxPenetrationM { get; set; }
    public double? QuayMaxSpeedMps { get; set; }
    public int LineBreaks { get; set; }
    public List<MooringLineMetric> Lines { get; set; } = new();
    public int TugLineBreaks { get; set; }
    public List<TugMetric> Tugs { get; set; } = new();
    public bool AnchorsUsed { get; set; }
    public List<AnchorMetric> Anchors { get; set; } = new();
    public double? FirstDragAtS { get; set; }
    public double? DragDetectedAtS { get; set; }
    public double? DragResponseAtS { get; set; }
    public string? DragResponseBy { get; set; }
    /// <summary>已離開泊位(曾接觸後所有碰墊壓縮為 0、未繫妥、船體距碼頭線 &gt; DepartedDistanceM;再次繫妥時解除)。預設值不寫出(舊快照格式不變)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Departed { get; set; }
    /// <summary>第一次成立「已離開泊位」的時刻</summary>
    public double? DepartedAtS { get; set; }
    /// <summary>已離開泊位後的再碰回次數(碰墊 + 碼頭)與接觸點法向速度最大值(berthing.maxReContactSpeed_mps)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ReContacts { get; set; }
    public double? MaxReContactSpeedMps { get; set; }
}

public sealed record ApproachSample
{
    public double TS { get; init; }
    public double BowMps { get; init; }
    public double MidshipMps { get; init; }
    public double SternMps { get; init; }
}

/// <summary>
/// 一次靠上(第一次接觸,或未繫妥且離開碰墊 ≥ 0.5 m 後再接觸):艏/舯/艉朝碼頭的對地速度(接觸前最後一個每秒取樣,僅供講評;
/// initial = 情境開始即接觸,無接近速度);contactSpeed = 此次靠上接近接觸點法向速度的最大值。
/// </summary>
public sealed record BerthingLanding
{
    [JsonPropertyName("t_s")] public double TS { get; init; }
    [JsonPropertyName("sampleAt_s")] public double? SampleAtS { get; init; }
    [JsonPropertyName("initial")] public bool Initial { get; init; }
    [JsonPropertyName("bow_mps")] public double? BowMps { get; init; }
    [JsonPropertyName("bow_kn")] public double? BowKn { get; init; }
    [JsonPropertyName("midship_mps")] public double? MidshipMps { get; init; }
    [JsonPropertyName("midship_kn")] public double? MidshipKn { get; init; }
    [JsonPropertyName("stern_mps")] public double? SternMps { get; init; }
    [JsonPropertyName("stern_kn")] public double? SternKn { get; init; }
    [JsonPropertyName("max_mps")] public double? MaxMps { get; init; }
    [JsonPropertyName("max_kn")] public double? MaxKn { get; init; }
    [JsonPropertyName("contactSpeed_mps")] public double? ContactSpeedMps { get; init; }
    [JsonPropertyName("contactSpeed_kn")] public double? ContactSpeedKn { get; init; }
}

/// <summary>一次碰墊接觸(fenderContact 事件):接觸點法向接近速度、能量與額定能量比、過載。</summary>
public sealed record FenderContactMetric
{
    [JsonPropertyName("fenderId")] public string FenderId { get; init; } = "";
    [JsonPropertyName("t_s")] public double TS { get; init; }
    /// <summary>接近接觸(未繫妥,且此碰墊自上次接觸以來曾離開 ≥ 0.5 m);false = 靠妥後輕碰 / 繫泊中擺盪 / 反彈未離開</summary>
    [JsonPropertyName("approach")] public bool Approach { get; init; }
    [JsonPropertyName("normalSpeed_mps")] public double NormalSpeedMps { get; init; }
    [JsonPropertyName("normalSpeed_kn")] public double NormalSpeedKn { get; init; }
    [JsonPropertyName("energy_kNm")] public double EnergyKNm { get; init; }
    [JsonPropertyName("ratedEnergy_kNm")] public double? RatedEnergyKNm { get; init; }
    [JsonPropertyName("energyRatio")] public double? EnergyRatio { get; init; }
    [JsonPropertyName("maxReaction_kN")] public double? MaxReactionKN { get; init; }
    [JsonPropertyName("deflection_pct")] public double? DeflectionPct { get; init; }
    [JsonPropertyName("overload")] public bool Overload { get; init; }
    /// <summary>已離開泊位後再碰回(berthing.maxReContactSpeed_mps;false 時不寫出)</summary>
    [JsonPropertyName("reContact"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool ReContact { get; init; }
}

public sealed class MooringLineMetric
{
    [JsonPropertyName("lineId")] public string LineId { get; set; } = "";
    [JsonPropertyName("maxTension_t")] public double MaxTensionT { get; set; }
    [JsonPropertyName("mbl_t")] public double MblT { get; set; }
    [JsonPropertyName("maxTensionRatio")] public double? MaxTensionRatio { get; set; }
    [JsonPropertyName("parted")] public bool Parted { get; set; }
    [JsonPropertyName("partedAt_s")] public double? PartedAtS { get; set; }
}

public sealed class TugMetric
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>status working 的累計秒數(每秒取樣)</summary>
    [JsonPropertyName("workingTime_s")] public double WorkingTimeS { get; set; }
    [JsonPropertyName("maxForce_t")] public double MaxForceT { get; set; }
    [JsonPropertyName("bollardPull_t")] public double BollardPullT { get; set; }
    [JsonPropertyName("lineParted")] public bool LineParted { get; set; }
    [JsonPropertyName("linePartedAt_s")] public double? LinePartedAtS { get; set; }
}

public sealed record AnchorMetric
{
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("letGoCount")] public int LetGoCount { get; set; }
    [JsonPropertyName("letGoAt_s")] public double? LetGoAtS { get; set; }
    /// <summary>最近一次 letGo 時的對地速度</summary>
    [JsonPropertyName("letGoSog_mps")] public double? LetGoSogMps { get; set; }
    [JsonPropertyName("letGoSog_kn")] public double? LetGoSogKn { get; set; }
    [JsonPropertyName("maxLetGoSog_kn")] public double? MaxLetGoSogKn { get; set; }
    /// <summary>letGo 指定的目標鏈長(省略 = 鏈端時 null)與預定 scope(目標鏈長 / 水深)</summary>
    [JsonPropertyName("targetLength_m")] public double? TargetLengthM { get; set; }
    [JsonPropertyName("plannedScopeRatio")] public double? PlannedScopeRatio { get; set; }
    [JsonPropertyName("waterDepth_m")] public double? WaterDepthM { get; set; }
    [JsonPropertyName("broughtUpAt_s")] public double? BroughtUpAtS { get; set; }
    [JsonPropertyName("broughtUpLength_m")] public double? BroughtUpLengthM { get; set; }
    [JsonPropertyName("broughtUpShackles")] public double? BroughtUpShackles { get; set; }
    /// <summary>帶上時鏈長 / 水深(鏈長選擇;與參數集倍數比較)</summary>
    [JsonPropertyName("scopeRatio")] public double? ScopeRatio { get; set; }
    [JsonPropertyName("maxLength_m")] public double MaxLengthM { get; set; }
    [JsonPropertyName("maxTension_t")] public double MaxTensionT { get; set; }
    [JsonPropertyName("dragEvents")] public int DragEvents { get; set; }
    [JsonPropertyName("firstDragAt_s")] public double? FirstDragAtS { get; set; }
    [JsonPropertyName("dragTime_s")] public double DragTimeS { get; set; }
    [JsonPropertyName("brakeSlips")] public int BrakeSlips { get; set; }
}

/// <summary>summary.berthing(情境有泊位或拖船時輸出)。</summary>
public sealed record SummaryBerthing
{
    [JsonPropertyName("berthId")] public string? BerthId { get; init; }
    /// <summary>靠泊橫向接近速度:接近接觸(contacts[].approach 與未繫妥時的碼頭硬接觸)接觸點法向速度的最大值;無接近接觸時 null</summary>
    [JsonPropertyName("maxLateralSpeed_mps")] public double? MaxLateralSpeedMps { get; init; }
    [JsonPropertyName("maxLateralSpeed_kn")] public double? MaxLateralSpeedKn { get; init; }
    [JsonPropertyName("maxContactSpeed_mps")] public double? MaxContactSpeedMps { get; init; }
    /// <summary>第一次「已離開泊位」(曾接觸後所有碰墊壓縮為 0、未繫妥、船體距碼頭線 &gt; departedDistance_m)的時刻;未離開時 null</summary>
    [JsonPropertyName("departedAt_s")] public double? DepartedAtS { get; init; }
    /// <summary>已離開泊位後再碰回的次數(碰墊 + 碼頭;無時 null)與接觸點法向速度最大值(berthing.maxReContactSpeed_mps)</summary>
    [JsonPropertyName("reContacts")] public int? ReContacts { get; init; }
    [JsonPropertyName("maxReContactSpeed_mps")] public double? MaxReContactSpeedMps { get; init; }
    [JsonPropertyName("maxEnergy_kNm")] public double? MaxEnergyKNm { get; init; }
    [JsonPropertyName("maxEnergyRatio")] public double? MaxEnergyRatio { get; init; }
    [JsonPropertyName("fenderContacts")] public int FenderContacts { get; init; }
    [JsonPropertyName("fenderOverloads")] public int FenderOverloads { get; init; }
    [JsonPropertyName("quayContacts")] public int QuayContacts { get; init; }
    [JsonPropertyName("quayMaxPenetration_m")] public double? QuayMaxPenetrationM { get; init; }
    [JsonPropertyName("quayMaxSpeed_mps")] public double? QuayMaxSpeedMps { get; init; }
    [JsonPropertyName("lineBreaks")] public int LineBreaks { get; init; }
    [JsonPropertyName("maxLineTensionRatio")] public double? MaxLineTensionRatio { get; init; }
    [JsonPropertyName("tugLineBreaks")] public int TugLineBreaks { get; init; }
    [JsonPropertyName("tugWorkingTime_s")] public double TugWorkingTimeS { get; init; }
    [JsonPropertyName("maxTugForce_t")] public double? MaxTugForceT { get; init; }
    /// <summary>泊位中心里程(碰墊列中點,m)</summary>
    [JsonPropertyName("berthCentreChainage_m")] public double? BerthCentreChainageM { get; init; }
    /// <summary>最近一次繫妥(所有纜繩受力、艏艉皆靠上碰墊)的時刻、船舯里程與泊位中心偏差(正 = 沿碼頭線方向超前);未繫妥時 null</summary>
    [JsonPropertyName("allFastAt_s")] public double? AllFastAtS { get; init; }
    [JsonPropertyName("allFastMidshipChainage_m")] public double? AllFastMidshipChainageM { get; init; }
    [JsonPropertyName("allFastPositionError_m")] public double? AllFastPositionErrorM { get; init; }
    [JsonPropertyName("landings")] public required IReadOnlyList<BerthingLanding> Landings { get; init; }
    [JsonPropertyName("contacts")] public required IReadOnlyList<FenderContactMetric> Contacts { get; init; }
    [JsonPropertyName("lines")] public required IReadOnlyList<MooringLineMetric> Lines { get; init; }
    [JsonPropertyName("tugs")] public required IReadOnlyList<TugMetric> Tugs { get; init; }
}

/// <summary>summary.anchoring(情境有 anchors 區段或錨曾被使用時輸出)。</summary>
public sealed record SummaryAnchoring
{
    [JsonPropertyName("letGoCount")] public int LetGoCount { get; init; }
    [JsonPropertyName("maxLetGoSog_kn")] public double? MaxLetGoSogKn { get; init; }
    [JsonPropertyName("dragEvents")] public int DragEvents { get; init; }
    [JsonPropertyName("dragTime_s")] public double DragTimeS { get; init; }
    [JsonPropertyName("maxChainTension_t")] public double? MaxChainTensionT { get; init; }
    [JsonPropertyName("minScopeRatio")] public double? MinScopeRatio { get; init; }
    [JsonPropertyName("firstDragAt_s")] public double? FirstDragAtS { get; init; }
    /// <summary>首次走錨後第一次 acknowledge(偵測/回報)</summary>
    [JsonPropertyName("dragDetectedAt_s")] public double? DragDetectedAtS { get; init; }
    /// <summary>首次走錨後第一個學員反應(車令/舵令/側推/操舵模式/錨指令)</summary>
    [JsonPropertyName("dragResponseAt_s")] public double? DragResponseAtS { get; init; }
    [JsonPropertyName("dragResponseTime_s")] public double? DragResponseTimeS { get; init; }
    [JsonPropertyName("dragResponseBy")] public string? DragResponseBy { get; init; }
    [JsonPropertyName("anchors")] public required IReadOnlyList<AnchorMetric> Anchors { get; init; }
}
