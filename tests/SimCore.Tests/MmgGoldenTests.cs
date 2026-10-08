using System.Globalization;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using Xunit;
using Xunit.Abstractions;
using static SimosaBRM.SimCore.Tests.MmgTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// MMG 黃金測試(規劃書第 6.4 節公差、第 14 章驗收):以 FSB1 滿載已識別係數(data/ships/FSB1/coefficients.full.json)
/// 執行 35° 迴旋、10/10 與 20/20 Z 形、緊急停船、速度–轉速穩態,與 trial_targets.json 的 identification / validation 目標比較,
/// 公差與 Python 測試(src/Tools.Calibration/tests)相同。Python 已標 xfail 的項目(右迴旋橫距、慣性停船、倒車→進車、側推 90° 左)
/// 在此以 Skip 標記並附 Python 的實際值;<see cref="GoldenReport"/> 另列出 C# 的實際值。
/// 另含 C# 對 Python 數值一致性測試(Golden/*.csv,由 export_reference.py 產生):相同係數、dt、舵令/車鐘時序,600 s 內位置與航向差異 &lt;1%。
/// </summary>
public class MmgGoldenTests
{
    private readonly ITestOutputHelper _out;
    public MmgGoldenTests(ITestOutputHelper output) => _out = output;

    private static JsonElement Ident => Fsb1Targets.GetProperty("identification");
    private static JsonElement Valid => Fsb1Targets.GetProperty("validation");
    private static double Lpp => Fsb1Full.Reference.Length_m;

    [Fact]
    public void CoefficientsAreIdentifiedAndMatchSchemaBasics()
    {
        var c = Fsb1Full;
        Assert.True(c.IsIdentified, "黃金測試需要已識別的係數檔(source.method = identify)");
        Assert.Equal("FSB1", c.ShipId);
        Assert.Equal("full", c.Loading);
        Assert.Equal(99.0, c.Reference.Length_m);
        Assert.Equal(11, c.Engine.Telegraph.Count);
        Assert.NotNull(c.Extra); // "$schema" 等未知欄位保留
        Assert.Contains("$schema", c.Extra!.Keys);
        Assert.True(c.Hull.Resistance.Extra!.ContainsKey("trialFit"));
        var d = Fsb1FullDynamics();
        Assert.Equal("mmg/1", d.ModelName);
        Assert.True(d.ActuatorParameters.CoupledRk4 && d.ActuatorParameters.EngineStateMachine);
        Assert.Equal(70.0, d.ActuatorParameters.RudderMaxDeg);
    }

    // ------------------------------------------------------------------ 35° 迴旋(識別區)

    [Theory]
    [InlineData("port", "advance_m")]
    [InlineData("port", "transfer_m")]
    [InlineData("port", "tacticalDiameter_m")]
    [InlineData("starboard", "advance_m")]
    [InlineData("starboard", "transfer_m", Skip = "已知偏差(Python xfail):右迴旋橫距 sim 142.8 m vs trial 175.5 m(公差 ±29.7 m);模型結構限制,見 README 已知限制;C# 實際值見 GoldenReport")]
    [InlineData("starboard", "tacticalDiameter_m")]
    public void TurningCircle35_WithinTolerance(string side, string key)
    {
        var tc = Ident.GetProperty("turningCircle35");
        var r = TurningCircle(tc.GetProperty("rudder_deg").GetDouble(), side, tc.GetProperty("approachSpeed_kn").GetDouble());
        Assert.True(r.Completed);
        var target = tc.GetProperty(side).GetProperty(key).GetDouble();
        var sim = key switch { "advance_m" => r.AdvanceM, "transfer_m" => r.TransferM, _ => r.TacticalDiameterM };
        var tol = TurningTolerance(target, Lpp);
        _out.WriteLine($"FSB1 {side} {key}: trial {target:F1} sim {sim:F1} tol ±{tol:F1}");
        Assert.InRange(sim, target - tol, target + tol);
    }

    [Theory]
    [InlineData("port")]
    [InlineData("starboard")]
    public void TurningCircle35_SteadySpeedInTurn(string side)
    {
        var tc = Ident.GetProperty("turningCircle35");
        var r = TurningCircle(35.0, side, tc.GetProperty("approachSpeed_kn").GetDouble());
        var range = tc.GetProperty("steadySpeedInTurn_kn");
        var (lo, hi) = (range[0].GetDouble(), range[1].GetDouble());
        var vTol = Tol("speed_kn");
        _out.WriteLine($"FSB1 {side} steady speed: trial {lo}–{hi} sim {r.SteadySpeedKn:F2}");
        Assert.InRange(r.SteadySpeedKn, lo - vTol, hi + vTol);
    }

    [Fact]
    public void TurningAsymmetry_StarboardDiameterLargerThanPort()
    {
        // 試俥:右迴旋戰術直徑大於左迴旋(右旋單俥);模擬方向須一致
        var rp = TurningCircle(35.0, "port", 14.5, maxHeadingChangeDeg: 200.0);
        var rs = TurningCircle(35.0, "starboard", 14.5, maxHeadingChangeDeg: 200.0);
        Assert.True(rs.TacticalDiameterM > rp.TacticalDiameterM, $"stbd {rs.TacticalDiameterM:F1} vs port {rp.TacticalDiameterM:F1}");
    }

    [Fact]
    public void Schilling70DegreeRudderTurnsTighterThan35()
    {
        var r35 = TurningCircle(35.0, "port", 10.0, maxHeadingChangeDeg: 200.0);
        var r70 = TurningCircle(70.0, "port", 10.0, maxHeadingChangeDeg: 200.0);
        _out.WriteLine($"35°: TD {r35.TacticalDiameterM:F1} m;70°: TD {r70.TacticalDiameterM:F1} m(Python 300.6 / 272.6)");
        Assert.True(r70.TacticalDiameterM < r35.TacticalDiameterM);
    }

    // ------------------------------------------------------------------ Z 形

    [Theory]
    [InlineData("portFirst")]
    [InlineData("starboardFirst")]
    public void Zigzag10_10_WithinTolerance(string first)
    {
        var zz = Ident.GetProperty("zigzag10_10");
        var trial = zz.GetProperty(first).GetProperty("overshoots_deg").EnumerateArray().Select(x => x.GetDouble()).ToList();
        var z = Zigzag(10.0, 10.0, first.Replace("First", ""), zz.GetProperty("approachSpeed_kn").GetDouble(), nOvershoots: trial.Count);
        Assert.True(z.Completed && z.OvershootsDeg.Count == trial.Count);
        for (var i = 0; i < trial.Count; i++)
        {
            var tol = ZigzagTolerance(trial[i]);
            _out.WriteLine($"FSB1 10/10 {first} overshoot {i + 1}: trial {trial[i]} sim {z.OvershootsDeg[i]:F2} tol ±{tol:F1}");
            Assert.InRange(z.OvershootsDeg[i], trial[i] - tol, trial[i] + tol);
        }
    }

    [Theory]
    [InlineData("portFirst")]
    [InlineData("starboardFirst")]
    public void Zigzag20_20_Validation(string first)
    {
        // 驗證資料(未用於擬合);Python 目前兩舷皆在公差內
        var zz = Valid.GetProperty("zigzag20_20");
        var trial = zz.GetProperty(first).GetProperty("overshoots_deg").EnumerateArray().Select(x => x.GetDouble()).ToList();
        var z = Zigzag(20.0, 20.0, first.Replace("First", ""), zz.GetProperty("approachSpeed_kn").GetDouble(), nOvershoots: trial.Count);
        Assert.True(z.Completed);
        for (var i = 0; i < trial.Count; i++)
        {
            var tol = ZigzagTolerance(trial[i]);
            _out.WriteLine($"FSB1 20/20 {first} overshoot {i + 1}: trial {trial[i]} sim {z.OvershootsDeg[i]:F2} tol ±{tol:F1}");
            Assert.InRange(z.OvershootsDeg[i], trial[i] - tol, trial[i] + tol);
        }
    }

    // ------------------------------------------------------------------ 停船(驗證區)

    [Fact]
    public void CrashStop_Validation()
    {
        var cs = Valid.GetProperty("crashStop");
        var r = CrashStop(cs.GetProperty("approachSpeed_kn").GetDouble());
        Assert.True(r.Completed && !double.IsNaN(r.AsternStartS));
        _out.WriteLine($"FSB1 crash stop: astern start {r.AsternStartS:F0} s (trial {cs.GetProperty("asternStart_s").GetDouble()}), heading change {r.FinalHeadingChangeDeg:+0.0}°");
        Assert.True(r.FinalHeadingChangeDeg > 0.0, "右旋槳倒車:艉向左、艏向右");
        var track = cs.GetProperty("trackReach_m").GetDouble();
        var time = cs.GetProperty("stopTime_s").GetDouble();
        _out.WriteLine($"FSB1 緊急停船 航跡距離: sim {r.TrackReachM:F1} vs trial {track} m(公差 ±{StoppingTolerance(track):F1});停船時間 sim {r.StopTimeS:F1} vs {time} s(±{StoppingTolerance(time):F1})");
        Assert.InRange(r.TrackReachM, track - StoppingTolerance(track), track + StoppingTolerance(track));
        Assert.InRange(r.StopTimeS, time - StoppingTolerance(time), time + StoppingTolerance(time));
    }

    [Fact(Skip = "已知偏差(Python xfail):慣性停船減至 5 kn 時間 sim 641.2 s vs trial 258 s(公差 ±38.7 s);試俥航跡彎曲,直線滑行無法重現;C# 實際值見 GoldenReport")]
    public void InertiaStop_TimeTo5kn_Validation()
    {
        var ins = Valid.GetProperty("inertiaStop");
        var r = InertiaStop(ins.GetProperty("approachSpeed_kn").GetDouble(), 5.0);
        var t = ins.GetProperty("timeTo5kn_s").GetDouble();
        Assert.InRange(r.TimeToTargetS, t - StoppingTolerance(t), t + StoppingTolerance(t));
    }

    [Fact(Skip = "已知偏差(Python xfail):倒車 6.2 kn → 全速進 停船時間 sim 176.8 s vs trial 121 s(公差 ±18.1 s);C# 實際值見 GoldenReport")]
    public void CrashAhead_StopTime_Validation()
    {
        var ca = Valid.GetProperty("crashAhead");
        var r = CrashAhead(ca.GetProperty("approachSpeedAstern_kn").GetDouble());
        var t = ca.GetProperty("time_s").GetDouble();
        Assert.InRange(r.StopTimeS, t - StoppingTolerance(t), t + StoppingTolerance(t));
    }

    [Theory]
    [InlineData("port", Skip = "已知偏差(Python xfail):艏側推 90° 左 sim 219.8 s vs trial 264 s(公差 ±39.6 s);C# 實際值見 GoldenReport")]
    [InlineData("starboard")]
    public void BowThrusterTurn90_Validation(string side)
    {
        var bt = Valid.GetProperty("bowThrusterTurn90");
        var r = ThrusterTurn(side, 90.0, bt.GetProperty("speed_kn").GetDouble());
        Assert.True(r.Completed);
        var t = bt.GetProperty($"{side}_s").GetDouble();
        _out.WriteLine($"FSB1 艏側推 {side} 90°: sim {r.TimeS:F1} vs trial {t} s(公差 ±{StoppingTolerance(t):F1});定常 ROT {r.SteadyRotDegPerMin:F1} °/min");
        Assert.InRange(r.TimeS, t - StoppingTolerance(t), t + StoppingTolerance(t));
    }

    // ------------------------------------------------------------------ 速度–轉速穩態

    [Fact]
    public void SpeedRpm_SteadyState_WithinHalfKnot()
    {
        var d = Fsb1FullDynamics();
        var tol = Tol("speed_kn");
        foreach (var p in Valid.GetProperty("speedPower").EnumerateArray())
        {
            var rpm = p.GetProperty("rpm").GetDouble();
            var trial = p.GetProperty("speed_kn").GetDouble();
            var sim = Units.MpsToKn(d.SteadySpeedForRpm(rpm));
            _out.WriteLine($"FSB1 {rpm} rpm: trial {trial:F2} kn, sim {sim:F2} kn");
            Assert.InRange(sim, trial - tol, trial + tol);
        }
        // 海報車鐘對照:單調遞增且與海報值差距 1.5 kn 內
        var ship = TestData.Fsb1();
        var prev = -1.0;
        foreach (var o in new[] { TelegraphOrder.DSAH, TelegraphOrder.SAH, TelegraphOrder.HAH, TelegraphOrder.FAH, TelegraphOrder.NAVF })
        {
            var v = Units.MpsToKn(d.SteadySpeedForRpm(ship.TelegraphRpm(o)));
            Assert.True(v > prev);
            prev = v;
            if (ship.TelegraphSpeedKn(o, LoadingCondition.Full) is { } poster) Assert.InRange(v, poster - 1.5, poster + 1.5);
        }
    }

    [Fact]
    public void SpeedRpm_TimeIntegrationAgreesWithSteadySolution()
    {
        // 由靜止以 168 rpm 加速 2400 s(dt 0.02)應收斂到解析穩態 0.1 kn 內
        var d = Fsb1FullDynamics();
        var steady = Units.MpsToKn(d.SteadySpeedForRpm(168.0));
        var e = MmgEngine(MmgScenario(0.0, rpm: 0.0));
        e.Enqueue(SimCommand.Rpm(168.0));
        e.Run((int)(2400.0 / Dt));
        var integrated = Units.MpsToKn(e.Motion.U);
        _out.WriteLine($"168 rpm:解析 {steady:F3} kn,時間積分 {integrated:F3} kn");
        Assert.InRange(integrated, steady - 0.1, steady + 0.1);
    }

    // ------------------------------------------------------------------ 對照表(含 Skip 項目的 C# 實際值)

    [Fact]
    public void GoldenReport()
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<(string Item, double Trial, double Sim, double Tol, string Note)>();
        var tc = Ident.GetProperty("turningCircle35");
        foreach (var side in new[] { "port", "starboard" })
        {
            var r = TurningCircle(35.0, side, tc.GetProperty("approachSpeed_kn").GetDouble());
            foreach (var key in new[] { "advance_m", "transfer_m", "tacticalDiameter_m" })
            {
                var target = tc.GetProperty(side).GetProperty(key).GetDouble();
                var sim = key switch { "advance_m" => r.AdvanceM, "transfer_m" => r.TransferM, _ => r.TacticalDiameterM };
                rows.Add(($"turning35.{side}.{key}", target, sim, TurningTolerance(target, Lpp), side == "starboard" && key == "transfer_m" ? "識別區;Python xfail(Skip)" : "識別區"));
            }
            rows.Add(($"turning35.{side}.steadySpeed_kn", 4.75, r.SteadySpeedKn, 0.15 + Tol("speed_kn"), "識別區(4.6–4.9 ±0.5)"));
        }
        foreach (var (name, rud, section) in new[] { ("zigzag10_10", 10.0, Ident), ("zigzag20_20", 20.0, Valid) })
        {
            var zz = section.GetProperty(name);
            foreach (var first in new[] { "portFirst", "starboardFirst" })
            {
                var trial = zz.GetProperty(first).GetProperty("overshoots_deg").EnumerateArray().Select(x => x.GetDouble()).ToList();
                var z = Zigzag(rud, rud, first.Replace("First", ""), zz.GetProperty("approachSpeed_kn").GetDouble(), nOvershoots: trial.Count);
                for (var i = 0; i < trial.Count; i++)
                    rows.Add(($"{name}.{first}.overshoot{i + 1}_deg", trial[i], i < z.OvershootsDeg.Count ? z.OvershootsDeg[i] : double.NaN, ZigzagTolerance(trial[i]), rud == 10.0 ? "識別區" : "驗證區"));
            }
        }
        var cs = Valid.GetProperty("crashStop");
        var crash = CrashStop(cs.GetProperty("approachSpeed_kn").GetDouble());
        rows.Add(("crashStop.asternStart_s", cs.GetProperty("asternStart_s").GetDouble(), crash.AsternStartS, double.NaN, "驗證區(機械特性,reversalDelay 取自此值 −5 s)"));
        rows.Add(("crashStop.stopTime_s", cs.GetProperty("stopTime_s").GetDouble(), crash.StopTimeS, StoppingTolerance(cs.GetProperty("stopTime_s").GetDouble()), "驗證區"));
        rows.Add(("crashStop.trackReach_m", cs.GetProperty("trackReach_m").GetDouble(), crash.TrackReachM, StoppingTolerance(cs.GetProperty("trackReach_m").GetDouble()), "驗證區"));
        var ins = Valid.GetProperty("inertiaStop");
        var inertia = InertiaStop(ins.GetProperty("approachSpeed_kn").GetDouble(), 5.0);
        rows.Add(("inertiaStop.timeTo5kn_s", ins.GetProperty("timeTo5kn_s").GetDouble(), inertia.TimeToTargetS, StoppingTolerance(ins.GetProperty("timeTo5kn_s").GetDouble()), "驗證區;Python xfail(Skip)"));
        rows.Add(("inertiaStop.distanceTo5kn_m", ins.GetProperty("distanceTo5kn_m").GetDouble(), inertia.DistanceToTargetM, StoppingTolerance(ins.GetProperty("distanceTo5kn_m").GetDouble()), "驗證區;參考"));
        var ca = Valid.GetProperty("crashAhead");
        var ahead = CrashAhead(ca.GetProperty("approachSpeedAstern_kn").GetDouble());
        rows.Add(("crashAhead.time_s", ca.GetProperty("time_s").GetDouble(), ahead.StopTimeS, StoppingTolerance(ca.GetProperty("time_s").GetDouble()), "驗證區;Python xfail(Skip)"));
        var bt = Valid.GetProperty("bowThrusterTurn90");
        foreach (var side in new[] { "port", "starboard" })
        {
            var r = ThrusterTurn(side, 90.0, bt.GetProperty("speed_kn").GetDouble());
            var t = bt.GetProperty($"{side}_s").GetDouble();
            rows.Add(($"bowThrusterTurn90.{side}_s", t, r.TimeS, StoppingTolerance(t), side == "port" ? "驗證區;Python xfail(Skip)" : "驗證區"));
        }
        var d = Fsb1FullDynamics();
        foreach (var p in Valid.GetProperty("speedPower").EnumerateArray())
        {
            var rpm = p.GetProperty("rpm").GetDouble();
            rows.Add(($"speedPower.{rpm.ToString(inv)}rpm_kn", p.GetProperty("speed_kn").GetDouble(), Units.MpsToKn(d.SteadySpeedForRpm(rpm)), Tol("speed_kn"), "驗證區"));
        }

        _out.WriteLine("| 項目 | 試俥 | C# 模擬 | 偏差 | 公差 | 結果 | 備註 |");
        _out.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (item, trial, sim, tol, note) in rows)
        {
            var dev = sim - trial;
            var pct = trial != 0 ? 100.0 * dev / trial : 0.0;
            var status = double.IsNaN(tol) ? "參考" : Math.Abs(dev) <= tol ? "通過" : "超出";
            static string Signed(double x) => (x >= 0 ? "+" : "") + x.ToString("F1", CultureInfo.InvariantCulture);
            _out.WriteLine($"| {item} | {trial.ToString("F1", inv)} | {sim.ToString("F1", inv)} | {Signed(dev)} ({Signed(pct)}%) | {(double.IsNaN(tol) ? "—" : "±" + tol.ToString("F1", inv))} | {status} | {note} |");
        }
        Assert.NotEmpty(rows);
    }

    // ------------------------------------------------------------------ C# 對 Python 數值一致性(規劃書第 14 章:軌跡差異 <1%)

    [Theory]
    [InlineData("fsb1_full_turn_stbd35.csv")]
    [InlineData("fsb1_full_zigzag10.csv")]
    [InlineData("fsb1_full_crashstop.csv")]
    public void PythonConsistency_TrajectoryWithinOnePercent(string file)
    {
        var g = LoadGolden(file);
        Assert.Equal(Dt, g.Dt);
        var sc = MmgScenario(g.SpeedKn, rpm: g.Rpm, telegraph: g.Telegraph, waterDepth: g.DepthM);
        sc.Initial.Heading = g.HeadingDeg;
        var e = MmgEngine(sc);

        // 引擎自己的直航平衡轉速應與 Python steady_rpm_for_speed 一致(同一力模型、同一二分法)
        var ownRpm = Fsb1FullDynamics().SteadyRpmForSpeed(Units.KnToMps(g.SpeedKn), g.DepthM);
        Assert.InRange(ownRpm, g.Rpm - 1e-6, g.Rpm + 1e-6);

        var events = g.Events.OrderBy(x => x.Tick).ToList();
        var idx = 0;
        var lastTick = g.Samples[^1].Tick;
        var track = 0.0;
        var (px, py) = (e.Motion.X, e.Motion.Y);
        var maxPos = 0.0; var maxPosPct = 0.0; var maxHdg = 0.0; var maxU = 0.0; var maxRpm = 0.0; var maxDelta = 0.0;
        long worstTick = 0;
        var si = 0;
        for (long tick = 0; tick <= lastTick; tick++)
        {
            if (si < g.Samples.Count && g.Samples[si].Tick == tick)
            {
                var s = g.Samples[si++];
                var m = e.Motion;
                var dPos = Math.Sqrt((m.X - s.X) * (m.X - s.X) + (m.Y - s.Y) * (m.Y - s.Y));
                var dHdg = Math.Abs(Units.WrapDeg180(Units.RadToDeg(m.Psi) - s.PsiDeg));
                var pct = track > 1.0 ? 100.0 * dPos / track : 0.0;
                if (pct > maxPosPct) { maxPosPct = pct; worstTick = tick; }
                maxPos = Math.Max(maxPos, dPos);
                maxHdg = Math.Max(maxHdg, dHdg);
                maxU = Math.Max(maxU, Math.Abs(m.U - s.U));
                maxRpm = Math.Max(maxRpm, Math.Abs(e.Actuators.Rpm - s.Rpm));
                maxDelta = Math.Max(maxDelta, Math.Abs(e.Actuators.RudderDeg - s.DeltaDeg));
            }
            if (tick == lastTick) break;
            while (idx < events.Count && events[idx].Tick == tick) e.Enqueue(events[idx++].Command);
            e.Step();
            track += Math.Sqrt((e.Motion.X - px) * (e.Motion.X - px) + (e.Motion.Y - py) * (e.Motion.Y - py));
            (px, py) = (e.Motion.X, e.Motion.Y);
        }
        Assert.Equal(g.Samples.Count, si);
        Assert.Equal(events.Count, idx);
        _out.WriteLine($"{g.Title}");
        _out.WriteLine($"  600 s 航跡長 {track:F1} m;最大位置差 {maxPos:F4} m({maxPosPct:F5}% of track @tick {worstTick});最大航向差 {maxHdg:F6}°;|Δu| {maxU:E2} m/s;|Δrpm| {maxRpm:E2};|Δδ| {maxDelta:E2}°");
        Assert.True(maxPosPct < 1.0, $"位置差 {maxPosPct:F4}% ≥ 1%");
        Assert.True(maxHdg < 3.6, $"航向差 {maxHdg:F4}° ≥ 1% × 360°");
    }
}
