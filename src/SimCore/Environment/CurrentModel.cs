using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Environment;

/// <summary>
/// 時變/空間變化流場(情境 environment.current.table | tidal | field;情境引擎設計第 1.5.2 節;需求 R3「隨時間變化的流場」):
/// 流向(去向)與流速為練習時間 t(秒)的純函數(均勻模型 table/tidal),或再加上位置 (x, y) 的純函數(空間網格 field,雙線性內插);
/// 引擎每步以 <see cref="EnvironmentConditions.RefreshCurrent"/> 以自船位置寫入
/// <see cref="EnvironmentConditions.CurrentSetRad"/>/<see cref="EnvironmentConditions.CurrentDriftMps"/>;動力學取樣、state.current 與
/// ramp/snapshot 仍走既有欄位,目標船與其他位置以 <see cref="EnvironmentConditions.CurrentVectorAt"/> 取樣。確定性:只由 t 與位置決定,
/// 不進快照與雜湊(還原後重算);`setEnvironment current` 覆寫為常數後時變/空間變化停止(<see cref="EnvironmentConditions.CurrentOverridden"/>,
/// 進快照),直到再載入情境。Python 參考實作 simosa_brm/current.py 同公式(運算順序相同)。
/// </summary>
public abstract class CurrentModel
{
    /// <summary>state.currentSource:"table" | "tidal" | "field" | "field+tidal" | "field+table"(常數流時 state 省略)</summary>
    public abstract string Source { get; }

    /// <summary>是否隨位置變化(<see cref="CurrentField"/> 為 true;均勻模型 false,位置參數被忽略)</summary>
    public virtual bool SpatiallyVarying => false;

    /// <summary>練習時間 t(秒)的流向(去向,弧度 [0, 2π))與流速(m/s);空間流場取原點 (0, 0)(一般請用含位置的多載)。</summary>
    public abstract (double SetRad, double DriftMps) At(double t);

    /// <summary>練習時間 t(秒)、位置 (xEast, yNorth)(本地 ENU,m)的流向(去向,弧度)與流速(m/s);均勻模型忽略位置。</summary>
    public virtual (double SetRad, double DriftMps) At(double t, double xEast, double yNorth) => At(t);

    /// <summary>
    /// 由情境建立模型:table 以向量(東/北分量)線性內插(範圍外取端點;utc 時刻換算為相對 <paramref name="startUtc"/> 的秒數),
    /// tidal 由潮高變化率 dH/dt 的符號決定漲/退並依 |dH/dt| 相對最大值縮放(需 <paramref name="tide"/>);
    /// field(空間網格)可與 table/tidal 並存:有 field 時回傳 <see cref="CurrentField"/>,table/tidal 只作為其 timeScale 的流速比例來源
    /// (timeScale none 時不使用)。皆未給時 null(常數流)。結構錯誤由 <see cref="Scenario.Scenario.StructuralIssues"/> 先擋,此處只做最低限度的防呆。
    /// </summary>
    public static CurrentModel? FromScenario(Scenario.ScenarioCurrent? current, TideModel? tide, DateTime? startUtc)
    {
        if (current is null) return null;
        var uniform = UniformFromScenario(current, tide, startUtc);
        if (current.Field is { } field) return CurrentField.FromScenario(field, uniform);
        return uniform;
    }

    /// <summary>均勻的時變模型(table | tidal);皆未給時 null。</summary>
    private static CurrentModel? UniformFromScenario(Scenario.ScenarioCurrent current, TideModel? tide, DateTime? startUtc)
    {
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

    /// <summary>各表點流速的最大值(m/s);向量線性內插的流速不會超過此值(空間流場 timeScale table 的比例分母)。</summary>
    public double MaxSpeedMps
    {
        get
        {
            var max = 0.0;
            foreach (var (_, e, n) in Points)
            {
                var s = Math.Sqrt(e * e + n * n);
                if (s > max) max = s;
            }
            return max;
        }
    }

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

/// <summary>
/// 空間變化流場(情境 environment.current.field;情境引擎設計第 1.5.2 節):規則網格上的東/北分量以**雙線性**內插,格外依 outside
/// 取最近邊緣值(edge)或 0(zero);timeScale = tidal | table 時整個場逐步乘以該均勻時變模型的「流速比例」
/// k(t) = 該模型當下流速 / 其最大流速(table 取各表點流速的最大值;tidal 取 max(flood, ebb);最大流速為 0 時 k = 0;k 夾到 [0, 1]),
/// **方向永遠取場的向量**(退潮相位不會使場反向;要反向請以分開的情境或表達成場本身的方向)。
/// 網格:<see cref="U"/>/<see cref="V"/> 為列優先一維陣列,索引 j·Nx + i(j 為 y 索引、i 為 x 索引),對應情境 u_kn[j][i](列為 y、行為 x)。
/// 運算順序與 Python CurrentField.interpolate / vector_at 相同(fx = (x − x0)/dx、a = u00 + (u10 − u00)·tx、u = a + (b − a)·ty),使跨語言夾具
/// (Golden/current_field.csv)比對到 1e-9。
/// </summary>
public sealed class CurrentField : CurrentModel
{
    public double X0 { get; }
    public double Y0 { get; }
    public double Dx { get; }
    public double Dy { get; }
    public int Nx { get; }
    public int Ny { get; }
    /// <summary>東向分量(m/s),列優先 j·Nx + i</summary>
    public double[] U { get; }
    /// <summary>北向分量(m/s),列優先 j·Nx + i</summary>
    public double[] V { get; }
    /// <summary>格外取 0(true)或最近邊緣值(false,預設 edge)</summary>
    public bool OutsideZero { get; }
    /// <summary>timeScale 的流速比例來源(table | tidal 模型;null = none,不縮放)</summary>
    public CurrentModel? Scaler { get; }
    /// <summary>縮放來源的最大流速(m/s;table 取各點流速最大值、tidal 取 max(flood, ebb);Scaler 為 null 時 0)</summary>
    public double ScalerMaxMps { get; }

    public CurrentField(double x0, double y0, double dx, double dy, int nx, int ny, double[] u, double[] v, bool outsideZero, CurrentModel? scaler)
    {
        if (!(dx > 0.0) || !(dy > 0.0)) throw new ArgumentException("流場網格的 dx/dy 必須為正");
        if (nx < 2 || ny < 2) throw new ArgumentException("流場網格的 nx/ny 必須 ≥ 2");
        if (u.Length != nx * ny || v.Length != nx * ny) throw new ArgumentException("流場網格的 u/v 長度必須為 nx·ny");
        X0 = x0; Y0 = y0; Dx = dx; Dy = dy; Nx = nx; Ny = ny; U = u; V = v;
        OutsideZero = outsideZero;
        Scaler = scaler;
        ScalerMaxMps = scaler switch
        {
            CurrentTable table => table.MaxSpeedMps,
            TidalCurrent tidal => Math.Max(tidal.FloodMps, tidal.EbbMps),
            null => 0.0,
            _ => throw new ArgumentException("timeScale 只能為 table 或 tidal 模型", nameof(scaler)),
        };
    }

    /// <summary>由情境區段建立(節 → m/s;outside 預設 edge;timeScale 對應的均勻模型為 <paramref name="uniform"/>,不符時 InvalidDataException)。</summary>
    public static CurrentField FromScenario(Scenario.ScenarioCurrentField field, CurrentModel? uniform)
    {
        var grid = field.Grid ?? throw new InvalidDataException("environment.current.field 需要 grid");
        var nx = grid.Nx;
        var ny = grid.Ny;
        static double[] Flatten(List<List<double>>? rows, int nx, int ny, string name)
        {
            if (rows is null || rows.Count != ny) throw new InvalidDataException($"environment.current.field.{name} 須有 ny = {ny} 列");
            var flat = new double[nx * ny];
            for (var j = 0; j < ny; j++)
            {
                var row = rows[j];
                if (row is null || row.Count != nx) throw new InvalidDataException($"environment.current.field.{name}[{j}] 須有 nx = {nx} 個值");
                for (var i = 0; i < nx; i++) flat[j * nx + i] = Units.KnToMps(row[i]);
            }
            return flat;
        }
        var outsideZero = string.Equals(field.Outside ?? Scenario.ScenarioCurrentField.DefaultOutside, "zero", StringComparison.OrdinalIgnoreCase);
        var timeScale = (field.TimeScale ?? Scenario.ScenarioCurrentField.DefaultTimeScale).ToLowerInvariant();
        CurrentModel? scaler = timeScale switch
        {
            "none" => null,
            "tidal" => uniform as TidalCurrent ?? throw new InvalidDataException("environment.current.field.timeScale tidal 需要 current.tidal"),
            "table" => uniform as CurrentTable ?? throw new InvalidDataException("environment.current.field.timeScale table 需要 current.table"),
            _ => throw new InvalidDataException($"environment.current.field.timeScale 須為 none | tidal | table:{field.TimeScale}"),
        };
        return new CurrentField(grid.X0, grid.Y0, grid.Dx, grid.Dy, nx, ny, Flatten(field.UKn, nx, ny, "u_kn"), Flatten(field.VKn, nx, ny, "v_kn"), outsideZero, scaler);
    }

    public override string Source => Scaler is null ? "field" : "field+" + Scaler.Source;
    public override bool SpatiallyVarying => true;

    /// <summary>t 的流速比例 k ∈ [0, 1](none → 1;最大流速 0 → 0)。Python CurrentField.scale_at 相同。</summary>
    public double ScaleAt(double t)
    {
        if (Scaler is null) return 1.0;
        if (!(ScalerMaxMps > 0.0)) return 0.0;
        var k = Scaler.At(t).DriftMps / ScalerMaxMps;
        return k > 1.0 ? 1.0 : k;
    }

    /// <summary>位置 (x, y) 未經時間縮放的東/北分量(m/s):雙線性內插;格外依 OutsideZero。Python CurrentField.interpolate 相同運算順序。</summary>
    public (double U, double V) Interpolate(double xEast, double yNorth)
    {
        var fx = (xEast - X0) / Dx;
        var fy = (yNorth - Y0) / Dy;
        double maxX = Nx - 1, maxY = Ny - 1;
        if (OutsideZero && (fx < 0.0 || fx > maxX || fy < 0.0 || fy > maxY)) return (0.0, 0.0);
        if (fx < 0.0) fx = 0.0; else if (fx > maxX) fx = maxX;
        if (fy < 0.0) fy = 0.0; else if (fy > maxY) fy = maxY;
        var i = (int)Math.Floor(fx);
        if (i > Nx - 2) i = Nx - 2;
        var j = (int)Math.Floor(fy);
        if (j > Ny - 2) j = Ny - 2;
        var tx = fx - i;
        var ty = fy - j;
        var k00 = j * Nx + i;
        var k10 = k00 + 1;
        var k01 = k00 + Nx;
        var k11 = k01 + 1;
        var ua = U[k00] + (U[k10] - U[k00]) * tx;
        var ub = U[k01] + (U[k11] - U[k01]) * tx;
        var va = V[k00] + (V[k10] - V[k00]) * tx;
        var vb = V[k01] + (V[k11] - V[k01]) * tx;
        return (ua + (ub - ua) * ty, va + (vb - va) * ty);
    }

    /// <summary>t、位置 (x, y) 的東/北分量(m/s)= 內插值 × k(t)(none 時不乘)。Python CurrentField.vector_at 相同。</summary>
    public (double East, double North) VectorAt(double t, double xEast, double yNorth)
    {
        var (u, v) = Interpolate(xEast, yNorth);
        if (Scaler is null) return (u, v);
        var k = ScaleAt(t);
        return (u * k, v * k);
    }

    public override (double SetRad, double DriftMps) At(double t) => At(t, 0.0, 0.0);

    public override (double SetRad, double DriftMps) At(double t, double xEast, double yNorth)
    {
        var (e, n) = VectorAt(t, xEast, yNorth);
        return (Units.NormalizeHeadingRad(Math.Atan2(e, n)), Math.Sqrt(e * e + n * n));
    }
}
