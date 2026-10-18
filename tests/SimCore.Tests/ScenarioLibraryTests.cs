using System.Text.Json;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.SimCore.Traffic;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 情境庫 E02 台灣海峽沿岸密集交通、E03 限制能見度、E04 高雄南口引水人登船、E06 台中港防波堤口橫流(漲潮/退潮兩版)
/// (規劃書第 8.1 節表列與設計規則;docs/design/scenario-library.md):載入/驗證無錯誤、參數引用、示意區域檔一致;
/// 無人操作時三步升級(normal → degraded → emergency)依序觸發且關鍵事件時刻固定;固定學員腳本(與主控程序相同的 MMG 動力學)
/// 達成必要事件,紀錄 → 重播雜湊/事件/summary 一致。時間表時刻來自參數集者以等值固定,物理事件以順序與範圍固定。
/// </summary>
public class ScenarioLibraryTests
{
    private readonly ITestOutputHelper _out;
    public ScenarioLibraryTests(ITestOutputHelper output) => _out = output;

    private const string E02 = "E02_coastal_traffic_strait";
    private const string E03 = "E03_restricted_visibility_strait";
    private const string E04 = "E04_pilot_boarding_kaohsiung";
    private const string E06Flood = "E06_taichung_channel_flood";
    private const string E06Ebb = "E06_taichung_channel_ebb";
    public static readonly TheoryData<string> LibraryIds = new() { E02, E03, E04, E06Flood, E06Ebb };

    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static JsonElement Area(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "data", "areas", file))).RootElement;

    /// <summary>每秒呼叫 <paramref name="script"/>(t − 1 s 送入的指令於該步套用);<paramref name="record"/> 時同時錄製並回傳路徑。</summary>
    private static (SimulationEngine Engine, List<SimEvent> Events, string? Path) Run(string id, double endS, Action<SimulationEngine, List<SimEvent>, int>? script = null, bool record = true)
    {
        var sc = Load(id);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var events = TestData.Capture(e);
        string? path = null;
        RecordWriter? w = null;
        if (record)
        {
            path = Path.Combine(TestData.TestRecordsDirectory, $"{id}-{Guid.NewGuid():N}.jsonl");
            w = new RecordWriter(path, e);
        }
        for (var t = 1; t <= endS && !e.Ended; t++)
        {
            script?.Invoke(e, events, t);
            var target = (long)Math.Round(t / e.Dt);
            while (e.Tick < target && !e.Ended) e.Step();
        }
        w?.Close();
        return (e, events, path);
    }

    /// <summary>重播比對後刪除紀錄(長情境 25 Hz 狀態行可達數百 MB,不留在 build/test-records)。</summary>
    private static void AssertReplays(string path)
    {
        try
        {
            var r = Replayer.Replay(path, TestData.Ship, Mmg);
            Assert.True(r.IntegrityOk);
            Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
            Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
            Assert.True(r.SummaryMatches);
            Assert.Null(r.Warning);
        }
        finally { File.Delete(path); }
    }

    private void Dump(List<SimEvent> events, AssessmentSummary? s = null)
    {
        foreach (var x in events.Where(x => x.Kind is not (TrafficEventKinds.CpaAlarm or TrafficEventKinds.CpaClear or TrafficEventKinds.Sound or SimEventKinds.Message or SimEventKinds.VoiceScript)))
            _out.WriteLine($"{x.T:F1} {x.Source}/{x.Kind} {x.ItemId} {x.TargetId} {x.Message}");
        if (s is not null)
        {
            foreach (var r in s.Assessment.Required) _out.WriteLine($"required {r.Id}: {r.State} dt={r.DtS} by={r.By}");
            foreach (var b in s.Assessment.Bands) _out.WriteLine($"band {b.Id}: {b.State} value={b.Value}");
            foreach (var h in s.Assessment.HardFails) _out.WriteLine($"hardFail {h.Id} @ {h.TS}");
            foreach (var c in s.CollisionAvoidance.Targets) _out.WriteLine($"enc {c.TargetId} det={c.DetectedAtS} act={c.FirstActionAtS} dt={c.TimeToActionS} minRange={c.MinRangeNm:F3} dC={c.CourseChangeDeg:F0} dV={c.SpeedChangeKn:F1} substantial={c.Substantial}");
            _out.WriteLine($"score {s.Assessment.Score} passed {s.Assessment.Passed}");
        }
    }

    private static double T(List<SimEvent> ev, string itemId) => ev.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == itemId).T;
    private static List<(double T, string From, string To)> Phases(List<SimEvent> ev) =>
        ev.Where(x => x.Kind == SimEventKinds.PhaseChanged).Select(x => (x.T, x.DataString("from")!, x.DataString("to")!)).ToList();
    private static RequiredResult Req(AssessmentSummary s, string id) => s.Assessment.Required.Single(r => r.Id == id);
    private static BandResult Band(AssessmentSummary s, string id) => s.Assessment.Bands.Single(r => r.Id == id);
    private static double Deg(double rad) => Units.NormalizeHeadingDeg(Units.RadToDeg(rad));

    /// <summary>無人操作時三步升級依序:normal → degraded → emergency,且各只一次。</summary>
    private static void AssertEscalation(List<SimEvent> ev, double degradedAt, double emergencyAt)
    {
        var ph = Phases(ev);
        Assert.Equal(2, ph.Count);
        Assert.Equal((degradedAt, "normal", "degraded"), ph[0]);
        Assert.Equal((emergencyAt, "degraded", "emergency"), ph[1]);
    }

    // ------------------------------------------------------------------ 共通

    [Theory]
    [MemberData(nameof(LibraryIds))]
    public void LoadsValidatesAndFollowsDesignRules(string id)
    {
        var sc = Load(id);
        Assert.Equal(id, sc.Id);
        var issues = ScenarioValidator.Check(sc, TestData.Ship(sc.Ship.Id));
        foreach (var i in issues) _out.WriteLine(i.ToString());
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Error);
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Warning);
        // 設計規則:交班與書面航程計畫開場(第一個時間表項目、必要事件 handoverChecklist、簡報附航程計畫)
        Assert.Equal("handover", sc.Timeline[0].Id);
        Assert.Contains("交班", sc.Briefing!.Text);
        Assert.Contains(sc.Briefing.Attachments!, a => a.Title.Contains("航程計畫"));
        Assert.Contains(sc.Assessment.Required, r => r.Id == "handoverChecklist" && r.After.ItemId == "handover");
        Assert.NotNull(sc.Route);
        // 三步升級各一個項目;課程前段:不注入技術故障
        Assert.Single(sc.Timeline, t => t.Phase == "degraded");
        Assert.Single(sc.Timeline, t => t.Phase == "emergency");
        Assert.DoesNotContain(sc.Timeline.SelectMany(t => t.Actions), a => a.Kind == "injectFault");
        // 參數引用:新鍵皆為「假設值,待公司程序」來源
        var resolved = ScenarioLoader.LastResolvedParameters;
        var ps = TestData.Parameters();
        foreach (var r in resolved.Where(r => r.Source.StartsWith("ASSUMED-", StringComparison.Ordinal)))
            Assert.StartsWith("假設值,待公司程序", ps.Sources[r.Source].Title);
        Assert.Contains(resolved, r => r.Key == "bridge.handover.checklist_s" && r.Source == "ASSUMED-HANDOVER");
        // 檔頭註解明確標註示意座標
        var header = string.Join("\n", File.ReadLines(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml")).TakeWhile(l => l.StartsWith('#')));
        Assert.Contains("示意", header);
        Assert.Contains("待 ENC 校正", header);
    }

    [Fact]
    public void AreaFilesAreMarkedSchematicAndMatchScenarios()
    {
        static (double, double) LL(JsonElement p) => (Math.Round(p.GetProperty("lat").GetDouble(), 5), Math.Round(p.GetProperty("lon").GetDouble(), 5));
        static (double, double) W(RouteWaypoint w) => (Math.Round(w.Lat!.Value, 5), Math.Round(w.Lon!.Value, 5));
        static (double, double) P(ScenarioPosition p) => (Math.Round(p.Lat!.Value, 5), Math.Round(p.Lon!.Value, 5));
        foreach (var f in new[] { "taiwan-strait-central-lanes.json", "kaohsiung-south-approach.json", "taichung-harbour-entrance.json" })
        {
            var a = Area(f);
            Assert.Contains("示意", a.GetProperty("source").GetString());
            Assert.Contains("待 ENC 校正", a.GetProperty("description").GetString() + a.GetProperty("source").GetString());
        }

        var lanes = Area("taiwan-strait-central-lanes.json");
        var cl = lanes.GetProperty("centreline").EnumerateArray().Select(LL).ToList();
        var sep = lanes.GetProperty("lanes").GetProperty("separationZone").GetProperty("polygon").EnumerateArray().Select(LL).ToList();
        foreach (var id in new[] { E02, E03 })
        {
            var sc = Load(id);
            Assert.Equal(cl, sc.Route!.Waypoints.Select(W));
            Assert.Equal(sep, sc.Zones.Single(z => z.Id == "separationZone").Polygon.Select(P));
            Assert.Equal(cl[0], (sc.Origin!.Lat, sc.Origin.Lon));
        }

        var khh = Area("kaohsiung-south-approach.json");
        var e04 = Load(E04);
        Assert.Equal(khh.GetProperty("route").EnumerateArray().Select(LL), e04.Route!.Waypoints.Select(W));
        Assert.Equal(khh.GetProperty("fairway").GetProperty("polygon").EnumerateArray().Select(LL), e04.Zones.Single(z => z.Id == "fairway").Polygon.Select(P));
        Assert.Equal(khh.GetProperty("holdingArea").GetProperty("polygon").EnumerateArray().Select(LL), e04.Zones.Single(z => z.Id == "holdingArea").Polygon.Select(P));
        Assert.Equal(LL(khh.GetProperty("pilotStations")[0]), (e04.Origin!.Lat, e04.Origin.Lon));
        // 外錨地與既有示意錨地檔相同(不重複定義)
        var anch = Area("kaohsiung-anchorage.json").GetProperty("anchorages")[0].GetProperty("polygon").EnumerateArray().Select(LL);
        Assert.Equal(anch, e04.Zones.Single(z => z.Id == "anchorage").Polygon.Select(P));

        var tc = Area("taichung-harbour-entrance.json");
        foreach (var id in new[] { E06Flood, E06Ebb })
        {
            var sc = Load(id);
            Assert.Equal(tc.GetProperty("channel").GetProperty("centreline").EnumerateArray().Select(LL), sc.Route!.Waypoints.Select(W));
            foreach (var bw in tc.GetProperty("breakwaters").EnumerateArray())
            {
                var z = sc.Zones.Single(z => z.Id == bw.GetProperty("id").GetString());
                Assert.Equal(ZoneType.NoGo, z.Type);
                Assert.Equal(bw.GetProperty("polygon").EnumerateArray().Select(LL), z.Polygon.Select(P));
            }
        }
        var streams = tc.GetProperty("tidalStreams");
        Assert.Equal(85, streams.GetProperty("flood").GetProperty("set_deg").GetDouble());
        Assert.Equal(265, streams.GetProperty("ebb").GetProperty("set_deg").GetDouble());
        // 雷達陸地:新檔排在既有高雄海岸之後(Sensors.Radar 測試以 Land[0] 為高雄海岸)
        Assert.True(string.CompareOrdinal("kaohsiung-approach.json", "taichung-harbour-entrance.json") < 0);
        Assert.Equal(3, tc.GetProperty("land").GetArrayLength());
    }

    // ------------------------------------------------------------------ E02

    [Fact]
    public void E02_TrafficMixMatchesExerciseCatalogue()
    {
        var sc = Load(E02);
        Assert.InRange(sc.Targets.Count, 6, 10);
        Assert.False(sc.Targets.Single(t => t.Id == "X1").Behaviour.Colreg!.Obey);
        Assert.Equal(4, sc.Targets.Count(t => t.ShipType == "fishing"));
        Assert.All(sc.Targets.Where(t => t.ShipType == "fishing"), t => Assert.Equal(1500, t.Trigger!.Time));
        Assert.Equal(3, sc.Targets.Count(t => !t.AisOn));
        Assert.Equal(600, sc.Timeline.Single(t => t.Id == "crossingNoAction").When!.AtLeast);
        Assert.Equal(1500, sc.Timeline.Single(t => t.Id == "overtakingAndFishing").When!.AtLeast);
        Assert.Equal(2400, sc.Timeline.Single(t => t.Id == "vhfInterference").When!.AtLeast);
        Assert.Equal(360, sc.Assessment.Required.Single(r => r.Id == "giveWayEarlyAction").WithinS);
        Assert.Equal("encounterStarted", sc.Assessment.Required.Single(r => r.Id == "giveWayEarlyAction").After.Event);
        Assert.Equal(1500, sc.Route!.XtdLimitM);
        // 白天轉黃昏:16:20 地方時開始
        Assert.Equal("2026-11-20T08:20:00Z", sc.StartTimeUtc);
    }

    /// <summary>無人操作:交班 → T+10 degraded(X1 未讓路)→ T+25 emergency(追越 + 漁船群出現)→ T+30 黃昏 → T+40 VHF 干擾 → T+55 結束;必要事件全部逾時。</summary>
    [Fact]
    public void E02_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E02, 3400);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        AssertEscalation(ev, 600, 1500);
        Assert.Equal(1.0, T(ev, "handover"));
        Assert.Equal(1800.0, T(ev, "dusk"));
        Assert.Equal(2400.0, T(ev, "vhfInterference"));
        Assert.Equal(3300.0, T(ev, "end"));
        Assert.True(e.Ended);
        Assert.Equal(2, ev.Count(x => x.Kind == SimEventKinds.VoiceScript && x.ItemId == "vhfInterference"));
        Assert.Equal(new[] { "F1", "F2", "F3", "F4" }, ev.Where(x => x.Kind == TrafficEventKinds.Activated).Select(x => x.TargetId).ToArray());
        Assert.All(ev.Where(x => x.Kind == TrafficEventKinds.Activated), x => Assert.Equal(1500.0, x.T));
        Assert.Equal(1.0, ev.First(x => x.Kind == SimEventKinds.EncounterStarted && x.TargetId == "X2").T);
        Assert.Contains(ev, x => x.Kind == TrafficEventKinds.Colreg && x.TargetId == "X1" && x.Message.Contains("ignored"));
        Assert.Equal(6.0, e.VisibilityNm);
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.Contains(s.Assessment.HardFails, h => h.Id == "cpaHard");
        Assert.Equal("fail", Band(s, "minCpa").State);
        Assert.Equal("ok", Band(s, "separationZone").State);
        Assert.Equal("ok", Band(s, "xtdMax").State);
        Assert.Equal(new[] { "normal", "degraded", "emergency" }, s.Phases.Select(p => p.Phase).ToArray());
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員腳本:交班檢查表 → 及早向右大幅改向讓 X2(自動舵 020 → 060,X2 通過後 350 回航道)→ T+10 X1 未讓路:通知船長、減速(第 17 條)→ 回原航向 →
    /// 漁船群:減速、待追越船通過後向右繞過 → VHF 干擾:質疑右舷對右舷的要求 → X3 通過後回航道;CL2 VTS 報告。固定:必要事件全部 met、無硬性失敗、X2 會遇為大幅行動。
    /// </summary>
    [Fact]
    public void E02_ScriptedWatchKeepingMeetsRequirements()
    {
        var reported = false;
        var (e, ev, path) = Run(E02, 3400, (eng, events, t) =>
        {
            switch (t)
            {
                case 30: eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case 90: eng.Enqueue(SimCommand.Autopilot(true, 60)); break;        // X2:及早、大幅向右
                case 420: eng.Enqueue(SimCommand.Autopilot(true, 350)); break;      // X2 通過後回航道
                case 610: eng.Enqueue(SimCommand.Acknowledge("callMaster")); break;
                case 620: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.HAH)); break;   // X1 未讓路:直航船減速(第 17 條 (a)(ii))
                case 900: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.FAH)); break;
                case 1100: eng.Enqueue(SimCommand.Autopilot(true, 20)); break;
                case 1510: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.HAH)); break;  // 漁船群 + 追越船:先減速
                case 1650: eng.Enqueue(SimCommand.Autopilot(true, 40)); break;      // 追越船通過後向右繞過漁船群東側
                case 2550: eng.Enqueue(SimCommand.Autopilot(true, 20)); break;
                case 2410: eng.Enqueue(SimCommand.Acknowledge("verbal", note: "VHF 干擾:以 AIS 辨識 X3,不同意右舷對右舷,維持左舷對左舷")); break;
                case 3150: eng.Enqueue(SimCommand.Autopilot(true, 5)); break;       // X3 與漁船群通過後回航道中心線
            }
            if (!reported && events.Any(x => x.Kind == SimEventKinds.ReportPointPassed))
            {
                eng.Enqueue(SimCommand.Acknowledge("report"));
                reported = true;
            }
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.False(s.Assessment.HardFailed);
        var x2 = s.CollisionAvoidance.Targets.First(c => c.TargetId == "X2");
        Assert.Equal(1.0, x2.DetectedAtS);
        Assert.Equal(89.0, x2.FirstActionAtS);   // t = 90 送入的指令於 89 s 套用
        Assert.True(x2.Substantial && x2.CourseChangeDeg >= 30, $"X2 改向 {x2.CourseChangeDeg:F0}°");
        Assert.Equal("acknowledge:verbal", Req(s, "vhfChallenge").By);
        Assert.Equal("ok", Band(s, "separationZone").State);
        Assert.Equal("ok", Band(s, "xtdMax").State);
        Assert.Equal("ok", Band(s, "minCpa").State);
        Assert.True(s.Assessment.Passed, $"score {s.Assessment.Score}");
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E03

    [Fact]
    public void E03_SameGeometryAsE02WithRestrictedVisibility()
    {
        var e02 = Load(E02);
        var e03 = Load(E03);
        Assert.Equal(e02.Route!.Waypoints.Select(w => (w.Lat, w.Lon)), e03.Route!.Waypoints.Select(w => (w.Lat, w.Lon)));
        Assert.Equal(2.0, e03.Environment.VisibilityNm);
        Assert.Equal(8.0, e03.Route.Waypoints.Single(w => w.Name == "CL2").SpeedLimitKn);
        Assert.Equal(6.0, e03.Route.Waypoints.Single(w => w.Name == "CL3").SpeedLimitKn);
        Assert.All(e03.Targets.Where(t => t.Id.StartsWith('R')), t => Assert.False(t.AisOn));
        var r2 = e03.Targets.Single(t => t.Id == "R2");
        Assert.Equal(TargetTriggerType.OwnDistance, r2.Trigger!.Type);
        Assert.Equal(TargetBehaviourMode.Scripted, r2.Behaviour.Mode);
        var req = e03.Assessment.Required.Single(r => r.Id == "lookoutReportR1");
        Assert.Equal("lookoutReport", req.Expect.Any![0].Acknowledge!.Kind);
        Assert.Equal(1.0, e03.Assessment.MinCpaNm);
    }

    /// <summary>無人操作:航段 1 霧區 degraded(能見度 2 → 0.5 nm,300 s 漸變)→ R2 近距離出現(一長聲霧號每 2 分鐘)→ 航段 2 emergency(→ 0.2 nm)→ R2 轉向;到 CL3 結束。</summary>
    [Fact]
    public void E03_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E03, 3600);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var fog = T(ev, "fogThickens");
        var dense = T(ev, "denseFog");
        AssertEscalation(ev, fog, dense);
        Assert.Equal(ev.First(x => x.Kind == SimEventKinds.RouteLegChanged).T, fog);
        // 霧區以航段表示(位置觸發):無人操作(FAH、自動舵 020°)時的時刻固定
        Assert.Equal(581.0, fog);
        Assert.Equal(1487.0, dense);
        var r2 = ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "R2").T;
        Assert.Equal(1445.0, r2);                                    // 距 CL2 0.25 nm,航段 2 之前
        Assert.Equal(2336.0, T(ev, "end"));                          // 距 CL3 0.3 nm
        Assert.Equal(r2, ev.First(x => x.Kind == SimEventKinds.EncounterStarted && x.TargetId == "R2").T);
        Assert.Equal(r2 + 20, ev.Single(x => x.Kind == SimEventKinds.VoiceScript && x.ItemId == "r2Heard").T);
        var horns = ev.Where(x => x.Kind == TrafficEventKinds.Sound && x.TargetId == "R2").Select(x => x.T).ToList();
        Assert.True(horns.Count >= 5);
        Assert.Equal(r2, horns[0]);
        Assert.All(horns.Zip(horns.Skip(1)), p => Assert.Equal(120.0, p.Second - p.First));
        Assert.All(ev.Where(x => x.Kind == TrafficEventKinds.Sound && x.TargetId == "R2"), x => Assert.Equal("prolonged", x.Message));
        Assert.Contains(ev, x => x.Kind == TrafficEventKinds.Sound && x.TargetId == "R1" && x.Message == "prolongedTwoShort");
        Assert.Equal(r2 + 120, ev.Single(x => x.Kind == TrafficEventKinds.Script && x.TargetId == "R2" && x.Message.Contains("275")).T);
        // 能見度漸變:2 → 0.5(fog + 300 s)→ 0.2(dense + 240 s)
        var done = ev.Where(x => x.Kind == SimEventKinds.EnvironmentRampDone).Select(x => x.T).ToList();
        Assert.Equal(new[] { fog + 300, dense + 240 }, done);
        Assert.Equal(0.2, e.VisibilityNm);
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.Equal("fail", Band(s, "safeSpeed").State);
        Assert.Contains(s.Assessment.HardFails, h => h.Id == "cpaHard");
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員腳本:交班檢查表、能見度 2 nm 即減至 SAH → R1(無 AIS)會遇開始後瞭望報告(acknowledge lookoutReport)→ 進霧區:通知船長、檢查表 →
    /// R2 出現:減至 DSAH 與最低舵效轉速(第 19 條 (e))、向右大幅避讓;固定:必要事件全部 met(lookoutReport 為自訂確認種類)、霧區超速時間在容許內。
    /// </summary>
    [Fact]
    public void E03_ScriptedSafeSpeedAndRadarLookout()
    {
        double? r1 = null, fog = null, r2 = null;
        var (e, ev, path) = Run(E03, 3600, (eng, events, t) =>
        {
            if (t == 30) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            if (t == 60) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));   // 交班時能見度已 2 nm:先減速(第 19 條 (b))
            r1 ??= events.FirstOrDefault(x => x.Kind == SimEventKinds.EncounterStarted && x.TargetId == "R1")?.T;
            fog ??= events.FirstOrDefault(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "fogThickens")?.T;
            r2 ??= events.FirstOrDefault(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "R2")?.T;
            if (r1 is { } a && t == (int)a + 30) eng.Enqueue(SimCommand.Acknowledge("lookoutReport", role: "lookout", note: "R1 雷達回波右舷 6 nm,無 AIS"));
            if (fog is { } f)
            {
                if (t == (int)f + 20) eng.Enqueue(SimCommand.Acknowledge("callMaster"));
                if (t == (int)f + 60) eng.Enqueue(SimCommand.Acknowledge("checklist", note: "霧號、主機備便、加派瞭望、手操舵"));
            }
            if (r2 is { } b)
            {
                if (t == (int)b + 25) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH));
                if (t == (int)b + 40) eng.Enqueue(SimCommand.Rpm(55));        // 最低舵效速度(第 19 條 (e))
                if (t == (int)b + 30) eng.Enqueue(SimCommand.Autopilot(true, 80));
                if (t == (int)b + 600) eng.Enqueue(SimCommand.Autopilot(true, 20));
            }
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.Equal("acknowledge:lookoutReport", Req(s, "lookoutReportR1").By);
        Assert.InRange(Req(s, "closeQuartersAction").DtS!.Value, 20, 30);   // DSAH(同時成為 R2 的會遇首次行動)
        Assert.Equal("ok", Band(s, "safeSpeed").State);
        Assert.False(s.Assessment.HardFailed);
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E04

    /// <summary>無人操作:T+7 引水人延誤(degraded)→ T+15 VTS 等候 → K1 報告點 → T+33 漲潮 1.2 → 1.8 kn + OUT1 出港(emergency)→ T+40 改舷要求 → T+55 引水船出發 → T+80 結束。</summary>
    [Fact]
    public void E04_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E04, 4800);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        AssertEscalation(ev, 420, 1980);
        Assert.Equal(900.0, T(ev, "vtsHold"));
        Assert.Equal(2400.0, T(ev, "boardingSideChange"));
        Assert.Equal(4800.0, T(ev, "timeUp"));
        var k1 = ev.First(x => x.Kind == SimEventKinds.ReportPointPassed).T;   // 其後的 reportPointPassed 為 reporting 區域(等候區/錨地)進入
        Assert.InRange(k1, 1000, 1300);
        Assert.Equal(k1, T(ev, "vtsAck"));
        Assert.Equal(1980.0, ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "OUT1").T);
        Assert.Equal(3300.0, ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "PILOT").T);
        Assert.Equal(new[] { 2580.0, 2580.0 }, ev.Where(x => x.Kind == SimEventKinds.EnvironmentRampDone).Select(x => x.T));
        Assert.Equal(1.8, Units.MpsToKn(e.Environment.CurrentDriftMps), 6);
        // 流壓:自動舵 045° 不修正時船被壓向西北,走廊超限
        Assert.Equal("fail", Band(s, "xtdTime").State);
        foreach (var id in new[] { "handoverChecklist", "vtsReport", "revisedEta", "holdSpeed", "boardingChallenge" }) Assert.Equal("missed", Req(s, id).State);
        foreach (var id in new[] { "boardingSpeed", "boardingLee", "mpx" }) Assert.Equal("notApplicable", Req(s, id).State);
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員腳本:交班檢查表、流壓修正(自動舵 052° → 漲潮增強後 060°)、K1 報告、回報修正 ETA、VTS 等候時減速、質疑上風舷/10 kn 的要求;
    /// 等候引水船時 <paramref name="slowDown"/> = true 減至 DSAH,false 則為趕登船時間加至 HAH(約 9 kn,超過登船航速);
    /// 教官於 T+3800 s 按「引水人登船」→ 30 s 後「下風舷確認」→ MPX。固定:登船航速自動判定、其餘必要事件 met、登船後 660 s 結束。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void E04_ScriptedApproachAndBoarding(bool slowDown)
    {
        const int boardAt = 3800;
        var reported = false;
        var (e, ev, path) = Run(E04, 4800, record: slowDown, script: (eng, events, t) =>
        {
            switch (t)
            {
                case 30: eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case 60: eng.Enqueue(SimCommand.Autopilot(true, 52)); break;
                case 440: eng.Enqueue(SimCommand.Acknowledge("report", note: "修正 ETA 已報船長與代理行")); break;
                case 920: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH)); break;
                case 930: eng.Enqueue(SimCommand.Autopilot(true, 54)); break;
                case 2100: eng.Enqueue(SimCommand.Autopilot(true, 58)); break;
                case 2420: eng.Enqueue(SimCommand.Acknowledge("verbal", note: "左舷為上風舷且 10 kn 過快:要求右舷、6 kn")); break;
                case 2600: eng.Enqueue(SimCommand.Autopilot(true, 60)); break;
                case 3000: eng.Enqueue(SimCommand.Telegraph(slowDown ? TelegraphOrder.DSAH : TelegraphOrder.HAH)); break;
                case boardAt: eng.Enqueue(SimCommand.TriggerTimeline("pilotOnBoard")); break;
                case boardAt + 30: eng.Enqueue(SimCommand.TriggerTimeline("leeConfirmed")); break;
                case boardAt + 300: eng.Enqueue(SimCommand.Acknowledge("checklist", note: "MPX 完成")); break;
            }
            if (!reported && events.Any(x => x.Kind == SimEventKinds.ReportPointPassed))
            {
                eng.Enqueue(SimCommand.Acknowledge("report"));
                reported = true;
            }
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        // 教官指令於 boardAt − 1 s 套用,延遲項目自下一個評估步(整秒)起算
        Assert.Equal(boardAt + 660.0, ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).T);
        Assert.Equal(boardAt + 30.0, ev.Single(x => x.Kind == TrafficEventKinds.Control && x.TargetId == "PILOT" && x.Message.Contains("刪除")).T);
        foreach (var id in new[] { "handoverChecklist", "vtsReport", "revisedEta", "holdSpeed", "boardingChallenge", "boardingLee", "mpx" })
            Assert.Equal("met", Req(s, id).State);
        Assert.Equal(slowDown ? "met" : "missed", Req(s, "boardingSpeed").State);
        Assert.Equal(slowDown, ev.Any(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "boardingSpeedOk"));
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "fairwayEntered");
        Assert.Equal(slowDown ? 100.0 : 90.0, s.Assessment.Score);
        if (slowDown) AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E06

    [Fact]
    public void E06_FloodAndEbbVersionsDifferOnlyInTidalStream()
    {
        var f = Load(E06Flood);
        var b = Load(E06Ebb);
        Assert.Equal((85.0, 1.5), (f.Environment.Current.Set, f.Environment.Current.Drift));
        Assert.Equal((265.0, 1.8), (b.Environment.Current.Set, b.Environment.Current.Drift));
        static double Ramp(Scenario.Scenario sc, string id) => sc.Timeline.Single(t => t.Id == id).Actions.Single(a => a.Kind == "setEnvironment").Payload.GetProperty("current").GetProperty("drift").GetDouble();
        Assert.Equal(2.5, Ramp(f, "currentIncrease"));
        Assert.Equal(3.0, Ramp(b, "currentIncrease"));
        Assert.Equal(f.Timeline.Select(t => t.Id), b.Timeline.Select(t => t.Id));
        Assert.Equal(f.Assessment.Required.Select(r => r.Id), b.Assessment.Required.Select(r => r.Id));
        Assert.Equal(f.Assessment.Bands.Select(r => r.Id), b.Assessment.Bands.Select(r => r.Id));
        Assert.Equal(f.Targets.Select(t => t.Id), b.Targets.Select(t => t.Id));
        Assert.Equal(40, f.Environment.Wind.TrueDir);
        Assert.InRange(f.Environment.Wind.TrueSpeed, 22, 27);   // 蒲氏 6 級
    }

    /// <summary>無人操作:航段 1 流速增加(degraded)→ 90 s 導燈遮蔽 → 距口門 0.8 nm(或 300 s 後)出港船在航道內(emergency)。漲潮版自動舵不修正時壓上北防波堤擱淺。</summary>
    [Theory]
    [InlineData(E06Flood, 368.0, 668.0, true)]
    [InlineData(E06Ebb, 353.0, 653.0, false)]
    public void E06_NoOperatorEscalatesInOrder(string id, double degradedAt, double emergencyAt, bool grounds)
    {
        var (e, ev, path) = Run(id, 1800);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        AssertEscalation(ev, degradedAt, emergencyAt);
        Assert.Equal(degradedAt, ev.First(x => x.Kind == SimEventKinds.RouteLegChanged).T);
        Assert.Equal(degradedAt + 90, T(ev, "leadingLightsObscured"));
        Assert.Equal(emergencyAt, ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "OUT1").T);
        Assert.Equal(degradedAt + 180, ev.First(x => x.Kind == SimEventKinds.EnvironmentRampDone).T);
        foreach (var r in new[] { "handoverChecklist", "leadingLightReport", "outboundResponse" }) Assert.Equal("missed", Req(s, r).State);
        if (grounds)
        {
            Assert.Contains(s.Assessment.HardFails, h => h.Id == "grounding");
            Assert.Contains(s.Assessment.HardFails, h => h.Id == "northBreakwater");
            Assert.Contains(ev, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "groundingEnd");
        }
        else Assert.Equal(1800.0, T(ev, "timeUp"));
        Assert.True(e.Ended);
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員/引水人腳本:每 10 s 以偏航角估計 + XTD 修正下自動舵航向(流壓預判),出港船通報時回報並減速,過口門後減至 SAH;
    /// 固定:不擱淺、不進入防波堤、走廊與 ROT 在容許內、必要事件 met、到達迴船池結束。
    /// </summary>
    [Theory]
    [InlineData(E06Flood)]
    [InlineData(E06Ebb)]
    public void E06_ScriptedCrabbingThroughEntrance(string id)
    {
        double? outbound = null, inside = null;
        double px = double.NaN, py = double.NaN;
        var ux = Math.Sin(Units.DegToRad(125)); var uy = Math.Cos(Units.DegToRad(125));
        var (e, ev, path) = Run(id, 1800, (eng, events, t) =>
        {
            if (t == 20) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            if (events.Any(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "leadingLightsObscured") && !events.Any(x => x.Kind == SimEventKinds.Acknowledge && x.DataString("kind") == "report"))
                eng.Enqueue(SimCommand.Acknowledge("report", note: "導燈被遮蔽,改用雷達平行指標"));
            outbound ??= events.FirstOrDefault(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "outboundInChannel")?.T;
            inside ??= events.FirstOrDefault(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "insideBreakwater")?.T;
            if (outbound is { } o && t == (int)o + 15) eng.Enqueue(SimCommand.Acknowledge("verbal", note: "與出港船左舷對左舷,靠航道右側"));
            if (inside is { } i && t == (int)i + 5) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
            if (t % 10 != 0) return;
            var m = eng.Motion;
            if (!double.IsNaN(px))
            {
                var cog = Math.Atan2(m.X - px, m.Y - py);
                var drift = Units.RadToDeg(Units.WrapRadPi(m.Psi - cog));
                var xtd = m.X * uy - m.Y * ux;                 // 右舷法線 (u.y, −u.x):正 = 偏右
                var cmd = 125 + drift - Math.Clamp(0.2 * xtd, -20, 20);
                eng.Enqueue(SimCommand.Autopilot(true, Math.Round(Units.NormalizeHeadingDeg(cmd))));
            }
            px = m.X; py = m.Y;
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.Equal("arrived", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.True(s.Track!.MaxXtdM < 100, $"max XTD {s.Track.MaxXtdM:F0} m");
        Assert.Equal(0, s.Manoeuvring.RudderOrders);
        AssertReplays(path!);
    }
}
