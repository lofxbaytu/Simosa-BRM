namespace SimosaBRM.SimCore.Environment;

/// <summary>
/// 潮高模型(情境 environment.tide;情境引擎設計第 1.5.1 節):潮高(m)為練習時間 t(秒,自練習開始)的純函數,
/// 有效水深 = 基底水深(海圖水深,含 zones 覆寫與 setEnvironment waterDepth 漸變)+ 潮高(t)。
/// 確定性:只由 t 決定,不進快照與雜湊(還原後以 Tick 重算);Python 參考實作 simosa_brm/tide.py 為同一公式。
/// </summary>
public abstract class TideModel
{
    /// <summary>M2 分潮週期(秒;12.4206 h):潮高表的平潮門檻參考週期(<see cref="ReferencePeriodS"/>)。</summary>
    public const double M2PeriodS = 44714.16;

    /// <summary>練習時間 t(秒)的潮高(m)。</summary>
    public abstract double HeightAt(double t);

    /// <summary>
    /// 練習時間 t 的潮高變化率 dH/dt(m/s;漲為正、退為負):潮流模型(environment.current.tidal,CurrentModel.cs)以其符號決定漲/退、
    /// 以 |dH/dt| / <see cref="MaxRate"/> 縮放流速。表為分段常數(範圍外 0),調和潮為解析導數;Python tide.py 同一運算順序。
    /// </summary>
    public abstract double RateAt(double t);

    /// <summary>|dH/dt| 的最大值(m/s):表為各段斜率絕對值的最大;調和潮為 Σ A·2π/T(上界;單一分潮時為精確值)。</summary>
    public abstract double MaxRate { get; }

    /// <summary>平潮門檻的參考週期(秒):調和潮取最大振幅分潮的週期,表取 M2;slackWindow 依此換算為 |dH/dt| 比例門檻。</summary>
    public abstract double ReferencePeriodS { get; }

    /// <summary>
    /// 由情境建立模型:table 以線性內插(範圍外取端點),utc 時刻換算為相對 <paramref name="startUtc"/> 的秒數;
    /// harmonic 為 mean + Σ A·cos(2πt/T + φ)。無 tide 時 null。結構錯誤(表點 &lt; 2、t 未遞增、週期 ≤ 0 …)由
    /// <see cref="Scenario.Scenario.StructuralIssues"/> 先擋,此處只做最低限度的防呆。
    /// </summary>
    public static TideModel? FromScenario(Scenario.ScenarioTide? tide, DateTime? startUtc)
    {
        if (tide is null) return null;
        if (tide.Table is { Count: > 0 } table)
        {
            var points = new List<(double T, double Height)>(table.Count);
            foreach (var p in table)
            {
                double t;
                if (p.T is { } given) t = given;
                else if (startUtc is { } start && Engine.SimulationEngine.TryParseUtc(p.Utc, out var utc)) t = (utc - start).TotalSeconds;
                else throw new InvalidDataException("environment.tide.table 的 utc 需有可解析的情境 startTimeUtc");
                points.Add((t, p.HeightM));
            }
            return new TideTable(points);
        }
        if (tide.Harmonic is { } h)
            return new HarmonicTide(h.MeanLevelM, h.Constituents.Select(c => (c.AmplitudeM, c.PeriodS, Contracts.Units.DegToRad(c.PhaseDeg))).ToList());
        return null;
    }
}

/// <summary>潮高表:(t, 高度) 線性內插,t 在範圍外取端點值;表至少 2 點且 t 嚴格遞增(結構檢查保證)。</summary>
public sealed class TideTable : TideModel
{
    public IReadOnlyList<(double T, double Height)> Points { get; }
    /// <summary>各段斜率(m/s):_slopes[i] 為 (i−1, i] 段;索引 0 未用</summary>
    private readonly double[] _slopes;

    public TideTable(IReadOnlyList<(double T, double Height)> points)
    {
        if (points.Count == 0) throw new ArgumentException("潮高表至少需要 1 點", nameof(points));
        Points = points;
        _slopes = new double[points.Count];
        var max = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            var (t0, h0) = points[i - 1];
            var (t1, h1) = points[i];
            _slopes[i] = (h1 - h0) / (t1 - t0);
            max = Math.Max(max, Math.Abs(_slopes[i]));
        }
        MaxRate = max;
    }

    public override double MaxRate { get; }
    public override double ReferencePeriodS => M2PeriodS;

    /// <summary>分段常數:t 在 [t0, tLast] 內取所在段(與 HeightAt 相同:第一個 t ≤ t_i 的段 (i−1, i]),範圍外 0。</summary>
    public override double RateAt(double t)
    {
        var pts = Points;
        if (pts.Count < 2 || t < pts[0].T || t > pts[pts.Count - 1].T) return 0.0;
        for (var i = 1; i < pts.Count; i++)
        {
            if (t > pts[i].T) continue;
            return _slopes[i];
        }
        return 0.0;
    }

    public override double HeightAt(double t)
    {
        var pts = Points;
        if (t <= pts[0].T) return pts[0].Height;
        var last = pts[pts.Count - 1];
        if (t >= last.T) return last.Height;
        // 線性搜尋(表通常只有數十點);公式與 Python TideTable.height_at 相同的運算順序
        for (var i = 1; i < pts.Count; i++)
        {
            var (t1, h1) = pts[i];
            if (t > t1) continue;
            var (t0, h0) = pts[i - 1];
            return h0 + (h1 - h0) * (t - t0) / (t1 - t0);
        }
        return last.Height;
    }
}

/// <summary>調和潮:高度 = meanLevel + Σ amplitude·cos(2π·t/period + phase)(phase 為弧度)。</summary>
public sealed class HarmonicTide : TideModel
{
    public double MeanLevelM { get; }
    public IReadOnlyList<(double AmplitudeM, double PeriodS, double PhaseRad)> Constituents { get; }

    public HarmonicTide(double meanLevelM, IReadOnlyList<(double AmplitudeM, double PeriodS, double PhaseRad)> constituents)
    {
        MeanLevelM = meanLevelM;
        Constituents = constituents;
        var max = 0.0;
        var refPeriod = M2PeriodS;
        var refAmp = double.NegativeInfinity;
        foreach (var (a, period, _) in constituents)
        {
            max += a * 2.0 * Math.PI / period;
            if (a > refAmp) { refAmp = a; refPeriod = period; }  // 最大振幅分潮(相同時取先出現者)
        }
        MaxRate = max;
        ReferencePeriodS = refPeriod;
    }

    public override double MaxRate { get; }
    public override double ReferencePeriodS { get; }

    /// <summary>解析導數:dH/dt = Σ −A·(2π/T)·sin(2π·t/T + φ)。</summary>
    public override double RateAt(double t)
    {
        var rate = 0.0;
        foreach (var (a, period, phase) in Constituents)
            rate += -a * (2.0 * Math.PI / period) * Math.Sin(2.0 * Math.PI * t / period + phase);
        return rate;
    }

    public override double HeightAt(double t)
    {
        var h = MeanLevelM;
        foreach (var (a, period, phase) in Constituents)
            h += a * Math.Cos(2.0 * Math.PI * t / period + phase);
        return h;
    }
}
