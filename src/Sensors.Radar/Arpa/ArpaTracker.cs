using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Traffic;

namespace SimosaBRM.Sensors.Radar.Arpa;

/// <summary>追蹤狀態(MSC.192(79):擷取中、追蹤中、失追)。</summary>
public enum TrackState
{
    Acquiring,
    Tracking,
    Lost,
}

/// <summary>擷取方式:手動(指定方位/距離)、自動(保護區)。</summary>
public enum AcquisitionMode
{
    Manual,
    Auto,
}

/// <summary>ARPA 事件(radar.schema.json <c>$defs/event</c>)。</summary>
public sealed record ArpaEvent(string Kind, double T, int? TargetId, string Message);

/// <summary>自船指示值(ARPA 穩定化與 CPA 計算用;位置為 CCRP = 船舶參考點)。</summary>
public readonly record struct OwnShipFix(double T, double X, double Y, double Lat, double Lon, double HeadingDeg, double CogDeg, double SogKn)
{
    public double VelE => Units.KnToMps(SogKn) * Math.Sin(Units.DegToRad(CogDeg));
    public double VelN => Units.KnToMps(SogKn) * Math.Cos(Units.DegToRad(CogDeg));
}

/// <summary>一個 ARPA 追蹤目標。</summary>
public sealed class ArpaTrack
{
    public required int Id { get; init; }
    public required AcquisitionMode Mode { get; init; }
    public TrackState State { get; internal set; } = TrackState.Acquiring;
    internal KalmanCv? Kf;
    /// <summary>並行的快速(高過程雜訊)濾波器;與精確濾波器的 NIS 指數平均比較以偵測緩慢轉向</summary>
    internal KalmanCv? KfFast;
    internal double NisAvg = 2.0, NisAvgFast = 2.0;
    internal double SearchX, SearchY;
    public double CreatedT { get; internal set; }
    public double FirstHitT { get; internal set; } = double.NaN;
    public double LastHitT { get; internal set; } = double.NaN;
    public int Hits { get; internal set; }
    public int Misses { get; internal set; }
    internal int HighNis;
    /// <summary>新息指數加權平均(偵測緩慢轉向:雜訊平均趨近 0,轉向時新息持續偏向一側)</summary>
    internal double BiasX, BiasY;
    internal double ManoeuvreUntilT = double.NegativeInfinity;
    /// <summary>操船期間精確濾波器使用的過程雜訊(緩慢轉向:FastNoise;突然轉向:ManoeuvreNoise)</summary>
    internal double ManoeuvreQ;
    internal double NextPastT;
    internal double SwapRiskUntilT = double.NegativeInfinity;
    public double LostT { get; internal set; } = double.NaN;
    public bool Dangerous { get; internal set; }
    public bool SwapRisk { get; internal set; }
    public bool Manoeuvring { get; internal set; }
    public List<(double T, double X, double Y)> Past { get; } = new();
    public Detection? LastDetection { get; internal set; }

    public bool HasEstimate => Kf is not null;
    /// <summary>追蹤時間(自第一次量測)</summary>
    public double AgeS(double t) => double.IsNaN(FirstHitT) ? 0.0 : t - FirstHitT;
}

/// <summary>追蹤目標於某時刻的解(對外訊息單位:nm、kn、度、分)。</summary>
public sealed record TrackSolution(
    double X, double Y, double RangeNm, double BearingDeg, double RelBearingDeg,
    double CourseDeg, double SpeedKn, double RelCourseDeg, double RelSpeedKn,
    double CpaNm, double TcpaMin, double? BcrNm, double? BctMin,
    double? TrialCpaNm, double? TrialTcpaMin);

/// <summary>
/// ARPA 追蹤器(規劃書第 5.2 節、第 7.2 節 MSC.192(79)):偵測 → 關聯(預測閘門 + 最近 Mahalanobis 距離)→ CV 卡爾曼濾波,
/// NIS 連續過大時判定操船並提高過程雜訊;手動擷取與保護區 M-of-N 自動擷取;漏掃描計數 → 失追;
/// 兩目標接近或一個偵測落入兩個閘門時標示交換風險;CPA/TCPA/BCR/BCT 以 SimCore <see cref="Encounter"/> 同一定義計算;試操船。
/// 所有集合為有序清單,與輸入順序一致(確定性)。
/// </summary>
public sealed class ArpaTracker
{
    private sealed class Candidate
    {
        public double X, Y, FirstX, FirstY, FirstT, LastT, Vx, Vy;
        public int Hits;
    }

    private readonly RadarConfig _cfg;
    private readonly double _cellM;
    private readonly List<ArpaTrack> _tracks = new();
    private readonly List<Candidate> _candidates = new();
    private readonly List<ArpaEvent> _events = new();
    private OwnShipFix _own;
    private bool _hasOwn;

    public ArpaTracker(RadarConfig cfg, double cellM)
    {
        _cfg = cfg;
        _cellM = cellM;
        CpaLimitNm = cfg.Arpa.CpaLimitNm;
        TcpaLimitMin = cfg.Arpa.TcpaLimitMin;
    }

    public IReadOnlyList<ArpaTrack> Tracks => _tracks;
    public double CpaLimitNm { get; set; }
    public double TcpaLimitMin { get; set; }
    public OwnShipFix Own => _own;

    public void SetOwn(OwnShipFix own)
    {
        _own = own;
        _hasOwn = true;
    }

    public List<ArpaEvent> DrainEvents()
    {
        var list = new List<ArpaEvent>(_events);
        _events.Clear();
        return list;
    }

    private int ActiveCount => _tracks.Count(t => t.State != TrackState.Lost);

    private int NextId()
    {
        for (var id = 1; id <= 99; id++)
            if (!_tracks.Any(t => t.Id == id)) return id;
        return -1;
    }

    /// <summary>手動擷取:自 CCRP 的真方位(度)與距離(nm)。回傳新目標編號或錯誤。</summary>
    public (int? Id, string? Error) AcquireManual(double t, double trueBearingDeg, double rangeNm)
    {
        if (!_hasOwn) return (null, "尚無自船位置");
        if (rangeNm <= 0 || rangeNm > _cfg.ArpaRangeNm) return (null, $"距離須在 0–{_cfg.ArpaRangeNm} nm");
        if (ActiveCount >= _cfg.Arpa.MaxTracks) return (null, $"追蹤目標已達上限 {_cfg.Arpa.MaxTracks}");
        var id = NextId();
        if (id < 0) return (null, "無可用目標編號");
        var b = Units.DegToRad(trueBearingDeg);
        var r = rangeNm * Units.MetresPerNauticalMile;
        var tr = new ArpaTrack { Id = id, Mode = AcquisitionMode.Manual, CreatedT = t };
        tr.SearchX = _own.X + r * Math.Sin(b);
        tr.SearchY = _own.Y + r * Math.Cos(b);
        _tracks.Add(tr);
        _events.Add(new ArpaEvent("acquireStarted", t, id, $"手動擷取 {id:00}:{trueBearingDeg:F1}° {rangeNm:F2} nm"));
        return (id, null);
    }

    public bool Cancel(int id, double t)
    {
        var i = _tracks.FindIndex(x => x.Id == id);
        if (i < 0) return false;
        _tracks.RemoveAt(i);
        _events.Add(new ArpaEvent("cancelled", t, id, $"取消追蹤 {id:00}"));
        return true;
    }

    public void CancelAll(double t, string reason)
    {
        if (_tracks.Count > 0) _events.Add(new ArpaEvent("cancelled", t, null, $"取消全部追蹤({reason})"));
        _tracks.Clear();
        _candidates.Clear();
    }

    private (double R00, double R01, double R11) MeasurementCov(in Detection d)
    {
        var dx = d.X - _own.X;
        var dy = d.Y - _own.Y;
        var r = Math.Max(1.0, Math.Sqrt(dx * dx + dy * dy));
        var ux = dx / r;
        var uy = dy / r;
        var sr = 0.3 * _cellM + 2.0 + 0.02 * d.RadialExtentM;
        var sc = r * Units.DegToRad(_cfg.Arpa.SigmaBearingDeg) + 2.0 + 0.02 * d.ArcExtentM;
        var vr = sr * sr;
        var vc = sc * sc;
        // R = vr u u' + vc v v'(v ⟂ u)
        return (vr * ux * ux + vc * uy * uy, (vr - vc) * ux * uy, vr * uy * uy + vc * ux * ux);
    }

    /// <summary>處理一個偵測(依掃描時間順序)。</summary>
    public void OnDetection(in Detection d, RadarControls controls)
    {
        if (!_hasOwn || d.Large) return;
        var scan = _cfg.ScanPeriodS;
        var a = _cfg.Arpa;
        var (r00, r01, r11) = MeasurementCov(d);
        ArpaTrack? best = null;
        var bestNis = double.PositiveInfinity;
        var inGate = 0;
        ArpaTrack? second = null;
        foreach (var tr in _tracks)
        {
            if (tr.State == TrackState.Lost || tr.Kf is null) continue;
            if (d.T - tr.LastHitT < 0.6 * scan) continue; // 本圈已更新
            // 漏掃描或 NIS 偏高時以操船雜訊放大閘門(目標轉向時不致立即失追)
            var q = tr.ManoeuvreUntilT > d.T || tr.Misses > 0 || tr.HighNis > 0 ? a.ManoeuvreNoise : a.ProcessNoise;
            var (px, py) = tr.Kf.PredictPosition(d.T);
            var (p00, p01, p11) = tr.Kf.PredictPositionCov(d.T, q);
            var nx = d.X - px;
            var ny = d.Y - py;
            var dist = Math.Sqrt(nx * nx + ny * ny);
            if (dist > a.MaxGateM) continue;
            var nis = KalmanCv.Nis(nx, ny, p00 + r00, p01 + r01, p11 + r11);
            if (nis > a.GateChi2 && dist > a.MinGateM * (1 + tr.Misses)) continue;
            inGate++;
            if (nis < bestNis)
            {
                second = best;
                bestNis = nis;
                best = tr;
            }
            else second ??= tr;
        }
        if (best is not null)
        {
            if (inGate >= 2)
            {
                best.SwapRiskUntilT = d.T + 30.0;
                if (second is not null) second.SwapRiskUntilT = d.T + 30.0;
            }
            Update(best, d, r00, r01, r11);
            return;
        }

        // 手動擷取中(尚無量測):搜尋半徑內最先出現的回波
        foreach (var tr in _tracks)
        {
            if (tr.Kf is not null || tr.State == TrackState.Lost) continue;
            var dx = d.X - tr.SearchX;
            var dy = d.Y - tr.SearchY;
            if (dx * dx + dy * dy > a.AcquireGateM * a.AcquireGateM) continue;
            Initialise(tr, d, 0, 0, 64.0, r00, r11);
            return;
        }

        // 保護區候選(M-of-N)
        var g = controls.Guard;
        if (!g.Enabled || d.Cells < a.AutoMinCells) return;
        var ox = d.X - _own.X;
        var oy = d.Y - _own.Y;
        var rangeNm = Math.Sqrt(ox * ox + oy * oy) / Units.MetresPerNauticalMile;
        var brg = Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(ox, oy)));
        if (!g.Contains(rangeNm, brg, _own.HeadingDeg)) return;
        // 已追蹤目標附近的回波不作候選(避免同一目標重複擷取)
        foreach (var tr in _tracks)
        {
            if (tr.Kf is null) continue;
            var (px, py) = tr.Kf.PredictPosition(d.T);
            if ((d.X - px) * (d.X - px) + (d.Y - py) * (d.Y - py) < a.GuardGateM * a.GuardGateM * 4) return;
        }
        Candidate? match = null;
        var maxSpeed = Units.KnToMps(a.GuardMaxSpeedKn);
        foreach (var c in _candidates)
        {
            var dt = d.T - c.LastT;
            if (dt < 0.6 * scan) continue;
            double ex, ey, gate;
            if (c.Hits >= 2)
            {
                // 第 3 次起:等速外推位置的小閘門(海雜波斑點不會沿直線等速移動)
                ex = d.X - (c.X + c.Vx * dt);
                ey = d.Y - (c.Y + c.Vy * dt);
                gate = a.GuardPredictGateM;
            }
            else
            {
                ex = d.X - c.X;
                ey = d.Y - c.Y;
                gate = Math.Min(a.GuardGateM, 20.0 + maxSpeed * dt);
            }
            if (ex * ex + ey * ey <= gate * gate) { match = c; break; }
        }
        if (match is null)
        {
            _candidates.Add(new Candidate { X = d.X, Y = d.Y, FirstX = d.X, FirstY = d.Y, FirstT = d.T, LastT = d.T, Hits = 1 });
            return;
        }
        var cdt = d.T - match.LastT;
        if (match.Hits == 1) { match.Vx = (d.X - match.X) / cdt; match.Vy = (d.Y - match.Y) / cdt; }
        else
        {
            var tot = d.T - match.FirstT;
            match.Vx = (d.X - match.FirstX) / tot;
            match.Vy = (d.Y - match.FirstY) / tot;
        }
        match.X = d.X;
        match.Y = d.Y;
        match.LastT = d.T;
        match.Hits++;
        if (match.Hits >= a.GuardHits)
        {
            _candidates.Remove(match);
            if (ActiveCount >= a.MaxTracks) return;
            var id = NextId();
            if (id < 0) return;
            var tr = new ArpaTrack { Id = id, Mode = AcquisitionMode.Auto, CreatedT = d.T };
            _tracks.Add(tr);
            var dt = d.T - match.FirstT;
            var vx = dt > 0 ? (d.X - match.FirstX) / dt : 0;
            var vy = dt > 0 ? (d.Y - match.FirstY) / dt : 0;
            Initialise(tr, d, vx, vy, 9.0, r00, r11);
            tr.Hits = match.Hits;
            tr.FirstHitT = match.FirstT;
            _events.Add(new ArpaEvent("newTarget", d.T, id, $"保護區新目標 {id:00}:{brg:F1}° {rangeNm:F2} nm(自動擷取)"));
        }
    }

    private void Initialise(ArpaTrack tr, in Detection d, double vx, double vy, double velVar, double r00, double r11)
    {
        tr.Kf = new KalmanCv(d.T, d.X, d.Y, vx, vy, Math.Max(r00, r11), velVar);
        tr.KfFast = tr.Kf.Clone();
        tr.NisAvg = tr.NisAvgFast = 2.0;
        tr.Hits = 1;
        tr.Misses = 0;
        tr.FirstHitT = d.T;
        tr.LastHitT = d.T;
        tr.NextPastT = d.T;
        tr.LastDetection = d;
    }

    private void Update(ArpaTrack tr, in Detection d, double r00, double r01, double r11)
    {
        var a = _cfg.Arpa;
        var q = tr.ManoeuvreUntilT > d.T ? tr.ManoeuvreQ : tr.Misses > 0 ? a.ManoeuvreNoise : a.ProcessNoise;
        tr.Kf!.Predict(d.T, q);
        var (nx, ny, s00, s01, s11) = tr.Kf.Innovation(d.X, d.Y, r00, r01, r11);
        var nis = tr.Kf.Update(d.X, d.Y, r00, r01, r11);
        tr.HighNis = nis > a.ManoeuvreNis ? tr.HighNis + 1 : 0;
        // 偏差檢定:EWMA(α = 0.3)新息的 Mahalanobis 距離 × 等效樣本數 (2 − α)/α ≈ 5.7,超過 χ²(2) 99% 即判定操船
        const double alpha = 0.3;
        tr.BiasX = (1 - alpha) * tr.BiasX + alpha * nx;
        tr.BiasY = (1 - alpha) * tr.BiasY + alpha * ny;
        var biasStat = KalmanCv.Nis(tr.BiasX, tr.BiasY, s00, s01, s11) * (2 - alpha) / alpha;
        var biased = biasStat > a.ManoeuvreNis && tr.Hits > 10;
        // 雙模型:快速濾波器同步更新;精確濾波器平均 NIS 超過快速濾波器 3 倍(且高於下限 0.2;量測雜訊設定偏保守,絕對 NIS 偏小)時判定操船,改用快速濾波器的狀態
        var fast = tr.KfFast!;
        fast.Predict(d.T, a.FastNoise);
        var nisFast = fast.Update(d.X, d.Y, r00, r01, r11);
        const double beta = 0.15;
        tr.NisAvg = (1 - beta) * tr.NisAvg + beta * Math.Min(nis, 50);
        tr.NisAvgFast = (1 - beta) * tr.NisAvgFast + beta * Math.Min(nisFast, 50);
        var switchModel = tr.Hits > 10 && tr.NisAvg > 0.2 && tr.NisAvg > 3.0 * tr.NisAvgFast;
        if (switchModel && tr.State != TrackState.Acquiring)
        {
            tr.Kf = fast.Clone();
            tr.NisAvg = tr.NisAvgFast;
            if (tr.ManoeuvreUntilT < d.T) tr.ManoeuvreQ = a.FastNoise;
            tr.ManoeuvreUntilT = Math.Max(tr.ManoeuvreUntilT, d.T + 30.0);
            tr.BiasX = 0;
            tr.BiasY = 0;
        }
        else if ((tr.HighNis >= 2 || biased) && tr.State != TrackState.Acquiring)
        {
            // 操船偵測:提高過程雜訊 30 s,速度共變異數放大以加速重新收斂
            if (tr.ManoeuvreUntilT < d.T)
            {
                tr.Kf.P[2, 2] += 4.0;
                tr.Kf.P[3, 3] += 4.0;
            }
            tr.ManoeuvreUntilT = d.T + 30.0;
            tr.ManoeuvreQ = a.ManoeuvreNoise;
            tr.HighNis = 0;
            tr.BiasX = 0;
            tr.BiasY = 0;
        }
        tr.Hits++;
        tr.Misses = 0;
        tr.LastHitT = d.T;
        tr.LastDetection = d;
        if (tr.State == TrackState.Acquiring && tr.Hits >= 5 && d.T - tr.FirstHitT >= a.TrendTimeS)
        {
            tr.State = TrackState.Tracking;
            _events.Add(new ArpaEvent("tracking", d.T, tr.Id, $"目標 {tr.Id:00} 追蹤中(向量有效)"));
        }
    }

    /// <summary>每幀維護:漏掃描/失追/刪除、候選逾時、過去位置、交換風險與危險目標。</summary>
    public void Maintain(double t, TrialManoeuvre? trial)
    {
        var a = _cfg.Arpa;
        var scan = _cfg.ScanPeriodS;
        for (var i = _tracks.Count - 1; i >= 0; i--)
        {
            var tr = _tracks[i];
            if (tr.State == TrackState.Lost)
            {
                if (t - tr.LostT > a.LostHoldS)
                {
                    _tracks.RemoveAt(i);
                    _events.Add(new ArpaEvent("deleted", t, tr.Id, $"失追目標 {tr.Id:00} 已刪除"));
                }
                continue;
            }
            var reference = tr.Kf is null ? tr.CreatedT : tr.LastHitT;
            while (t - reference > scan * (tr.Misses + 1.5)) tr.Misses++;
            if (tr.State == TrackState.Acquiring && tr.Misses >= a.AcquireMisses)
            {
                _tracks.RemoveAt(i);
                _events.Add(new ArpaEvent("acquireFailed", t, tr.Id, $"目標 {tr.Id:00} 擷取失敗(無回波)"));
                continue;
            }
            if (tr.State == TrackState.Tracking && tr.Misses >= a.LostMisses)
            {
                tr.State = TrackState.Lost;
                tr.LostT = t;
                tr.Dangerous = false;
                _events.Add(new ArpaEvent("targetLost", t, tr.Id, $"目標 {tr.Id:00} 失追"));
                continue;
            }
            if (tr.Kf is not null && tr.State == TrackState.Tracking && t >= tr.NextPastT)
            {
                var (px, py) = tr.Kf.PredictPosition(t);
                tr.Past.Add((t, px, py));
                if (tr.Past.Count > a.PastCount) tr.Past.RemoveAt(0);
                tr.NextPastT = t + a.PastIntervalS;
            }
            tr.Manoeuvring = tr.ManoeuvreUntilT > t;
        }
        _candidates.RemoveAll(c => t - c.LastT > 2.0 * scan || (c.Hits < a.GuardHits && t - c.FirstT > a.GuardWindowScans * scan));

        // 交換風險:兩追蹤目標預測位置接近,或近期一個偵測落入兩個閘門
        foreach (var tr in _tracks) tr.SwapRisk = tr.SwapRiskUntilT > t;
        for (var i = 0; i < _tracks.Count; i++)
        {
            var ti = _tracks[i];
            if (ti.Kf is null || ti.State == TrackState.Lost) continue;
            var (xi, yi) = ti.Kf.PredictPosition(t);
            for (var j = i + 1; j < _tracks.Count; j++)
            {
                var tj = _tracks[j];
                if (tj.Kf is null || tj.State == TrackState.Lost) continue;
                var (xj, yj) = tj.Kf.PredictPosition(t);
                if ((xi - xj) * (xi - xj) + (yi - yj) * (yi - yj) < a.SwapRiskM * a.SwapRiskM)
                {
                    ti.SwapRisk = true;
                    tj.SwapRisk = true;
                }
            }
        }

        // 危險目標(CPA/TCPA 限值)
        foreach (var tr in _tracks)
        {
            if (tr.State != TrackState.Tracking) continue;
            var s = Solve(tr, t, null);
            var danger = s.CpaNm < CpaLimitNm && s.TcpaMin >= 0 && s.TcpaMin <= TcpaLimitMin;
            if (danger && !tr.Dangerous)
                _events.Add(new ArpaEvent("dangerousTarget", t, tr.Id, $"危險目標 {tr.Id:00}:CPA {s.CpaNm:F2} nm、TCPA {s.TcpaMin:F1} 分"));
            tr.Dangerous = danger;
        }
    }

    /// <summary>計算追蹤目標於時間 t 的位置、向量與 CPA/TCPA/BCR/BCT(含試操船)。</summary>
    public TrackSolution Solve(ArpaTrack tr, double t, TrialManoeuvre? trial)
    {
        double x, y, vx = 0, vy = 0;
        if (tr.Kf is { } kf)
        {
            if (tr.State == TrackState.Lost) (x, y) = kf.PredictPosition(tr.LostT);
            else (x, y) = kf.PredictPosition(t);
            vx = kf.X[2];
            vy = kf.X[3];
        }
        else
        {
            x = tr.SearchX;
            y = tr.SearchY;
        }
        var own = _own;
        var g = Encounter.Compute(own.X, own.Y, own.VelE, own.VelN, Units.DegToRad(own.HeadingDeg), x, y, vx, vy);
        var rvx = vx - own.VelE;
        var rvy = vy - own.VelN;
        double? trialCpa = null, trialTcpa = null;
        if (trial is { } tm)
        {
            var (c, tc) = TrialCpa(x - own.X, y - own.Y, vx, vy, own.VelE, own.VelN,
                Units.KnToMps(tm.SpeedKn) * Math.Sin(Units.DegToRad(tm.CourseDeg)),
                Units.KnToMps(tm.SpeedKn) * Math.Cos(Units.DegToRad(tm.CourseDeg)), tm.DelayMin * 60.0);
            trialCpa = c / Units.MetresPerNauticalMile;
            trialTcpa = tc / 60.0;
        }
        return new TrackSolution(x, y, g.RangeNm, Units.RadToDeg(g.BearingRad), Units.RadToDeg(g.RelBearingRad),
            Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(vx, vy))), Units.MpsToKn(Math.Sqrt(vx * vx + vy * vy)),
            Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(rvx, rvy))), Units.MpsToKn(Math.Sqrt(rvx * rvx + rvy * rvy)),
            g.CpaNm, g.TcpaMin, g.BcrNm, g.BctMin, trialCpa, trialTcpa);
    }

    /// <summary>
    /// 試操船 CPA(m)與 TCPA(s,自現在起):自船以目前速度向量航行 delay 秒後改為新速度向量,目標等速;
    /// 兩段相對運動各取最近點,取較小者。兩段皆遠離時回傳一般(負)TCPA。
    /// </summary>
    public static (double Cpa, double Tcpa) TrialCpa(double rx, double ry, double tvx, double tvy, double ovx, double ovy, double nvx, double nvy, double delayS)
    {
        var w1x = tvx - ovx;
        var w1y = tvy - ovy;
        var w1 = w1x * w1x + w1y * w1y;
        var raw1 = w1 < 1e-12 ? 0.0 : -(rx * w1x + ry * w1y) / w1;
        var t1 = Math.Clamp(raw1, 0.0, delayS);
        var d1 = Math.Sqrt(Sq(rx + w1x * t1) + Sq(ry + w1y * t1));
        var dx = rx + w1x * delayS;
        var dy = ry + w1y * delayS;
        var w2x = tvx - nvx;
        var w2y = tvy - nvy;
        var w2 = w2x * w2x + w2y * w2y;
        var raw2 = w2 < 1e-12 ? 0.0 : -(dx * w2x + dy * w2y) / w2;
        var t2 = Math.Max(0.0, raw2);
        var d2 = Math.Sqrt(Sq(dx + w2x * t2) + Sq(dy + w2y * t2));
        if (delayS <= 0)
            return (Math.Sqrt(Sq(rx + w2x * raw2) + Sq(ry + w2y * raw2)), raw2);
        if (raw1 < 0 && raw2 <= 0)
            return (Math.Sqrt(Sq(rx + w1x * raw1) + Sq(ry + w1y * raw1)), raw1);
        return d1 <= d2 ? (d1, t1) : (d2, delayS + t2);

        static double Sq(double v) => v * v;
    }
}
