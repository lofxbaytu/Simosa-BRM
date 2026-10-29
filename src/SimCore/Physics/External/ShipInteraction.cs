using System.Buffers.Binary;
using System.Text.Json.Serialization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.SimCore.Traffic;

namespace SimosaBRM.SimCore.Physics.External;

/// <summary>
/// 船間交互作用的係數與離散化參數(設計文件 docs/design/physics-ship-interaction.md 第 3、4 節)。
/// 數值為文獻量級的估計值(兩船無船間交互作用模型試驗;離線開發),集中於此以便校正;C# 與 Python 參考實作(external.py SHIP_INTERACTION_DEFAULTS)相同。
/// </summary>
public sealed record ShipInteractionCoefficients
{
    /// <summary>每艘船沿船長等分的站數(中點法;源強度與剖面積都在站上取值)</summary>
    public int Stations { get; init; } = 16;
    /// <summary>有限水深鏡像數(每側;海底與剛性自由面的鏡像 z = ±2kh,k = 1..Images,其餘以積分尾項補足)</summary>
    public int Images { get; init; } = 6;
    /// <summary>水深 ≥ 此值(或 ≤ 0、無限大)視為深水,只取 k = 0 項(m)</summary>
    public double DeepDepthM { get; init; } = 1000.0;
    /// <summary>海水密度(kg/m³)</summary>
    public double Rho { get; init; } = 1025.0;
    /// <summary>船舯剖面係數 Cm(Cp = Cb/Cm 決定進流/去流段長度)</summary>
    public double MidshipCoefficient { get; init; } = 0.98;
    /// <summary>橫向附加質量比 k_a = m_a/(ρ S):壓力梯度項乘 (1 + k_a)(Lewis 剖面 0.6–1.0)</summary>
    public double SwayAddedMassRatio { get; init; } = 0.8;
    /// <summary>核距離 δ = CoreFactor·(B_o + B_t):線源核 1/(r² + δ²)^{3/2},避免船體相觸時發散</summary>
    public double CoreFactor { get; init; } = 0.25;
    /// <summary>縱向作用範圍 |x_t − x_o| &lt; RangeLengths·L_o</summary>
    public double RangeLengths { get; init; } = 3.0;
    /// <summary>橫向作用範圍 |y| &lt; LateralBeams·(B_o + B_t)</summary>
    public double LateralBeams { get; init; } = 2.0;
    /// <summary>範圍內自 TaperStart·限值起以平滑步階(smoothstep)漸減到限值處為 0,避免力在邊界跳變</summary>
    public double TaperStart { get; init; } = 0.8;
    /// <summary>事件離開門檻 = EventHysteresis × 進入門檻</summary>
    public double EventHysteresis { get; init; } = 0.5;
    /// <summary>剖面積曲線的方塊係數夾限(由排水量反推時)</summary>
    public double MinCb { get; init; } = 0.35;
    public double MaxCb { get; init; } = 0.95;

    public static ShipInteractionCoefficients Default { get; } = new();
}

/// <summary>
/// 船間交互作用看到的目標船(運動學目標;C# 由 <see cref="TargetShip"/> 組出,Python 參考實作為直線等速目標):
/// ENU 位置(船舯)、航向(弧度)、對水速度(m/s,沿航向)、船長/船寬/吃水(m)與方塊係數。
/// </summary>
public readonly record struct InteractionTarget(string Id, double X, double Y, double PsiRad, double SpeedMps, double LengthM, double BeamM, double DraftM, double Cb)
{
    /// <summary>由交通模組的目標船組出(船長 = loa、船寬/吃水 = 情境值、Cb 由 displacement_t 反推或依船型預設;設計文件第 2.2 節)。</summary>
    public static InteractionTarget From(TargetShip t, double rho)
        => new(t.Id, t.X, t.Y, t.Psi, t.Speed, t.Spec.Loa, t.Spec.Beam, TargetHullDefaults.Draft(t.Spec), TargetHullDefaults.BlockCoefficient(t.Spec, rho));
}

/// <summary>目標船船體資料的預設(情境 targets[] 只有 loa/beam/draft/shipType;設計文件 physics-ship-interaction.md 第 2.2 節)。</summary>
public static class TargetHullDefaults
{
    /// <summary>依船型的方塊係數預設(無 displacement_t 時;與 Python TARGET_CB_DEFAULTS 相同)。</summary>
    public static double DefaultCb(string? shipType) => (shipType ?? "cargo").ToLowerInvariant() switch
    {
        "tanker" => 0.82,
        "cargo" => 0.72,
        "passenger" => 0.62,
        "fishing" => 0.55,
        "tug" => 0.55,
        "pilot" => 0.50,
        "sar" => 0.50,
        "pleasure" => 0.45,
        "hsc" => 0.45,
        "sailing" => 0.40,
        _ => 0.65,
    };

    /// <summary>吃水:情境值(&gt; 0),否則 0.3·B(最少 0.5 m)。</summary>
    public static double Draft(ScenarioTarget spec) => spec.Draft > 0.0 ? spec.Draft : Math.Max(0.5, 0.3 * spec.Beam);

    /// <summary>方塊係數:displacement_t 給定時 Δ/(ρ·L·B·T) 夾在 [0.35, 0.95],否則依船型預設。</summary>
    public static double BlockCoefficient(ScenarioTarget spec, double rho, double minCb = 0.35, double maxCb = 0.95)
    {
        if (spec.DisplacementT is { } disp && disp > 0.0)
        {
            var vol = spec.Loa * spec.Beam * Draft(spec);
            if (vol > 0.0) return Units.Clamp(disp * 1000.0 / (rho * vol), minCb, maxCb);
        }
        return DefaultCb(spec.ShipType);
    }
}

/// <summary>
/// 一次交互作用計算的結果(船體座標;N、N·m):合力、是否有目標在作用範圍內、主要目標(等效側向力最大者)及其縱向錯位 ξ 與橫向間距 η(以自船 L 無因次)。
/// </summary>
public readonly record struct ShipInteractionResult(double X, double Y, double N, bool Active, string? TargetId, double Xi, double Eta, int Count)
{
    public static readonly ShipInteractionResult Zero = new(0.0, 0.0, 0.0, false, null, 0.0, 0.0, 0);
    public BodyForce Force => new(X, Y, N);
}

/// <summary>
/// 船間交互作用(規劃書第 6.2 節、R2「船間交互作用」;設計文件 docs/design/physics-ship-interaction.md):
/// 目標船通過/追越自船時的流體交互作用力(surge/sway/yaw)。Tuck–Newman(1974)型細長體理論的經驗化:兩船各以沿中心線的線源
/// σ(x) = −U·S'(x)(艏為源、艉為匯)代表,雙體(鏡像自由面)在有限水深以海底鏡像級數求目標船在自船中心線上的誘導速度場;
/// 自船受力 = Lagally 源項(−ρ·σ_o·u_ext,∝ U_o·U_t)+ 壓力梯度/附加質量項(ρ(1 + k_a)S_o·a_ext,a_ext 為目標船定常壓力場的流體加速度,∝ U_t²)。
/// 單向:目標船為運動學目標,不受自船影響。確定性:純函數、無亂數;RK4 各階段以該階段的自船狀態與步首的目標船狀態計算。
/// 情境 environment.shipInteraction.enabled 且動力學接受外力時啟用(<see cref="Active"/>);未啟用的情境零力、輸出與雜湊不變。
/// </summary>
public sealed class ShipInteractionModel : IExternalForceField
{
    public const long HashMarker = 0x53484950_494E5452; // "SHIPINTR"
    public const int HashLongs = 7;
    public const double TonneForce = 9806.65;
    private const double TwoPi = 2.0 * Math.PI;

    private readonly HullProfile _own;
    private InteractionTarget[] _targets = Array.Empty<InteractionTarget>();
    /// <summary>
    /// 目標船剖面積曲線快取,鍵 (L, B, T, Cb)(目標船尺寸在練習中不變;只省去每次 Compute 重建 <see cref="HullProfile"/> 的配置,
    /// 同鍵回傳同一實例、數值與每次重建逐位元相同;不進快照與雜湊)。上限 <see cref="MaxCachedTargetProfiles"/>,超過即清空重建。
    /// </summary>
    private readonly Dictionary<(double L, double B, double T, double Cb), HullProfile> _targetProfiles = new();
    public const int MaxCachedTargetProfiles = 64;

    public ShipInteractionCoefficients Coefficients { get; }
    public bool Supported { get; }
    public bool Enabled { get; }
    public bool Active => Supported && Enabled;
    /// <summary>整體倍率(情境 coefficientScale,預設 1;0 = 只顯示不施力)</summary>
    public double Scale { get; }
    /// <summary>事件進入門檻(N;情境 eventThreshold_t × 9806.65)</summary>
    public double EventThresholdN { get; }
    public double L { get; }
    public double B { get; }
    public double T { get; }
    public double Cb { get; }
    /// <summary>進流/去流段長度(m)</summary>
    public double EntranceLengthM => _own.Le;
    /// <summary>船舯剖面積(m²;由排水體積與進流段長度反推)</summary>
    public double MidshipAreaM2 => _own.Am;
    /// <summary>最近一次步末計算(雜湊、state.shipInteraction 與事件;引擎於每步末(目標船推進後)與還原後更新)</summary>
    public ShipInteractionResult Last { get; private set; } = ShipInteractionResult.Zero;
    /// <summary>事件狀態:交互作用力已超過門檻(進入事件已發、離開事件未發;進快照與雜湊)</summary>
    public bool EventActive { get; private set; }
    /// <summary>進入事件的目標 id(離開事件沿用;進快照)</summary>
    public string? EventTargetId { get; private set; }
    /// <summary>本步看到的目標船(PreStep / UpdateLast 設定)</summary>
    public IReadOnlyList<InteractionTarget> Targets => _targets;

    public ShipInteractionModel(ShipParticulars ship, LoadingCondition loading, ScenarioShipInteraction cfg, bool supported, ShipInteractionCoefficients? coefficients = null)
    {
        Supported = supported;
        Enabled = cfg.Enabled;
        Coefficients = coefficients ?? ShipInteractionCoefficients.Default;
        Scale = cfg.CoefficientScale ?? 1.0;
        EventThresholdN = (cfg.EventThresholdT ?? ScenarioShipInteraction.DefaultEventThresholdT) * TonneForce;
        var lc = ship.GetLoading(loading);
        L = ship.Hull.LengthBetweenPerpendiculars_m;
        B = ship.Hull.Breadth_m;
        T = lc.DraftMean_m ?? lc.MaxDraft_m;
        Cb = lc.BlockCoefficient ?? 0.75;
        _own = HullProfile.Build(L, B, T, Cb, Coefficients);
    }

    /// <summary>情境有 environment.shipInteraction 且 enabled 時建立(否則 null)。</summary>
    public static ShipInteractionModel? Create(ShipParticulars ship, LoadingCondition loading, Scenario.Scenario sc, bool supported)
        => sc.Environment.ShipInteraction is { Enabled: true } cfg ? new ShipInteractionModel(ship, loading, cfg, supported) : null;

    // ------------------------------------------------------------------ 目標船

    /// <summary>步首:以目前(步首)的目標船狀態組出本步四個 RK4 階段共用的目標清單(出現中、非跟隨自船的護航目標)。</summary>
    public void PreStep(IReadOnlyList<TargetShip> targets) => _targets = Collect(targets);

    /// <summary>步末(目標船推進後)與還原後:重組目標清單並以步末狀態重算 <see cref="Last"/>;回傳事件轉換(進入/離開門檻)。</summary>
    public ShipInteractionTransition UpdateLast(in StateVector s, double waterDepth, IReadOnlyList<TargetShip> targets)
    {
        _targets = Collect(targets);
        Last = Compute(s, waterDepth, _targets);
        return UpdateEvent(Last);
    }

    /// <summary>還原快照後:只重算步末結果,不改事件狀態。</summary>
    public void Refresh(in StateVector s, double waterDepth, IReadOnlyList<TargetShip> targets)
    {
        _targets = Collect(targets);
        Last = Compute(s, waterDepth, _targets);
    }

    private InteractionTarget[] Collect(IReadOnlyList<TargetShip> targets)
    {
        var n = 0;
        for (var i = 0; i < targets.Count; i++) if (Eligible(targets[i])) n++;
        if (n == 0) return Array.Empty<InteractionTarget>();
        var list = new InteractionTarget[n];
        var k = 0;
        for (var i = 0; i < targets.Count; i++)
            if (Eligible(targets[i])) list[k++] = InteractionTarget.From(targets[i], Coefficients.Rho);
        return list;
    }

    /// <summary>納入計算的目標:已出現且不是跟隨自船的護航目標(引水船/拖船;拖船力由拖船模組計算)。</summary>
    public static bool Eligible(TargetShip t) => t.Active && !t.EscortsOwnShip;

    // ------------------------------------------------------------------ 事件

    /// <summary>等效側向力 max(|Y|, 4|N|/L)(N),用於門檻比較。</summary>
    public double EquivalentForceN(in ShipInteractionResult r) => Math.Max(Math.Abs(r.Y), 4.0 * Math.Abs(r.N) / L);

    private ShipInteractionTransition UpdateEvent(in ShipInteractionResult r)
    {
        var f = EquivalentForceN(r);
        if (!EventActive)
        {
            if (EventThresholdN > 0.0 && f >= EventThresholdN && r.TargetId is not null)
            {
                EventActive = true;
                EventTargetId = r.TargetId;
                return ShipInteractionTransition.Enter;
            }
            return ShipInteractionTransition.None;
        }
        if (f < Coefficients.EventHysteresis * EventThresholdN || r.TargetId is null)
        {
            EventActive = false;
            return ShipInteractionTransition.Exit;
        }
        return ShipInteractionTransition.None;
    }

    /// <summary>快照還原事件狀態。</summary>
    public void RestoreEvent(bool active, string? targetId)
    {
        EventActive = active;
        EventTargetId = targetId;
    }

    // ------------------------------------------------------------------ 力

    public BodyForce Evaluate(in StateVector state, in EnvironmentSample env) => Compute(state, env.WaterDepthM, _targets).Force;

    /// <summary>
    /// 交互作用力(設計文件第 3 節):對每艘在範圍內的目標船,以自船船體座標的目標位置 (dx, dy)、相對航向 β 與兩船線源計算
    /// Lagally 項與壓力梯度項,乘範圍漸減權重與 coefficientScale 後相加;主要目標為等效側向力最大者。
    /// </summary>
    public ShipInteractionResult Compute(in StateVector s, double waterDepth, ReadOnlySpan<InteractionTarget> targets)
    {
        var c = Coefficients;
        double tx = 0.0, ty = 0.0, tn = 0.0;
        var count = 0;
        string? best = null;
        var bestF = -1.0;
        double bestXi = 0.0, bestEta = 0.0;
        var xl = c.RangeLengths * L;
        var h = waterDepth;
        var deep = !(h > 0.0) || double.IsInfinity(h) || h >= c.DeepDepthM;
        var sn = Math.Sin(s.Psi);
        var cs = Math.Cos(s.Psi);
        for (var k = 0; k < targets.Length; k++)
        {
            var t = targets[k];
            // 目標船舯在自船船體座標(BodyFrame.FromEnu)
            var de = t.X - s.X;
            var dn = t.Y - s.Y;
            var dx = de * sn + dn * cs;
            var dy = de * cs - dn * sn;
            var yl = c.LateralBeams * (B + t.BeamM);
            var ax = Math.Abs(dx);
            var ay = Math.Abs(dy);
            if (ax >= xl || ay >= yl) continue;
            count++;
            var w = Taper(ax / xl, c.TaperStart) * Taper(ay / yl, c.TaperStart);
            var xi = dx / L;
            var eta = dy / L;
            var beta = t.PsiRad - s.Psi;
            var cb = Math.Cos(beta);
            var sb = Math.Sin(beta);
            var tgt = TargetProfile(t);
            var delta = c.CoreFactor * (B + t.BeamM);
            var delta2 = delta * delta;
            var uo = s.U;
            var ut = t.SpeedMps;
            var lag = -c.Rho * uo * ut / TwoPi;
            var fk = -c.Rho * ut * ut / TwoPi;
            var fkY = fk * (1.0 + c.SwayAddedMassRatio);
            double x = 0.0, y = 0.0, nn = 0.0;
            var ox = _own.X; var oa = _own.A; var os = _own.S;
            var px = tgt.X; var pa = tgt.A;
            for (var i = 0; i < ox.Length; i++)
            {
                var xi0 = ox[i];
                double ux = 0.0, uy = 0.0, dux = 0.0, duy = 0.0;
                for (var j = 0; j < px.Length; j++)
                {
                    var sj = px[j];
                    var qx = dx + sj * cb;
                    var qy = dy + sj * sb;
                    var rx = xi0 - qx;
                    var ry = 0.0 - qy;
                    var r2 = rx * rx + ry * ry + delta2;
                    Kernel(r2, h, deep, c.Images, out var g, out var dg);
                    var m = pa[j];
                    ux += m * rx * g;
                    uy += m * ry * g;
                    // 場點沿目標船軸向移動的導數 d/ds (r⃗ G):t̂ G + r⃗ G'·2(r⃗·t̂)
                    var rt = rx * cb + ry * sb;
                    dux += m * (cb * g + rx * dg * 2.0 * rt);
                    duy += m * (sb * g + ry * dg * 2.0 * rt);
                }
                var fx = lag * oa[i] * ux + fk * os[i] * dux;
                var fy = lag * oa[i] * uy + fkY * os[i] * duy;
                x += fx;
                y += fy;
                nn += xi0 * fy;
            }
            var ws = w * Scale;
            var xk = x * ws;
            var yk = y * ws;
            var nk = nn * ws;
            tx += xk;
            ty += yk;
            tn += nk;
            var f = Math.Max(Math.Abs(yk), 4.0 * Math.Abs(nk) / L);
            if (f > bestF) { bestF = f; best = t.Id; bestXi = xi; bestEta = eta; }
        }
        return new ShipInteractionResult(tx, ty, tn, count > 0, best, bestXi, bestEta, count);
    }

    /// <summary>目標船的剖面積曲線(快取;同尺寸回傳同一實例,見 <see cref="_targetProfiles"/>)。</summary>
    public HullProfile TargetProfile(in InteractionTarget t)
    {
        var key = (t.LengthM, t.BeamM, t.DraftM, t.Cb);
        if (_targetProfiles.TryGetValue(key, out var cached)) return cached;
        if (_targetProfiles.Count >= MaxCachedTargetProfiles) _targetProfiles.Clear();
        var built = HullProfile.Build(t.LengthM, t.BeamM, t.DraftM, t.Cb, Coefficients);
        _targetProfiles[key] = built;
        return built;
    }

    /// <summary>目前快取的目標船剖面數(診斷/測試用)。</summary>
    public int CachedTargetProfiles => _targetProfiles.Count;

    /// <summary>範圍漸減:q ≤ start 為 1,start–1 之間以 smoothstep 降到 0。</summary>
    public static double Taper(double q, double start)
    {
        if (q <= start) return 1.0;
        if (q >= 1.0) return 0.0;
        var p = (q - start) / (1.0 - start);
        return 1.0 - p * p * (3.0 - 2.0 * p);
    }

    /// <summary>
    /// 線源核 G(r²) = Σ_k (r² + (2kh)²)^{−3/2}(k = −K..K;深水只有 k = 0)加上 |k| &gt; K 的積分尾項 (1 − s0/√(r² + s0²))/(h·r²),s0 = (2K + 1)h,
    /// 與其對 r² 的導數 dG/d(r²)(壓力梯度項用)。
    /// </summary>
    public static void Kernel(double r2, double h, bool deep, int images, out double g, out double dg)
    {
        if (deep)
        {
            g = Math.Pow(r2, -1.5);
            dg = -1.5 * g / r2;
            return;
        }
        var gs = 0.0;
        var dgs = 0.0;
        for (var k = -images; k <= images; k++)
        {
            var z = 2.0 * k * h;
            var q = r2 + z * z;
            var p = Math.Pow(q, -1.5);
            gs += p;
            dgs += -1.5 * p / q;
        }
        var s0 = (2.0 * images + 1.0) * h;
        var q0 = r2 + s0 * s0;
        var sq = Math.Sqrt(q0);
        var tail = (1.0 - s0 / sq) / (h * r2);
        var dtail = (0.5 * s0 / (q0 * sq)) / (h * r2) - (1.0 - s0 / sq) / (h * r2 * r2);
        g = gs + tail;
        dg = dgs + dtail;
    }

    // ------------------------------------------------------------------ 雜湊

    /// <summary>雜湊區塊(只在 Active 時追加):標記、事件狀態、步末 X、Y、N、ξ、η。</summary>
    public int WriteHash(Span<byte> buf)
    {
        var o = 0;
        void L64(Span<byte> b, long v) { BinaryPrimitives.WriteInt64LittleEndian(b[o..], v); o += 8; }
        void D64(Span<byte> b, double v) => L64(b, BitConverter.DoubleToInt64Bits(v));
        var r = Last;
        L64(buf, HashMarker);
        L64(buf, EventActive ? 1 : 0);
        D64(buf, r.X); D64(buf, r.Y); D64(buf, r.N); D64(buf, r.Xi); D64(buf, r.Eta);
        return o;
    }

    // ------------------------------------------------------------------ 狀態

    /// <summary>state.shipInteraction(對外 t、t·m;只在 Active 時輸出)。</summary>
    public ShipInteractionState State()
    {
        var r = Last;
        return new ShipInteractionState
        {
            Active = r.Active,
            TargetId = r.TargetId,
            ForceSurgeT = r.X / TonneForce,
            ForceSwayT = r.Y / TonneForce,
            MomentYawTm = r.N / TonneForce,
            Xi = r.Active ? r.Xi : null,
            Eta = r.Active ? r.Eta : null,
            Targets = r.Count,
            Event = EventActive,
        };
    }

    public ShipInteractionSnapshot CreateSnapshot() => new(EventActive, EventTargetId);
}

/// <summary>事件轉換:步末等效側向力跨過門檻(進入)或降到離開門檻以下(離開)。</summary>
public enum ShipInteractionTransition
{
    None,
    Enter,
    Exit,
}

/// <summary>
/// 船體剖面積曲線的離散(設計文件第 3.2 節):平行中體 + 拋物線進流/去流段,S(x) = A_m(1 − s²),s = (|x| − Lp/2)/L_e;
/// L_e = 1.5(1 − Cp)·L 夾在 [0.1L, 0.5L],Cp = min(Cb/Cm, 0.98);A_m 由排水體積 Cb·L·B·T 反推(A_m·(L − 2L_e/3))。
/// 站:沿船長等分 N 段取中點;A[i] = −S'(x_i)·Δx(艏正 = 源、艉負 = 匯)、S[i] = S(x_i)·Δx。
/// </summary>
public sealed class HullProfile
{
    public double L { get; }
    public double Le { get; }
    public double Am { get; }
    public double[] X { get; }
    public double[] A { get; }
    public double[] S { get; }

    private HullProfile(double l, double le, double am, double[] x, double[] a, double[] s)
    {
        L = l; Le = le; Am = am; X = x; A = a; S = s;
    }

    public static HullProfile Build(double l, double b, double t, double cb, ShipInteractionCoefficients c)
    {
        var cp = Math.Min(cb / c.MidshipCoefficient, 0.98);
        var le = Math.Min(0.5 * l, Math.Max(0.1 * l, 1.5 * (1.0 - cp) * l));
        var am = cb * b * t / (1.0 - (2.0 / 3.0) * (le / l));
        var lp = l - 2.0 * le;
        var n = c.Stations;
        var xs = new double[n];
        var a = new double[n];
        var s = new double[n];
        var dx = l / n;
        for (var j = 0; j < n; j++)
        {
            var x = -0.5 * l + (j + 0.5) * dx;
            var ax = Math.Abs(x);
            double sx, ds;
            if (ax <= 0.5 * lp) { sx = am; ds = 0.0; }
            else
            {
                var q = (ax - 0.5 * lp) / le;
                sx = am * (1.0 - q * q);
                ds = -2.0 * am * q / le * (x > 0.0 ? 1.0 : -1.0);
            }
            xs[j] = x;
            a[j] = -ds * dx;
            s[j] = sx * dx;
        }
        return new HullProfile(l, le, am, xs, a, s);
    }
}

/// <summary>船間交互作用快照(只在啟用時寫入 EngineSnapshot;舊快照缺此欄位時事件狀態還原為未進入)。</summary>
public sealed record ShipInteractionSnapshot(bool EventActive, string? EventTargetId);

/// <summary>
/// 船間交互作用狀態(state.shipInteraction;情境 environment.shipInteraction.enabled 且動力學接受外力時輸出;C# 與 Python 參考實作皆輸出;
/// 設計文件 physics-ship-interaction.md 第 5 節)。力為船體座標:X 向艏正、Y 右舷正、N 艏向右正(與 u/v/r、MMG 相同慣例);單位噸力(t)與 t·m。
/// </summary>
public sealed record ShipInteractionState
{
    /// <summary>有目標船在作用範圍內(3 L 內且橫向 &lt; 2(B_o + B_t))</summary>
    [JsonPropertyName("active")] public required bool Active { get; init; }
    /// <summary>主要目標(等效側向力最大者;無目標在範圍內時省略)</summary>
    [JsonPropertyName("targetId")] public string? TargetId { get; init; }
    /// <summary>交互作用 surge 力(t,向艏正)</summary>
    [JsonPropertyName("forceSurge_t")] public double ForceSurgeT { get; init; }
    /// <summary>交互作用 sway 力(t,右舷正;向目標為吸引)</summary>
    [JsonPropertyName("forceSway_t")] public required double ForceSwayT { get; init; }
    /// <summary>交互作用 yaw 力矩(t·m,艏向右正)</summary>
    [JsonPropertyName("momentYaw_tm")] public required double MomentYawTm { get; init; }
    /// <summary>主要目標的縱向錯位 ξ = (x_t − x_o)/L(正 = 目標在前)</summary>
    [JsonPropertyName("xi")] public double? Xi { get; init; }
    /// <summary>主要目標的橫向間距 η = y/L(正 = 目標在右舷)</summary>
    [JsonPropertyName("eta")] public double? Eta { get; init; }
    /// <summary>範圍內的目標數</summary>
    [JsonPropertyName("targets")] public int Targets { get; init; }
    /// <summary>交互作用力已超過事件門檻(shipInteraction 進入事件已發、離開未發)</summary>
    [JsonPropertyName("event")] public bool Event { get; init; }
}
