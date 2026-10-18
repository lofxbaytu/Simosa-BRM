using System.Text.Json;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Environment;
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
/// 情境庫收尾 E19 引航中火災/ISPS 警報(選配)、E20 綜合評估航程(教官選 2 項與預設組合兩檔)、E22a–E22d 1.39 教室模式迷你情境
/// (規劃書第 8.1 節表列與設計規則、第 8.0 節;docs/design/scenario-library.md 第 18–21 節)。
/// </summary>
public class ScenarioLibraryFinalTests
{
    private readonly ITestOutputHelper _out;
    public ScenarioLibraryFinalTests(ITestOutputHelper output) => _out = output;

    private const string E19 = "E19_fire_isps_pilotage_kaohsiung";
    private const string E20 = "E20_comprehensive_assessment_kaohsiung";
    private const string E20D = "E20_comprehensive_assessment_default";
    private const string E22A = "E22a_handover_conflict";
    private const string E22B = "E22b_fatigue_misjudgement";
    private const string E22C = "E22c_challenge_senior";
    private const string E22D = "E22d_cultural_communication";

    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);

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

    private void Dump(List<SimEvent> events, AssessmentSummary? s = null)
    {
        foreach (var x in events.Where(x => x.Kind is not (TrafficEventKinds.CpaAlarm or TrafficEventKinds.CpaClear or TrafficEventKinds.Sound or SimEventKinds.Message or SimEventKinds.VoiceScript
                     or SimEventKinds.FenderContact or SimEventKinds.TugCommand)))
            _out.WriteLine($"{x.T:F1} {x.Source}/{x.Kind} {x.ItemId} {x.TargetId} {x.Message}");
        if (s is null) return;
        foreach (var r in s.Assessment.Required) _out.WriteLine($"required {r.Id}: {r.State} dt={r.DtS} by={r.By}");
        foreach (var b in s.Assessment.Bands) _out.WriteLine($"band {b.Id}: {b.State} value={b.Value}");
        foreach (var h in s.Assessment.HardFails) _out.WriteLine($"hardFail {h.Id} @ {h.TS}");
        foreach (var c in s.CollisionAvoidance.Targets) _out.WriteLine($"enc {c.TargetId} det={c.DetectedAtS} act={c.FirstActionAtS} minRange={c.MinRangeNm:F3}");
        _out.WriteLine($"ukc min={s.Ukc.MinM:F2} below={s.Ukc.TimeBelowLimitS} score {s.Assessment.Score} passed {s.Assessment.Passed}");
    }


    private static double? FirstT(List<SimEvent> ev, string kind, string? itemId = null, string? targetId = null)
        => ev.FirstOrDefault(x => x.Kind == kind && (itemId is null || x.ItemId == itemId) && (targetId is null || x.TargetId == targetId))?.T;

    /// <summary>循跡(自動舵修正流壓;每次 ≤ 4°,同 ScenarioLibraryLateCourseTests)。</summary>
    private sealed class TrackKeeper
    {
        private double _set;
        public double Offset { get; set; }
        public bool Enabled { get; set; } = true;
        public TrackKeeper(double initialHeading) => _set = initialHeading;
        public double Set => _set;
        public void Override(double heading) => _set = heading;

        public void Step(SimulationEngine eng, int t, int every = 20, double gain = 0.12)
        {
            if (!Enabled || t % every != 0) return;
            var st = eng.BuildState();
            if (st.Route is not { LegCourseDeg: { } leg, XtdM: { } xtd } || st.Sog < 1) return;
            var drift = Units.RadToDeg(Units.WrapRadPi(Units.DegToRad(st.Heading - st.Cog)));
            var cmd = leg + drift - Math.Clamp(gain * (xtd - Offset), -15, 15);
            var delta = Units.RadToDeg(Units.WrapRadPi(Units.DegToRad(cmd - _set)));
            delta = Math.Clamp(delta, -4, 4);
            if (Math.Abs(delta) < 1) return;
            _set = Math.Round(Units.NormalizeHeadingDeg(_set + delta));
            eng.Enqueue(SimCommand.Autopilot(true, _set));
        }
    }

    /// <summary>
    /// 手操舵循跡(引水人舵令 → 舵工;以真航向代表舵工依磁羅經/疊標操舵):期望艏向 = 航段航向 + 偏航角 − 增益 × XTD,
    /// 舵角 = Kp × 艏向誤差 − Kd × 轉向率;變更 ≥ 2° 才下令(每 2 s)。
    /// </summary>
    private sealed class Helm
    {
        private double _rudder;
        public double? HeadingOverride { get; set; }
        public void Step(SimulationEngine eng, int t, double gain = 0.15)
        {
            if (t % 2 != 0) return;
            var st = eng.BuildState();
            var m = eng.Motion;
            var hdg = Units.NormalizeHeadingDeg(Units.RadToDeg(m.Psi));
            double want;
            if (HeadingOverride is { } ho) want = ho;
            else
            {
                if (st.Route is not { LegCourseDeg: { } leg, XtdM: { } xtd }) return;
                var drift = st.Sog > 1 ? Units.RadToDeg(Units.WrapRadPi(Units.DegToRad(hdg - st.Cog))) : 0;
                want = leg + Math.Clamp(drift, -15, 15) - Math.Clamp(gain * xtd, -20, 20);
            }
            var err = Units.RadToDeg(Units.WrapRadPi(Units.DegToRad(want - hdg)));
            var rot = Units.RadToDeg(m.R);
            var rudder = Math.Round(Math.Clamp(1.5 * err - 30.0 * rot, -25, 25));
            if (Math.Abs(rudder - _rudder) < 2) return;
            _rudder = rudder;
            eng.Enqueue(SimCommand.Rudder(rudder));
        }
    }

    public static readonly TheoryData<string> LibraryIds = new() { E19, E20, E20D, E22A, E22B, E22C, E22D };
    private static readonly string[] MiniIds = { E22A, E22B, E22C, E22D };

    private static JsonElement Area(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "data", "areas", file))).RootElement;
    private static double T(List<SimEvent> ev, string itemId) => ev.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == itemId).T;
    private static List<(double T, string From, string To)> Phases(List<SimEvent> ev) =>
        ev.Where(x => x.Kind == SimEventKinds.PhaseChanged).Select(x => (x.T, x.DataString("from")!, x.DataString("to")!)).ToList();
    private static RequiredResult Req(AssessmentSummary s, string id) => s.Assessment.Required.Single(r => r.Id == id);
    private static BandResult Band(AssessmentSummary s, string id) => s.Assessment.Bands.Single(r => r.Id == id);
    private static double Param(string key) => TestData.Parameters().Parameters[key].NumberValue!.Value;

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

    /// <summary>合理操作的共同判定:必要事件(適用者)全部 met、無硬性失敗、容許帶皆 ok、100 分。</summary>
    private static void AssertClean(AssessmentSummary s, params string[] notApplicable)
    {
        foreach (var r in s.Assessment.Required)
            Assert.True(notApplicable.Contains(r.Id) ? r.State == "notApplicable" : r.State == "met", $"{r.Id}: {r.State}");
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.All(s.Assessment.Bands, b => Assert.True(b.State == "ok", $"{b.Id}: {b.State} {b.Value}"));
        Assert.Equal(100.0, s.Assessment.Score);
        Assert.True(s.Assessment.Passed);
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
        // 交班與書面計畫開場
        Assert.Equal("handover", sc.Timeline[0].Id);
        Assert.Equal(1.0, sc.Timeline[0].When!.AtLeast);
        Assert.StartsWith("交班", sc.Briefing!.Text);
        Assert.Contains(sc.Briefing.Attachments!, a => a.Title.Contains("計畫"));
        Assert.Contains(sc.Briefing.Attachments!, a => a.Title.Contains("交班檢查表"));
        Assert.Contains(sc.Assessment.Required, r => r.Id == "handoverChecklist" && r.After.ItemId == "handover");
        if (MiniIds.Contains(id))
        {
            // 1.39 教室模式:不套用三步升級、無硬性失敗、討論點、10–15 分鐘
            Assert.DoesNotContain(sc.Timeline, t => t.Phase is "degraded" or "emergency");
            Assert.Single(sc.Timeline, t => t.Phase == "discussion");
            Assert.Empty(sc.Assessment.HardFail);
            Assert.Empty(sc.Assessment.Bands);
            Assert.InRange(sc.Briefing.DurationS!.Value, 600, 900);
            Assert.Contains("1.39", sc.Briefing.Text);
            Assert.Contains(sc.Briefing.Attachments!, a => a.Title.Contains("1.39") || a.Title.Contains("SMCP"));
        }
        else
        {
            Assert.Single(sc.Timeline, t => t.Phase == "degraded");
            Assert.Single(sc.Timeline, t => t.Phase == "emergency");
        }
        // 只記錄故障(fireAlarmEngineRoom、blackout、radarFailure、ecdisAlarmFlood)一律以 recordOnly: true 明示(驗證器不警告)
        Assert.All(sc.Timeline.SelectMany(t => t.Actions).Where(a => a.Kind == "injectFault" && !ScenarioValidator.ModelledFaults.Contains(a.AsString())),
            a => Assert.True(a.RecordOnly, a.AsString()));
        // 參數引用:新鍵皆為「假設值,待公司程序」來源
        var resolved = ScenarioLoader.LastResolvedParameters;
        var ps = TestData.Parameters();
        foreach (var r in resolved.Where(r => r.Source.StartsWith("ASSUMED-", StringComparison.Ordinal)))
            Assert.StartsWith("假設值,待公司程序", ps.Sources[r.Source].Title);
        Assert.Contains(resolved, r => r.Key == "bridge.handover.checklist_s");
        Assert.Contains(resolved, r => r.Path == "assessment.scoring.passMark");
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
        var a = Area("kaohsiung-e20-passage.json");
        Assert.Contains("示意", a.GetProperty("source").GetString());
        Assert.Contains("待 ENC 校正", a.GetProperty("description").GetString());
        Assert.Contains("待 ENC 校正", a.GetProperty("source").GetString());
        Assert.False(a.TryGetProperty("land", out _));                         // 雷達陸地回波仍以 kaohsiung-approach.json 為 Land[0]
        Assert.True(string.CompareOrdinal("kaohsiung-approach.json", "kaohsiung-e20-passage.json") < 0);
        var inbound = Area("kaohsiung-second-port-inbound.json").GetProperty("route").EnumerateArray().Select(LL).ToList();
        var route = a.GetProperty("route").EnumerateArray().Select(LL).ToList();
        Assert.Equal(inbound, route.Take(4));                                   // 港外段同 E15
        var entrance = Area("kaohsiung-south-approach.json").GetProperty("harbourEntrances")[0];
        Assert.Equal(LL(entrance), route[4]);                                   // 口門同 kaohsiung-south-approach.json
        var anchA = Area("kaohsiung-anchorage.json").GetProperty("anchorages")[0].GetProperty("polygon").EnumerateArray().Select(LL).ToList();
        var anchB = Area("kaohsiung-anchorage-b-e15.json").GetProperty("anchorages")[0].GetProperty("polygon").EnumerateArray().Select(LL).ToList();
        foreach (var id in new[] { E20, E20D })
        {
            var sc = Load(id);
            Assert.Equal(route, sc.Route!.Waypoints.Select(W));
            Assert.Equal(LL(a.GetProperty("route")[0]), P(sc.Initial.Position!));
            Assert.Equal(a.GetProperty("channel").GetProperty("polygon").EnumerateArray().Select(LL), sc.Zones.Single(z => z.Id == "channel").Polygon.Select(P));
            Assert.Equal(a.GetProperty("channel").GetProperty("depth_m").GetDouble(), sc.Zones.Single(z => z.Id == "channel").WaterDepthM);
            foreach (var g in a.GetProperty("breakwaters").EnumerateArray().Concat(a.GetProperty("shoals").EnumerateArray()))
            {
                var z = sc.Zones.Single(z => z.Id == g.GetProperty("id").GetString());
                Assert.Equal(g.GetProperty("depth_m").GetDouble(), z.WaterDepthM);
                Assert.Equal(g.GetProperty("polygon").EnumerateArray().Select(LL), z.Polygon.Select(P));
            }
            Assert.Equal(a.GetProperty("berthSpeedZone").GetProperty("polygon").EnumerateArray().Select(LL), sc.Zones.Single(z => z.Id == "berthApproach").Polygon.Select(P));
            Assert.Equal(anchA, sc.Zones.Single(z => z.Id == "anchorage").Polygon.Select(P));
            Assert.Equal(anchB, sc.Zones.Single(z => z.Id == "anchorageB").Polygon.Select(P));
            Assert.Equal("data/areas/kaohsiung-e20-passage.json", sc.Berth!.File);
            Assert.Equal("KHH-E20-OIL", sc.Berth.Definition!.Id);
            Assert.Equal(12, sc.Berth.Definition.Fenders.Count);
            var outTrack = a.GetProperty("outboundTrack").GetProperty("points").EnumerateArray().Select(LL).ToList();
            var out1 = sc.Targets.Single(t => t.Id == "OUT1");
            Assert.Equal(outTrack[0], P(out1.Initial.Position!));
            Assert.Equal(outTrack.Skip(1), out1.Behaviour!.Waypoints!.Select(w => (Math.Round(w.Lat!.Value, 5), Math.Round(w.Lon!.Value, 5))));
        }
        var e19 = Load(E19);
        Assert.Equal(inbound, e19.Route!.Waypoints.Select(W));
        Assert.Equal(anchA, e19.Zones.Single(z => z.Id == "anchorage").Polygon.Select(P));
        Assert.Equal(anchB, e19.Zones.Single(z => z.Id == "anchorageB").Polygon.Select(P));
    }

    // ------------------------------------------------------------------ E20 兩檔與手動鈕

    /// <summary>預設組合版本與教官選擇版本只差 id/name/description 與 injectE09/injectE15 的觸發方式。</summary>
    [Fact]
    public void E20DefaultDiffersOnlyInDefaultTriggers()
    {
        static System.Text.Json.Nodes.JsonObject Node(Scenario.Scenario sc) => (System.Text.Json.Nodes.JsonObject)JsonSerializer.SerializeToNode(sc, ContractJson.Options)!;
        var main = Load(E20);
        var dflt = Load(E20D);
        foreach (var id in new[] { "injectE07", "injectE08", "injectE09", "injectE15", "e07EmergencySteering", "e08GyroRestored", "e09InstrumentsRestored", "allFast" })
            Assert.True(main.Timeline.Single(t => t.Id == id).IsManual, id);
        Assert.True(dflt.Timeline.Single(t => t.Id == "injectE07").IsManual);
        Assert.True(dflt.Timeline.Single(t => t.Id == "injectE08").IsManual);
        Assert.False(dflt.Timeline.Single(t => t.Id == "injectE09").IsManual);
        Assert.False(dflt.Timeline.Single(t => t.Id == "injectE15").IsManual);
        // 各「注入 X」鈕的動作為對應故障/語音(只記錄故障以訊息表示)
        Assert.Equal(new[] { "steeringGear" }, main.Timeline.Single(t => t.Id == "injectE07").Actions.Where(a => a.Kind == "injectFault").Select(a => a.AsString()));
        Assert.Equal(new[] { "gyroDrift" }, main.Timeline.Single(t => t.Id == "injectE08").Actions.Where(a => a.Kind == "injectFault").Select(a => a.AsString()));
        Assert.Equal(new[] { "mainEngine", "steeringGear", "gyroFailure", "blackout", "radarFailure" }, main.Timeline.Single(t => t.Id == "injectE09").Actions.Where(a => a.Kind == "injectFault").Select(a => a.AsString()));
        Assert.Contains(main.Timeline.Single(t => t.Id == "injectE15").Actions, a => a.Kind == "voiceScript");
        var a = Node(main);
        var b = Node(dflt);
        foreach (var n in new[] { a, b })
        {
            n.Remove("id"); n.Remove("name"); n.Remove("description");
            var tl = (System.Text.Json.Nodes.JsonArray)n["timeline"]!;
            foreach (var item in tl.OfType<System.Text.Json.Nodes.JsonObject>().Where(i => (string?)i["id"] is "injectE09" or "injectE15"))
            {
                item.Remove("manual"); item.Remove("when"); item.Remove("name");
            }
        }
        Assert.Equal(a.ToJsonString(), b.ToJsonString());
    }

    /// <summary>
    /// 教官選擇版本無人操作(教官未注入):T+15 min 提示選模組;不升級;所有模組必要事件 notApplicable(未選的模組不扣分)。
    /// </summary>
    [Fact]
    public void E20_WithoutSelectionRemindsInstructorAndModulesAreNotApplicable()
    {
        var (e, ev, _) = Run(E20, 1000, record: false);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Assert.Equal(Param("emergency.e20.selectionReminderAt_s"), T(ev, "selectionReminder"));
        Assert.Empty(Phases(ev));
        foreach (var r in s.Assessment.Required.Where(r => r.Id.StartsWith('e')))
            Assert.Equal("notApplicable", r.State);
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.FaultInjected);
    }


    /// <summary>教官注入:T+400 s E08(港外段);過口門後舵角小於 3° 時 E07(港內航道)。</summary>
    private static void ManualE08E07(SimulationEngine eng, List<SimEvent> events, int t)
    {
        if (t == 400) eng.Enqueue(SimCommand.TriggerTimeline("injectE08"));
        if (FirstT(events, SimEventKinds.TimelineTriggered, "injectE07") is null && FirstT(events, SimEventKinds.TimelineTriggered, "harbourCurrent") is { } h && t >= h + 60)
        {
            var st = eng.BuildState();
            if (Math.Abs(st.Rudder) < 3) eng.Enqueue(SimCommand.TriggerTimeline("injectE07"));
        }
    }

    /// <summary>
    /// E20 合理操作學員腳本:交班檢查表 → E15 每次干擾 20 s 內授權/延後、警報氾濫指派專人 → 循跡修正流壓(自動舵,港外)→ FW2 VTS 報告、減至 SAH →
    /// OUTER 後改手操舵循跡 → 失電:5 s 車鐘歸零、20 s 雙錨備便、30 s 報告 VTS;主機恢復後 DSAH,教官確認儀器重啟 → 過口門靠泊簡報 →
    /// IN1 拖船帶纜 → 依距泊位中心的期望速度以車令控速 → 碼頭前以拖船推靠(同 E13 參考腳本)→ 繫 4 纜 → 120 s 後教官 allFast。
    /// </summary>
    private static (SimulationEngine Engine, List<SimEvent> Events, string? Path, List<string> Info) RunE20Reasonable(string id, bool record = true,
        Action<SimulationEngine, List<SimEvent>, int>? injector = null)
    {
        const double centre = 130.0;
        const double quayHeading = 60.0;
        var info = new List<string>();
        var keeper = new TrackKeeper(74);
        var helm = new Helm();
        var acked = new HashSet<string>();
        var mode = "approach";
        var manual = false;
        var tg = TelegraphOrder.HAH;
        var lastTg = 0;
        var fastAt = -1;
        var integ = 0.0;
        var sent = new Dictionary<string, (string Mode, double Dir, double Power)>();
        void Tel(SimulationEngine eng, TelegraphOrder o, int t)
        {
            if (o == tg) return;
            tg = o; lastTg = t;
            eng.Enqueue(SimCommand.Telegraph(o));
        }
        var (e, ev, path) = Run(id, 4500, (eng, events, t) =>
        {
            injector?.Invoke(eng, events, t);
            if (t == 20) eng.Enqueue(SimCommand.Acknowledge("checklist", note: "交班:航程與靠泊計畫、流壓、交通已核對"));
            foreach (var (item, kind, note, delay) in new[]
                     {
                         ("injectE15", "verbal", "授權大副處理卸貨計畫,引航中不簽字", 20), ("e15PscEmail", "verbal", "PSC 郵件靠妥後回覆", 20),
                         ("e15CompanyCall", "verbal", "告知公司正在引航進港,靠妥後回電", 20), ("e15AlarmFlood", "verbal", "指派二副確認 ECDIS 警報,船副專注航跡", 20),
                         ("injectE09", "anchorReady", "雙錨備便,視漂移決定下錨", 20), ("tugsAvailable", "checklist", "靠泊簡報:拖船右舷艏艉、繫纜順序、接近速度", 30),
                         ("injectE07", "notifyEcr", "通知機控室舵機故障", 30), ("injectE08", "gyroCheck", "電羅經與磁羅經比對差異", 60),
                     })
            {
                if (!acked.Contains(item) && FirstT(events, SimEventKinds.TimelineTriggered, item) is { } at && t >= at + delay)
                {
                    eng.Enqueue(SimCommand.Acknowledge(kind, note: note));
                    acked.Add(item);
                }
            }
            if (!acked.Contains("vts") && FirstT(events, SimEventKinds.ReportPointPassed) is { } rp && t >= rp + 20)
            {
                eng.Enqueue(SimCommand.Acknowledge("report", note: "高雄 VTS:FW2 報告,ETA 口門"));
                acked.Add("vts");
            }
            var blackout = FirstT(events, SimEventKinds.TimelineTriggered, "injectE09");
            if (blackout is { } bo)
            {
                if (t == (int)bo + 5) Tel(eng, TelegraphOrder.STOP, t);
                if (t == (int)bo + 30) eng.Enqueue(SimCommand.Acknowledge("report", note: "VTS:全船失電,漂向口門,請派拖船"));
                if (t == (int)bo + 400) eng.Enqueue(SimCommand.TriggerTimeline("e09InstrumentsRestored"));
            }
            // E08:交叉核對發現漂移(+60 s,gyroCheck)→ 改手操舵(真航向代表磁羅經/疊標);失效警報 10 s 內確認、下舵令、30 s 通知船長;教官 300 s 後確認重啟
            if (FirstT(events, SimEventKinds.TimelineTriggered, "injectE08") is { } g0 && t == (int)g0 + 60 && mode == "approach")
            {
                manual = true;
                eng.Enqueue(SimCommand.Autopilot(false));
            }
            if (FirstT(events, SimEventKinds.TimelineTriggered, "e08GyroFail") is { } g1)
            {
                if (t == (int)g1 + 10) { eng.Enqueue(SimCommand.AlertAcknowledge("gyroFailure")); eng.Enqueue(SimCommand.Rudder(eng.BuildState().RudderOrder)); }
                if (t == (int)g1 + 30) eng.Enqueue(SimCommand.Acknowledge("callMaster", note: "電羅經失效,手操舵依磁羅經"));
                if (t == (int)g1 + 300) eng.Enqueue(SimCommand.TriggerTimeline("e08GyroRestored"));
            }
            // E07:卸舵 10 s 內減速(車令)、30 s 通知機控室;教官 120 s 後確認應急操舵
            if (FirstT(events, SimEventKinds.TimelineTriggered, "injectE07") is { } s0)
            {
                if (t == (int)s0 + 10) Tel(eng, tg == TelegraphOrder.STOP ? TelegraphOrder.DSAH : TelegraphOrder.STOP, t);
                if (t == (int)s0 + 120) eng.Enqueue(SimCommand.TriggerTimeline("e07EmergencySteering"));
            }
            var engineBack = FirstT(events, SimEventKinds.TimelineTriggered, "e09EngineRestored");
            var st = eng.BuildState();
            var leg = st.Route?.LegIndex ?? 0;
            var q = eng.External.Berth!.Segments[0];
            var m = eng.Motion;
            var env = eng.Environment.SampleAt(m.X, m.Y);
            var (ve, vn) = EnvironmentMath.GroundVelocity(m.U, m.V, m.Psi, env.CurrentEastMps, env.CurrentNorthMps);
            var toward = -(ve * q.Nx + vn * q.Ny);
            var along = ve * q.Tx + vn * q.Ty;
            var dist = (m.X - q.Ax) * q.Nx + (m.Y - q.Ay) * q.Ny - 10.0;
            var chain = (m.X - q.Ax) * q.Tx + (m.Y - q.Ay) * q.Ty;
            var hdgErr = Units.RadToDeg(Units.WrapRadPi(m.Psi - Units.DegToRad(quayHeading)));
            if (t % 60 == 0) info.Add($"t={t} mode={mode} leg={leg} sog={st.Sog:F2} xtd={st.Route?.XtdM:F0} chain={chain:F0} dist={dist:F0} along={along:F2} toward={toward:F2} tg={tg}");

            switch (mode)
            {
                case "approach":
                    if (manual) helm.Step(eng, t);
                    else keeper.Step(eng, t);
                    if (leg >= 2 && tg == TelegraphOrder.HAH) Tel(eng, TelegraphOrder.SAH, t);
                    if (leg >= 3)
                    {
                        mode = "harbour";
                        if (!manual) eng.Enqueue(SimCommand.Autopilot(false));
                    }
                    break;
                case "harbour":
                    helm.Step(eng, t);
                    if (engineBack is { } eb && t == (int)eb + 5) Tel(eng, TelegraphOrder.DSAH, t);
                    if (leg >= 5 && !sent.ContainsKey("TG1"))
                    {
                        foreach (var tid in new[] { "TG1", "TG2" }) { eng.Enqueue(SimCommand.TugControl(tid, "pull", 90, 0)); sent[tid] = ("pull", 90, 0); }
                    }
                    // 期望沿碼頭速度(m/s):距泊位中心越近越慢
                    if ((blackout is null || engineBack is not null) && leg >= 4 && t - lastTg >= 15)
                    {
                        var vDes = Math.Clamp(0.45 + 0.0028 * (centre - chain), 0.35, 2.5);
                        var o = along > vDes + 0.6 ? TelegraphOrder.SAS : along > vDes + 0.25 ? TelegraphOrder.DSAS : along < vDes - 0.25 ? TelegraphOrder.DSAH : TelegraphOrder.STOP;
                        Tel(eng, o, t);
                    }
                    if (chain > -60 && dist < 90) { mode = "berth"; helm.HeadingOverride = quayHeading; }
                    break;
                case "berth":
                    if (t - lastTg >= 15)
                    {
                        var vDes = chain < centre - 8 ? Math.Clamp(0.005 * (centre - chain), 0.12, 0.5) : 0.0;
                        var o = along > vDes + 0.25 ? TelegraphOrder.DSAS : along < vDes - 0.08 && chain < centre - 8 ? TelegraphOrder.DSAH : TelegraphOrder.STOP;
                        Tel(eng, o, t);
                    }
                    if (tg != TelegraphOrder.STOP) helm.Step(eng, t);
                    {
                        // 橫向:泊位中心前 40 m 以拖船拉住、保持離碰墊約 40 m(艉不越過碼頭端,吹開風);之後換推頂緩慢推靠(換位 30 s)
                        var hold = chain < centre - 30;
                        var rotDegPerMin = Units.RadToDeg(m.R) * 60.0;
                        var h = Math.Clamp(2.0 * hdgErr + 4.0 * rotDegPerMin, -25.0, 25.0);
                        var vd = Math.Clamp(-0.004 * (chain - centre), -0.1, 0.3);
                        var d = Math.Clamp(200.0 * (vd - along), -35.0, 35.0);
                        // 拉住階段:只拉(離碰墊 50 m 內時防止靠近);推靠階段:只推,PI 控制朝碼頭速度(吹開風為煞車)
                        string tm;
                        double p;
                        if (hold)
                        {
                            tm = "pull";
                            p = dist < 50 ? Math.Clamp(80.0 * (toward + 0.02), 0.0, 40.0) : 0.0;
                        }
                        else
                        {
                            tm = "push";
                            var target = dist > 20 ? 0.08 : dist > 6 ? 0.05 : 0.03;
                            var err = target - toward;
                            integ = Math.Clamp(integ + 0.5 * err, 0.0, 30.0);
                            p = Math.Clamp(60.0 * err + integ, 0.0, 50.0);
                        }
                        var (p1, p2, dir) = tm == "push" ? (p + h, p - h, 270.0 + d) : (p - h, p + h, 90.0 - d);
                        foreach (var (tid, pw) in new[] { ("TG1", p1), ("TG2", p2) })
                        {
                            var cmd = (tm, Math.Round(dir / 5.0) * 5.0, Math.Round(Math.Clamp(pw, 0.0, 100.0) / 2.0) * 2.0);
                            if (sent.TryGetValue(tid, out var last) && last == cmd) continue;
                            eng.Enqueue(SimCommand.TugControl(tid, cmd.Item1, cmd.Item2, cmd.Item3));
                            sent[tid] = cmd;
                        }
                    }
                    if (fastAt < 0 && dist < 3.0 && Math.Abs(chain - centre) < 8.0 && Math.Abs(hdgErr) < 5.0)
                    {
                        fastAt = t;
                        Tel(eng, TelegraphOrder.STOP, t);
                        foreach (var l in new[] { "HEAD", "FWDSPRING", "AFTSPRING", "STERN" }) eng.Enqueue(SimCommand.Mooring(l, "makeFast"));
                        info.Add($"makeFast t={t} chain={chain:F1} hdgErr={hdgErr:F1}");
                    }
                    if (fastAt > 0 && t == fastAt + 120) eng.Enqueue(SimCommand.TriggerTimeline("allFast"));
                    break;
            }
        }, record);
        return (e, ev, path, info);
    }

    // ------------------------------------------------------------------ E19

    /// <summary>
    /// 無人操作:T+4 ISPS 保全警報 → T+8 機艙火警 degraded → 150 s 後確認火災 emergency → 120 s 後 VTS 等候指示;
    /// 集合決定逾時 → 提示;停主機回覆逾時 → 機艙停主機(mainEngine);確認後 720 s 火已撲滅(清除);必要事件全部逾時、不及格。
    /// </summary>
    [Fact]
    public void E19_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E19, 1800);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var ph = Phases(ev);
        Assert.Equal(new[] { (480.0, "normal", "degraded"), (630.0, "degraded", "emergency") }, ph);
        Assert.Equal(240.0, T(ev, "ispsAlert"));
        Assert.Equal(750.0, T(ev, "vtsHold"));
        Assert.Equal(752.0, T(ev, "musterPrompt"));
        Assert.Equal(812.0, T(ev, "engineStopped"));
        Assert.Equal(new[] { (480.0, "fireAlarmEngineRoom"), (812.0, "mainEngine") }, ev.Where(x => x.Kind == SimEventKinds.FaultInjected).Select(x => (x.T, x.Message!)));
        Assert.Equal(1350.0, T(ev, "fireExtinguished"));
        Assert.Equal(new[] { (1350.0, "mainEngine"), (1350.0, "fireAlarmEngineRoom") }, ev.Where(x => x.Kind == SimEventKinds.FaultCleared).Select(x => (x.T, x.Message!)));
        Assert.Equal(600.0, ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "OUT1").T);
        Assert.Equal(1800.0, T(ev, "timeUp"));
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.False(s.Assessment.HardFailed);
        Assert.False(s.Assessment.Passed);
        Assert.Empty(e.BuildState().Faults);
        AssertReplays(path!);
    }

    /// <summary>
    /// 合理操作:交班檢查表 → ISPS 20 s 內授權大副依 SSP 處理並通報 CSO → 火警 20 s 叫船長、50 s 聯繫機艙 →
    /// 確認火災 30 s 內決定集合人力(當值團隊留駕駛台)、60 s 回覆機艙(主機保持至減速等候)、90 s 報告 VTS → VTS 等候指示 20 s 內減至 DSAH;
    /// 全程以自動舵循跡。固定:必要事件全部 met、主機未被停止、容許帶皆 ok、100 分。
    /// </summary>
    [Fact]
    public void E19_ScriptedPrioritisationMeetsRequirements()
    {
        var keeper = new TrackKeeper(74);
        var (e, ev, path) = Run(E19, 1800, (eng, events, t) =>
        {
            switch (t)
            {
                case 20: eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case 260: eng.Enqueue(SimCommand.Acknowledge("notifyCompany", note: "授權大副依 SSP 檢查舵機房限制區並通報 CSO;駕駛台專注引航")); break;
                case 500: eng.Enqueue(SimCommand.Acknowledge("callMaster", note: "機艙煙感火警")); break;
                case 530: eng.Enqueue(SimCommand.Acknowledge("notifyEcr", note: "機艙確認火警位置,隨時回報")); break;
                case 660: eng.Enqueue(SimCommand.Acknowledge("verbal", note: "全船警報;消防隊集合由大副現場指揮,舵工、瞭望、二副留駕駛台")); break;
                case 690: eng.Enqueue(SimCommand.Acknowledge("notifyEcr", note: "主機保持運轉,減速等候後再依機艙需要停俥")); break;
                case 720: eng.Enqueue(SimCommand.Acknowledge("report", note: "向 VTS 與引水人報告機艙火災,請求拖船/消防船")); break;
                case 770: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH)); break;
            }
            keeper.Step(eng, t);
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        AssertClean(s);
        Assert.DoesNotContain(ev, x => x.ItemId is "engineStopped" or "musterPrompt");
        Assert.Equal(new[] { "fireAlarmEngineRoom" }, ev.Where(x => x.Kind == SimEventKinds.FaultInjected).Select(x => x.Message!));   // 主機未被停止
        Assert.Equal("telegraphChange", Req(s, "holdSpeedReduction").By);
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E20

    /// <summary>
    /// 預設組合無人操作:T+5 injectE15(degraded)→ 干擾每 2 分鐘、T+11 警報氾濫 + 漲潮增強 → 通過 OUTER 60 s 後 injectE09(emergency);
    /// 自動舵 074° 不隨航線轉向、未修正流壓 → 偏向北防波堤擱淺結束;必要事件逾時、硬性失敗。
    /// </summary>
    [Fact]
    public void E20Default_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E20D, 4500);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var outer = ev.Single(x => x.Kind == SimEventKinds.RouteLegChanged && x.DataNumber("legIndex") == 3).T;
        var blackout = T(ev, "injectE09");
        Assert.Equal(outer + Param("emergency.e20.defaultE09After_s"), blackout);
        Assert.Equal(new[] { (300.0, "normal", "degraded"), (blackout, "degraded", "emergency") }, Phases(ev));
        foreach (var (item, at) in new[] { ("injectE15", 300.0), ("e15PscEmail", 420.0), ("e15CompanyCall", 540.0), ("e15AlarmFlood", 660.0), ("e15CurrentIncrease", 660.0) })
            Assert.Equal(at, T(ev, item));
        Assert.Equal(blackout + Param("emergency.blackout.emergencyPower_s"), T(ev, "e09EmergencyPower"));
        Assert.Equal(new[] { "ecdisAlarmFlood", "mainEngine", "steeringGear", "gyroFailure", "blackout", "radarFailure" }, ev.Where(x => x.Kind == SimEventKinds.FaultInjected).Select(x => x.Message!));
        Assert.Equal(1822.0, outer);
        // 失電後 127 s 擱淺於北防波堤(下錨決策與 VTS 報告的期限 180 s 未到,仍為 pending)
        Assert.Equal(2009.0, s.Assessment.HardFails.Single(h => h.Id == "grounding").TS);
        Assert.Contains(s.Assessment.HardFails, h => h.Id == "northBreakwater");
        Assert.Equal(("groundingEnd", 2011.0), ev.Where(x => x.Kind == SimEventKinds.ExerciseEnded).Select(x => (x.ItemId!, x.T)).Single());
        Assert.False(s.Assessment.Passed);
        foreach (var id in new[] { "handoverChecklist", "vtsReport", "e09FirstAction", "e15SterileCargoCall", "e15SterilePscEmail", "e15SterileCompanyCall", "e15AlarmFloodManaged" })
            Assert.Equal("missed", Req(s, id).State);
        Assert.Equal("pending", Req(s, "e09AnchorDecision").State);
        Assert.Equal("pending", Req(s, "e09NotifyVts").State);
        Assert.Equal(1944.0, T(ev, "e09PilotPrompt"));
        foreach (var id in new[] { "e07FirstAction", "e07NotifyEcr", "e07EmergencySteering", "e08DriftDetected", "e08AlarmAck", "e08ManualSteering", "e08CallMaster" })
            Assert.Equal("notApplicable", Req(s, id).State);
        AssertReplays(path!);
    }

    /// <summary>
    /// 預設組合合理操作(見 <see cref="RunE20Reasonable"/>):E15 與 E09 必要事件 met、E07/E08 notApplicable、無硬性失敗、
    /// 所有類別容許帶皆 ok(航跡、UKC、會遇、速限、ROT、靠泊接近速度/碰墊能量/過載/繫妥位置)、教官 allFast 結束、100 分。
    /// </summary>
    [Fact]
    public void E20Default_ScriptedPassageAndBerthing()
    {
        var (e, ev, path, info) = RunE20Reasonable(E20D);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev.Where(x => x.Source != SimEventSources.Instructor).ToList(), s);
        foreach (var l in info) _out.WriteLine(l);
        AssertClean(s, "e07FirstAction", "e07NotifyEcr", "e07EmergencySteering", "e08DriftDetected", "e08AlarmAck", "e08ManualSteering", "e08CallMaster");
        Assert.Equal(new[] { "normal", "degraded", "emergency", "berthing" }, s.Phases.Select(p => p.Phase).ToArray());
        Assert.Equal("allFast", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        var b = s.Berthing!;
        Assert.Equal("KHH-E20-OIL", b.BerthId);
        Assert.NotNull(b.AllFastAtS);
        Assert.InRange(b.MaxLateralSpeedMps!.Value, 0.01, Param("berthing.approachSpeed_mps"));
        Assert.InRange(Math.Abs(b.AllFastPositionErrorM!.Value), 0, Param("berthing.allFastPositionTolerance_m"));
        Assert.DoesNotContain(ev, x => x.Kind is SimEventKinds.QuayContact or SimEventKinds.MooringLineParted);
        Assert.InRange(e.Tick * e.Dt, 3600, 4500);
        AssertReplays(path!);
    }

    /// <summary>
    /// 教官選擇版本:教官於 T+400 s 注入 E08(港外)、過口門後注入 E07(港內),合理操作同一腳本:
    /// 升級依注入順序(E08 → degraded、E07 → emergency);E07/E08 必要事件 met、E09/E15 notApplicable;容許帶皆 ok、靠泊完成。
    /// </summary>
    [Fact]
    public void E20_InstructorChoosesE08AndE07()
    {
        var (e, ev, path, info) = RunE20Reasonable(E20, injector: ManualE08E07);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev.Where(x => x.Source != SimEventSources.Instructor).ToList(), s);
        var e08 = T(ev, "injectE08");
        var e07 = T(ev, "injectE07");
        Assert.True(ev.Single(x => x.ItemId == "injectE08" && x.Kind == SimEventKinds.TimelineTriggered).DataBool("manual"));
        var ph = Phases(ev);
        Assert.Equal(("normal", "degraded"), (ph[0].From, ph[0].To));
        Assert.Equal(("degraded", "emergency"), (ph[1].From, ph[1].To));
        Assert.InRange(ph[0].T - e08, 0, 1);
        Assert.InRange(ph[1].T - e07, 0, 1);
        Assert.Equal(e08 + Param("emergency.gyroFailure.failAfter_s"), T(ev, "e08GyroFail"));
        AssertClean(s, "e09FirstAction", "e09AnchorDecision", "e09NotifyVts", "e15SterileCargoCall", "e15SterilePscEmail", "e15SterileCompanyCall", "e15AlarmFloodManaged");
        Assert.Equal("allFast", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E22 教室模式迷你情境

    /// <summary>無人操作:腳本事件依時刻發生、必要事件全部逾時、無硬性失敗、分數低於及格(講評素材)。</summary>
    [Theory]
    [InlineData(E22A, "arpaCue", 90.0, "discussionPoint", 240.0)]
    [InlineData(E22B, "lightSighted", 180.0, "discussionPoint", 300.0)]
    [InlineData(E22C, "masterOrder", 150.0, "discussionPoint", 300.0)]
    [InlineData(E22D, "helmsmanMisunderstands", 153.0, "discussionPoint", 540.0)]
    public void E22_NoOperatorRunsScriptAndMissesRequirements(string id, string cue, double cueAt, string discussion, double discussionAt)
    {
        var (e, ev, path) = Run(id, 900);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.Equal(cueAt, T(ev, cue));
        Assert.Equal(discussionAt, T(ev, discussion));
        Assert.Equal(new[] { (discussionAt, "normal", "discussion") }, Phases(ev));
        Assert.Equal(720.0, T(ev, "timeUp"));
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.False(s.Assessment.HardFailed);
        Assert.True(s.Assessment.Score < Param("assessment.passMark"), $"{s.Assessment.Score}");
        if (id == E22C)
        {
            // 未質疑:舵工照令左舵 10、60 s 後回正(船長接受質疑的語音不出現)
            Assert.Equal(242.0, T(ev, "masterOrderExecuted"));
            Assert.Equal(302.0, T(ev, "helmsmanSteadies"));
            Assert.DoesNotContain(ev, x => x.ItemId == "masterAccepts");
        }
        if (id == E22D) Assert.Equal(420.0, T(ev, "ecrCall"));
        AssertReplays(path!);
    }

    /// <summary>合理操作:各迷你情境的期望行為於期限內以教官確認(acknowledge)或舵令表示;必要事件 met、100 分。</summary>
    [Theory]
    [InlineData(E22A)]
    [InlineData(E22B)]
    [InlineData(E22C)]
    [InlineData(E22D)]
    public void E22_ScriptedResponsesMeetRequirements(string id)
    {
        var (e, ev, path) = Run(id, 900, (eng, events, t) =>
        {
            switch (id, t)
            {
                case (E22A, 120): eng.Enqueue(SimCommand.Acknowledge("verbal", note: "請交班者留下:日誌 07:30 改向、ARPA X1 CPA 0.5 nm,與口頭說明不符")); break;
                case (E22A, 200): eng.Enqueue(SimCommand.Acknowledge("checklist", note: "資訊釐清後接班")); break;
                case (E22B, 60): eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case (E22B, 200): eng.Enqueue(SimCommand.Acknowledge("callMaster", note: "疲勞,請船長上駕駛台或加派瞭望")); break;
                case (E22B, 230): eng.Enqueue(SimCommand.Acknowledge("report", note: "雷達確認:右舷貨船橫越,航向 280,CPA 0.5 nm,不是錨泊船")); break;
                case (E22C, 30): eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case (E22C, 180): eng.Enqueue(SimCommand.Acknowledge("verbal", note: "船長,我們是讓路船,向左從他船艏前過違反第 15 條,建議右轉從他船艉後過")); break;
                case (E22C, 200): eng.Enqueue(SimCommand.Autopilot(true, 220)); break;
                case (E22C, 600): eng.Enqueue(SimCommand.Autopilot(true, 180)); break;
                case (E22D, 30): eng.Enqueue(SimCommand.Acknowledge("checklist", note: "工作語言英語,舵令 SMCP")); break;
                case (E22D, 168): eng.Enqueue(SimCommand.Rudder(-10)); break;
                case (E22D, 185): eng.Enqueue(SimCommand.Acknowledge("verbal", note: "Port ten — repeat: port ten. 舵工複誦確認")); break;
                case (E22D, 215): eng.Enqueue(SimCommand.Rudder(0)); break;
                case (E22D, 440): eng.Enqueue(SimCommand.Acknowledge("verbal", note: "Read back: reduce RPM for five minutes — reason? 二管輪:淨油機換濾器")); break;
            }
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        AssertClean(s);
        if (id == E22C)
        {
            Assert.Equal(180.0, T(ev, "masterAccepts"));
            Assert.DoesNotContain(ev, x => x.ItemId == "masterOrderExecuted");
        }
        AssertReplays(path!);
    }
}
