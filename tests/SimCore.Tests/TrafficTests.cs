using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Traffic;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 交通模組(規劃書第 5.2 節 Traffic、第 9.1 節目標船、第 8.3 節避碰指標):會遇幾何解析解、碰撞偵測、
/// 行為模式(航點、腳本、錨泊、跟隨、COLREG 對遇/橫越/不守規則)、觸發條件、targetControl 指令、AIS 錯誤注入。
/// 自船用暫代模型(確定性且快),目標船的行為與自船模型無關。
/// </summary>
public class TrafficTests
{
    private const double Kn = Units.KnotToMetresPerSecond;

    /// <summary>無風無流、深水、自船航向北 7.8 kn 的情境(E01 幾何但環境歸零,便於解析比對)。</summary>
    internal static Scenario.Scenario CalmScenario(params ScenarioTarget[] targets)
    {
        var sc = TestData.Baseline();
        sc.Environment.Wind.TrueSpeed = 0;
        sc.Environment.Wind.Gustiness = 0;
        sc.Environment.Current.Drift = 0;
        sc.Targets = targets.ToList();
        return sc;
    }

    internal static ScenarioTarget Target(string id, double x, double y, double headingDeg, double speedKn, TargetBehaviourMode mode = TargetBehaviourMode.Hold,
        double loa = 100, double beam = 16)
        => new()
        {
            Id = id,
            Loa = loa,
            Beam = beam,
            Initial = new TargetInitial { Position = new ScenarioPosition { X = x, Y = y }, Heading = headingDeg, Speed = speedKn },
            Behaviour = new TargetBehaviour { Mode = mode },
        };

    private static SimulationEngine Engine(Scenario.Scenario sc) => new(TestData.Fsb1(), sc);

    // ------------------------------------------------------------------ 會遇幾何

    [Fact]
    public void CpaTcpaBcrMatchAnalyticSolution()
    {
        // 自船原點向北 5 m/s;目標 (2000, 3000) 向西 5 m/s:
        // r = (2000, 3000)、v = (−5, −5) → TCPA = 25000/50 = 500 s、CPA = |(−500, 500)| = 707.1 m;
        // 艏越:r_x(t) = 0 → t = 400 s,r_y = 3000 − 2000 = 1000 m(艏前)
        var g = Encounter.Compute(0, 0, 0, 5, 0, 2000, 3000, -5, 0);
        Assert.Equal(500.0, g.TcpaS, 9);
        Assert.Equal(500.0 * Math.Sqrt(2.0), g.CpaM, 6);
        Assert.Equal(Math.Sqrt(13.0) * 1000.0, g.RangeM, 6);
        Assert.Equal(Units.DegToRad(33.690067525979785), g.BearingRad, 9); // atan(2000/3000)
        Assert.Equal(400.0, g.BctS!.Value, 9);
        Assert.Equal(1000.0, g.BcrM!.Value, 6);

        // 目標已通過(遠離):TCPA 負,無艏越
        var passed = Encounter.Compute(0, 0, 0, 5, 0, -1000, -1000, -5, 0);
        Assert.True(passed.TcpaS < 0);
        Assert.Null(passed.BcrM);

        // 平行同速:相對速度零 → TCPA 0、CPA = 距離
        var parallel = Encounter.Compute(0, 0, 0, 5, 0, 500, 0, 0, 5);
        Assert.Equal(0.0, parallel.TcpaS);
        Assert.Equal(500.0, parallel.CpaM, 9);
        Assert.Null(parallel.BcrM);

        // 自船航向 090 時艏向線為東向:目標自北向南通過自船艏前 800 m → 艉後為負
        var astern = Encounter.Compute(0, 0, 5, 0, Units.DegToRad(90.0), -800, 1000, 0, -5);
        Assert.True(astern.BcrM < 0, $"目標由艉後通過應為負:{astern.BcrM}");
    }

    [Fact]
    public void ShipOutlineAndPolygonIntersection()
    {
        var a = Encounter.ShipOutline(0, 0, 0, 100, 20);
        Assert.Equal(5, a.Length);
        Assert.Equal((0.0, 50.0), a[0]); // 艏尖向北
        var touching = Encounter.ShipOutline(0, 100, 0, 100, 20);        // 艉在 y = 50,正好接觸
        var apart = Encounter.ShipOutline(0, 101, 0, 100, 20);
        var crossing = Encounter.ShipOutline(0, 0, Units.DegToRad(90.0), 100, 20);
        var beside = Encounter.ShipOutline(21, 0, 0, 100, 20);
        Assert.True(Encounter.PolygonsIntersect(a, touching));
        Assert.False(Encounter.PolygonsIntersect(a, apart));
        Assert.True(Encounter.PolygonsIntersect(a, crossing));
        Assert.False(Encounter.PolygonsIntersect(a, beside));
        Assert.True(Encounter.PolygonsIntersect(a, Encounter.ShipOutline(19, 0, 0, 100, 20)));
    }

    // ------------------------------------------------------------------ 碰撞

    [Fact]
    public void CollisionSetsFlagAndRaisesEvent()
    {
        // 目標停在自船正前方 300 m,自船 7.8 kn 北行 → 約 60 s 後船體相交
        var sc = CalmScenario(Target("WALL", 0, 300, 90, 0));
        var e = Engine(sc);
        var events = new List<TrafficEvent>();
        e.TrafficEventRaised += (_, ev) => events.Add(ev);
        Assert.False(e.BuildState().Flags!.Collision);
        e.Run(50 * 120);
        Assert.True(e.Collision);
        Assert.True(e.BuildState().Flags!.Collision);
        var col = Assert.Single(events, ev => ev.Kind == TrafficEventKinds.Collision);
        Assert.Equal("WALL", col.TargetId);
        Assert.InRange(col.T, 30.0, 90.0);
        // 最小 CPA 事件(門檻 0.5 nm)也已記錄
        Assert.Contains(events, ev => ev.Kind == TrafficEventKinds.CpaAlarm && ev.TargetId == "WALL");
    }

    [Fact]
    public void StateCarriesTargetMetricsAndNoTargetsMeansNull()
    {
        var sc = CalmScenario(Target("T", 1852.0, 0, 270, 0));
        var e = Engine(sc);
        e.Run(1);
        var s = e.BuildState();
        var t = Assert.Single(s.Targets!);
        Assert.Equal("T", t.Id);
        Assert.Equal(1.0, t.RangeNm, 3);
        Assert.Equal(90.0, t.BearingDeg, 1);
        Assert.Equal(TrafficManager.DefaultMmsiBase, t.Mmsi);
        Assert.Equal("hold", t.Behaviour);
        Assert.Equal("powerDriven", t.Lights);
        Assert.NotNull(t.Ais);
        Assert.Equal(70, t.Ais!.ShipType);
        Assert.True(t.Ais.Lat > 23.79);
        Assert.Null(t.Colreg);
        // 無目標船:targets 省略,JSON 與舊版相同
        var plain = TestData.Baseline();
        plain.Targets.Clear();
        var e2 = Engine(plain);
        e2.Run(1);
        Assert.Null(e2.BuildState().Targets);
        Assert.DoesNotContain("\"targets\"", ContractJson.Serialize(e2.BuildState()));
    }

    // ------------------------------------------------------------------ 行為模式

    [Fact]
    public void WaypointsAreReachedInOrderAndSpeedChanges()
    {
        var t = Target("WP", 5000, 0, 0, 8, TargetBehaviourMode.Waypoints, loa: 60);
        t.Behaviour.Waypoints = new List<TargetWaypoint>
        {
            new() { X = 5000, Y = 1000, Speed = 8, Radius = 100 },
            new() { X = 7000, Y = 1000, Speed = 12, Radius = 100 },
            new() { X = 7000, Y = -2000, Speed = 6, Radius = 100 },
        };
        var e = Engine(CalmScenario(t));
        var reached = new List<double>();
        e.TrafficEventRaised += (_, ev) => { if (ev.Kind == TrafficEventKinds.Waypoint) reached.Add(ev.T); };
        var ship = e.Traffic.Find("WP")!;
        e.Run(50 * 230); // 1000 m @ 8 kn ≈ 243 s;到達半徑 100 m → 約 219 s
        Assert.Single(reached);
        Assert.Equal(1, ship.WaypointIndex);
        Assert.InRange(Units.RadToDeg(ship.HeadingCmdRad), 85.0, 95.0); // 第 2 航點在正東
        e.Run(50 * 400);                                                 // t = 630 s:2000 m 東行(8 → 12 kn)已到第 2 航點
        Assert.Equal(2, ship.WaypointIndex);
        Assert.InRange(Units.MpsToKn(ship.Speed), 10.0, 12.0);          // 速度已向 12 kn 爬升(τ = 120 s)
        Assert.True(Units.RadToDeg(ship.Psi) > 100.0, $"應正右轉向南:{Units.RadToDeg(ship.Psi):F1}");
        e.Run(50 * 200);                                                 // 50°/min → 90° 轉向約 2 分鐘內完成
        Assert.InRange(Units.RadToDeg(ship.Psi), 170.0, 190.0);
        e.Run(50 * 900);
        Assert.Equal(3, ship.WaypointIndex);                             // 終點:保持最後航向
        Assert.Equal(3, reached.Count);
        Assert.InRange(Units.MpsToKn(ship.Speed), 5.5, 6.5);
    }

    [Fact]
    public void ScriptedStepsApplyAtTheirTimes()
    {
        var t = Target("SC", 3000, 3000, 0, 5, TargetBehaviourMode.Scripted, loa: 80);
        t.Behaviour.Script = new List<TargetScriptStep>
        {
            new() { T = 30, Heading = 90 },
            new() { T = 300, Speed = 10 },
            new() { T = 400, Heading = 180, Rot = 10 },
        };
        var e = Engine(CalmScenario(t));
        var ship = e.Traffic.Find("SC")!;
        e.Run(50 * 29);
        Assert.Equal(0, ship.ScriptIndex);
        Assert.Equal(0.0, Units.RadToDeg(ship.Psi), 6);
        e.Run(50 * 60); // t = 89 s:轉向東中(L = 80 m → 最大迴轉率 37.5°/min、τ 8 s;90° 約需 3 分鐘)
        Assert.Equal(1, ship.ScriptIndex);
        Assert.InRange(Units.RadToDeg(ship.Psi), 20.0, 60.0);
        Assert.Equal(ship.MaxRotRadps, ship.RotLimitRadps, 12);
        e.Run(50 * 200); // t = 289 s
        Assert.Equal(1, ship.ScriptIndex);
        Assert.InRange(Units.RadToDeg(ship.Psi), 88.0, 92.0);
        Assert.Equal(5.0, Units.MpsToKn(ship.Speed), 6);
        e.Run(50 * 60); // t = 349 s
        Assert.Equal(2, ship.ScriptIndex);
        Assert.True(ship.Speed > 5.0 * Kn, "航速應在爬升(τ = 160 s)");
        e.Run(50 * 60); // t = 409 s:第 3 步以 10°/min 轉向南
        Assert.Equal(3, ship.ScriptIndex);
        Assert.Equal(Units.DegPerMinToRadPerSec(10.0), ship.RotLimitRadps, 12);
        e.Run(50 * 240); // t = 649 s:10°/min × 4 min ≈ 40°,尚在 130° 左右
        Assert.InRange(Units.RadToDeg(ship.Psi), 115.0, 145.0);
        e.Run(50 * 500); // t = 1149 s
        Assert.InRange(Units.RadToDeg(ship.Psi), 178.0, 182.0);
        Assert.InRange(Units.MpsToKn(ship.Speed), 9.5, 10.0);
    }

    [Fact]
    public void AnchoredTargetSwingsAboutAnchorWithoutUsingEngineRandom()
    {
        var sc = TestData.Baseline(); // 有風(東北)與流(200°)
        sc.Targets = new List<ScenarioTarget> { Target("ANC", 1500, 2200, 45, 0, TargetBehaviourMode.Anchored, loa: 90, beam: 15) };
        var e = Engine(sc);
        var ship = e.Traffic.Find("ANC")!;
        var countBefore = e.Random.Count;
        e.Run(777);
        Assert.Equal(16, e.Random.Count - countBefore); // 與 SnapshotPreservesRandomStateWithGusts 相同:交通模組不抽亂數
        e.Run(50 * 600);
        var d = Math.Sqrt((ship.X - 1500) * (ship.X - 1500) + (ship.Y - 2200) * (ship.Y - 2200));
        Assert.InRange(d, 89.9, 90.1); // 船舯在錨位後方 scope = 船長
        var s = e.BuildState().Targets!.Single();
        Assert.Equal("anchored", s.Behaviour);
        Assert.Equal("anchored", s.Lights);
        Assert.Equal(1, s.Ais!.NavStatus);
        Assert.True(s.Sog < 0.5, $"錨泊微漂 SOG {s.Sog:F2} kn");
        // 艏向大致朝風流合成來向(風 NE 15 kn ×3 % ≈ 0.23 m/s 來向 45°,流 0.26 m/s 去向 200° → 來向 20°)
        var hdg = Units.RadToDeg(ship.AnchorBaseRad);
        Assert.InRange(hdg, 15.0, 50.0);
    }

    [Fact]
    public void FollowKeepsStationOnOwnShip()
    {
        var t = Target("PILOT", 200, -300, 0, 0, TargetBehaviourMode.Follow, loa: 18, beam: 5);
        t.ShipType = "pilot";
        t.Behaviour.Follow = new TargetFollow { Leader = "own", Bearing = 120, Range = 70, Gain = 0.05, MaxSpeed = 20 };
        t.Motion = new TargetMotion { SpeedTau = 8 };
        var e = Engine(CalmScenario(t));
        e.Run(50 * 400);
        var own = e.Motion;
        var ship = e.Traffic.Find("PILOT")!;
        var ang = own.Psi + Units.DegToRad(120.0);
        var px = own.X + 70.0 * Math.Sin(ang);
        var py = own.Y + 70.0 * Math.Cos(ang);
        var err = Math.Sqrt((ship.X - px) * (ship.X - px) + (ship.Y - py) * (ship.Y - py));
        Assert.True(err < 15.0, $"跟隨位置誤差 {err:F1} m");
        Assert.InRange(Math.Abs(Units.RadToDeg(Units.WrapRadPi(ship.Psi - own.Psi))), 0.0, 10.0);
        Assert.InRange(Units.MpsToKn(ship.Speed), 7.0, 8.6);
        Assert.Equal("pilot", e.BuildState().Targets!.Single().Lights);
    }

    [Fact]
    public void FollowAnotherTargetUsesItsKinematics()
    {
        var leader = Target("TUG", 0, 2000, 90, 6, TargetBehaviourMode.Hold, loa: 30, beam: 9);
        var barge = Target("BARGE", -150, 2000, 90, 6, TargetBehaviourMode.Follow, loa: 40, beam: 10);
        barge.Behaviour.Follow = new TargetFollow { Leader = "TUG", Bearing = 180, Range = 120, Gain = 0.05, MaxSpeed = 10 };
        barge.Motion = new TargetMotion { SpeedTau = 20 };
        var e = Engine(CalmScenario(leader, barge));
        e.Run(50 * 300);
        var l = e.Traffic.Find("TUG")!;
        var b = e.Traffic.Find("BARGE")!;
        var dx = l.X - 120.0 - b.X;
        var dy = l.Y - b.Y;
        Assert.True(Math.Sqrt(dx * dx + dy * dy) < 10.0, $"拖帶距離誤差 {Math.Sqrt(dx * dx + dy * dy):F1} m");
    }

    [Fact]
    public void FollowerStartingOnWrongSideDetoursAroundLeaderHullWithoutCollision()
    {
        // 引水船在自船左艏前方,站位在右舷後方:直線路徑會穿過自船 → 先繞艉再進站,全程不得碰撞
        var t = Target("PILOT", -150, 400, 180, 0, TargetBehaviourMode.Follow, loa: 18, beam: 5);
        t.ShipType = "pilot";
        t.Behaviour.Follow = new TargetFollow { Leader = "own", Bearing = 120, Range = 70, Gain = 0.05, MaxSpeed = 20 };
        t.Motion = new TargetMotion { SpeedTau = 8, MaxRot = 120 };
        var e = Engine(CalmScenario(t));
        var events = new List<TrafficEvent>();
        e.TrafficEventRaised += (_, ev) => events.Add(ev);
        e.Run(50 * 600);
        Assert.False(e.Collision);
        Assert.DoesNotContain(events, ev => ev.Kind == TrafficEventKinds.Collision);
        Assert.DoesNotContain(events, ev => ev.Kind == TrafficEventKinds.CpaAlarm); // 跟隨自船者不列入 CPA 警報
        var own = e.Motion;
        var ship = e.Traffic.Find("PILOT")!;
        var ang = own.Psi + Units.DegToRad(120.0);
        var err = Math.Sqrt(Math.Pow(ship.X - (own.X + 70.0 * Math.Sin(ang)), 2) + Math.Pow(ship.Y - (own.Y + 70.0 * Math.Cos(ang)), 2));
        Assert.True(err < 15.0, $"最終站位誤差 {err:F1} m");
        Assert.True(TrafficManager.SegmentIntersectsRect(-10, 0, 10, 0, -5, 5, -5, 5));
        Assert.False(TrafficManager.SegmentIntersectsRect(-10, 6, 10, 6, -5, 5, -5, 5));
        Assert.True(TrafficManager.SegmentIntersectsRect(0, 0, 100, 100, -5, 5, -5, 5));
    }

    // ------------------------------------------------------------------ COLREG

    [Fact]
    public void HeadOnTargetAltersCourseToStarboard()
    {
        // 自船向北 7.8 kn;目標 5 nm 正前方向南 9 kn,略偏左 → 對遇;TCPA 約 17.9 min,門檻 12 min → 約 6 min 後右轉 30°
        var t = Target("HO", -100, 9260, 180, 9, TargetBehaviourMode.Colreg, loa: 120, beam: 19);
        t.Behaviour.Colreg = new TargetColreg { CpaThreshold = 1.0, TcpaThreshold = 12.0, Turn = 30.0 };
        var e = Engine(CalmScenario(t));
        var events = new List<TrafficEvent>();
        e.TrafficEventRaised += (_, ev) => events.Add(ev);
        var ship = e.Traffic.Find("HO")!;
        e.Run(50 * 300);
        Assert.Equal(ColregPhase.None, ship.ColregPhase);
        Assert.Equal("headOn/giveWay", ship.ColregSituation);
        Assert.Equal(180.0, Units.RadToDeg(ship.Psi), 6);
        e.Run(50 * 180); // t = 480 s
        Assert.Equal(ColregPhase.GiveWay, ship.ColregPhase);
        Assert.StartsWith("headOn/giveWay/avoiding", ship.ColregSituation);
        Assert.InRange(Units.RadToDeg(ship.Psi), 205.0, 245.0);          // 右轉 30°(CPA 仍略低於門檻時可再加 30°)
        Assert.True(ship.ColregTurnAccumRad >= Units.DegToRad(30.0) - 1e-9);
        var st = e.BuildState().Targets!.Single();
        Assert.StartsWith("headOn/giveWay/avoiding", st.Colreg);
        var action = events.First(ev => ev.Kind == TrafficEventKinds.Colreg && ev.Message!.Contains("avoiding"));
        Assert.InRange(action.T, 330.0, 420.0);
        Assert.True(action.TcpaMin < 12.0 && action.CpaNm < 1.0);
        // 通過後回到基底行為(冷卻),航向保持在避讓後的值
        e.Run(50 * 900);
        Assert.True(ship.ColregPhase is ColregPhase.Resumed or ColregPhase.None, ship.ColregPhase.ToString());
        Assert.Contains(events, ev => ev.Kind == TrafficEventKinds.Colreg && ev.Message == "resumed");
        // 預測 CPA 的最小值含行動前的對遇(≈ 0.05 nm);實際最近距離須因右轉而大於 0.4 nm
        Assert.True(ship.MinCpaNm < 0.1);
        Assert.True(ship.MinRangeNm > 0.4, $"避讓後實際最近距離 {ship.MinRangeNm:F2} nm");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossingGiveWayTargetTurnsAndSlowsUnlessIgnoringRules(bool obey)
    {
        // 自船向北 7.8 kn;目標在左前方 (−3000, 3000) 向東 7.8 kn:自船在目標右舷艏 45° → 目標讓路(右轉 30°、減速 0.7);TCPA ≈ 12.5 min
        var t = Target("CR", -3000, 3000, 90, 7.8, TargetBehaviourMode.Colreg, loa: 100, beam: 16);
        t.Behaviour.Colreg = new TargetColreg { Obey = obey, CpaThreshold = 1.0, TcpaThreshold = 12.0, Turn = 30.0, SpeedFactor = 0.7 };
        var e = Engine(CalmScenario(t));
        var ship = e.Traffic.Find("CR")!;
        e.Run(50 * 240);
        var state = e.BuildState().Targets!.Single();
        if (obey)
        {
            Assert.Equal(ColregPhase.GiveWay, ship.ColregPhase);
            Assert.StartsWith("crossing/giveWay/avoiding", state.Colreg);
            // 右轉 30° 後 CPA 約 0.8 nm 仍低於門檻 → 轉向完成 60 s 後再加 30°(累計 ≤ 90°)
            Assert.InRange(Units.RadToDeg(ship.Psi), 115.0, 185.0);
            Assert.True(ship.ColregTurnAccumRad >= Units.DegToRad(30.0) - 1e-9);
            Assert.True(ship.Speed < 7.0 * Kn, $"應減速:{Units.MpsToKn(ship.Speed):F2} kn");
            e.Run(50 * 1200);
            Assert.True(ship.MinRangeNm > 0.5, $"讓路後實際最近距離 {ship.MinRangeNm:F2} nm");
        }
        else
        {
            Assert.Equal(ColregPhase.None, ship.ColregPhase);
            Assert.Equal("crossing/giveWay(ignored)", state.Colreg);
            Assert.Equal(90.0, Units.RadToDeg(ship.Psi), 6);
            Assert.Equal(7.8, Units.MpsToKn(ship.Speed), 6);
        }
    }

    [Fact]
    public void CrossingStandOnTargetHoldsThenTakesLastResort()
    {
        // 自船向北;目標在右前方 (3000, 3000) 向西:自船在目標左舷 → 目標直航;自船不讓 → CPA < 0.5、TCPA < 4 min 時最後手段右轉
        var t = Target("SO", 3000, 3000, 270, 7.8, TargetBehaviourMode.Colreg, loa: 100, beam: 16);
        t.Behaviour.Colreg = new TargetColreg { CpaThreshold = 1.0, TcpaThreshold = 12.0, Turn = 30.0, LastResort = true };
        var e = Engine(CalmScenario(t));
        var ship = e.Traffic.Find("SO")!;
        e.Run(50 * 120);
        Assert.Equal(ColregPhase.StandOn, ship.ColregPhase);
        Assert.Equal("crossing/standOn/holding", ship.ColregSituation);
        Assert.Equal(270.0, Units.RadToDeg(ship.Psi), 6);
        e.Run(50 * 480); // t = 600 s,TCPA ≈ 2.5 min
        Assert.Equal(ColregPhase.LastResort, ship.ColregPhase);
        Assert.True(Units.RadToDeg(ship.Psi) > 275.0, $"最後手段應右轉:{Units.RadToDeg(ship.Psi):F1}");
    }

    [Fact]
    public void ForcedGiveWayAndStandOnViaCommand()
    {
        var t = Target("F", 0, 6000, 180, 6, TargetBehaviourMode.Hold, loa: 100);
        var e = Engine(CalmScenario(t));
        var ship = e.Traffic.Find("F")!;
        e.Enqueue(SimCommand.TargetControl(new { id = "F", colreg = "giveWay" }));
        e.Run(50 * 70);
        Assert.True(ship.ColregEnabled);
        Assert.Equal(ColregPhase.GiveWay, ship.ColregPhase);
        Assert.InRange(Units.RadToDeg(ship.Psi), 204.0, 212.0);          // 強制讓路:立即右轉 30°(30°/min)
        Assert.True(ship.Speed < 6.0 * Kn, "強制讓路同時減速");
        e.Enqueue(SimCommand.TargetControl(new { id = "F", colreg = "standOn" }));
        e.Run(50 * 5);
        Assert.Equal(ColregPhase.StandOn, ship.ColregPhase);
        e.Run(50 * 150);                                                 // 直航:收斂到強制時的航向,不再轉向(二階迴路約 80 s 安定)
        Assert.Equal(Units.RadToDeg(ship.ColregHeadingRad), Units.RadToDeg(ship.Psi), 1);
        Assert.True(Math.Abs(Units.RadPerSecToDegPerMin(ship.R)) < 0.5);
        e.Enqueue(SimCommand.TargetControl(new { id = "F", colreg = "off" }));
        e.Run(2);
        Assert.False(ship.ColregEnabled);
        Assert.Null(e.BuildState().Targets!.Single().Colreg);
    }

    // ------------------------------------------------------------------ 觸發與指令

    [Fact]
    public void TriggersActivateTargetsOnTimeDistanceAndHeading()
    {
        var byTime = Target("TM", 2000, 0, 0, 5);
        byTime.Trigger = new TargetTrigger { Type = TargetTriggerType.Time, Time = 20.0 };
        var byDist = Target("DI", -2000, 0, 0, 5);
        byDist.Trigger = new TargetTrigger { Type = TargetTriggerType.OwnDistance, Point = new ScenarioPosition { X = 0, Y = 400 }, LessThan = 0.1 }; // 185 m
        var byHdg = Target("HD", 0, -3000, 0, 5);
        byHdg.Trigger = new TargetTrigger { Type = TargetTriggerType.OwnHeading, GreaterThan = 20.0, LessThan = 60.0 };
        var e = Engine(CalmScenario(byTime, byDist, byHdg));
        var activated = new List<(string Id, double T)>();
        e.TrafficEventRaised += (_, ev) => { if (ev.Kind == TrafficEventKinds.Activated) activated.Add((ev.TargetId!, ev.T)); };
        Assert.Null(e.BuildState().Targets!.FirstOrDefault(x => x.Id == "TM"));
        Assert.Empty(e.BuildState().Targets!);
        e.Run(50 * 25);
        Assert.Contains(activated, a => a.Id == "TM" && Math.Abs(a.T - 20.0) < 0.5);
        Assert.Single(e.BuildState().Targets!);
        e.Run(50 * 60); // 自船 7.8 kn ≈ 4 m/s:85 s 後約 340 m → 距 (0,400) < 185 m
        Assert.Contains(activated, a => a.Id == "DI");
        Assert.DoesNotContain(activated, a => a.Id == "HD");
        e.Enqueue(SimCommand.Autopilot(true, heading: 40));
        e.Run(50 * 300);                                                 // 自動舵 15°/min 限制,約 3 分鐘進入 20–60° 扇區
        Assert.Contains(activated, a => a.Id == "HD");
        Assert.Equal(3, e.BuildState().Targets!.Count);
        // 教官強制啟動
        var manual = Target("MA", 0, -5000, 0, 0);
        manual.Trigger = new TargetTrigger { Type = TargetTriggerType.Time, Time = 99999 };
        e.Enqueue(SimCommand.TargetControl(new { add = manual }));
        e.Run(2);
        Assert.False(e.Traffic.Find("MA")!.Active);
        e.Enqueue(SimCommand.TargetControl(new { id = "MA", activate = true }));
        e.Run(2);
        Assert.True(e.Traffic.Find("MA")!.Active);
    }

    [Fact]
    public void TargetControlOverridesAddsRemovesAndChangesAis()
    {
        var e = Engine(CalmScenario(Target("A", 2000, 2000, 0, 5)));
        var a = e.Traffic.Find("A")!;
        // 覆寫航向/航速 → manual;release 回復
        e.Enqueue(SimCommand.TargetOverride("A", headingDeg: 90, speedKn: 10));
        e.Run(50 * 200);
        Assert.True(a.Manual);
        Assert.Equal("manual", e.BuildState().Targets!.Single().Behaviour);
        Assert.InRange(Units.RadToDeg(a.Psi), 88.0, 92.0);
        Assert.True(a.Speed > 7.0 * Kn);
        e.Enqueue(SimCommand.TargetRelease("A"));
        e.Run(2);
        Assert.False(a.Manual);
        Assert.Equal("hold", e.BuildState().Targets!.Single().Behaviour);
        // AIS 關/錯誤注入
        e.Enqueue(SimCommand.TargetAis("A", false));
        e.Run(2);
        var s = e.BuildState().Targets!.Single();
        Assert.False(s.AisOn);
        Assert.Null(s.Ais);
        e.Enqueue(SimCommand.TargetAis("A", true));
        e.Enqueue(SimCommand.TargetControl(new { id = "A", aisError = new { positionOffset = 1000.0, positionOffsetBearing = 90.0, headingError = 20.0, sogError = 2.0, staticError = true } }));
        e.Run(2);
        s = e.BuildState().Targets!.Single();
        Assert.NotNull(s.Ais);
        Assert.Equal(Units.NormalizeHeadingDeg(s.Heading + 20.0), s.Ais!.Heading, 6);
        Assert.Equal(s.Sog + 2.0, s.Ais.Sog, 6);
        Assert.True(s.Ais.Lon > s.Pos.Lon + 0.005, "位置向東偏移 1000 m");
        Assert.Equal(s.Pos.Lat, s.Ais.Lat, 9);
        Assert.Equal("A II", s.Ais.Name);
        Assert.Equal(37, s.Ais.ShipType);
        Assert.Equal(25.0, s.Ais.DimToBow);
        e.Enqueue(SimCommand.TargetControl(new { id = "A", aisError = new { silent = true } }));
        e.Run(2);
        s = e.BuildState().Targets!.Single();
        Assert.True(s.AisOn);
        Assert.Null(s.Ais);
        // 燈號與聲號
        e.Enqueue(SimCommand.TargetControl(new { id = "A", lights = "notUnderCommand", sound = "twoShort" }));
        e.Run(2);
        s = e.BuildState().Targets!.Single();
        Assert.Equal("notUnderCommand", s.Lights);
        Assert.Equal("twoShort", s.Sound);
        e.Run(50 * 5);
        Assert.Null(e.BuildState().Targets!.Single().Sound);
        // 航點修改切換為 waypoints
        e.Enqueue(SimCommand.TargetControl(new { id = "A", waypoints = new[] { new { x = 2000.0, y = 9000.0, speed = 9.0 } } }));
        e.Run(2);
        Assert.Equal(TargetBehaviourMode.Waypoints, a.Spec.Behaviour.Mode);
        Assert.Equal("waypoints", e.BuildState().Targets!.Single().Behaviour);
        // 新增與刪除
        e.Enqueue(SimCommand.TargetControl(new { add = Target("B", -500, 4000, 180, 4) }));
        e.Run(2);
        Assert.Equal(2, e.BuildState().Targets!.Count);
        Assert.Equal(TrafficManager.DefaultMmsiBase + 1, e.Traffic.Find("B")!.Mmsi);
        e.Enqueue(SimCommand.TargetRemove("B"));
        e.Enqueue(SimCommand.TargetRemove("NOPE")); // 無此目標:忽略
        e.Run(2);
        Assert.Single(e.BuildState().Targets!);
        // 行為切換為錨泊(先清除 AIS 錯誤:args.aisError = null)
        e.Enqueue(SimCommand.Parse("""{"type":"targetControl","args":{"id":"A","aisError":null}}""")!);
        e.Enqueue(SimCommand.TargetControl(new { id = "A", behaviour = new { mode = "anchored" } }));
        e.Run(50 * 10);
        Assert.Null(a.Spec.AisError);
        Assert.Equal("anchored", e.BuildState().Targets!.Single().Behaviour);
        Assert.Equal(1, e.BuildState().Targets!.Single().Ais!.NavStatus);
    }

    [Fact]
    public void CpaAlarmUsesScenarioThresholdAndClearsAfterPassing()
    {
        // 目標由東向西通過自船前方 0.3 nm(自船 7.8 kn 北行,目標 0 kn → 相對運動由自船造成)
        var sc = CalmScenario(Target("X", 0, 556, 270, 0));
        sc.Assessment.MinCpaNm = 0.4;
        var e = Engine(sc);
        Assert.Equal(0.4, e.Traffic.MinCpaThresholdNm);
        var events = new List<TrafficEvent>();
        e.TrafficEventRaised += (_, ev) => events.Add(ev);
        e.Run(50 * 300);
        // 目標在正前方 0.3 nm 靜止,自船直衝 → CPA 0 < 0.4 立即警報;通過(碰撞後 tcpa < 0)後解除
        Assert.Contains(events, ev => ev.Kind == TrafficEventKinds.CpaAlarm && ev.CpaNm < 0.4);
        Assert.Contains(events, ev => ev.Kind == TrafficEventKinds.CpaClear);
        Assert.True(e.Traffic.Find("X")!.MinRangeNm < 0.05);
    }
}
