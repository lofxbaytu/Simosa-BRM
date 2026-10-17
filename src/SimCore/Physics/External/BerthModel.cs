using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Geo;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Physics.External;

/// <summary>碰墊型式(非線性壓縮–反力曲線,SI)。</summary>
public sealed class FenderType
{
    public double HeightM { get; }
    public double WidthM { get; }
    public double RatedReactionN { get; }
    public double RatedDeflectionM { get; }
    public double RatedEnergyJ { get; }
    public double DampingNsPerM { get; }
    public double Friction { get; }
    /// <summary>曲線點(壓縮 m、反力 N),自 (0, 0) 起遞增;超過最後一點以觸底剛度 20·R/H 外插</summary>
    private readonly double[] _d, _r, _e;
    private readonly double _bottomSlope;

    public FenderType(FenderTypeDefinition def)
    {
        HeightM = def.HeightM;
        WidthM = def.WidthM;
        RatedReactionN = def.RatedReactionKN * 1000.0;
        RatedDeflectionM = def.RatedDeflectionPct / 100.0 * def.HeightM;
        DampingNsPerM = def.DampingKNsPerM * 1000.0;
        Friction = def.Friction;
        var curve = def.Curve is { Count: >= 2 } c ? c : new List<double[]> { new[] { 0.0, 0.0 }, new[] { def.RatedDeflectionPct, 100.0 } };
        _d = curve.Select(p => p[0] / 100.0 * HeightM).ToArray();
        _r = curve.Select(p => p[1] / 100.0 * RatedReactionN).ToArray();
        _e = new double[_d.Length];
        for (var i = 1; i < _d.Length; i++) _e[i] = _e[i - 1] + 0.5 * (_r[i] + _r[i - 1]) * (_d[i] - _d[i - 1]);
        _bottomSlope = 20.0 * RatedReactionN / HeightM;
        RatedEnergyJ = def.RatedEnergyKNm is { } e ? e * 1000.0 : Energy(RatedDeflectionM);
    }

    /// <summary>靜態反力(N)。</summary>
    public double Reaction(double delta)
    {
        if (delta <= 0.0) return 0.0;
        var n = _d.Length;
        if (delta >= _d[n - 1]) return _r[n - 1] + _bottomSlope * (delta - _d[n - 1]);
        var i = 1;
        while (_d[i] < delta) i++;
        var f = (delta - _d[i - 1]) / (_d[i] - _d[i - 1]);
        return _r[i - 1] + f * (_r[i] - _r[i - 1]);
    }

    /// <summary>吸收能量 ∫₀^δ R dδ(J;分段線性曲線的解析積分)。</summary>
    public double Energy(double delta)
    {
        if (delta <= 0.0) return 0.0;
        var n = _d.Length;
        if (delta >= _d[n - 1])
        {
            var x = delta - _d[n - 1];
            return _e[n - 1] + _r[n - 1] * x + 0.5 * _bottomSlope * x * x;
        }
        var i = 1;
        while (_d[i] < delta) i++;
        var rd = Reaction(delta);
        return _e[i - 1] + 0.5 * (_r[i - 1] + rd) * (delta - _d[i - 1]);
    }

    /// <summary>[0, δ] 內的最大靜態反力(挫屈型曲線非單調)。</summary>
    public double MaxReaction(double delta)
    {
        var m = Reaction(delta);
        for (var i = 0; i < _d.Length && _d[i] <= delta; i++) m = Math.Max(m, _r[i]);
        return m;
    }
}

/// <summary>碼頭面線段(ENU):起點 A、單位切向 t、指向水域的單位法向 n、長度、自折線起點的里程。</summary>
public readonly record struct QuaySegment(double Ax, double Ay, double Tx, double Ty, double Nx, double Ny, double Length, double Chainage);

/// <summary>單一碰墊(位置與接觸事件狀態)。</summary>
public sealed class Fender
{
    public string Id { get; }
    public FenderType Type { get; }
    public int Segment { get; }
    /// <summary>沿線段的位置(m,自線段起點)</summary>
    public double S { get; }

    // ---- 接觸事件狀態(快照、雜湊)----
    public bool InContact { get; set; }
    public bool PeakReported { get; set; }
    public double MaxDeflection { get; set; }
    public double ApproachSpeed { get; set; }

    // ---- 最近一步(廣播)----
    public double LastDeflection { get; set; }
    public double LastReactionN { get; set; }

    public Fender(string id, FenderType type, int segment, double s)
    {
        Id = id;
        Type = type;
        Segment = segment;
        S = s;
    }
}

public sealed record Bollard(string Id, double X, double Y, double? SwlT);

/// <summary>繫纜狀態(state.berth.lines[].status)。</summary>
public enum MooringStatus
{
    Stowed,
    Fast,
    Heaving,
    Slacking,
    LetGo,
    Parted,
}

/// <summary>
/// 繫纜(設計文件第 4.3 節):非線性彈性 T = MBL·(ε/ε_b)^n(ε = D/L₀ − 1 > 0,只拉不推),加 Kelvin–Voigt 阻尼(鬆弛時間 1 s);
/// 絞纜(heave)以速率縮短 L₀ 至目標張力、鬆纜(slack)以速率放長、斷裂於 T > MBL。
/// </summary>
public sealed class MooringLine
{
    public const double TonneForce = 9.80665 * 1000.0;
    public const double DampingTauS = 1.0;

    public string Id { get; }
    public double FairX { get; }
    public double FairY { get; }
    public double MblN { get; }
    public double BreakStrain { get; }
    public double Exponent { get; }
    public double MaxLength { get; }
    public double HeaveRateMps { get; }
    public double WinchPullN { get; }
    public double PretensionN { get; }
    public string? DefaultBollard { get; }

    // ---- 跨步狀態 ----
    public MooringStatus Status { get; set; } = MooringStatus.Stowed;
    public int BollardIndex { get; set; } = -1;
    /// <summary>未受力纜長 L₀(m)</summary>
    public double RestLength { get; set; }
    public double TargetTensionN { get; set; }
    public double SlackRemaining { get; set; }

    public double LastTensionN { get; set; }

    public MooringLine(ScenarioMooringLine l, ShipParticulars ship)
    {
        Id = l.Id;
        (FairX, FairY) = l.Fairlead!.Resolve(ship);
        MblN = l.MblT * TonneForce;
        BreakStrain = l.BreakStrain;
        Exponent = l.Exponent;
        MaxLength = l.LengthM;
        HeaveRateMps = l.HeaveRateMPerMin / 60.0;
        WinchPullN = l.WinchPullT * TonneForce;
        PretensionN = l.PretensionT * TonneForce;
        DefaultBollard = l.Bollard;
    }

    public bool Loaded => Status is MooringStatus.Fast or MooringStatus.Heaving or MooringStatus.Slacking;

    /// <summary>靜態張力(N)。</summary>
    public double StaticTension(double distance)
    {
        if (RestLength <= 0) return 0.0;
        var eps = distance / RestLength - 1.0;
        return eps <= 0.0 ? 0.0 : MblN * Math.Pow(eps / BreakStrain, Exponent);
    }

    /// <summary>給定張力的應變。</summary>
    public double StrainFor(double tensionN) => tensionN <= 0 ? 0.0 : BreakStrain * Math.Pow(tensionN / MblN, 1.0 / Exponent);

    /// <summary>含阻尼的張力(N):T = T_s + k(ε)·τ·Ḋ,夾在 [0, 1.5·MBL]。</summary>
    public double Tension(double distance, double rate)
    {
        if (!Loaded || RestLength <= 0) return 0.0;
        var eps = distance / RestLength - 1.0;
        if (eps <= 0.0) return 0.0;
        var ts = MblN * Math.Pow(eps / BreakStrain, Exponent);
        var k = MblN * Exponent * Math.Pow(eps / BreakStrain, Exponent - 1.0) / (BreakStrain * RestLength);
        var t = ts + k * DampingTauS * rate;
        return t < 0.0 ? 0.0 : Math.Min(t, 1.5 * MblN);
    }
}

/// <summary>
/// 泊位:碼頭面折線、碰墊、繫船柱與自船繫纜(設計文件第 4 節)。外力 = Σ 碰墊(只推不拉;靜態曲線 + 阻尼 + 面板摩擦)
/// + 碼頭硬接觸(懲罰彈簧-阻尼)+ Σ 繫纜張力。船體外形以 11 點多邊形近似(LOA × B,艏部收窄)。
/// </summary>
public sealed class BerthModel
{
    public string Id { get; }
    public string? Name { get; }
    public IReadOnlyList<QuaySegment> Segments { get; }
    public IReadOnlyList<Fender> Fenders { get; }
    public IReadOnlyList<Bollard> Bollards { get; }
    public IReadOnlyList<MooringLine> Lines { get; }
    public double QuayStiffness { get; }
    public double QuayDamping { get; }
    /// <summary>船體外形(船體座標)</summary>
    public (double X, double Y)[] Hull { get; }

    // ---- 碼頭硬接觸事件狀態 ----
    public bool QuayInContact { get; set; }
    public double QuayMaxPenetration { get; set; }

    public BerthModel(BerthDefinition def, IReadOnlyList<ScenarioMooringLine> lines, ShipParticulars ship, double shipMassKg, LocalTangentPlane proj)
    {
        Id = def.Id;
        Name = def.Name;
        var pts = def.Quay.Select(p => p.X is not null && p.Y is not null ? (p.X.Value, p.Y.Value) : proj.ToLocal(p.Lat!.Value, p.Lon!.Value)).ToArray();
        var segs = new List<QuaySegment>();
        var right = def.WaterSide != "left";
        var chain = 0.0;
        for (var i = 0; i + 1 < pts.Length; i++)
        {
            var (ax, ay) = pts[i];
            var (bx, by) = pts[i + 1];
            var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (len < 1e-6) continue;
            var tx = (bx - ax) / len;
            var ty = (by - ay) / len;
            // ENU 中沿 t 前進的右手側 = (ty, −tx)
            var (nx, ny) = right ? (ty, -tx) : (-ty, tx);
            segs.Add(new QuaySegment(ax, ay, tx, ty, nx, ny, len, chain));
            chain += len;
        }
        Segments = segs;
        var types = def.FenderTypes.ToDictionary(kv => kv.Key, kv => new FenderType(kv.Value), StringComparer.Ordinal);
        Fenders = def.Fenders.Select(f =>
        {
            var (seg, s) = Locate(f.ChainageM);
            return new Fender(f.Id, types[f.Type], seg, s);
        }).ToList();
        Bollards = def.Bollards.Select(b =>
        {
            var (seg, s) = Locate(b.ChainageM);
            var q = Segments[seg];
            return new Bollard(b.Id, q.Ax + s * q.Tx - b.SetbackM * q.Nx, q.Ay + s * q.Ty - b.SetbackM * q.Ny, b.SwlT);
        }).ToList();
        Lines = lines.Select(l => new MooringLine(l, ship)).ToList();
        QuayStiffness = (def.QuayStiffnessKNpm ?? 20000.0) * 1000.0;
        // 臨界阻尼比 0.5:c = 2·0.5·√(k·m)
        QuayDamping = Math.Sqrt(QuayStiffness * Math.Max(1.0, shipMassKg));
        var l2 = 0.5 * (ship.Hull.LengthOverall_m > 0 ? ship.Hull.LengthOverall_m : ship.Hull.LengthBetweenPerpendiculars_m);
        var b2 = 0.5 * ship.Hull.Breadth_m;
        Hull = new[]
        {
            (l2, 0.0), (0.84 * l2, 0.60 * b2), (0.60 * l2, 0.96 * b2), (0.50 * l2, b2), (-0.80 * l2, b2), (-l2, 0.84 * b2),
            (-l2, -0.84 * b2), (-0.80 * l2, -b2), (0.50 * l2, -b2), (0.60 * l2, -0.96 * b2), (0.84 * l2, -0.60 * b2),
        };
    }

    /// <summary>里程 → (線段, 線段內距離)。</summary>
    private (int Seg, double S) Locate(double chainage)
    {
        for (var i = 0; i < Segments.Count; i++)
            if (chainage <= Segments[i].Chainage + Segments[i].Length || i == Segments.Count - 1)
                return (i, Math.Clamp(chainage - Segments[i].Chainage, 0.0, Segments[i].Length));
        return (0, 0.0);
    }

    public int BollardIndex(string id)
    {
        for (var i = 0; i < Bollards.Count; i++) if (Bollards[i].Id == id) return i;
        return -1;
    }

    /// <summary>
    /// 碰墊與船體的接觸幾何:船體外形與碰墊面板帶(沿碼頭 ±寬/2)交集中離碼頭面最近的距離 d → 壓縮量 δ = H − d(m);
    /// 接觸點(ENU)取碰墊軸線上距碼頭 d 處。無接觸回傳 false。
    /// </summary>
    public bool FenderGeometry(Fender f, ReadOnlySpan<(double E, double N)> hull, out double delta, out double pe, out double pn)
    {
        var q = Segments[f.Segment];
        var lo = f.S - 0.5 * f.Type.WidthM;
        var hi = f.S + 0.5 * f.Type.WidthM;
        var best = double.PositiveInfinity;
        var n = hull.Length;
        for (var i = 0; i < n; i++)
        {
            var (e0, n0) = hull[i];
            var (e1, n1) = hull[(i + 1) % n];
            var s0 = (e0 - q.Ax) * q.Tx + (n0 - q.Ay) * q.Ty;
            var d0 = (e0 - q.Ax) * q.Nx + (n0 - q.Ay) * q.Ny;
            var s1 = (e1 - q.Ax) * q.Tx + (n1 - q.Ay) * q.Ty;
            var d1 = (e1 - q.Ax) * q.Nx + (n1 - q.Ay) * q.Ny;
            double ta, tb;
            var ds = s1 - s0;
            if (Math.Abs(ds) < 1e-12)
            {
                if (s0 < lo || s0 > hi) continue;
                (ta, tb) = (0.0, 1.0);
            }
            else
            {
                ta = (lo - s0) / ds;
                tb = (hi - s0) / ds;
                if (ta > tb) (ta, tb) = (tb, ta);
                ta = Math.Max(0.0, ta);
                tb = Math.Min(1.0, tb);
                if (ta > tb) continue;
            }
            var da = d0 + ta * (d1 - d0);
            if (da < best) best = da;
            var db = d0 + tb * (d1 - d0);
            if (db < best) best = db;
        }
        delta = f.Type.HeightM - best;
        // 反力沿碰墊軸線(碼頭法向、通過碰墊中心)作用:力的作用線決定力矩,沿線位置不影響,
        // 因此取 (碰墊中心里程, 最小距離);船側平直段與面板全接觸時即為面板中心(不因端點取捨產生虛假力矩)
        pe = q.Ax + f.S * q.Tx + best * q.Nx;
        pn = q.Ay + f.S * q.Ty + best * q.Ny;
        // 船體在碼頭陸側很深處(越過碼頭 > 一個船寬)視為無效幾何,不施力
        return !double.IsPositiveInfinity(best) && delta > 0.0 && best >= -2.0 * Hull[3].Y;
    }

    /// <summary>船體外形 ENU 頂點(寫入 <paramref name="buf"/>)。</summary>
    public void HullEnu(in StateVector s, Span<(double E, double N)> buf)
    {
        for (var i = 0; i < Hull.Length; i++) buf[i] = BodyFrame.ToEnu(s, Hull[i].X, Hull[i].Y);
    }

    /// <summary>某狀態下的泊位外力(碰墊 + 碼頭硬接觸 + 繫纜)。</summary>
    public BodyForce Force(in StateVector s, in EnvironmentSample env)
    {
        Span<(double E, double N)> hull = stackalloc (double, double)[Hull.Length];
        HullEnu(s, hull);
        var total = BodyForce.Zero;
        foreach (var f in Fenders)
        {
            if (!FenderGeometry(f, hull, out var delta, out var pe, out var pn)) continue;
            var q = Segments[f.Segment];
            var (xb, yb) = BodyFrame.FromEnu(s, pe, pn);
            var (ve, vn) = BodyFrame.PointGroundVelocity(s, env, xb, yb);
            var vNormal = -(ve * q.Nx + vn * q.Ny);
            var fn = f.Type.Reaction(delta) + f.Type.DampingNsPerM * vNormal;
            if (fn <= 0.0) continue;
            var vt = ve * q.Tx + vn * q.Ty;
            var ft = -f.Type.Friction * fn * Math.Tanh(vt / 0.05);
            total += BodyForce.FromEnu(fn * q.Nx + ft * q.Tx, fn * q.Ny + ft * q.Ty, xb, yb, s.Psi);
        }
        foreach (var q in Segments)
            for (var i = 0; i < hull.Length; i++)
            {
                var (e, n) = hull[i];
                var sa = (e - q.Ax) * q.Tx + (n - q.Ay) * q.Ty;
                if (sa < 0.0 || sa > q.Length) continue;
                var d = (e - q.Ax) * q.Nx + (n - q.Ay) * q.Ny;
                if (d >= 0.0 || d < -2.0 * Hull[3].Y) continue;
                var (xb, yb) = Hull[i];
                var (ve, vn) = BodyFrame.PointGroundVelocity(s, env, xb, yb);
                var vNormal = -(ve * q.Nx + vn * q.Ny);
                var fn = QuayStiffness * -d + QuayDamping * vNormal;
                if (fn <= 0.0) continue;
                total += BodyForce.FromEnu(fn * q.Nx, fn * q.Ny, xb, yb, s.Psi);
            }
        foreach (var l in Lines)
        {
            if (!l.Loaded || l.BollardIndex < 0) continue;
            var b = Bollards[l.BollardIndex];
            var (fe, fnn) = BodyFrame.ToEnu(s, l.FairX, l.FairY);
            var de = b.X - fe;
            var dn = b.Y - fnn;
            var dist = Math.Sqrt(de * de + dn * dn);
            if (dist < 1e-6) continue;
            var (ue, un) = (de / dist, dn / dist);
            var (ve, vn) = BodyFrame.PointGroundVelocity(s, env, l.FairX, l.FairY);
            var rate = -(ve * ue + vn * un);
            var t = l.Tension(dist, rate);
            if (t <= 0.0) continue;
            total += BodyForce.FromEnu(t * ue, t * un, l.FairX, l.FairY, s.Psi);
        }
        return total;
    }

    /// <summary>導纜孔至繫船柱距離(m)與伸長速率(m/s)。</summary>
    public (double Distance, double Rate) LineGeometry(MooringLine l, in StateVector s, in EnvironmentSample env)
    {
        var b = Bollards[l.BollardIndex];
        var (fe, fn) = BodyFrame.ToEnu(s, l.FairX, l.FairY);
        var de = b.X - fe;
        var dn = b.Y - fn;
        var dist = Math.Sqrt(de * de + dn * dn);
        if (dist < 1e-6) return (dist, 0.0);
        var (ve, vn) = BodyFrame.PointGroundVelocity(s, env, l.FairX, l.FairY);
        return (dist, -(ve * de + vn * dn) / dist);
    }

    /// <summary>
    /// 步末更新:碰墊接觸事件(峰值時發 fenderContact)、碼頭硬接觸事件、繫纜絞/鬆與斷裂。
    /// </summary>
    public void PostStep(in StateVector s, in EnvironmentSample env, double dt, List<ExternalEvent> events)
    {
        Span<(double E, double N)> hull = stackalloc (double, double)[Hull.Length];
        HullEnu(s, hull);
        foreach (var f in Fenders)
        {
            var contact = FenderGeometry(f, hull, out var delta, out var pe, out var pn);
            var q = Segments[f.Segment];
            var vNormal = 0.0;
            if (contact)
            {
                var (xb, yb) = BodyFrame.FromEnu(s, pe, pn);
                var (ve, vn) = BodyFrame.PointGroundVelocity(s, env, xb, yb);
                vNormal = -(ve * q.Nx + vn * q.Ny);
            }
            f.LastDeflection = contact ? delta : 0.0;
            f.LastReactionN = contact ? f.Type.Reaction(delta) : 0.0;
            if (contact && !f.InContact)
            {
                f.InContact = true;
                f.PeakReported = false;
                f.MaxDeflection = delta;
                f.ApproachSpeed = vNormal;
            }
            else if (contact)
            {
                if (delta > f.MaxDeflection) f.MaxDeflection = delta;
                if (!f.PeakReported && vNormal <= 0.0) ReportFender(f, events);
            }
            else if (f.InContact)
            {
                if (!f.PeakReported) ReportFender(f, events);
                f.InContact = false;
                f.MaxDeflection = 0.0;
                f.ApproachSpeed = 0.0;
            }
        }

        // 碼頭硬接觸
        var maxPen = 0.0;
        var vAt = 0.0;
        foreach (var q in Segments)
            for (var i = 0; i < hull.Length; i++)
            {
                var (e, n) = hull[i];
                var sa = (e - q.Ax) * q.Tx + (n - q.Ay) * q.Ty;
                if (sa < 0.0 || sa > q.Length) continue;
                var d = (e - q.Ax) * q.Nx + (n - q.Ay) * q.Ny;
                if (d >= 0.0 || d < -2.0 * Hull[3].Y) continue;
                if (-d > maxPen)
                {
                    maxPen = -d;
                    var (ve, vn) = BodyFrame.PointGroundVelocity(s, env, Hull[i].X, Hull[i].Y);
                    vAt = -(ve * q.Nx + vn * q.Ny);
                }
            }
        if (maxPen > 0.0 && !QuayInContact)
        {
            QuayInContact = true;
            QuayMaxPenetration = maxPen;
            events.Add(new ExternalEvent("quayContact", Id, "船體觸碰碼頭(硬接觸)", new { berthId = Id, penetration_m = maxPen, normalSpeed_mps = vAt }));
        }
        else if (maxPen > 0.0) QuayMaxPenetration = Math.Max(QuayMaxPenetration, maxPen);
        else if (QuayInContact) { QuayInContact = false; QuayMaxPenetration = 0.0; }

        // 繫纜
        foreach (var l in Lines)
        {
            if (!l.Loaded || l.BollardIndex < 0) { l.LastTensionN = 0.0; continue; }
            var (dist, rate) = LineGeometry(l, s, env);
            var t = l.Tension(dist, rate);
            l.LastTensionN = t;
            if (t > l.MblN)
            {
                l.Status = MooringStatus.Parted;
                l.LastTensionN = 0.0;
                events.Add(new ExternalEvent("mooringLineParted", l.Id, $"繫纜 {l.Id} 斷裂",
                    new { lineId = l.Id, tension_t = t / MooringLine.TonneForce, mbl_t = l.MblN / MooringLine.TonneForce, bollardId = Bollards[l.BollardIndex].Id }));
                continue;
            }
            switch (l.Status)
            {
                case MooringStatus.Heaving:
                    if (l.StaticTension(dist) >= l.TargetTensionN) l.Status = MooringStatus.Fast;
                    else l.RestLength = Math.Max(1.0, l.RestLength - l.HeaveRateMps * dt);
                    break;
                case MooringStatus.Slacking:
                {
                    var dl = Math.Min(l.SlackRemaining, l.HeaveRateMps * dt);
                    l.RestLength = Math.Min(l.MaxLength, l.RestLength + dl);
                    l.SlackRemaining -= dl;
                    if (l.SlackRemaining <= 1e-9 || l.RestLength >= l.MaxLength) { l.SlackRemaining = 0.0; l.Status = MooringStatus.Fast; }
                    break;
                }
            }
        }
    }

    private static void ReportFender(Fender f, List<ExternalEvent> events)
    {
        f.PeakReported = true;
        var energy = f.Type.Energy(f.MaxDeflection);
        var maxR = f.Type.MaxReaction(f.MaxDeflection);
        var overload = f.MaxDeflection > f.Type.RatedDeflectionM + 1e-9 || energy > f.Type.RatedEnergyJ + 1e-6;
        events.Add(new ExternalEvent("fenderContact", f.Id, overload ? $"碰墊 {f.Id} 過載" : $"碰墊 {f.Id} 接觸",
            new
            {
                fenderId = f.Id,
                normalSpeed_mps = f.ApproachSpeed,
                energy_kNm = energy / 1000.0,
                maxReaction_kN = maxR / 1000.0,
                maxDeflection_m = f.MaxDeflection,
                deflection_pct = 100.0 * f.MaxDeflection / f.Type.HeightM,
                ratedEnergy_kNm = f.Type.RatedEnergyJ / 1000.0,
                ratedReaction_kN = f.Type.RatedReactionN / 1000.0,
                overload,
            }));
    }

    /// <summary>
    /// 繫纜指令(makeFast|heave|slack|letGo);回傳 null = 接受,否則拒絕原因。<paramref name="bollardId"/> 未給時用纜的預設繫船柱。
    /// </summary>
    public string? Command(MooringLine l, string action, string? bollardId, double? tensionT, double? lengthM, in StateVector s, in EnvironmentSample env)
    {
        switch (action)
        {
            case "makeFast":
            {
                if (l.Status == MooringStatus.Parted) return "mooringLineParted";
                var id = bollardId ?? l.DefaultBollard;
                var bi = id is null ? -1 : BollardIndex(id);
                if (bi < 0) return "unknownBollard";
                l.BollardIndex = bi;
                var (dist, _) = LineGeometry(l, s, env);
                if (dist > l.MaxLength) { l.BollardIndex = -1; return "bollardOutOfReach"; }
                var pre = tensionT is { } pt ? pt * MooringLine.TonneForce : l.PretensionN;
                l.RestLength = dist / (1.0 + l.StrainFor(pre));
                l.Status = MooringStatus.Fast;
                l.SlackRemaining = 0.0;
                return null;
            }
            case "heave":
                if (!l.Loaded) return "mooringLineNotFast";
                l.TargetTensionN = Math.Min(l.WinchPullN, tensionT is { } tt ? tt * MooringLine.TonneForce : l.WinchPullN);
                l.Status = MooringStatus.Heaving;
                return null;
            case "slack":
                if (!l.Loaded) return "mooringLineNotFast";
                l.SlackRemaining = Math.Max(0.1, lengthM ?? 2.0);
                l.Status = MooringStatus.Slacking;
                return null;
            case "letGo":
                if (l.Status == MooringStatus.Parted) return "mooringLineParted";
                l.Status = MooringStatus.LetGo;
                l.LastTensionN = 0.0;
                return null;
            default:
                return "unknownAction";
        }
    }
}
