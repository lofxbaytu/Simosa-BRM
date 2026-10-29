using System.Globalization;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 空間變化流場(情境 environment.current.field;情境引擎設計第 1.5.2 節;需求 R3):雙線性內插(角點/中點/格內/格外 edge|zero)、
/// timeScale tidal|table 的流速比例縮放(方向取場的向量)、無 field 的情境行為與數值不變、引擎以自船位置刷新 state.current 與
/// currentSource = field|field+tidal|field+table、目標船以自身位置取流、快照/倒帶/紀錄重播確定性、setEnvironment current 覆寫語意、
/// 結構檢查(與 Python current_field_issues 訊息相同)、C#↔Python 跨語言夾具 Golden/current_field.csv(export_current_reference.py 產生)。
/// </summary>
public class CurrentFieldTests
{
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static string GoldenDirectory => Path.Combine(TestData.Root, "tests", "SimCore.Tests", "Golden");

    /// <summary>內嵌情境:<paramref name="current"/> 為 environment.current 之下的區段(四格縮排),<paramref name="tide"/> 為 environment 之下的區段,<paramref name="tail"/> 為頂層附加區段。</summary>
    private static Scenario.Scenario Yaml(string current, string tide = "", string tail = "", double x = 0, double y = 0, double speed = 8)
        => ScenarioLoader.FromYaml(string.Join("\n", new[]
        {
            "id: T_field",
            "seed: 7",
            "ship: { id: FSB1, loading: ballast }",
            "origin: { lat: 23.80, lon: 120.05 }",
            $"initial: {{ position: {{ x: {x.ToString(CultureInfo.InvariantCulture)}, y: {y.ToString(CultureInfo.InvariantCulture)} }}, heading: 0, speed: {speed.ToString(CultureInfo.InvariantCulture)}, telegraph: HAH }}",
            "environment:",
            "  wind: { trueSpeed: 0, trueDir: 0, gustiness: 0 }",
            "  current:",
            "    set: 350",
            "    drift: 1",
            current,
            "  waterDepth: 30",
            tide,
            tail,
        }) + "\n");

    // 3 × 2 網格:x ∈ {0, 100, 200}、y ∈ {0, 100};u 隨 x 增加(列同值)、v 只隨 y 變
    private const string Field =
        "    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 3, ny: 2 }\n" +
        "      u_kn: [[1, 2, 3], [1, 2, 3]]\n      v_kn: [[0, 0, 0], [1, 1, 1]]";
    private const string FieldZero = Field + "\n      outside: zero";
    private const string Table = "    table:\n      - { t: 0, speed_kn: 1, direction_deg: 0 }\n      - { t: 10, speed_kn: 2, direction_deg: 90 }";
    private const string Tidal = "    tidal:\n      flood: { speed_kn: 2.5, direction_deg: 85 }\n      ebb: { speed_kn: 3.0, direction_deg: 265 }\n      slackWindow_min: 0.1";
    private const string HarmonicTideYaml = "  tide:\n    harmonic:\n      meanLevel_m: 1\n      constituents:\n        - { name: T1, amplitude_m: 1, period_s: 120, phase_deg: -60 }";

    private static double Kn(double kn) => Units.KnToMps(kn);
    private static double AngleDiffDeg(double a, double b) => Math.Abs(Units.WrapDeg180(a - b));

    // ------------------------------------------------------------------ 模型

    [Fact]
    public void BilinearInterpolationAtCornersMidpointsInteriorAndOutside()
    {
        // 2 × 2 網格(x0 = 10, y0 = 20, dx = 100, dy = 50):u = [[1, 3], [5, 7]]、v = [[0, 2], [4, 8]](列為 y 索引)
        var f = new CurrentField(10, 20, 100, 50, 2, 2, new[] { 1.0, 3.0, 5.0, 7.0 }, new[] { 0.0, 2.0, 4.0, 8.0 }, outsideZero: false, scaler: null);
        Assert.Equal("field", f.Source);
        Assert.True(f.SpatiallyVarying);
        Assert.Equal((1.0, 0.0), f.Interpolate(10, 20));
        Assert.Equal(3.0, f.Interpolate(110, 20).U, 12);
        Assert.Equal(2.0, f.Interpolate(110, 20).V, 12);
        Assert.Equal(5.0, f.Interpolate(10, 70).U, 12);
        Assert.Equal(7.0, f.Interpolate(110, 70).U, 12);
        Assert.Equal(8.0, f.Interpolate(110, 70).V, 12);
        // 邊中點與格中心:線性平均
        Assert.Equal(2.0, f.Interpolate(60, 20).U, 12);
        Assert.Equal(6.0, f.Interpolate(60, 70).V, 12);
        Assert.Equal(4.0, f.Interpolate(60, 45).U, 12);     // (1 + 3 + 5 + 7) / 4
        Assert.Equal(3.5, f.Interpolate(60, 45).V, 12);     // (0 + 2 + 4 + 8) / 4
        // 格內任意點:雙線性公式 a = u00 + (u10 − u00)·tx、b = u01 + (u11 − u01)·tx、u = a + (b − a)·ty
        var (tx, ty) = (0.3, 0.8);
        var a = 1.0 + (3.0 - 1.0) * tx;
        var b = 5.0 + (7.0 - 5.0) * tx;
        Assert.Equal(a + (b - a) * ty, f.Interpolate(10 + 100 * tx, 20 + 50 * ty).U, 12);
        // 格外(edge):取最近邊緣值
        Assert.Equal((1.0, 0.0), f.Interpolate(-500, -500));
        Assert.Equal((7.0, 8.0), f.Interpolate(1e6, 1e6));
        Assert.Equal(2.0, f.Interpolate(60, -100).U, 12);   // x 在格內、y 在格外 → y 夾到 y0
        Assert.Equal(6.0, f.Interpolate(60, 999).U, 12);
        // 格外(zero):0,格內與邊界上不受影響
        var z = new CurrentField(10, 20, 100, 50, 2, 2, new[] { 1.0, 3.0, 5.0, 7.0 }, new[] { 0.0, 2.0, 4.0, 8.0 }, outsideZero: true, scaler: null);
        Assert.Equal((0.0, 0.0), z.Interpolate(-500, 20));
        Assert.Equal((0.0, 0.0), z.Interpolate(60, 70.0001));
        Assert.Equal(f.Interpolate(60, 45), z.Interpolate(60, 45));
        Assert.Equal((7.0, 8.0), z.Interpolate(110, 70));
        // At:set = atan2(E, N)、drift = √(E²+N²);無 timeScale 時 t 無關;At(t) 取原點
        var (set, drift) = f.At(123.4, 110, 70);
        Assert.Equal(Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(7, 8))), Units.RadToDeg(set), 9);
        Assert.Equal(Math.Sqrt(49 + 64), drift, 12);
        Assert.Equal(f.At(0, 0, 0), f.At(0));
        Assert.Equal(1.0, f.ScaleAt(999));
        // 參數防呆
        Assert.Throws<ArgumentException>(() => new CurrentField(0, 0, 0, 1, 2, 2, new double[4], new double[4], false, null));
        Assert.Throws<ArgumentException>(() => new CurrentField(0, 0, 1, 1, 1, 2, new double[2], new double[2], false, null));
        Assert.Throws<ArgumentException>(() => new CurrentField(0, 0, 1, 1, 2, 2, new double[3], new double[4], false, null));
    }

    [Fact]
    public void FieldIsBuiltFromScenarioInKnotsWithDefaults()
    {
        var sc = Yaml(Field).Validate();
        Assert.Empty(sc.StructuralIssues());
        var f = Assert.IsType<CurrentField>(CurrentModel.FromScenario(sc.Environment.Current, null, null));
        Assert.Equal("field", f.Source);
        Assert.Null(f.Scaler);
        Assert.False(f.OutsideZero);
        Assert.Equal((0.0, 0.0, 100.0, 100.0, 3, 2), (f.X0, f.Y0, f.Dx, f.Dy, f.Nx, f.Ny));
        Assert.Equal(new[] { Kn(1), Kn(2), Kn(3), Kn(1), Kn(2), Kn(3) }, f.U);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, Kn(1), Kn(1), Kn(1) }, f.V);
        // (150, 50):u = 2.5 kn、v = 0.5 kn
        var (set, drift) = f.At(0, 150, 50);
        Assert.Equal(Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(2.5, 0.5))), Units.RadToDeg(set), 9);
        Assert.Equal(Kn(Math.Sqrt(2.5 * 2.5 + 0.25)), drift, 12);
        Assert.True(Assert.IsType<CurrentField>(CurrentModel.FromScenario(Yaml(FieldZero).Validate().Environment.Current, null, null)).OutsideZero);
        // 無 field:仍為 table / 常數流
        Assert.IsType<CurrentTable>(CurrentModel.FromScenario(Yaml(Table).Validate().Environment.Current, null, null));
        Assert.Null(CurrentModel.FromScenario(Yaml("").Validate().Environment.Current, null, null));
    }

    [Fact]
    public void TimeScaleTidalScalesSpeedByTidalRatioAndKeepsFieldDirection()
    {
        var sc = Yaml(Tidal + "\n" + Field + "\n      timeScale: tidal", HarmonicTideYaml).Validate();
        Assert.Empty(sc.StructuralIssues());
        var tide = TideModel.FromScenario(sc.Environment.Tide, null);
        var f = Assert.IsType<CurrentField>(CurrentModel.FromScenario(sc.Environment.Current, tide, null));
        var tidal = Assert.IsType<TidalCurrent>(f.Scaler);
        Assert.Equal("field+tidal", f.Source);
        Assert.Equal(Kn(3.0), f.ScalerMaxMps);                 // max(flood 2.5, ebb 3.0)
        foreach (var t in new[] { 0.0, 7.5, 20.0, 50.0, 110.0 })
        {
            var expected = tidal.At(t).DriftMps / Kn(3.0);
            Assert.Equal(expected > 1 ? 1.0 : expected, f.ScaleAt(t), 12);
            Assert.InRange(f.ScaleAt(t), 0.0, 1.0);
            var (u, v) = f.Interpolate(150, 50);
            var (e, n) = f.VectorAt(t, 150, 50);
            Assert.Equal(u * f.ScaleAt(t), e, 12);
            Assert.Equal(v * f.ScaleAt(t), n, 12);
        }
        // 平潮(高潮 t = 20):比例 0 → 零向量;中退(t = 50):比例接近 1、方向仍為場的向量(不反向)
        Assert.Equal((0.0, 0.0), f.VectorAt(20.0, 150, 50));
        Assert.Equal(0.0, f.At(20.0, 150, 50).DriftMps);
        var (setEbb, driftEbb) = f.At(50.0, 150, 50);
        Assert.Equal(Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(2.5, 0.5))), Units.RadToDeg(setEbb), 9);
        Assert.Equal(Kn(Math.Sqrt(2.5 * 2.5 + 0.25)), driftEbb, 9);   // 單分潮:中退比例恰為 1
        Assert.Equal(265.0, Units.RadToDeg(tidal.At(50.0).SetRad), 9);  // 潮流本身為退潮向,但場不反向
    }

    [Fact]
    public void TimeScaleTableScalesByTableSpeedRatioClampedToOne()
    {
        var sc = Yaml(Table + "\n" + Field + "\n      timeScale: table").Validate();
        Assert.Empty(sc.StructuralIssues());
        var f = Assert.IsType<CurrentField>(CurrentModel.FromScenario(sc.Environment.Current, null, null));
        var table = Assert.IsType<CurrentTable>(f.Scaler);
        Assert.Equal("field+table", f.Source);
        Assert.Equal(Kn(2.0), f.ScalerMaxMps, 12);
        Assert.Equal(Kn(2.0), table.MaxSpeedMps, 12);
        Assert.Equal(0.5, f.ScaleAt(-5), 12);                   // 範圍外取首點 1 kn / 2 kn
        Assert.Equal(1.0, f.ScaleAt(10), 12);
        Assert.Equal(1.0, f.ScaleAt(1e6), 12);
        var mid = table.At(5.0).DriftMps / Kn(2.0);             // 000° 1 kn → 090° 2 kn 的中點:向量內插的流速 < 2 kn
        Assert.InRange(mid, 0.5, 1.0);
        Assert.Equal(mid, f.ScaleAt(5.0), 12);
        var (u, v) = f.Interpolate(50, 0);
        Assert.Equal((u * 0.5, v * 0.5), f.VectorAt(0.0, 50, 0));
        // 最大流速 0 的表:比例 0
        var zero = new CurrentField(0, 0, 1, 1, 2, 2, new[] { 1.0, 1.0, 1.0, 1.0 }, new double[4], false,
            new CurrentTable(new[] { (0.0, 0.0, 0.0), (10.0, 0.0, 0.0) }));
        Assert.Equal(0.0, zero.ScaleAt(3.0));
        Assert.Equal((0.0, 0.0), zero.VectorAt(3.0, 0.5, 0.5));
    }

    // ------------------------------------------------------------------ 引擎

    [Fact]
    public void EngineRefreshesCurrentAtOwnPositionAndSamplesOtherPositionsFromTheField()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(Field, x: 150, y: 50).Validate());
        var env = e.Environment;
        Assert.True(env.CurrentModelActive);
        Assert.True(env.CurrentFieldActive);
        Assert.Equal("field", env.CurrentSource);
        var s0 = e.BuildState();
        Assert.Equal("field", s0.CurrentSource);
        Assert.Contains("\"currentSource\":\"field\"", ContractJson.Serialize(s0));
        // state.current 為自船位置 (150, 50) 的值(u 2.5 kn、v 0.5 kn),而非情境 set/drift 常數
        Assert.Equal(Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(2.5, 0.5))), s0.Current.Set, 9);
        Assert.Equal(Math.Sqrt(2.5 * 2.5 + 0.25), s0.Current.Drift, 9);
        // 其他位置依場取樣;自船位置的取樣與 CurrentSetRad/DriftMps 一致
        var own = env.SampleAt(e.Motion.X, e.Motion.Y);
        Assert.Equal(env.CurrentDriftMps * Math.Sin(env.CurrentSetRad), own.CurrentEastMps);
        Assert.Equal(env.CurrentDriftMps * Math.Cos(env.CurrentSetRad), own.CurrentNorthMps);
        var west = env.SampleAt(0, 0);
        Assert.Equal(Kn(1), west.CurrentEastMps, 12);
        Assert.Equal(0.0, west.CurrentNorthMps, 12);
        var far = env.SampleAt(1e5, 1e5);
        Assert.Equal(Kn(3), far.CurrentEastMps, 12);
        Assert.Equal(Kn(1), far.CurrentNorthMps, 12);
        Assert.Equal((west.CurrentEastMps, west.CurrentNorthMps), env.CurrentVectorAt(env.CurrentTimeS, 0, 0));
        // 推進:每步以新位置重算;流隨船位改變(船北上、受東向流漂移 → u 隨 x 增、v 隨 y 增)
        TestData.RunTo(e, 20);
        Assert.Equal(e.Time, env.CurrentTimeS);
        var m = e.Motion;
        var (set, drift) = env.CurrentModel!.At(e.Time, m.X, m.Y);
        Assert.Equal(set, env.CurrentSetRad);
        Assert.Equal(drift, env.CurrentDriftMps);
        Assert.True(m.X > 150 && m.Y > 50);
        Assert.True(e.BuildState().Current.Drift > s0.Current.Drift);
        Assert.Equal("field", e.BuildState().CurrentSource);
    }

    [Fact]
    public void TargetShipsTakeTheCurrentAtTheirOwnPosition()
    {
        // 自船在 y = 100 列(v = 1 kn 北向、u = 2 kn 東向);目標 T 在 y = 0 列(v = 0)以 090° 定速 5 kn 東行 → 目標只有東向流,且沿途 u 隨 x 變化
        const string targets = "targets:\n  - id: T\n    loa: 100\n    beam: 16\n    initial: { position: { x: 0, y: 0 }, heading: 90, speed: 5 }\n    behaviour: { mode: hold }";
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(Field, tail: targets, x: 100, y: 100, speed: 0).Validate());
        var env = e.Environment;
        var target = Assert.Single(e.Traffic.Targets);
        var (tx, ty) = (target.X, target.Y);
        var expectedE = Kn(5) + env.CurrentVectorAt(0.0, tx, ty).East;    // 步首位置、步首時刻的流
        e.Step();
        Assert.Equal(expectedE, target.VelE, 9);
        Assert.Equal(0.0, target.VelN, 9);                                 // 目標所在列 v = 0;自船位置的 v = 1 kn 不會套到目標
        Assert.Equal(Kn(1), env.CurrentDriftMps * Math.Cos(env.CurrentSetRad), 9);
        Assert.Equal(tx + expectedE * e.Dt, target.X, 9);
        Assert.Equal(ty, target.Y, 9);
        // 多步:每步皆以目標當時位置取流(u 隨 x 線性增加 → 東向對地速度單調遞增)
        var prev = target.VelE;
        for (var i = 0; i < 250; i++)
        {
            e.Step();
            Assert.True(target.VelE >= prev - 1e-12);
            prev = target.VelE;
        }
        Assert.Equal(Kn(5) + env.CurrentVectorAt(e.Time - e.Dt, target.X - target.VelE * e.Dt, target.Y).East, target.VelE, 6);
    }

    [Fact]
    public void ScenariosWithoutFieldAreUnchanged()
    {
        // 常數流:無模型、取樣與位置/時間無關、CurrentVectorAt 與 SampleAt 同值、快照無覆寫旗標
        var e = TestData.MinimalEngine("", speedKn: 8);
        var env = e.Environment;
        Assert.Null(env.CurrentModel);
        Assert.False(env.CurrentFieldActive);
        Assert.Null(env.CurrentSource);
        TestData.RunTo(e, 3);
        var a = env.SampleAt(0, 0);
        var b = env.SampleAt(12345, -6789);
        Assert.Equal((a.CurrentEastMps, a.CurrentNorthMps), (b.CurrentEastMps, b.CurrentNorthMps));
        Assert.Equal((a.CurrentEastMps, a.CurrentNorthMps), env.CurrentVectorAt(999.0, 1, 2));
        Assert.Equal((env.CurrentDriftMps * Math.Sin(env.CurrentSetRad), env.CurrentDriftMps * Math.Cos(env.CurrentSetRad)), (a.CurrentEastMps, a.CurrentNorthMps));
        Assert.DoesNotContain("currentSource", ContractJson.Serialize(e.BuildState()));
        Assert.Null(e.CreateSnapshot().Environment.CurrentOverridden);
        // 均勻時變流場(table):SpatiallyVarying false、取樣與位置無關、currentSource 仍為 table
        var t = new SimulationEngine(TestData.Fsb1(), Yaml(Table).Validate());
        Assert.False(t.Environment.CurrentModel!.SpatiallyVarying);
        Assert.False(t.Environment.CurrentFieldActive);
        TestData.RunTo(t, 5);
        Assert.Equal("table", t.BuildState().CurrentSource);
        var c = t.Environment.SampleAt(0, 0);
        var d = t.Environment.SampleAt(5000, 5000);
        Assert.Equal((c.CurrentEastMps, c.CurrentNorthMps), (d.CurrentEastMps, d.CurrentNorthMps));
        Assert.Equal(t.Environment.CurrentModel.At(5.0), t.Environment.CurrentModel.At(5.0, 5000, 5000));
        // 既有情境(E06b 潮流)載入後無空間流場
        var e06b = new SimulationEngine(TestData.Fsb1(), ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", "E06b_tidal_stream.yaml")), Mmg);
        Assert.Null(e06b.Scenario.Environment.Current.Field);
        Assert.False(e06b.Environment.CurrentFieldActive);
        Assert.Equal("tidal", e06b.BuildState().CurrentSource);
    }

    [Fact]
    public void SnapshotRestoreRecomputesFieldFromTickAndPosition()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(Tidal + "\n" + Field + "\n      timeScale: tidal", HarmonicTideYaml).Validate());
        Assert.Equal("field+tidal", e.BuildState().CurrentSource);
        TestData.RunTo(e, 10);
        var snap = e.TakeSnapshot();
        var s10 = e.BuildState();
        TestData.RunTo(e, 40);
        var (hash, s40) = (e.StateHash, e.BuildState());
        Assert.Equal("field+tidal", s40.CurrentSource);
        Assert.NotEqual(s10.Current, s40.Current);
        e.Restore(snap);
        Assert.Equal(s10.Current, e.BuildState().Current);
        Assert.Equal(10.0, e.Environment.CurrentTimeS, 9);
        TestData.RunTo(e, 40);
        Assert.Equal(hash, e.StateHash);
        Assert.Equal(s40.Current, e.BuildState().Current);
        Assert.Equal(s40.Pos.X, e.BuildState().Pos.X);
    }

    [Fact]
    public void SetEnvironmentCurrentStopsTheFieldUntilRewindOrReload()
    {
        var e = new SimulationEngine(TestData.Fsb1(), Yaml(Field, x: 150, y: 50).Validate());
        var ev = TestData.Capture(e);
        TestData.RunTo(e, 5);
        var snap = e.TakeSnapshot();
        TestData.RunTo(e, 10);
        // 只改風/水深:場不受影響
        e.Enqueue(SimCommand.SetEnvironment(windTrueSpeedKn: 10, windTrueDirDeg: 90, waterDepth: 25));
        e.Step();
        Assert.True(e.Environment.CurrentFieldActive);
        Assert.False(ev.Last(x => x.Kind == SimEventKinds.EnvironmentChange).Data!.Value.TryGetProperty("currentModelStopped", out _));
        // 覆寫流:場停用,之後各位置皆為常數(漸變自自船位置的場值起算)
        var before = e.BuildState().Current;
        e.Enqueue(SimCommand.SetEnvironment(currentSetDeg: 100, currentDriftKn: 0.5, rampS: 2));
        e.Step();
        Assert.True(e.Environment.CurrentOverridden);
        Assert.False(e.Environment.CurrentFieldActive);
        Assert.Null(e.BuildState().CurrentSource);
        Assert.True(ev.Last(x => x.Kind == SimEventKinds.EnvironmentChange).Data!.Value.GetProperty("currentModelStopped").GetBoolean());
        Assert.True(AngleDiffDeg(before.Set, e.BuildState().Current.Set) < 1.0);   // 漸變起點為覆寫前的場值
        TestData.RunTo(e, 15);
        Assert.Equal(100.0, e.BuildState().Current.Set, 9);
        Assert.Equal(0.5, e.BuildState().Current.Drift, 9);
        var p = e.Environment.SampleAt(0, 0);
        var q = e.Environment.SampleAt(1e5, 1e5);
        Assert.Equal((p.CurrentEastMps, p.CurrentNorthMps), (q.CurrentEastMps, q.CurrentNorthMps));
        Assert.Equal(Kn(0.5) * Math.Sin(Units.DegToRad(100)), p.CurrentEastMps, 12);
        Assert.Equal(true, e.CreateSnapshot().Environment.CurrentOverridden);
        // 倒帶到覆寫前:場恢復並以 Tick 與位置重算
        e.Restore(snap);
        Assert.False(e.Environment.CurrentOverridden);
        Assert.True(e.Environment.CurrentFieldActive);
        Assert.Equal("field", e.BuildState().CurrentSource);
        Assert.NotEqual(e.Environment.SampleAt(0, 0).CurrentEastMps, e.Environment.SampleAt(1e5, 1e5).CurrentEastMps);
        // 覆寫後 reset:回到情境初始,場恢復
        e.Enqueue(SimCommand.SetEnvironment(currentSetDeg: 100, currentDriftKn: 0.5));
        e.Step();
        Assert.True(e.Environment.CurrentOverridden);
        e.Enqueue(new SimCommand { Type = SimCommandType.Reset });
        e.Step();
        Assert.False(e.Environment.CurrentOverridden);
        Assert.Equal("field", e.BuildState().CurrentSource);
    }

    [Fact]
    public void OverrideAndRewindAreRecordReplayConsistent()
    {
        var sc = Yaml(Tidal + "\n" + Field + "\n      timeScale: tidal", HarmonicTideYaml).Validate();
        var e = new SimulationEngine(TestData.Fsb1(), sc);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"current-field-{Guid.NewGuid():N}.jsonl");
        var w = new RecordWriter(path, e);
        void RunTo(double s) { var target = (long)Math.Round(s / e.Dt); while (e.Tick < target) e.Step(); }
        RunTo(6);
        e.Enqueue(SimCommand.SetEnvironment(currentSetDeg: 100, currentDriftKn: 0.5, rampS: 2));
        RunTo(12);
        e.Enqueue(new SimCommand { Type = SimCommandType.Restore });   // 倒帶到最近的自動快照(10 s;覆寫後)
        RunTo(20);
        e.Enqueue(new SimCommand { Type = SimCommandType.Reset });     // 回到初始:場恢復
        RunTo(26);
        w.Close();
        try
        {
            var r = Replayer.Replay(path, TestData.Ship);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ 結構檢查

    [Theory]
    [InlineData("    field:\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]", "", "environment.current.field.grid", "field 需要 grid")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 0, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]", "", "environment.current.field.grid", "dx 必須為正")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: -1, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]", "", "environment.current.field.grid", "dy 必須為正")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 1, ny: 2 }\n      u_kn: [[1], [1]]\n      v_kn: [[0], [0]]", "", "environment.current.field.grid", "nx 必須為 ≥ 2 的整數")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 0 }\n      u_kn: []\n      v_kn: []", "", "environment.current.field.grid", "ny 必須為 ≥ 2 的整數")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      v_kn: [[0, 0], [0, 0]]", "", "environment.current.field.u_kn", "field 需要 u_kn")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]", "", "environment.current.field.v_kn", "field 需要 v_kn")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]", "", "environment.current.field.u_kn", "u_kn 須有 ny = 2 列(得到 3)")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0, 0]]", "", "environment.current.field.v_kn[1]", "v_kn 每列須有 nx = 2 個值(得到 3)")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]\n      outside: wrap", "", "environment.current.field.outside", "outside 須為 edge | zero")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]\n      timeScale: moon", "", "environment.current.field.timeScale", "timeScale 須為 none | tidal | table")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]\n      timeScale: tidal", "", "environment.current.field.timeScale", "timeScale tidal 需要 current.tidal")]
    [InlineData("    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]\n      timeScale: table", "", "environment.current.field.timeScale", "timeScale table 需要 current.table")]
    [InlineData(Tidal + "\n    field:\n      grid: { x0: 0, y0: 0, dx: 100, dy: 100, nx: 2, ny: 2 }\n      u_kn: [[1, 2], [1, 2]]\n      v_kn: [[0, 0], [0, 0]]\n      timeScale: table", HarmonicTideYaml, "environment.current.field.timeScale", "timeScale table 需要 current.table")]
    public void StructuralIssuesRejectBadField(string current, string tide, string path, string message)
    {
        var sc = Yaml(current, tide);
        var issues = sc.StructuralIssues().ToList();
        Assert.Contains(issues, i => i.Severity == ScenarioIssueSeverity.Error && i.Path == path && i.Message.Contains(message));
        Assert.Throws<InvalidDataException>(() => sc.Validate());
    }

    [Fact]
    public void ValidFieldsHaveNoStructuralIssues()
    {
        Assert.Empty(Yaml(Field).StructuralIssues());
        Assert.Empty(Yaml(FieldZero + "\n      timeScale: none").StructuralIssues());
        Assert.Empty(Yaml(Tidal + "\n" + Field + "\n      timeScale: tidal", HarmonicTideYaml).StructuralIssues());
        Assert.Empty(Yaml(Table + "\n" + Field + "\n      timeScale: table").StructuralIssues());
        Assert.Empty(Yaml(Table + "\n" + Field).StructuralIssues());                 // timeScale none:table 只保留不使用
        Assert.Empty(Yaml(Tidal + "\n" + FieldZero, HarmonicTideYaml).StructuralIssues());
        // field 不影響 table/tidal 擇一的規則
        Assert.Contains(Yaml(Table + "\n" + Tidal + "\n" + Field, HarmonicTideYaml).StructuralIssues(), i => i.Message.Contains("只能擇一"));
        // 載入後 field 區段保留原值
        var f = Yaml(FieldZero + "\n      timeScale: none").Validate().Environment.Current.Field!;
        Assert.Equal(("zero", "none"), (f.Outside, f.TimeScale));
        Assert.Equal(2, f.UKn!.Count);
        Assert.Equal(3, f.UKn[0].Count);
    }

    // ------------------------------------------------------------------ 跨語言

    /// <summary>
    /// Python export_current_reference.py 的 current_field.csv(kind,tick,t,x,y,set_deg,drift_kn,east_mps,north_mps,currentSource):
    /// point 列以 CurrentModel.At(t, x, y) / EnvironmentConditions.CurrentVectorAt 直接比對(1e-9);run 列推進到同 tick 後比對 currentSource、
    /// 以 Python 記錄的位置取樣的流(1e-9;驗證引擎的時刻語意),並檢查自船位置與 state.current 在跨語言船模差異的公差內。
    /// </summary>
    [Fact]
    public void GoldenFieldMatchesPythonReference()
    {
        var rows = File.ReadLines(Path.Combine(GoldenDirectory, "current_field.csv")).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("kind", StringComparison.Ordinal))
            .Select(l => l.Split(',')).Select(c => (Kind: c[0], Tick: long.Parse(c[1], CultureInfo.InvariantCulture), T: double.Parse(c[2], CultureInfo.InvariantCulture),
                X: double.Parse(c[3], CultureInfo.InvariantCulture), Y: double.Parse(c[4], CultureInfo.InvariantCulture),
                Set: double.Parse(c[5], CultureInfo.InvariantCulture), Drift: double.Parse(c[6], CultureInfo.InvariantCulture),
                East: double.Parse(c[7], CultureInfo.InvariantCulture), North: double.Parse(c[8], CultureInfo.InvariantCulture), Source: c[9])).ToList();
        Assert.True(rows.Count(r => r.Kind == "point") >= 10);
        Assert.True(rows.Count(r => r.Kind == "run") >= 10);
        var sc = ScenarioLoader.Load(Path.Combine(GoldenDirectory, "current_field.yaml"));
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var env = e.Environment;
        var model = Assert.IsType<CurrentField>(env.CurrentModel);
        Assert.Equal("field+tidal", model.Source);
        foreach (var r in rows.Where(r => r.Kind == "point"))
        {
            var (set, drift) = model.At(r.T, r.X, r.Y);
            var (east, north) = env.CurrentVectorAt(r.T, r.X, r.Y);
            Assert.Equal(r.Source, model.Source);
            if (r.Drift > 1e-12) Assert.True(AngleDiffDeg(r.Set, Units.RadToDeg(set)) <= 1e-9, $"point t={r.T} ({r.X},{r.Y}): set Python {r.Set:R} vs C# {Units.RadToDeg(set):R}");
            Assert.True(Math.Abs(r.Drift - Units.MpsToKn(drift)) <= 1e-9, $"point t={r.T} ({r.X},{r.Y}): drift Python {r.Drift:R} vs C# {Units.MpsToKn(drift):R}");
            Assert.True(Math.Abs(r.East - east) <= 1e-9, $"point t={r.T} ({r.X},{r.Y}): east Python {r.East:R} vs C# {east:R}");
            Assert.True(Math.Abs(r.North - north) <= 1e-9, $"point t={r.T} ({r.X},{r.Y}): north Python {r.North:R} vs C# {north:R}");
        }
        // 平潮點(t = 20)為零向量;格外點取邊緣值(與角點同值)
        Assert.Contains(rows, r => r.Kind == "point" && r.T == 20.0 && r.Drift == 0.0);
        foreach (var r in rows.Where(r => r.Kind == "run"))
        {
            while (e.Tick < r.Tick) e.Step();
            var s = e.BuildState();
            Assert.Equal(r.T, s.T, 9);
            Assert.Equal(r.Source, s.CurrentSource);
            Assert.Equal(r.T, env.CurrentTimeS, 9);
            var (east, north) = env.CurrentVectorAt(r.T, r.X, r.Y);          // Python 的位置、同時刻:純模型比對
            Assert.True(Math.Abs(r.East - east) <= 1e-9, $"run tick {r.Tick}: east Python {r.East:R} vs C# {east:R}");
            Assert.True(Math.Abs(r.North - north) <= 1e-9, $"run tick {r.Tick}: north Python {r.North:R} vs C# {north:R}");
            // 自船位置與 state.current:兩套船模的軌跡差異(60 s 內 < 1 mm)× 場梯度(0.5 kn / 100 m)
            Assert.True(Math.Abs(r.X - e.Motion.X) <= 1e-3 && Math.Abs(r.Y - e.Motion.Y) <= 1e-3, $"run tick {r.Tick}: position Python ({r.X:R},{r.Y:R}) vs C# ({e.Motion.X:R},{e.Motion.Y:R})");
            Assert.True(Math.Abs(r.Drift - s.Current.Drift) <= 1e-6, $"run tick {r.Tick}: drift Python {r.Drift:R} vs C# {s.Current.Drift:R}");
            Assert.True(AngleDiffDeg(r.Set, s.Current.Set) <= 1e-5, $"run tick {r.Tick}: set Python {r.Set:R} vs C# {s.Current.Set:R}");
        }
        Assert.True(e.Motion.Y > 100.0);   // 船已穿越 y = 100 的格線(流由位置決定)
    }
}
