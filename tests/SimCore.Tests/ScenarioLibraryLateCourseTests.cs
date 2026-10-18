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
/// 情境庫課程後段 E15 干擾與工作負荷過載(高雄)、E16 夜間進港燈光混淆(基隆)、E17 颱風準備與離港決策(高雄泊位)、
/// E18 油碼頭進港 UKC/squat 限制(安平)、E21 貨物加熱系統故障下的到港壓力(安平)(規劃書第 8.1 節表列與設計規則;
/// docs/design/scenario-library.md 第 9–14 節):載入/驗證無錯誤無警告、交班開場、三步升級各一、示意區域檔一致;
/// 無人操作時升級時刻與關鍵事件固定;一次「合理操作」學員腳本(與主控程序相同的 MMG 動力學、每秒迴圈)必要事件 met、
/// 容許帶不誤扣(皆非 fail,且及格);紀錄 → 重播雜湊/事件/summary 一致(重播後刪除紀錄)。
/// </summary>
public class ScenarioLibraryLateCourseTests
{
    private readonly ITestOutputHelper _out;
    public ScenarioLibraryLateCourseTests(ITestOutputHelper output) => _out = output;

    private const string E15 = "E15_distraction_workload_kaohsiung";
    private const string E16 = "E16_night_approach_keelung";
    private const string E17 = "E17_typhoon_departure_kaohsiung";
    private const string E18 = "E18_ukc_squat_anping";
    private const string E21 = "E21_cargo_heating_pressure_anping";
    public static readonly TheoryData<string> LibraryIds = new() { E15, E16, E17, E18, E21 };

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
        foreach (var x in events.Where(x => x.Kind is not (TrafficEventKinds.CpaAlarm or TrafficEventKinds.CpaClear or TrafficEventKinds.Sound or SimEventKinds.Message or SimEventKinds.VoiceScript
                     or SimEventKinds.FenderContact)))
            _out.WriteLine($"{x.T:F1} {x.Source}/{x.Kind} {x.ItemId} {x.TargetId} {x.Message}");
        if (s is null) return;
        foreach (var r in s.Assessment.Required) _out.WriteLine($"required {r.Id}: {r.State} dt={r.DtS} by={r.By}");
        foreach (var b in s.Assessment.Bands) _out.WriteLine($"band {b.Id}: {b.State} value={b.Value}");
        foreach (var h in s.Assessment.HardFails) _out.WriteLine($"hardFail {h.Id} @ {h.TS}");
        foreach (var c in s.CollisionAvoidance.Targets) _out.WriteLine($"enc {c.TargetId} det={c.DetectedAtS} act={c.FirstActionAtS} minRange={c.MinRangeNm:F3}");
        _out.WriteLine($"ukc min={s.Ukc.MinM:F2} below={s.Ukc.TimeBelowLimitS} score {s.Assessment.Score} passed {s.Assessment.Passed}");
    }

    private static double T(List<SimEvent> ev, string itemId) => ev.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == itemId).T;
    private static double? FirstT(List<SimEvent> ev, string kind, string? itemId = null, string? targetId = null)
        => ev.FirstOrDefault(x => x.Kind == kind && (itemId is null || x.ItemId == itemId) && (targetId is null || x.TargetId == targetId))?.T;
    private static List<(double T, string From, string To)> Phases(List<SimEvent> ev) =>
        ev.Where(x => x.Kind == SimEventKinds.PhaseChanged).Select(x => (x.T, x.DataString("from")!, x.DataString("to")!)).ToList();
    private static RequiredResult Req(AssessmentSummary s, string id) => s.Assessment.Required.Single(r => r.Id == id);
    private static BandResult Band(AssessmentSummary s, string id) => s.Assessment.Bands.Single(r => r.Id == id);
    private static double Param(string key) => TestData.Parameters().Parameters[key].NumberValue!.Value;

    /// <summary>無人操作時三步升級依序:normal → degraded → emergency,且各只一次。</summary>
    private static void AssertEscalation(List<SimEvent> ev, double degradedAt, double emergencyAt)
    {
        var ph = Phases(ev);
        Assert.Equal(2, ph.Count);
        Assert.Equal((degradedAt, "normal", "degraded"), ph[0]);
        Assert.Equal((emergencyAt, "degraded", "emergency"), ph[1]);
    }

    /// <summary>
    /// 循跡(學員以自動舵修正流壓):每 <paramref name="every"/> s 以航段航向 + 偏航角(艏向 − COG)− 增益 × (XTD − 期望偏移)下自動舵航向;
    /// 每次變更限制在 ±4°(小於會遇「首次行動」門檻 5°,不會被誤判為避讓行動)。
    /// </summary>
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
        // 設計規則:交班與書面計畫開場、三步升級各一個項目
        Assert.Equal("handover", sc.Timeline[0].Id);
        Assert.Contains("交班", sc.Briefing!.Text);
        Assert.Contains(sc.Briefing.Attachments!, a => a.Title.Contains("計畫"));
        Assert.Contains(sc.Assessment.Required, r => r.Id == "handoverChecklist" && r.After.ItemId == "handover");
        Assert.Single(sc.Timeline, t => t.Phase == "degraded");
        Assert.Single(sc.Timeline, t => t.Phase == "emergency");
        // 只記錄故障(ecdisAlarmFlood、cargoHeatingFailure)以訊息/語音表示,不注入(--validate 無警告)
        Assert.DoesNotContain(sc.Timeline.SelectMany(t => t.Actions), a => a.Kind == "injectFault" && !ScenarioValidator.ModelledFaults.Contains(a.AsString()));
        // 參數引用:新鍵皆為「假設值,待公司程序」來源
        var resolved = ScenarioLoader.LastResolvedParameters;
        var ps = TestData.Parameters();
        foreach (var r in resolved.Where(r => r.Source.StartsWith("ASSUMED-", StringComparison.Ordinal)))
            Assert.StartsWith("假設值,待公司程序", ps.Sources[r.Source].Title);
        Assert.Contains(resolved, r => r.Key == "bridge.handover.checklist_s");
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
        foreach (var f in new[] { "kaohsiung-second-port-inbound.json", "keelung-approach.json", "kaohsiung-south-oil-berth.json", "anping-approach.json" })
        {
            var a = Area(f);
            Assert.Contains("示意", a.GetProperty("source").GetString());
            Assert.Contains("待 ENC 校正", a.GetProperty("description").GetString() + a.GetProperty("source").GetString());
            Assert.False(a.TryGetProperty("land", out _));   // 雷達陸地回波仍以 kaohsiung-approach.json 為 Land[0](Sensors.Radar 測試)
        }

        var anch = Area("kaohsiung-anchorage.json").GetProperty("anchorages")[0].GetProperty("polygon").EnumerateArray().Select(LL).ToList();
        var khh = Area("kaohsiung-second-port-inbound.json");
        var e15 = Load(E15);
        Assert.Equal(khh.GetProperty("route").EnumerateArray().Select(LL), e15.Route!.Waypoints.Select(W));
        Assert.Equal(anch, e15.Zones.Single(z => z.Id == "anchorage").Polygon.Select(P));
        Assert.Equal(LL(khh.GetProperty("route")[0]), (e15.Initial.Position.Lat!.Value, e15.Initial.Position.Lon!.Value));

        var kel = Area("keelung-approach.json");
        var e16 = Load(E16);
        Assert.Equal(kel.GetProperty("route").EnumerateArray().Select(LL), e16.Route!.Waypoints.Select(W));
        foreach (var sh in kel.GetProperty("shoals").EnumerateArray())
        {
            var z = e16.Zones.Single(z => z.Id == sh.GetProperty("id").GetString());
            Assert.Equal(ZoneType.UkcPolicy, z.Type);
            Assert.Equal(sh.GetProperty("depth_m").GetDouble(), z.WaterDepthM);
            Assert.Equal(sh.GetProperty("polygon").EnumerateArray().Select(LL), z.Polygon.Select(P));
        }

        var e17 = Load(E17);
        Assert.Equal("KHH-SOB-1", e17.Berth!.Definition!.Id);
        Assert.Contains("示意", e17.Berth.Definition.Source ?? Area("kaohsiung-south-oil-berth.json").GetProperty("source").GetString());

        var anp = Area("anping-approach.json");
        var route = anp.GetProperty("route").EnumerateArray().Select(LL).ToList();
        foreach (var id in new[] { E18, E21 })
        {
            var sc = Load(id);
            var wps = sc.Route!.Waypoints.Select(W).ToList();
            var start = route.IndexOf(wps[0]);
            Assert.True(start >= 0, $"{id} 起點不在區域檔航線上");
            Assert.Equal(route.Skip(start), wps);                       // 區域檔航線的連續子序列(到 BASIN)
            Assert.Equal(anp.GetProperty("channel").GetProperty("polygon").EnumerateArray().Select(LL), sc.Zones.Single(z => z.Id == "channel").Polygon.Select(P));
            Assert.Equal(anp.GetProperty("channel").GetProperty("polygon").EnumerateArray().Select(LL), sc.Zones.Single(z => z.Id == "channelSpeed").Polygon.Select(P));
            foreach (var sh in anp.GetProperty("shoals").EnumerateArray())
            {
                var z = sc.Zones.Single(z => z.Id == sh.GetProperty("id").GetString());
                Assert.Equal(ZoneType.UkcPolicy, z.Type);
                Assert.Equal(sh.GetProperty("depth_m").GetDouble(), z.WaterDepthM);
                Assert.Equal(sh.GetProperty("polygon").EnumerateArray().Select(LL), z.Polygon.Select(P));
            }
        }
        // 檔名排序:新檔不含 land,不影響 Sensors.Radar 的 Land[0]
        Assert.True(string.CompareOrdinal("kaohsiung-approach.json", "kaohsiung-second-port-inbound.json") < 0);
    }

    // ------------------------------------------------------------------ E15

    /// <summary>
    /// 無人操作:每 2 分鐘干擾 → T+8 degraded(警報氾濫 + 漲潮 0.8 → 1.4 kn)→ T+13 X1 起步橫越(關鍵事件)→ X1 進入 1 nm emergency → T+30 結束;
    /// 必要事件全部逾時、X1 最近距離低於硬性失敗門檻、航跡偏出走廊(warn)。
    /// </summary>
    [Fact]
    public void E15_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E15, 1800);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var emergency = T(ev, "crossingCloseQuarters");
        AssertEscalation(ev, 480, emergency);
        Assert.Equal(1043.0, emergency);                                         // X1 進入 1 nm(時間後備 1260 s 之前)
        foreach (var (item, at) in new[] { ("cargoPlanCall", 120.0), ("pscEmail", 240.0), ("agentVhf", 360.0), ("companyCall", 600.0), ("ecrCall", 720.0),
                     ("bosunCall", 840.0), ("vtsInfo", 960.0), ("agentVhf2", 1080.0), ("ecdisAlarmFlood2", 1200.0), ("pscFollowup", 1320.0) })
            Assert.Equal(at, T(ev, item));
        // 干擾每 2 分鐘一次(T+2 至 T+22,含 T+8 警報氾濫)
        var interruptions = new[] { "cargoPlanCall", "pscEmail", "agentVhf", "ecdisAlarmFlood", "companyCall", "ecrCall", "bosunCall", "vtsInfo", "agentVhf2", "ecdisAlarmFlood2", "pscFollowup" }
            .Select(i => T(ev, i)).ToList();
        Assert.All(interruptions.Zip(interruptions.Skip(1)), p => Assert.Equal(120.0, p.Second - p.First));
        Assert.Equal(8, ev.Count(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "alarmFloodRepeat"));
        Assert.Equal(780.0, T(ev, "crossingTurn"));
        Assert.Equal(798.0, FirstT(ev, SimEventKinds.EncounterStarted, targetId: "X1"));   // 關鍵事件藏在干擾 5(T+12)與 6(T+14)之間
        Assert.Equal(new[] { 780.0, 780.0 }, ev.Where(x => x.Kind == SimEventKinds.EnvironmentRampDone).Select(x => x.T));
        Assert.Equal(1800.0, T(ev, "timeUp"));
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.Contains(s.Assessment.HardFails, h => h.Id == "cpaHard");
        Assert.Equal("fail", Band(s, "minCpa").State);
        Assert.Equal("warn", Band(s, "xtdMax").State);                          // 漲潮增強後未再修正流壓:偏出走廊
        Assert.Equal(1300.0, s.Assessment.HardFails.Single(h => h.Id == "cpaHard").TS);
        Assert.Equal(new[] { "normal", "degraded", "emergency" }, s.Phases.Select(p => p.Phase).ToArray());
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員腳本(合理操作):交班檢查表 → 每次干擾 20 s 內授權/延後(acknowledge verbal)→ 警報氾濫時指派專人處理 → 以自動舵修正流壓循跡(每次 ≤ 4°)→
    /// X1 會遇開始 60 s 後瞭望報告;X1 起步(對地 > 2 kn)即減至 DSAH 並向右 20°(由 X1 艉後通過)→ X1 通過最近點且在左舷後回航跡。固定:必要事件全部 met、無硬性失敗、容許帶皆 ok、到達口門外。
    /// </summary>
    [Fact]
    public void E15_ScriptedSterileBridgeMeetsRequirements()
    {
        var keeper = new TrackKeeper(74);
        double? x1 = null;
        int? avoidAt = null;
        var acked = new HashSet<string>();
        var (e, ev, path) = Run(E15, 1800, (eng, events, t) =>
        {
            if (t == 20) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            foreach (var (item, note) in new[] { ("cargoPlanCall", "授權大副處理卸貨計畫,引航中不簽字"), ("pscEmail", "PSC 郵件靠妥後回覆"),
                         ("ecdisAlarmFlood", "指派二副確認 ECDIS 警報,船副專注航跡與交通"), ("companyCall", "告知公司正在引航進港,靠妥後回電") })
            {
                if (!acked.Contains(item) && FirstT(events, SimEventKinds.TimelineTriggered, item) is { } at && t >= at + 20)
                {
                    eng.Enqueue(SimCommand.Acknowledge("verbal", note: note));
                    acked.Add(item);
                }
            }
            x1 ??= FirstT(events, SimEventKinds.EncounterStarted, targetId: "X1");
            if (x1 is { } a && t == (int)a + 60) eng.Enqueue(SimCommand.Acknowledge("lookoutReport", role: "lookout", note: "X1 右舷貨船,注意動態"));
            if (x1 is not null && t % 5 == 0)
            {
                var st = eng.BuildState();
                var tx = st.Targets!.Single(x => x.Id == "X1");
                var rel = Units.NormalizeHeadingDeg(tx.BearingDeg - st.Heading);
                if (avoidAt is null && tx.Sog > 2)
                {
                    // X1 起步橫越:讓路船及早大幅向右並減速,由 X1 艉後通過(第 15、16 條)
                    avoidAt = t;
                    keeper.Enabled = false;
                    keeper.Override(Math.Round(keeper.Set + 20));
                    eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH));
                    eng.Enqueue(SimCommand.Autopilot(true, keeper.Set));
                }
                else if (avoidAt is not null && !keeper.Enabled && tx.TcpaMin <= 0 && rel > 180)
                {
                    // X1 已通過最近點且在左舷:回航跡(先轉回航段航向減 10°,再由循跡修正)
                    keeper.Override(60);
                    keeper.Enabled = true;
                    eng.Enqueue(SimCommand.Autopilot(true, 60));
                    eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.HAH));
                }
            }
            keeper.Step(eng, t);
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.Equal("acknowledge:lookoutReport", Req(s, "crossingDetected").By);
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.All(s.Assessment.Bands, b => Assert.Equal("ok", b.State));
        Assert.Equal("arrived", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.Equal(100.0, s.Assessment.Score);
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E16

    /// <summary>
    /// 無人操作:F1(無燈無 AIS)距 1.2 nm 才在雷達出現 → 通過 K1 degraded(後導燈熄滅、風浪增強)→ 距登船點 1.0 nm emergency(OUT1 出港、流壓增強)→ 到達登船點;
    /// 必要事件全部逾時、F1 最近距離低於硬性失敗門檻。
    /// </summary>
    [Fact]
    public void E16_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E16, 2100);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var k1 = ev.Single(x => x.Kind == SimEventKinds.ReportPointPassed).T;
        AssertEscalation(ev, 771, 1090);
        Assert.Equal(k1, T(ev, "leadingLightsOut"));
        Assert.Equal(534.0, ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "F1").T);
        Assert.Equal(534.0, FirstT(ev, SimEventKinds.EncounterStarted, targetId: "F1"));
        Assert.Equal(1090.0, ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "OUT1").T);
        Assert.Equal(new[] { 1071.0, 1071.0, 1071.0, 1071.0, 1071.0, 1330.0, 1330.0 }, ev.Where(x => x.Kind == SimEventKinds.EnvironmentRampDone).Select(x => x.T));
        Assert.Equal(1419.0, T(ev, "arrived"));
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.Contains(s.Assessment.HardFails, h => h.Id == "cpaHard");
        Assert.DoesNotContain(s.Assessment.HardFails, h => h.Id == "grounding");
        Assert.True(s.CollisionAvoidance.Targets.Single(c => c.TargetId == "F1").MinRangeNm < Param("navigation.cpa.hardFail_nm"));
        Assert.False(e.BuildState().Targets!.Single(x => x.Id == "F1").AisOn);
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員腳本(合理操作):交班檢查表 → 循跡(自動舵修正流壓)→ F1 雷達回波出現 40 s 內瞭望報告,改以導航線西側 400 m 通過(F1 在線上偏東 0.06 nm)→
    /// K1 VTS 報告、後導燈看不到時通知船長(查航船布告、改用雷達平行指標)→ F1 通過後回導航線 → OUT1 出港時口頭確認左舷對左舷並減至 SAH → 到達登船點。
    /// 固定:必要事件全部 met、無硬性失敗(未觸淺礁)、容許帶皆 ok。
    /// </summary>
    [Fact]
    public void E16_ScriptedLookoutAndRadarCrossCheck()
    {
        var keeper = new TrackKeeper(193);
        double? f1 = null, k1 = null, outbound = null;
        var (e, ev, path) = Run(E16, 2100, (eng, events, t) =>
        {
            if (t == 20) eng.Enqueue(SimCommand.Acknowledge("checklist", note: "航船布告:後導燈熄滅已讀"));
            f1 ??= FirstT(events, SimEventKinds.EncounterStarted, targetId: "F1");
            k1 ??= FirstT(events, SimEventKinds.ReportPointPassed);
            outbound ??= FirstT(events, SimEventKinds.TimelineTriggered, "outboundAndSet");
            if (f1 is { } a)
            {
                if (t == (int)a + 40) eng.Enqueue(SimCommand.Acknowledge("lookoutReport", role: "lookout", note: "F1 雷達微弱回波,正前方 1.2 nm,無燈無 AIS"));
                if (t == (int)a + 40) keeper.Offset = 400;                // 由 F1 西側(右舷)通過
            }
            if (k1 is { } k)
            {
                if (t == (int)k + 20) eng.Enqueue(SimCommand.Acknowledge("report", note: "基隆 VTS:K1 報告"));
                if (t == (int)k + 40) eng.Enqueue(SimCommand.Acknowledge("callMaster", note: "後導燈看不到;查航船布告,改用雷達平行指標"));
            }
            if (f1 is not null && keeper.Offset > 0 && t % 5 == 0)
            {
                var st = eng.BuildState();
                var tx = st.Targets!.Single(x => x.Id == "F1");
                if (tx.TcpaMin < -1) keeper.Offset = 0;                  // F1 已通過:回導航線
            }
            if (outbound is { } o && t == (int)o + 20)
            {
                eng.Enqueue(SimCommand.Acknowledge("verbal", note: "OUT1 左舷對左舷,保持導航線右側"));
                eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
            }
            keeper.Step(eng, t);
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.Equal("acknowledge:lookoutReport", Req(s, "lookoutReportF1").By);
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.All(s.Assessment.Bands, b => Assert.Equal("ok", b.State));
        Assert.Equal("arrived", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.Equal(100.0, s.Assessment.Score);
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E17

    [Fact]
    public void E17_LoadsAlongsideWithOneTugAndWindProfile()
    {
        var sc = Load(E17);
        Assert.Equal(LoadingCondition.Ballast, sc.Ship.Loading);
        var tug = Assert.Single(sc.Tugs!);                                   // 拖船可用數縮減後只剩 TG1(TUG2 為目標船,改派後駛離)
        Assert.Equal("TG1", tug.Id);
        Assert.Contains(sc.Targets, t => t.Id == "TUG2" && t.ShipType == "tug");
        Assert.All(sc.Berth!.Lines, l => Assert.Equal("fast", l.Initial));
        static double Wind(Scenario.Scenario sc, string id) => sc.Timeline.Single(t => t.Id == id).Actions.Single(a => a.Kind == "setEnvironment").Payload.GetProperty("wind").GetProperty("trueSpeed").GetDouble();
        Assert.Equal(15.0, sc.Environment.Wind.TrueSpeed);
        Assert.Equal(25.0, Wind(sc, "tugReduced"));
        Assert.Equal(40.0, Wind(sc, "outerBands"));
        Assert.Equal(Param("typhoon.e17.stormAt_s") - Param("typhoon.e17.portOrderAt_s"), Param("typhoon.e17.departBy_s"));
    }

    /// <summary>
    /// 無人操作(留在泊位):T+1 min 港方要求離港 → T+7 degraded(拖船 2 → 1、風 15 → 25 kn)→ T+25 emergency(25 → 40 kn)→ T+45 時間到;
    /// 決策、主機備便、拖船計畫與使用、強風前離泊全部逾時;船靠在碰墊上(吹攏風),TUG2 駛離。
    /// </summary>
    [Fact]
    public void E17_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E17, 2700);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        AssertEscalation(ev, 420, 1500);
        Assert.Equal(60.0, T(ev, "portOrder"));
        Assert.Equal(240.0, T(ev, "companyQuery"));
        Assert.Equal(2700.0, T(ev, "timeUp"));
        Assert.Equal(new[] { 1020.0, 1020.0, 1020.0, 2100.0, 2100.0, 2100.0 }, ev.Where(x => x.Kind == SimEventKinds.EnvironmentRampDone).Select(x => x.T));
        Assert.Equal(420.0, ev.Single(x => x.Kind == TrafficEventKinds.Control && x.TargetId == "TUG2").T);
        Assert.Equal(40.0, Units.MpsToKn(e.Environment.WindTrueSpeedMps), 6);
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "clearOfBerth");
        Assert.Empty(s.Berthing!.Tugs.Where(t => t.WorkingTimeS > 0));
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員腳本(合理操作):交班檢查表 → 港方要求後決定離港並通報公司、通知機艙備便 → 拖船縮減後修正計畫(艏倒纜開艉、一艘拖船拉艉)→
    /// TG1 左舷艉帶纜待命 → 留艏倒纜、右滿舵微速進車開艉 → 解艏倒纜、慢速退俥、TG1 依開出角拉艉(抵抗吹攏風) → 離開泊位區後 60 s 結束(強風前)。
    /// 固定:必要事件全部 met、無硬性失敗(碼頭硬接觸、斷纜)、容許帶皆 ok、拖船有工作時間。
    /// </summary>
    [Fact]
    public void E17_ScriptedEarlyDepartureWithOneTug()
    {
        var tugPower = 0.0;
        var (e, ev, path) = Run(E17, 2700, (eng, events, t) =>
        {
            switch (t)
            {
                case 20: eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case 90: eng.Enqueue(SimCommand.Acknowledge("notifyCompany", note: "依港方要求離港避颱,預計 30 分鐘內離泊")); break;
                case 100: eng.Enqueue(SimCommand.Acknowledge("notifyEcr", note: "主機備便")); break;
                case 260: eng.Enqueue(SimCommand.Acknowledge("verbal", note: "回覆公司:港方已要求離港,風力持續增強,維持離港決定")); break;
                case 440: eng.Enqueue(SimCommand.Acknowledge("verbal", note: "只剩一艘拖船:艏倒纜與俥舵開艉,TG1 左舷艉拉住艉,提前離泊")); break;
                case 450: eng.Enqueue(SimCommand.TugControl("TG1", "pull", 270, 0)); break;   // 帶纜(connect_s 90)後待命
                case 560:
                    foreach (var l in new[] { "HEAD", "AFTSPRING", "STERN" }) eng.Enqueue(SimCommand.Mooring(l, "letGo"));
                    break;
                case 570: eng.Enqueue(SimCommand.Rudder(35)); eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH)); break;   // 以艏倒纜開艉(E14 同法)
                case 670:
                    eng.Enqueue(SimCommand.Mooring("FWDSPRING", "letGo"));
                    eng.Enqueue(SimCommand.Rudder(0));
                    eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAS));                  // 艉已開出約 15°:退俥離開碼頭
                    eng.Enqueue(SimCommand.Thruster(-0.3));
                    break;
            }
            // TG1 拉艉(左舷艉、相對方位 270°):開出角小於 15° 時拉住艉,抵抗吹攏風把艉壓回碼頭
            if (t >= 670 && t % 5 == 0)
            {
                var hdg = Units.RadToDeg(Units.WrapRadPi(eng.Motion.Psi));
                var power = Math.Round(Math.Clamp(8 * (15 - hdg), 0, 80) / 10) * 10;
                if (power != tugPower)
                {
                    tugPower = power;
                    eng.Enqueue(SimCommand.TugControl("TG1", "pull", 270, power));
                }
            }
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        _out.WriteLine(JsonSerializer.Serialize(s.Berthing! with { Landings = s.Berthing.Landings.Take(2).ToArray(), Contacts = s.Berthing.Contacts.TakeLast(3).ToArray() }, ContractJson.Indented));
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.All(s.Assessment.Bands, b => Assert.NotEqual("fail", b.State));
        Assert.True(T(ev, "clearOfBerth") < Param("typhoon.e17.stormAt_s"));
        Assert.Equal("endAfterClear", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.True(s.Berthing!.Tugs.Single(x => x.Id == "TG1").WorkingTimeS > 0);
        Assert.True(s.Assessment.Passed, $"score {s.Assessment.Score}");
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E18

    /// <summary>
    /// 無人操作(保持 HAH 約 7.5 kn,不減速):T+5 degraded(潮位 8.9 → 8.6 m)→ 淺水警報 → T+12 碼頭要求加速 → 通過 BAR emergency(→ 8.4 m)→ 到達迴船池;
    /// squat 約 0.75 m 使 UKC 長時間低於政策(超限時間 fail)、超過航道速限,必要事件全部逾時。
    /// </summary>
    [Fact]
    public void E18_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E18, 3300);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var bar = ev.Where(x => x.Kind == SimEventKinds.CheckpointPassed).Select(x => x.T).ToList();
        AssertEscalation(ev, 300, 1333);
        Assert.Equal(bar[1], T(ev, "barShoaling"));                             // 通過沙洲段 BAR(時間後備 2100 s 之前)
        Assert.Equal(720.0, T(ev, "speedPressure"));
        Assert.Equal(new[] { 900.0, 1933.0 }, ev.Where(x => x.Kind == SimEventKinds.EnvironmentRampDone).Select(x => x.T));
        Assert.Equal(8.4, e.Environment.Depth is ConstantDepth c ? c.Depth : ((ZoneDepth)e.Environment.Depth).Base.Depth, 6);
        Assert.InRange(FirstT(ev, SimEventKinds.AlertRaised, "shallowWater")!.Value, 580, 590);
        Assert.Equal(2167.0, T(ev, "arrived"));
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.Equal("fail", Band(s, "ukcTime").State);
        Assert.Equal("warn", Band(s, "ukcMin").State);
        Assert.Equal("warn", Band(s, "speed").State);
        Assert.InRange(s.Ukc.MinSquatM!.Value, 0.6, 0.9);
        Assert.False(s.Assessment.Passed);
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員腳本(合理操作):交班檢查表 → 開場即減至 SAH(航道外端前)→ 潮位通報後重算 UKC 並回報 → 拒絕碼頭加速要求 → 淺水警報即減至 DSAH(未響則通過 BAR 時減)→
    /// 循跡(自動舵修正橫流)→ 到達迴船池。固定:必要事件 met(淺水警報未響時 ukcAlarmResponse 為 notApplicable)、無硬性失敗、容許帶皆 ok。
    /// </summary>
    [Fact]
    public void E18_ScriptedSlowSpeedWithinUkcPolicy()
    {
        var keeper = new TrackKeeper(77);
        double? tide = null, pressure = null, alarm = null, bar = null;
        var slowed = false;
        var (e, ev, path) = Run(E18, 3300, (eng, events, t) =>
        {
            if (t == 20) eng.Enqueue(SimCommand.Acknowledge("checklist", note: "UKC/squat 計算、潮窗、安全等深線 9 m、測深儀警報 0.8 m 已核對"));
            if (t == 30) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
            tide ??= FirstT(events, SimEventKinds.TimelineTriggered, "tideLower");
            pressure ??= FirstT(events, SimEventKinds.TimelineTriggered, "speedPressure");
            alarm ??= FirstT(events, SimEventKinds.AlertRaised, "shallowWater");
            bar ??= FirstT(events, SimEventKinds.TimelineTriggered, "barShoaling");
            if (tide is { } a && t == (int)a + 40) eng.Enqueue(SimCommand.Acknowledge("report", note: "潮位低 0.3 m:SAH 時 UKC 約 0.95 m,BAR 前再評估"));
            if (pressure is { } p && t == (int)p + 40) eng.Enqueue(SimCommand.Acknowledge("verbal", note: "拒絕加速:HAH 時 squat 0.8 m,UKC 低於政策且超過航道速限"));
            if (!slowed && ((alarm is { } al && t >= al + 15) || (bar is { } b && t >= b + 15)))
            {
                eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH));
                slowed = true;
            }
            keeper.Step(eng, t);
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        foreach (var r in s.Assessment.Required.Where(r => r.Id != "ukcAlarmResponse")) Assert.Equal("met", r.State);
        Assert.Contains(Req(s, "ukcAlarmResponse").State, new[] { "met", "notApplicable" });
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.All(s.Assessment.Bands, b => Assert.Equal("ok", b.State));
        Assert.True(s.Ukc.MinM >= Param("navigation.ukc.channel_fraction") * 7.2, $"UKC {s.Ukc.MinM:F2}");
        Assert.Equal("arrived", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.Equal(100.0, s.Assessment.Score);
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E21

    [Fact]
    public void E21_SharesE18GeometryWithFallingTide()
    {
        var e18 = Load(E18);
        var e21 = Load(E21);
        Assert.Equal(e18.Route!.Waypoints.Select(w => (w.Lat, w.Lon)), e21.Route!.Waypoints.Skip(1).Select(w => (w.Lat, w.Lon)));
        Assert.Equal(e18.Zones.Select(z => (z.Id, z.Type)), e21.Zones.Select(z => (z.Id, z.Type)));
        Assert.Equal(("FSB2", LoadingCondition.Full), (e18.Ship.Id, e18.Ship.Loading));
        Assert.Equal(("FSB1", LoadingCondition.Full), (e21.Ship.Id, e21.Ship.Loading));
        static double Depth(Scenario.Scenario sc, string id) => sc.Timeline.Single(t => t.Id == id).Actions.Single(a => a.Kind == "setEnvironment").Payload.GetProperty("waterDepth").GetDouble();
        Assert.Equal(new[] { 7.6, 7.2, 6.6 }, new[] { e21.Environment.WaterDepth, Depth(e21, "companyPressure"), Depth(e21, "tideWindowClosing") });
        Assert.Equal(new[] { 8.9, 8.6, 8.4 }, new[] { e18.Environment.WaterDepth, Depth(e18, "tideLower"), Depth(e18, "barShoaling") });
        // 只記錄故障 cargoHeatingFailure 以時間表項目 + 訊息表示(不注入,--validate 無警告)
        Assert.Contains(e21.Timeline, t => t.Id == "cargoHeatingFailure" && t.Actions.All(a => a.Kind is "voiceScript" or "message"));
    }

    /// <summary>
    /// 無人操作(保持 FAH 約 9.5 kn):T+2 熱油鍋爐故障 → T+5 修復時間不定 → T+10 degraded(公司催促、潮位 7.6 → 7.2 m)→ 通過 OUTER emergency(潮窗將過、→ 6.6 m)→
    /// 到達迴船池;航道超速(fail)、偏出走廊,決策類必要事件全部逾時。
    /// </summary>
    [Fact]
    public void E21_NoOperatorEscalatesInOrder()
    {
        var (e, ev, path) = Run(E21, 3900);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        AssertEscalation(ev, 600, 899);
        Assert.Equal(ev.First(x => x.Kind == SimEventKinds.CheckpointPassed).T, T(ev, "tideWindowClosing"));
        Assert.Equal(120.0, T(ev, "cargoHeatingFailure"));
        Assert.Equal(300.0, T(ev, "repairUncertain"));
        var temps = ev.Where(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "cargoTemp").Select(x => x.T).ToList();
        Assert.Equal(120.0, temps[0]);
        Assert.All(temps.Zip(temps.Skip(1)), p => Assert.Equal(300.0, p.Second - p.First));
        Assert.Equal(2, ev.Count(x => x.Kind == SimEventKinds.VoiceScript && x.ItemId == "tideWindowClosing"));
        Assert.Empty(e.BuildState().Faults ?? Array.Empty<string>());               // 只記錄故障未注入
        Assert.Equal(2264.0, T(ev, "arrived"));
        Assert.All(s.Assessment.Required, r => Assert.Equal("missed", r.State));
        Assert.Equal("fail", Band(s, "speed").State);
        Assert.Equal("fail", Band(s, "xtdMax").State);
        Assert.False(s.Assessment.Passed);
        AssertReplays(path!);
    }

    /// <summary>
    /// 學員腳本(合理操作,安全優先):交班檢查表 → 鍋爐故障通知船長與公司 → 對公司「全速、不管速限」的指示提出質疑 → 航道外端前減至 SAH(航道速限內)→
    /// 潮窗將過時決定依計畫航速進港(UKC 足夠,低於政策即放棄錨泊)→ 循跡 → 到達迴船池。固定:必要事件全部 met、無硬性失敗、容許帶皆 ok。
    /// </summary>
    [Fact]
    public void E21_ScriptedSafetyFirstUnderPressure()
    {
        var keeper = new TrackKeeper(77);
        double? heating = null, pressure = null, window = null;
        var (e, ev, path) = Run(E21, 3900, (eng, events, t) =>
        {
            if (t == 20) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            heating ??= FirstT(events, SimEventKinds.TimelineTriggered, "cargoHeatingFailure");
            pressure ??= FirstT(events, SimEventKinds.TimelineTriggered, "companyPressure");
            window ??= FirstT(events, SimEventKinds.TimelineTriggered, "tideWindowClosing");
            if (heating is { } h)
            {
                if (t == (int)h + 30) eng.Enqueue(SimCommand.Acknowledge("callMaster", note: "熱油鍋爐跳脫"));
                if (t == (int)h + 90) eng.Enqueue(SimCommand.Acknowledge("notifyCompany", note: "鍋爐故障、貨溫 150 °C、修復時間不定"));
            }
            if (pressure is { } p && t == (int)p + 60)
                eng.Enqueue(SimCommand.Acknowledge("verbal", note: "向公司提出質疑:不違反航道速限與 UKC 政策;依計畫航速可在潮窗內進港,否則錨泊等候,請 DPA 確認"));
            if (t == 450) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));       // 航道外端前減至速限以內(約 6 kn)
            if (window is { } w && t == (int)w + 60)
                eng.Enqueue(SimCommand.Acknowledge("verbal", note: "依計畫 SAH 進港:BAR 預估 UKC 約 0.8 m;低於 0.57 m 即放棄並錨泊"));
            keeper.Step(eng, t);
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.All(s.Assessment.Bands, b => Assert.Equal("ok", b.State));
        Assert.Equal("arrived", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.Equal(100.0, s.Assessment.Score);
        AssertReplays(path!);
    }
}
