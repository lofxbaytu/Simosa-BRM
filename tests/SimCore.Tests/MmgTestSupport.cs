using System.Globalization;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// MMG 測試共用:FSB1 係數檔、試俥目標、以 <see cref="SimulationEngine"/> 執行的標準操縱試驗
/// (移植自 Python simosa_brm.manoeuvres,量測定義相同:MSC.137(76) / ITTC 7.5-02-06-03),
/// 以及 Python 參考實作匯出的黃金時間序列(tests/SimCore.Tests/Golden/*.csv)。
/// </summary>
internal static class MmgTestSupport
{
    public const double Dt = 0.02;
    public const double DeepWaterM = 1000.0; // h/T ≥ 11 → 淺水倍率為 1,與 Python water_depth=None 等價
    public static string GoldenDirectory => Path.Combine(TestData.Root, "tests", "SimCore.Tests", "Golden");

    private static readonly Lazy<MmgCoefficients> Fsb1FullCoefficients = new(() => MmgCoefficients.LoadFromDataRoot(TestData.Root, "FSB1", LoadingCondition.Full));
    private static readonly Lazy<JsonDocument> Fsb1TrialTargets = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "data", "ships", "FSB1", "trial_targets.json"))));

    public static MmgCoefficients Fsb1Full => Fsb1FullCoefficients.Value;
    public static JsonElement Fsb1Targets => Fsb1TrialTargets.Value.RootElement;
    public static MmgDynamics Fsb1FullDynamics() => new(TestData.Fsb1(), LoadingCondition.Full, Fsb1Full);

    public static double Tol(string key) => Fsb1Targets.GetProperty("tolerances").GetProperty(key).GetDouble();
    public static double TurningTolerance(double target, double lpp) => Math.Max(Tol("turning_pct") / 100.0 * target, Tol("turning_minL") * lpp);
    public static double ZigzagTolerance(double target) => Math.Max(Tol("zigzag_deg"), Tol("zigzag_pct") / 100.0 * target);
    public static double StoppingTolerance(double target) => Tol("stopping_pct") / 100.0 * target;

    /// <summary>深水、無風無流、航向北、原點 (0,0) 的直航情境;rpm 省略時由引擎取該航速的直航平衡轉速(Python reset 相同)。</summary>
    public static Scenario.Scenario MmgScenario(double speedKn, double? rpm = null, string? telegraph = null,
        double waterDepth = DeepWaterM, LoadingCondition loading = LoadingCondition.Full, string id = "mmg-test") => new()
    {
        Id = id,
        Seed = 1,
        Ship = new ScenarioShip { Id = "FSB1", Loading = loading },
        Origin = new GeoPoint { Lat = 23.80, Lon = 120.15 },
        Initial = new ScenarioInitial { Position = new ScenarioPosition { X = 0.0, Y = 0.0 }, Heading = 0.0, Speed = speedKn, Rpm = rpm, Telegraph = telegraph },
        Environment = new ScenarioEnvironment { WaterDepth = waterDepth },
    };

    public static SimulationEngine MmgEngine(Scenario.Scenario sc, MmgCoefficients? coeffs = null, ShipParticulars? ship = null)
        => new(ship ?? TestData.Fsb1(), sc, (s, l) => new MmgDynamics(s, l, coeffs ?? Fsb1Full),
            new EngineOptions { Dt = Dt, AutoSnapshotIntervalTicks = 0 });

    public static SimulationEngine MmgEngine(double speedKn, double? rpm = null, string? telegraph = null)
        => MmgEngine(MmgScenario(speedKn, rpm, telegraph));

    private static double WrapPi(double a) => Units.WrapRadPi(a);
    private static double SpeedKn(in StateVector s) => Units.MpsToKn(Math.Sqrt(s.U * s.U + s.V * s.V));

    // ------------------------------------------------------------------ 迴旋

    public sealed class TurningResult
    {
        public double AdvanceM = double.NaN, TransferM = double.NaN, TacticalDiameterM = double.NaN;
        public double SteadySpeedKn = double.NaN, SteadyRotDegPerMin = double.NaN, SteadyDiameterM = double.NaN;
        public Dictionary<int, (double T, double SpeedKn, double Along, double Across)> Marks = new();
        public bool Completed;
    }

    /// <summary>定常迴旋:90° 時前進距離/橫距、180° 時戰術直徑(步間線性內插)、360° 後定常速度與迴轉率。</summary>
    public static TurningResult TurningCircle(double rudderDeg, string side, double approachSpeedKn,
        double maxHeadingChangeDeg = 540.0, double maxTimeS = 1800.0)
    {
        var sign = side.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? 1.0 : -1.0;
        var e = MmgEngine(approachSpeedKn);
        var res = new TurningResult();
        e.Enqueue(SimCommand.Rudder(sign * rudderDeg));
        var psi0 = e.Motion.Psi;
        var (x0, y0) = (e.Motion.X, e.Motion.Y);
        var total = 0.0;
        var prevPsi = psi0;
        var nextMark = 90;
        var speedsAfter360 = new List<double>();
        var rotsAfter360 = new List<double>();
        var steps = (int)Math.Round(maxTimeS / Dt);
        var prev = (Change: 0.0, T: 0.0, Speed: 0.0, Along: 0.0, Across: 0.0);
        for (var i = 0; i < steps; i++)
        {
            e.Step();
            var s = e.Motion;
            total += WrapPi(s.Psi - prevPsi);
            prevPsi = s.Psi;
            var change = sign * Units.RadToDeg(total);
            var dx = s.X - x0;
            var dy = s.Y - y0;
            var along = dx * Math.Sin(psi0) + dy * Math.Cos(psi0);
            var across = sign * (dx * Math.Cos(psi0) - dy * Math.Sin(psi0));
            var cur = (Change: change, T: e.Time, Speed: SpeedKn(s), Along: along, Across: across);
            while (nextMark <= 360 && change >= nextMark)
            {
                var f = cur.Change > prev.Change ? (nextMark - prev.Change) / (cur.Change - prev.Change) : 1.0;
                f = Math.Clamp(f, 0.0, 1.0);
                var tm = prev.T + f * (cur.T - prev.T);
                var vm = prev.Speed + f * (cur.Speed - prev.Speed);
                var alm = prev.Along + f * (cur.Along - prev.Along);
                var acm = prev.Across + f * (cur.Across - prev.Across);
                res.Marks[nextMark] = (tm, vm, alm, acm);
                if (nextMark == 90) (res.AdvanceM, res.TransferM) = (alm, acm);
                else if (nextMark == 180) res.TacticalDiameterM = acm;
                nextMark += 90;
            }
            prev = cur;
            if (change >= 360.0)
            {
                speedsAfter360.Add(Math.Sqrt(s.U * s.U + s.V * s.V));
                rotsAfter360.Add(s.R);
            }
            if (change >= maxHeadingChangeDeg) { res.Completed = true; break; }
        }
        if (speedsAfter360.Count > 0)
        {
            var n = speedsAfter360.Count;
            var tail = speedsAfter360.Skip(n / 2).ToList();
            var rt = rotsAfter360.Skip(n / 2).ToList();
            var vMean = tail.Average();
            var rMean = rt.Average();
            res.SteadySpeedKn = Units.MpsToKn(vMean);
            res.SteadyRotDegPerMin = Units.RadPerSecToDegPerMin(rMean);
            if (Math.Abs(rMean) > 1e-9) res.SteadyDiameterM = 2.0 * vMean / Math.Abs(rMean);
        }
        return res;
    }

    // ------------------------------------------------------------------ Z 形

    public sealed class ZigzagResult
    {
        public List<double> OvershootsDeg = new();
        public List<double> ReversalTimesS = new();
        public bool Completed;
    }

    /// <summary>Z 形操舵:航向變化達 ±heading 時反舵,量各次超越角(反舵後航向繼續偏離的最大值 − heading)。</summary>
    public static ZigzagResult Zigzag(double rudderDeg, double headingDeg, string firstSide, double approachSpeedKn,
        int nOvershoots = 3, double maxTimeS = 1200.0)
    {
        var sign = firstSide.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? -1.0 : 1.0;
        var e = MmgEngine(approachSpeedKn);
        var res = new ZigzagResult();
        var psi0 = e.Motion.Psi;
        var cur = sign;
        e.Enqueue(SimCommand.Rudder(cur * rudderDeg));
        var waiting = true;
        var extreme = 0.0;
        var steps = (int)Math.Round(maxTimeS / Dt);
        for (var i = 0; i < steps; i++)
        {
            e.Step();
            var s = e.Motion;
            var dpsi = Units.RadToDeg(WrapPi(s.Psi - psi0));
            if (waiting)
            {
                if (cur * dpsi >= headingDeg)
                {
                    cur = -cur;
                    e.Enqueue(SimCommand.Rudder(cur * rudderDeg));
                    res.ReversalTimesS.Add(e.Time);
                    waiting = false;
                    extreme = dpsi;
                }
            }
            else
            {
                if (-cur * dpsi > -cur * extreme) extreme = dpsi;
                else if (-cur * s.R < 0.0)
                {
                    res.OvershootsDeg.Add(Math.Abs(extreme) - headingDeg);
                    waiting = true;
                    if (res.OvershootsDeg.Count >= nOvershoots) { res.Completed = true; break; }
                }
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ 停船

    public sealed class StopResult
    {
        public double AsternStartS = double.NaN, StopTimeS = double.NaN, TrackReachM = double.NaN, HeadReachM = double.NaN, SideReachM = double.NaN;
        public double TimeToTargetS = double.NaN, DistanceToTargetM = double.NaN, FinalHeadingChangeDeg = double.NaN;
        public bool Completed;
    }

    private static StopResult RunStop(SimulationEngine e, StopResult res, double maxTimeS, bool stopWhenUZero, double? targetKn)
    {
        var psi0 = e.Motion.Psi;
        var (x0, y0) = (e.Motion.X, e.Motion.Y);
        var track = 0.0;
        var (px, py) = (x0, y0);
        var steps = (int)Math.Round(maxTimeS / Dt);
        for (var i = 0; i < steps; i++)
        {
            e.Step();
            var s = e.Motion;
            track += Math.Sqrt((s.X - px) * (s.X - px) + (s.Y - py) * (s.Y - py));
            (px, py) = (s.X, s.Y);
            if (double.IsNaN(res.AsternStartS) && e.Actuators.Rpm < 0.0) res.AsternStartS = e.Time;
            var spdKn = SpeedKn(s);
            var dx = s.X - x0;
            var dy = s.Y - y0;
            var along = dx * Math.Sin(psi0) + dy * Math.Cos(psi0);
            var across = dx * Math.Cos(psi0) - dy * Math.Sin(psi0);
            if (targetKn is { } tk && double.IsNaN(res.TimeToTargetS) && spdKn <= tk)
            {
                res.TimeToTargetS = e.Time;
                res.DistanceToTargetM = track;
                if (!stopWhenUZero)
                {
                    res.HeadReachM = along;
                    res.SideReachM = Math.Abs(across);
                    res.TrackReachM = track;
                    res.FinalHeadingChangeDeg = Units.RadToDeg(WrapPi(s.Psi - psi0));
                    res.Completed = true;
                    break;
                }
            }
            if (stopWhenUZero && s.U <= 0.0)
            {
                res.StopTimeS = e.Time;
                res.TrackReachM = track;
                res.HeadReachM = along;
                res.SideReachM = Math.Abs(across);
                res.FinalHeadingChangeDeg = Units.RadToDeg(WrapPi(s.Psi - psi0));
                res.Completed = true;
                break;
            }
        }
        return res;
    }

    /// <summary>緊急停船:直航穩態 → EFAS;量倒車啟動時間、停船時間(u 過零)、航跡距離、head/side reach。</summary>
    public static StopResult CrashStop(double approachSpeedKn, TelegraphOrder astern = TelegraphOrder.EFAS, double maxTimeS = 1500.0)
    {
        var e = MmgEngine(approachSpeedKn);
        e.Enqueue(SimCommand.Telegraph(astern));
        return RunStop(e, new StopResult(), maxTimeS, stopWhenUZero: true, targetKn: null);
    }

    /// <summary>慣性停船(停俥滑行):到達 target 的時間與距離。</summary>
    public static StopResult InertiaStop(double approachSpeedKn, double targetKn = 5.0, double maxTimeS = 2400.0)
    {
        var e = MmgEngine(approachSpeedKn);
        e.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
        return RunStop(e, new StopResult(), maxTimeS, stopWhenUZero: false, targetKn: targetKn);
    }

    /// <summary>倒退(半速退運轉中)下全速進令至停船(u 由負過零)。</summary>
    public static StopResult CrashAhead(double asternSpeedKn, TelegraphOrder ahead = TelegraphOrder.FAH, double maxTimeS = 900.0)
    {
        var ship = TestData.Fsb1();
        var e = MmgEngine(MmgScenario(-asternSpeedKn, rpm: ship.TelegraphRpm(TelegraphOrder.HAS), telegraph: "HAS"));
        e.Enqueue(SimCommand.Telegraph(ahead));
        var res = new StopResult();
        var psi0 = e.Motion.Psi;
        var (x0, y0) = (e.Motion.X, e.Motion.Y);
        var track = 0.0;
        var (px, py) = (x0, y0);
        var steps = (int)Math.Round(maxTimeS / Dt);
        for (var i = 0; i < steps; i++)
        {
            e.Step();
            var s = e.Motion;
            track += Math.Sqrt((s.X - px) * (s.X - px) + (s.Y - py) * (s.Y - py));
            (px, py) = (s.X, s.Y);
            if (s.U >= 0.0)
            {
                res.StopTimeS = e.Time;
                res.TrackReachM = track;
                var dx = s.X - x0;
                var dy = s.Y - y0;
                res.HeadReachM = Math.Abs(dx * Math.Sin(psi0) + dy * Math.Cos(psi0));
                res.SideReachM = Math.Abs(dx * Math.Cos(psi0) - dy * Math.Sin(psi0));
                res.Completed = true;
                break;
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ 艏側推迴轉

    public sealed class ThrusterTurnResult
    {
        public double TimeS = double.NaN, SteadyRotDegPerMin = double.NaN;
        public bool Completed;
    }

    /// <summary>主機停俥、艏側推全推,量 90° 迴轉時間與定常迴轉率(60 s 後最大值)。</summary>
    public static ThrusterTurnResult ThrusterTurn(string side, double angleDeg = 90.0, double speedKn = 0.0, double maxTimeS = 1800.0)
    {
        var sign = side.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? 1.0 : -1.0;
        var e = MmgEngine(MmgScenario(speedKn, rpm: 0.0));
        e.Enqueue(SimCommand.Thruster(sign));
        var res = new ThrusterTurnResult();
        var psi0 = e.Motion.Psi;
        var total = 0.0;
        var prev = psi0;
        var maxRot = double.NegativeInfinity;
        var steps = (int)Math.Round(maxTimeS / Dt);
        for (var i = 0; i < steps; i++)
        {
            e.Step();
            var s = e.Motion;
            total += WrapPi(s.Psi - prev);
            prev = s.Psi;
            if (e.Time > 60.0) maxRot = Math.Max(maxRot, sign * s.R);
            if (sign * Units.RadToDeg(total) >= angleDeg)
            {
                res.TimeS = e.Time;
                res.Completed = true;
                break;
            }
        }
        if (!double.IsNegativeInfinity(maxRot)) res.SteadyRotDegPerMin = Units.RadPerSecToDegPerMin(maxRot);
        return res;
    }

    // ------------------------------------------------------------------ Python 匯出的黃金時間序列

    public sealed record GoldenSample(long Tick, double T, double X, double Y, double PsiDeg, double U, double V, double R, double DeltaDeg, double Rpm, double Thr);

    public sealed class GoldenSeries
    {
        public string Title = "";
        public double SpeedKn, HeadingDeg, Rpm, DepthM, Dt;
        public string Telegraph = "";
        public List<(long Tick, SimCommand Command)> Events = new();
        public List<GoldenSample> Samples = new();
    }

    /// <summary>讀取 export_reference.py 的 CSV(# 標頭含初始狀態與事件)。</summary>
    public static GoldenSeries LoadGolden(string fileName)
    {
        var g = new GoldenSeries();
        var inv = CultureInfo.InvariantCulture;
        foreach (var raw in File.ReadLines(Path.Combine(GoldenDirectory, fileName)))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var body = line.TrimStart('#').Trim();
                if (body.StartsWith("initial:"))
                {
                    foreach (var kv in body["initial:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = kv.IndexOf('=');
                        if (eq < 0) continue;
                        var (k, v) = (kv[..eq], kv[(eq + 1)..]);
                        switch (k)
                        {
                            case "speed_kn": g.SpeedKn = double.Parse(v, inv); break;
                            case "heading_deg": g.HeadingDeg = double.Parse(v, inv); break;
                            case "rpm": g.Rpm = double.Parse(v, inv); break;
                            case "depth_m": g.DepthM = double.Parse(v, inv); break;
                            case "dt": g.Dt = double.Parse(v, inv); break;
                            case "telegraph": g.Telegraph = v; break;
                        }
                    }
                }
                else if (body.StartsWith("event:"))
                {
                    long tick = 0;
                    SimCommand? cmd = null;
                    foreach (var kv in body["event:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = kv.IndexOf('=');
                        if (eq < 0) continue;
                        var (k, v) = (kv[..eq], kv[(eq + 1)..]);
                        switch (k)
                        {
                            case "tick": tick = long.Parse(v, inv); break;
                            case "rudder_deg": cmd = SimCommand.Rudder(double.Parse(v, inv)); break;
                            case "telegraph": cmd = SimCommand.Telegraph(Enum.Parse<TelegraphOrder>(v, ignoreCase: true)); break;
                            case "rpm": cmd = SimCommand.Rpm(double.Parse(v, inv)); break;
                            case "thruster": cmd = SimCommand.Thruster(double.Parse(v, inv)); break;
                        }
                    }
                    if (cmd is not null) g.Events.Add((tick, cmd));
                }
                else if (g.Title.Length == 0) g.Title = body;
                continue;
            }
            if (line.StartsWith("tick,")) continue;
            var c = line.Split(',');
            g.Samples.Add(new GoldenSample((long)double.Parse(c[0], inv), double.Parse(c[1], inv), double.Parse(c[2], inv), double.Parse(c[3], inv),
                double.Parse(c[4], inv), double.Parse(c[5], inv), double.Parse(c[6], inv), double.Parse(c[7], inv), double.Parse(c[8], inv),
                double.Parse(c[9], inv), double.Parse(c[10], inv)));
        }
        if (g.Samples.Count == 0) throw new InvalidDataException($"黃金檔 {fileName} 無資料列");
        return g;
    }
}
