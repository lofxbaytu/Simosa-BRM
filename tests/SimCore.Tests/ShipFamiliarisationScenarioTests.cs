using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 船舶熟悉 F01–F03 與 E10、E11 情境(規劃書第 8.1 節;docs/design/ship-familiarisation.md):載入/驗證無錯誤、操縱量測啟用條件、
/// 無人操作時時間表示範流程的關鍵事件(量測 id 與順序、結束)、有人操作時必要事件 met、E11 Williamson 迴旋的量測與分類、
/// 紀錄 → 重播的雜湊/事件序列/summary 一致。No.2 壓載的 MMG 係數尚未產生,F01_FSB2_ballast / F02_FSB2 以暫代模型執行(主控程序 --dynamics placeholder)。
/// </summary>
public class ShipFamiliarisationScenarioTests
{
    private readonly ITestOutputHelper _out;
    public ShipFamiliarisationScenarioTests(ITestOutputHelper output) => _out = output;

    private static readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg = (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> Placeholder = (s, l) => new PlaceholderDynamics(s, l);

    public static readonly string[] AllIds =
    {
        "F01_FSB1_full", "F01_FSB1_ballast", "F01_FSB2_full", "F01_FSB2_ballast", "F02_FSB1_low_speed", "F02_FSB2_low_speed",
        "F03_FSB1_channel", "F03_FSB2_channel", "E10_engine_failure_lee_shore", "E11_man_overboard",
    };

    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));

    /// <summary>係數檔存在時用 MMG(與主控程序預設相同),否則暫代模型。</summary>
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> DynamicsFor(Scenario.Scenario sc)
        => File.Exists(MmgCoefficients.PathFor(TestData.Root, sc.Ship.Id, sc.Ship.Loading)) ? Mmg : Placeholder;

    private static (SimulationEngine Engine, List<SimEvent> Events) Engine(string id)
    {
        var sc = Load(id);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, DynamicsFor(sc), new EngineOptions { AutoSnapshotIntervalTicks = 0 });
        return (e, TestData.Capture(e));
    }

    private static void RunUntilEnded(SimulationEngine e, double maxS)
    {
        var steps = (long)Math.Round(maxS / e.Dt);
        for (long i = 0; i < steps && !e.Ended; i++) e.Step();
    }

    private static List<string?> Measured(List<SimEvent> events) => events.Where(x => x.Kind == ManoeuvreEventKinds.Measured).Select(x => x.ItemId).ToList();

    // ------------------------------------------------------------------ 載入與驗證

    [Theory]
    [MemberData(nameof(Ids))]
    public void LoadsAndValidatesWithoutErrors(string id)
    {
        var sc = Load(id);
        Assert.Equal(id, sc.Id);
        var issues = ScenarioValidator.Check(sc, TestData.Ship(sc.Ship.Id));
        foreach (var i in issues) _out.WriteLine(i.ToString());
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Error);
        Assert.NotNull(sc.ParameterSetInfo);
        // 新增參數標來源「假設值,待公司程序」;流程時間/門檻皆引用參數集
        var sources = TestData.Parameters().Sources;
        foreach (var p in sc.ParameterSetInfo!.Resolved.Where(p => p.Key.StartsWith("familiarisation.") || p.Key.StartsWith("emergency.engineFailure.") || p.Key.StartsWith("emergency.manOverboard.")))
            Assert.StartsWith("假設值,待公司程序", sources[p.Source].Title);
        // 操縱量測啟用:F01–F03(時間表/必要事件引用 manoeuvre 事件)與 E11(MOB 目標);E10 不使用
        Assert.Equal(!id.StartsWith("E10", StringComparison.Ordinal), ManoeuvreTracker.IsReferenced(sc));
        if (id.StartsWith("F", StringComparison.Ordinal))
        {
            Assert.Empty(sc.Assessment.HardFail.Where(h => h.Rule is not ("grounding" or "ukc")));   // 船舶熟悉不設操作類硬性失敗
            Assert.All(sc.Assessment.Required, r => Assert.Equal(0.0, r.Penalty));                   // 只記錄
            Assert.Contains(sc.Assessment.Required, r => r.Expect.Leaves().Any(l => l.Acknowledge?.Kind == "prediction"));
        }
    }

    public static IEnumerable<object[]> Ids() => AllIds.Select(x => new object[] { x });

    [Fact]
    public void ParameterSetHasFamiliarisationAndEmergencyKeys()
    {
        var p = TestData.Parameters();
        foreach (var key in new[] { "familiarisation.predictionTime_s", "familiarisation.demoAfter_s", "familiarisation.settle_s", "familiarisation.manoeuvreComplete_s",
                     "emergency.engineFailure.injectAt_s", "emergency.engineFailure.repairEstimate_s", "emergency.engineFailure.tugEta_s", "emergency.engineFailure.anchorDecision_s",
                     "emergency.manOverboard.immediateAction_s", "emergency.manOverboard.notifySar_s", "emergency.manOverboard.return_s" })
            Assert.True(p.TryGet(key, out _), key);
        Assert.True(p.TryGet("emergency.engineFailure.repairEstimate_s", out var repair));
        Assert.Equal(1800, repair.Value.GetDouble());       // 規劃書 E10:機控室估修 30 分鐘
        Assert.True(p.TryGet("emergency.engineFailure.tugEta_s", out var tug));
        Assert.Equal(7200, tug.Value.GetDouble());          // 拖船 2 小時後到
    }

    // ------------------------------------------------------------------ F01

    /// <summary>F01 No.1 滿載無人操作:系統示範全流程,量測順序與比較(試俥 trial)、Schilling 註記、結束。</summary>
    [Fact]
    public void F01_Fsb1Full_HandsOffDemoRunsWholeFlow()
    {
        var (e, events) = Engine("F01_FSB1_full");
        RunUntilEnded(e, 7200);
        Assert.True(e.Ended);
        Assert.Equal(new[] { "turning35.port", "turning35.starboard", "zigzag10.portFirst", "crashStop", "inertiaStop", "turning35.port", "turning70.port" }, Measured(events));
        Assert.DoesNotContain(events, x => x.Kind == ManoeuvreEventKinds.Aborted);
        var recs = e.LatestSummary!.Manoeuvres!;
        Assert.Equal(7, recs.Count);
        var port = recs[0];
        Assert.True(port.Complete && port.Approach.Steady && port.Approach.Autopilot);
        Assert.Equal("trial", port.DataQuality);
        Assert.All(port.Comparisons, c => Assert.True(c.Comparable && c.WithinTolerance, c.Key));
        Assert.Contains(recs[1].Comparisons, c => c.Key == "transfer_m" && !c.WithinTolerance);   // 右迴旋橫距:已知偏差
        Assert.Equal(3, recs[2].Comparisons.Count(c => c.Comparable && c.WithinTolerance));         // 10/10 三個超越角
        Assert.Equal("EFAS", recs[3].Order);
        Assert.True(recs[3].Comparisons.Single(c => c.Key == "stopTime_s" && c.DataQuality == "trial").WithinTolerance);
        Assert.False(recs[4].Comparisons.Single(c => c.Key == "time_s").WithinTolerance);            // 慣性停船:已知偏差
        Assert.False(recs[5].Complete);                                                               // 低速 35° 只做到 180°
        Assert.True(recs[5].Values.ContainsKey("tacticalDiameter_m"));
        Assert.Contains(recs[6].Notes, n => n.Contains("僅經船長驗證"));
        Assert.True(recs[6].Values["tacticalDiameter_m"] < recs[5].Values["tacticalDiameter_m"]);   // Schilling 70° 比 35° 迴旋小
        // 預測未記錄 → 必要事件 missed(扣分 0);量測完成 → met;不評分
        var summary = e.LatestSummary;
        Assert.All(summary.Assessment.Required.Where(r => r.Id.StartsWith("prediction")), r => Assert.Equal("missed", r.State));
        Assert.All(summary.Assessment.Required.Where(r => r.Id.StartsWith("measured")), r => Assert.Equal("met", r.State));
        Assert.Equal(100, summary.Assessment.Score);
        Assert.True(summary.Assessment.Passed);
        Assert.Contains(events, x => x.Kind == SimEventKinds.ExerciseEnded && x.ItemId == "end");
    }

    /// <summary>F01 有人操作:預測確認後立即提示執行,學員自行下舵令(不示範),量測與必要事件 met。</summary>
    [Fact]
    public void F01_StudentPredictionAndOrderAreUsed()
    {
        var (e, events) = Engine("F01_FSB1_full");
        var script = new (long Tick, SimCommand Cmd)[]
        {
            (50 * 40, SimCommand.Acknowledge("prediction", itemId: "predictTurnPort", role: "officer", note: "前進 300 m、戰術直徑 290 m、90° 55 s")),
            (50 * 80, SimCommand.Rudder(-35)),
        };
        EngineDeterminismTests.RunScript(e, 50 * 520, script);
        Assert.Equal(41.0, events.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "executeTurnPort").T);   // 確認後下一個評估步
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "demoTurnPort");
        var started = events.Single(x => x.Kind == ManoeuvreEventKinds.Started);
        Assert.Equal(("turning35.port", 80.0, SimEventSources.Assessment), (started.ItemId, started.T, started.Source));
        Assert.Equal(new[] { "turning35.port" }, Measured(events));
        var req = e.LatestSummary!.Assessment.Required;
        Assert.Equal(("met", "acknowledge:prediction"), (req.Single(r => r.Id == "predictionTurnPort").State, req.Single(r => r.Id == "predictionTurnPort").By));
        Assert.Equal("met", req.Single(r => r.Id == "measuredTurnPort").State);
        var data = events.Single(x => x.Kind == ManoeuvreEventKinds.Measured).Data!.Value;
        Assert.Equal("turning35.port", data.GetProperty("id").GetString());
        Assert.Equal("trial", data.GetProperty("dataQuality").GetString());
        Assert.True(data.GetProperty("values").GetProperty("tacticalDiameter_m").GetDouble() > 250);
    }

    [Theory]
    [InlineData("F01_FSB1_ballast", "estimate")]
    [InlineData("F01_FSB2_full", "poster")]
    [InlineData("F01_FSB2_ballast", "none")]
    public void F01_OtherVariantsCompleteWithDataQuality(string id, string turningQuality)
    {
        var (e, events) = Engine(id);
        RunUntilEnded(e, 7200);
        Assert.True(e.Ended);
        var expected = id == "F01_FSB1_ballast"
            ? new[] { "turning35.port", "turning35.starboard", "zigzag10.portFirst", "crashStop", "inertiaStop", "turning35.port", "turning70.port" }
            : new[] { "turning35.port", "turning35.starboard", "zigzag10.portFirst", "crashStop", "inertiaStop" };
        Assert.Equal(expected, Measured(events));
        var recs = e.LatestSummary!.Manoeuvres!;
        Assert.Equal(turningQuality, recs[0].DataQuality);
        switch (id)
        {
            case "F01_FSB1_ballast":
                // 海報壓載估計值進入速度 15.5 kn 高於模型可達 14.8 kn:比較標為不可比並說明
                Assert.All(recs[0].Comparisons, c => Assert.False(c.Comparable));
                Assert.Contains(recs[0].Notes, n => n.Contains("進入速度") && n.Contains("15.5"));
                Assert.Contains(recs[3].Comparisons, c => c.DataQuality == "estimate" && c.Key == "stopTime_s");
                break;
            case "F01_FSB2_full":
                Assert.Equal("FAS", recs[3].Order);
                Assert.Contains(recs[0].Notes, n => n.Contains("公差為 No.1 的 1.5 倍"));
                Assert.Contains(recs[0].Comparisons, c => c.Key == "timeTo90_s" && c.Comparable);
                Assert.Contains(recs[2].Notes, n => n.Contains("不列入數值驗收"));
                break;
            case "F01_FSB2_ballast":
                Assert.StartsWith("placeholder", recs[0].Dynamics);
                Assert.Empty(recs[0].Comparisons);
                break;
        }
    }

    // ------------------------------------------------------------------ F02

    [Theory]
    [InlineData("F02_FSB1_low_speed")]
    [InlineData("F02_FSB2_low_speed")]
    public void F02_HandsOffDemoMeasuresThrusterAsternBlowOffAndSwing(string id)
    {
        var (e, events) = Engine(id);
        RunUntilEnded(e, 3600);
        Assert.True(e.Ended);
        Assert.Equal(new[] { "thrusterTurn.starboard", "crashStop", "thrusterTurn.starboard", "inertiaStop", "rudderSwing.toStarboard" }, Measured(events));
        var recs = e.LatestSummary!.Manoeuvres!.Where(r => r.State == "measured").ToList();
        Assert.Equal("HAS", recs[1].Order);
        Assert.True(recs[1].Values.ContainsKey("headingChange_deg"));           // 倒俥艉偏
        Assert.True(recs[3].Values.ContainsKey("headingChange_deg"));           // bow blow-off
        Assert.Equal("1", recs[4].Pump);
        // 東北風於 15 分鐘內增至 25 kn(種子化陣風)
        Assert.Contains(events, x => x.Kind == SimEventKinds.EnvironmentRampDone);
        if (id == "F02_FSB1_low_speed")
        {
            // 側推效能隨航速衰減:近零速的 60 s 航向變化大於 6 kn 時
            Assert.True(recs[2].Values["headingChange60_deg"] > recs[0].Values["headingChange60_deg"] + 5.0);
            var swing = recs[4].Comparisons.Single();
            Assert.Equal(19.2, swing.Target);
            Assert.True(swing.Comparable && swing.WithinTolerance);
        }
        else
        {
            Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "promptBothPumps");
            Assert.Equal(28.0, recs[4].Comparisons.Single().Target);
        }
    }

    // ------------------------------------------------------------------ F03

    [Fact]
    public void F03_BothShipsSameChannel_Fsb2SquatsMoreAndStopsLonger()
    {
        var results = new Dictionary<string, AssessmentSummary>();
        foreach (var id in new[] { "F03_FSB1_channel", "F03_FSB2_channel" })
        {
            var (e, events) = Engine(id);
            RunUntilEnded(e, 4000);
            Assert.True(e.Ended);
            Assert.Equal(new[] { "turning35.starboard", "crashStop" }, Measured(events));
            var s = e.LatestSummary!;
            Assert.Equal(2, s.Track!.Waypoints.Count);                         // WP2、WP3 通過
            Assert.All(s.Track.Waypoints, w => Assert.InRange(Math.Abs(w.XtdM), 0, 50)); // 迴旋後回到航線(走廊半寬 50 m)
            Assert.Equal(0, s.Ukc.Groundings);
            Assert.Contains(s.Assessment.Parameters, p => p.Key == "navigation.ukc.channel_fraction");   // UKC 政策引用參數集
            Assert.False(s.Assessment.HardFailed);
            Assert.Contains(s.Manoeuvres![0].Notes, n => n.Contains("淺水"));
            results[id] = s;
        }
        var (a, b) = (results["F03_FSB1_channel"], results["F03_FSB2_channel"]);
        _out.WriteLine($"squat No.1 {a.Ukc.MinSquatM:F2} m / No.2 {b.Ukc.MinSquatM:F2} m;最小 UKC {a.Ukc.MinM:F2} / {b.Ukc.MinM:F2} m");
        Assert.True(b.Ukc.MinSquatM > a.Ukc.MinSquatM + 0.2);
        Assert.True(b.Ukc.MinM < a.Ukc.MinM);
        Assert.True(b.Manoeuvres![0].Values["tacticalDiameter_m"] > a.Manoeuvres![0].Values["tacticalDiameter_m"]);
        Assert.True(b.Manoeuvres[1].Values["stopTime_s"] > a.Manoeuvres[1].Values["stopTime_s"]);
    }

    // ------------------------------------------------------------------ E10

    [Fact]
    public void E10_HandsOff_DriftsOntoLeeShoreAndGrounds()
    {
        var (e, events) = Engine("E10_engine_failure_lee_shore");
        RunUntilEnded(e, 7300);
        Assert.True(e.Ended);
        var injected = events.Single(x => x.Kind == SimEventKinds.FaultInjected);
        Assert.Equal((60.0, "engineFailure", FaultNames.MainEngine), (injected.T, injected.ItemId, injected.DataString("fault")));
        Assert.Contains(events, x => x.Kind == SimEventKinds.VoiceScript && x.ItemId == "ecrEstimate" && x.DataString("text")!.Contains("30 分鐘"));
        Assert.Contains(events, x => x.Kind == SimEventKinds.VoiceScript && x.ItemId == "tugInfo" && x.DataString("text")!.Contains("2 小時"));
        var shelf = events.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "shelfEntered");
        var grounding = events.Single(x => x.Kind == SimEventKinds.Grounding);
        _out.WriteLine($"進入可錨泊水深 T+{shelf.T - 60:F0} s,擱淺 T+{grounding.T - 60:F0} s");
        Assert.InRange(shelf.T - 60, 900, 2400);                   // 漂流計算與下錨決策有足夠但有限的時間
        Assert.True(grounding.T > shelf.T + 600);
        Assert.True(grounding.T - 60 < 7200);                      // 拖船 2 小時到不了:需要下錨
        Assert.Contains(events, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "repairDelay" && x.T == 60 + 1800);
        var s = e.LatestSummary!;
        Assert.True(s.Assessment.HardFailed);
        Assert.Contains(s.Assessment.HardFails, h => h.Id == "grounding");
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.Null(s.Manoeuvres);                                   // E10 不使用操縱量測
    }

    [Fact]
    public void E10_CrewResponseAndAnchorHoldingEndBeforeGrounding()
    {
        var (e, events) = Engine("E10_engine_failure_lee_shore");
        var script = new (long Tick, SimCommand Cmd)[]
        {
            (50 * 70, SimCommand.Telegraph(TelegraphOrder.STOP)),
            (50 * 80, SimCommand.Acknowledge("notifyEcr", itemId: "engineFailure", role: "officer")),
            (50 * 100, SimCommand.Acknowledge("callMaster", itemId: "engineFailure", role: "officer")),
            (50 * 400, SimCommand.Acknowledge("driftCalculation", itemId: "engineFailure", role: "officer", note: "漂向 300°、0.8 kn,約 25 分鐘到 60 m 等深線")),
            (50 * 500, SimCommand.Acknowledge("anchorReady", itemId: "engineFailure", role: "master")),
            (50 * 600, SimCommand.Acknowledge("notifyCompany", itemId: "engineFailure", role: "master")),
            (50 * 650, SimCommand.Acknowledge("notifySar", itemId: "engineFailure", role: "master", note: "MRCC Taiwan、航行警告")),
            (50 * 1000, SimCommand.Acknowledge("anchorDecision", itemId: "engineFailure", role: "master", note: "進入 60 m 等深線即下右錨 6 節")),
            (50 * 1900, SimCommand.TriggerTimeline("anchorHolding")),
        };
        EngineDeterminismTests.RunScript(e, 50 * 1910, script);
        Assert.True(e.Ended);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.Grounding);
        var s = e.LatestSummary!;
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.Equal(100, s.Assessment.Score);
        Assert.True(s.Assessment.Passed);
        var fr = s.FaultResponses.Single();
        Assert.Equal(FaultNames.MainEngine, fr.Fault);
        Assert.Equal("telegraphChange", fr.FirstAction!.Kind);
        Assert.Contains(fr.Acknowledgements, a => a.Kind == "driftCalculation");
        Assert.Contains(events, x => x.Kind == SimEventKinds.ExerciseEnded && x.ItemId == "anchorHolding");
    }

    // ------------------------------------------------------------------ E11

    /// <summary>E11:落水後立即左滿舵、60° 時右滿舵(Williamson)、近對向航向回正並以自動舵 020°、接近落水者減速;量測方式、距離、時間與必要事件。</summary>
    [Fact]
    public void E11_WilliamsonTurnIsMeasuredAndClassified()
    {
        var (e, events) = Engine("E11_man_overboard");
        var mobAppeared = false;
        double psi0 = 0;
        var stage = 0;
        var script = new List<(long, SimCommand)>
        {
            (50 * 126, SimCommand.Acknowledge("verbal", role: "officer", note: "人員落水左舷!")),
            (50 * 140, SimCommand.Acknowledge("callMaster", role: "officer")),
            (50 * 300, SimCommand.Acknowledge("notifySar", role: "master", note: "DSC 遇險/VHF 16 MAYDAY RELAY")),
        };
        var idx = 0;
        for (var i = 0; i < 50 * 1500 && !e.Ended; i++)
        {
            while (idx < script.Count && script[idx].Item1 == e.Tick) e.Enqueue(script[idx++].Item2);
            var mob = e.Traffic.Find("MOB");
            if (!mobAppeared && mob is { Active: true }) { mobAppeared = true; psi0 = e.Motion.Psi; }
            if (mobAppeared)
            {
                var change = Units.RadToDeg(Units.WrapRadPi(e.Motion.Psi - psi0));
                var d = Math.Sqrt((e.Motion.X - mob!.X) * (e.Motion.X - mob.X) + (e.Motion.Y - mob.Y) * (e.Motion.Y - mob.Y));
                switch (stage)
                {
                    case 0 when e.Time >= 124: e.Enqueue(SimCommand.Rudder(-35)); stage = 1; break;           // 落水舷滿舵
                    case 1 when change <= -60: e.Enqueue(SimCommand.Rudder(35)); stage = 2; break;           // 60° 後反舵
                    case 2 when Math.Abs(Units.WrapDeg180(Units.RadToDeg(e.Motion.Psi) - 20)) <= 15:              // 接近對向航向
                        e.Enqueue(SimCommand.Rudder(0)); e.Enqueue(SimCommand.Autopilot(true, 20)); stage = 3; break;
                    case 3 when d < 600: e.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH)); stage = 4; break;
                }
            }
            e.Step();
        }
        var measured = events.Single(x => x.Kind == ManoeuvreEventKinds.Measured && x.ItemId == "manOverboard");
        var r = e.Assessment!.Manoeuvres.State.Records.Single(x => x.Id == "manOverboard");
        foreach (var (k, v) in r.Values) _out.WriteLine($"{k} = {v}");
        foreach (var n in r.Notes) _out.WriteLine(n);
        Assert.Equal("MOB", r.TargetId);
        Assert.Equal("port", r.Side);
        Assert.Equal("williamson", r.Method);
        Assert.True(r.Complete);
        Assert.InRange(r.Values["initialRange_m"], 20, 60);
        Assert.Equal(1, r.Values["initialTurnTowardMob"]);
        Assert.InRange(r.Values["reactionTime_s"], 3.9, 4.1);
        Assert.True(r.Values["minDistance_m"] < r.Values["returnRadius_m"]);
        Assert.True(r.Values["returnTrackOffset_m"] < 300);            // Williamson 回到原航跡附近
        Assert.Contains(r.Marks, m => m.Type == "reciprocal");
        Assert.True(measured.T < 120 + 1200);
        var req = e.LatestSummary!.Assessment.Required.ToDictionary(x => x.Id);
        Assert.Equal("met", req["immediateAction"].State);
        Assert.Equal("met", req["alarm"].State);
        Assert.Equal("met", req["callMaster"].State);
        Assert.Equal("met", req["notifySar"].State);
        Assert.Equal("met", req["returnToMob"].State);
        Assert.Contains(e.LatestSummary.CollisionAvoidance.Targets, t => t.TargetId == "T1");   // 交通(第二個複雜因素)逐目標檢視
        Assert.False(e.Collision);
    }

    // ------------------------------------------------------------------ 紀錄 → 重播

    [Theory]
    [InlineData("F01_FSB1_full")]
    [InlineData("F02_FSB2_low_speed")]
    [InlineData("F03_FSB2_channel")]
    [InlineData("E10_engine_failure_lee_shore")]
    [InlineData("E11_man_overboard")]
    public void RecordAndReplay600sMatches(string id)
    {
        var sc = Load(id);
        var dyn = DynamicsFor(sc);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"{id}-{Guid.NewGuid():N}.jsonl");
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, dyn);   // 預設自動快照每 10 s(快照含操縱量測狀態)
        var script = new (long Tick, SimCommand Cmd)[]
        {
            (50 * 40, SimCommand.Acknowledge("prediction", itemId: "predictTurnPort", note: "測試")),
            (50 * 130, SimCommand.Rudder(-35)),
            (50 * 300, SimCommand.Rudder(0)),
        };
        using (var writer = new RecordWriter(path, e))
        {
            EngineDeterminismTests.RunScript(e, 50 * 600, script);
            writer.Close();
        }
        var result = Replayer.Replay(path, TestData.Ship, dyn);
        Assert.True(result.IntegrityOk);
        Assert.True(result.HashMatches, $"{result.ExpectedHash} vs {result.ActualHash}");
        Assert.True(result.EventSequenceMatches, result.FirstEventMismatch);
        Assert.True(result.SummaryMatches);
        File.Delete(path);
    }
}
