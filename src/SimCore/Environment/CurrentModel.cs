using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Environment;

/// <summary>
/// 時變流場(情境 environment.current.table | tidal;情境引擎設計第 1.5.2 節;需求 R3「隨時間變化的流場」):
/// 均勻流的流向(去向)與流速為練習時間 t(秒)的純函數,引擎每步以 <see cref="EnvironmentConditions.RefreshCurrent"/> 寫入
/// <see cref="EnvironmentConditions.CurrentSetRad"/>/<see cref="EnvironmentConditions.CurrentDriftMps"/>;動力學取樣、state.current 與
/// ramp/snapshot 仍走既有欄位。確定性:只由 t 決定,不進快照與雜湊(還原後重算);`setEnvironment current` 覆寫為常數後時變停止
/// (<see cref="EnvironmentConditions.CurrentOverridden"/>,進快照),直到再載入情境。Python 參考實作 simosa_brm/current.py 同公式。
/// </summary>
public abstract class CurrentModel
{
    /// <summary>state.currentSource:"table" | "tidal"(常數流時 state 省略)</summary>
    public abstract string Source { get; }

    /// <summary>練習時間 t(秒)的流向(去向,弧度 [0, 2π))與流速(m/s)。</summary>
    public abstract (double SetRad, double DriftMps) At(double t);

    /// <summary>
    /// 由情境建立模型:table 以向量(東/北分量)線性內插(範圍外取端點;utc 時刻換算為相對 <paramref name="startUtc"/> 的秒數),
    /// tidal 由潮高變化率 dH/dt 的符號決定漲/退並依 |dH/dt| 相對最大值縮放(需 <paramref name="tide"/>)。皆未給時 null(常數流)。
    /// 結構錯誤由 <see cref="Scenario.Scenario.StructuralIssues"/> 先擋,此處只做最低限度的防呆。
    /// </summary>
    public static CurrentModel? FromScenario(Scenario.ScenarioCurrent? current, TideModel? tide, DateTime? startUtc)
    {
        if (current is null) return null;
        if (current.Table is { Count: > 0 } table)
        {
            var points = new List<(double T, double East, double North)>(table.Count);
            foreach (var p in table)
            {
                double t;
                if (p.T is { } given) t = given;
                else if (startUtc is { } start && Engine.SimulationEngine.TryParseUtc(p.Utc, out var utc)) t = (utc - start).TotalSeconds;
                else throw new InvalidDataException("environment.current.table 的 utc 需有可解析的情境 startTimeUtc");
                var speed = Units.KnToMps(p.SpeedKn);
                var dir = Units.DegToRad(p.DirectionDeg);
                points.Add((t, speed * Math.Sin(dir), speed * Math.Cos(dir)));
            }
            return new CurrentTable(points);
        }
        if (current.Tidal is { Flood: { } flood, Ebb: { } ebb } tidal)
        {
            if (tide is null) throw new InvalidDataException("environment.current.tidal 需要 environment.tide");
            return new TidalCurrent(tide,
                Units.DegToRad(flood.DirectionDeg), Units.KnToMps(flood.SpeedKn),
                Units.DegToRad(ebb.DirectionDeg), Units.KnToMps(ebb.SpeedKn),
                (tidal.SlackWindowMin ?? Scenario.ScenarioTidalCurrent.DefaultSlackWindowMin) * 60.0);
        }
        return null;
    }
}

/// <summary>
/// 流場表:(t, 東分量, 北分量) 向量線性內插(方向跨 360° 時不會繞遠路、流速在兩點間不會憑空歸零),t 在範圍外取端點值;
/// 表至少 2 點且 t 嚴格遞增(結構檢查保證)。輸出 set = atan2(E, N)、drift = √(E²+N²)。
/// </summary>
public sealed class CurrentTable : CurrentModel
{
    public IReadOnlyList<(double T, double East, double North)> Points { get; }

    public CurrentTable(IReadOnlyList<(double T, double East, double North)> points)
    {
        if (points.Count == 0) throw new ArgumentException("流場表至少需要 1 點", nameof(points));
        Points = points;
    }

    public override string Source => "table";

    /// <summary>t 的東/北分量(m/s);運算順序與 Python CurrentTable.vector_at 相同。</summary>
    public (double East, double North) VectorAt(double t)
    {
        var pts = Points;
        if (t <= pts[0].T) return (pts[0].East, pts[0].North);
        var last = pts[pts.Count - 1];
        if (t >= last.T) return (last.East, last.North);
        for (var i = 1; i < pts.Count; i++)
        {
            var (t1, e1, n1) = pts[i];
            if (t > t1) continue;
            var (t0, e0, n0) = pts[i - 1];
            var f = (t - t0) / (t1 - t0);
            return (e0 + (e1 - e0) * f, n0 + (n1 - n0) * f);
        }
        return (last.East, last.North);
    }

    public override (double SetRad, double DriftMps) At(double t)
    {
        var (e, n) = VectorAt(t);
        return (Units.NormalizeHeadingRad(Math.Atan2(e, n)), Math.Sqrt(e * e + n * n));
    }
}

/// <summary>
/// 潮流:由潮高模型的變化率決定。rate = dH/dt ≥ 0 → 漲潮向/速,否則退潮向/速;流速 = 該相的速度 × scale,
/// scale 由 ratio = |rate| / MaxRate 經平潮門檻 s 線性映射:ratio ≤ s → 0(平潮),否則 (ratio − s)/(1 − s)。
/// 門檻 s = sin(π·slackWindow/T_ref)(半日潮近似:轉流前後各半個窗口內 |dH/dt| 小於此比例),T_ref 為潮高模型的參考週期
/// (調和潮取最大振幅分潮;潮高表取 M2)。方向固定為情境給定的漲/退潮向(平潮時流速 0,方向為當時相位的方向)。
/// </summary>
public sealed class TidalCurrent : CurrentModel
{
    public TideModel Tide { get; }
    public double FloodSetRad { get; }
    public double FloodMps { get; }
    public double EbbSetRad { get; }
    public double EbbMps { get; }
    public double SlackWindowS { get; }
    /// <summary>平潮門檻(|dH/dt| / MaxRate 比例)</summary>
    public double SlackThreshold { get; }

    public TidalCurrent(TideModel tide, double floodSetRad, double floodMps, double ebbSetRad, double ebbMps, double slackWindowS)
    {
        Tide = tide;
        FloodSetRad = Units.NormalizeHeadingRad(floodSetRad);
        FloodMps = Math.Max(0.0, floodMps);
        EbbSetRad = Units.NormalizeHeadingRad(ebbSetRad);
        EbbMps = Math.Max(0.0, ebbMps);
        SlackWindowS = Math.Max(0.0, slackWindowS);
        SlackThreshold = Threshold(SlackWindowS, tide.ReferencePeriodS);
    }

    public override string Source => "tidal";

    /// <summary>s = sin(π·W/T);W ≥ T/2 時 1(全程平潮);T ≤ 0 時 0。Python TidalCurrent.threshold 相同。</summary>
    public static double Threshold(double slackWindowS, double referencePeriodS)
    {
        if (slackWindowS <= 0.0 || referencePeriodS <= 0.0) return 0.0;
        var x = Math.PI * slackWindowS / referencePeriodS;
        return x >= Math.PI / 2.0 ? 1.0 : Math.Sin(x);
    }

    /// <summary>t 的流速縮放係數 [0, 1] 與相位(漲 true / 退 false)。</summary>
    public (double Scale, bool Flood) ScaleAt(double t)
    {
        var rate = Tide.RateAt(t);
        var max = Tide.MaxRate;
        var ratio = max > 0.0 ? Math.Abs(rate) / max : 0.0;
        var s = SlackThreshold;
        var scale = s >= 1.0 || ratio <= s ? 0.0 : (ratio - s) / (1.0 - s);
        if (scale > 1.0) scale = 1.0;
        return (scale, rate >= 0.0);
    }

    public override (double SetRad, double DriftMps) At(double t)
    {
        var (scale, flood) = ScaleAt(t);
        return flood ? (FloodSetRad, FloodMps * scale) : (EbbSetRad, EbbMps * scale);
    }
}
