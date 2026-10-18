using System.Text.Json;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Physics.Mmg;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;
using SimosaBRM.SimCore.Traffic;
using Xunit;
using Xunit.Abstractions;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 情境庫課程後段(E15/E16/E17/E21)審查發現的回歸測試(docs/design/scenario-library.md 第 15 節「審查修正」;規劃書第 8.1 節設計規則):
/// E16 F1 狀態中確實無燈;E17 留在泊位時風向轉為離岸、纜繩張力真的上升;E21 潮窗過後仍未通過沙洲時 UKC 評估作用;
/// E15 外錨地區域可到達、檔頭時間後備與參數一致。動力學與主控程序相同(MMG、每秒迴圈)。
/// </summary>
public class ScenarioLibraryLateCourseReviewFixTests
{
    private readonly ITestOutputHelper _out;
    public ScenarioLibraryLateCourseReviewFixTests(ITestOutputHelper output) => _out = output;

    private const string E15 = "E15_distraction_workload_kaohsiung";
    private const string E16 = "E16_night_approach_keelung";
    private const string E17 = "E17_typhoon_departure_kaohsiung";
    private const string E21 = "E21_cargo_heating_pressure_anping";

    private static string ScenarioPath(string id) => Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml");
    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(ScenarioPath(id));
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static double Param(string key) => TestData.Parameters().Parameters[key].NumberValue!.Value;
    private static double? FirstT(List<SimEvent> ev, string kind, string? itemId = null)
        => ev.FirstOrDefault(x => x.Kind == kind && (itemId is null || x.ItemId == itemId))?.T;
    private static BandResult Band(AssessmentSummary s, string id) => s.Assessment.Bands.Single(r => r.Id == id);

    /// <summary>每秒呼叫 <paramref name="script"/>(與 ScenarioLibraryLateCourseTests.Run 相同;不錄製)。</summary>
    private static (SimulationEngine Engine, List<SimEvent> Events) Run(string id, double endS, Action<SimulationEngine, List<SimEvent>, int>? script = null,
        Action<SimulationEngine, int>? observe = null)
    {
        var sc = Load(id);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var events = TestData.Capture(e);
        for (var t = 1; t <= endS && !e.Ended; t++)
        {
            script?.Invoke(e, events, t);
            var target = (long)Math.Round(t / e.Dt);
            while (e.Tick < target && !e.Ended) e.Step();
            observe?.Invoke(e, t);
        }
        return (e, events);
    }

    private void Dump(List<SimEvent> events, AssessmentSummary s)
    {
        foreach (var x in events.Where(x => x.Kind is SimEventKinds.TimelineTriggered or SimEventKinds.PhaseChanged or SimEventKinds.ExerciseEnded))
            _out.WriteLine($"{x.T:F1} {x.Kind} {x.ItemId} {x.Message}");
        foreach (var b in s.Assessment.Bands) _out.WriteLine($"band {b.Id}: {b.State} value={b.Value}");
        foreach (var h in s.Assessment.HardFails) _out.WriteLine($"hardFail {h.Id} @ {h.TS}");
        _out.WriteLine($"ukc min={s.Ukc.MinM:F2} below={s.Ukc.TimeBelowLimitS} score {s.Assessment.Score}");
    }

    // ------------------------------------------------------------------ E16

    /// <summary>F1「無燈無 AIS」:啟動(雷達出現)時以 targetControl 關燈,狀態/紀錄中的燈號為 none(不再依 shipType 推定為 fishing)。</summary>
    [Fact]
    public void E16_F1IsUnlitAndWithoutAisOnceActivated()
    {
        string? lights = null;
        bool? ais = null;
        var (_, ev) = Run(E16, 600, observe: (e, t) =>
        {
            if (t != 600) return;
            var f1 = e.BuildState().Targets!.Single(x => x.Id == "F1");
            lights = f1.Lights;
            ais = f1.AisOn;
        });
        Assert.Equal(534.0, ev.Single(x => x.Kind == TrafficEventKinds.Activated && x.TargetId == "F1").T);   // 啟動時刻不變
        Assert.Equal(534.0, ev.Single(x => x.Kind == TrafficEventKinds.Control && x.TargetId == "F1").T);
        Assert.Equal("none", lights);
        Assert.False(ais);
    }

    // ------------------------------------------------------------------ E17

    /// <summary>
    /// 留在泊位(無人操作):外圍環流強風(吹攏)漸強完成後,颱風接近、風向轉為離岸(windVeer);4 纜張力上升超過工作負荷限值(50 % MBL,lineTension 容許帶 warn),
    /// 未斷纜。外圍環流期間仍為吹攏風(強風中離泊的碼頭硬接觸風險不變)。
    /// </summary>
    [Fact]
    public void E17_StayingAlongsideLoadsLinesWhenWindVeersOffshore()
    {
        double dirAt2100 = double.NaN, dirAtEnd = double.NaN;
        var (e, ev) = Run(E17, 2700, observe: (eng, t) =>
        {
            if (t == 2100) dirAt2100 = Units.NormalizeHeadingDeg(Units.RadToDeg(eng.Environment.WindTrueDirFromRad));
            if (t == 2700) dirAtEnd = Units.NormalizeHeadingDeg(Units.RadToDeg(eng.Environment.WindTrueDirFromRad));
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.Equal(1500.0, FirstT(ev, SimEventKinds.TimelineTriggered, "outerBands"));
        Assert.Equal(2101.0, FirstT(ev, SimEventKinds.TimelineTriggered, "windVeer"));
        Assert.Equal(250.0, dirAt2100, 3);                                                   // 外圍環流期間吹攏(水側 → 碼頭)
        Assert.InRange(dirAtEnd, 90.0, 130.0);                                               // 練習結束時已轉為離岸(碼頭 → 水側)
        Assert.Equal(40.0, Units.MpsToKn(e.Environment.WindTrueSpeedMps), 6);
        var b = s.Berthing!;
        Assert.Equal(0, b.LineBreaks);
        Assert.True(b.MaxLineTensionRatio > 0.5, $"maxLineTensionRatio {b.MaxLineTensionRatio:F3}");
        Assert.Equal("warn", Band(s, "lineTension").State);
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "clearOfBerth");
        // 檔頭不再宣稱「吹攏風下纜繩張力上升」
        var header = string.Join("\n", File.ReadLines(ScenarioPath(E17)).TakeWhile(l => l.StartsWith('#')));
        Assert.DoesNotContain("40 kn 吹攏風下纜繩張力上升", header);
        Assert.Contains("windVeer", header);
    }

    // ------------------------------------------------------------------ E21

    /// <summary>E21 合理操作的共通部分(與 ScenarioLibraryLateCourseTests.E21_ScriptedSafetyFirstUnderPressure 相同的通報/質疑/減速);<paramref name="waitThenResumeAt"/> 時於潮窗將過 60 s 後停俥等候、該秒恢復 SAH。</summary>
    private static Action<SimulationEngine, List<SimEvent>, int> E21Script(int? stopAt = null, int? resumeAt = null, bool stopAfterWindow = false)
    {
        double? heating = null, pressure = null, window = null;
        var lastSet = 77.0;
        return (eng, events, t) =>
        {
            if (t == 20) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            heating ??= FirstT(events, SimEventKinds.TimelineTriggered, "cargoHeatingFailure");
            pressure ??= FirstT(events, SimEventKinds.TimelineTriggered, "companyPressure");
            window ??= FirstT(events, SimEventKinds.TimelineTriggered, "tideWindowClosing");
            if (heating is { } h)
            {
                if (t == (int)h + 30) eng.Enqueue(SimCommand.Acknowledge("callMaster", note: "熱油鍋爐跳脫"));
                if (t == (int)h + 90) eng.Enqueue(SimCommand.Acknowledge("notifyCompany", note: "鍋爐故障、修復時間不定"));
            }
            if (pressure is { } p && t == (int)p + 60) eng.Enqueue(SimCommand.Acknowledge("verbal", note: "質疑公司指示:不違反航道速限與 UKC 政策"));
            if (stopAt is { } s0 && t == s0) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
            else if (stopAt is null && t == 450) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
            if (window is { } w)
            {
                if (t == (int)w + 60) eng.Enqueue(SimCommand.Acknowledge("verbal", note: "潮窗將過:決定"));
                if (stopAfterWindow && t == (int)w + 60) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP));
            }
            if (resumeAt is { } r && t == r) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
            // 循跡(簡化:每 20 s 以航段航向 + 偏航角 − 0.12 × XTD,每次 ≤ 4°)
            if (t % 20 == 0)
            {
                var st = eng.BuildState();
                if (st.Route is { LegCourseDeg: { } leg, XtdM: { } xtd } && st.Sog >= 1)
                {
                    var drift = Units.RadToDeg(Units.WrapRadPi(Units.DegToRad(st.Heading - st.Cog)));
                    var cmd = leg + drift - Math.Clamp(0.12 * xtd, -15, 15);
                    var delta = Math.Clamp(Units.RadToDeg(Units.WrapRadPi(Units.DegToRad(cmd - lastSet))), -4, 4);
                    if (Math.Abs(delta) >= 1)
                    {
                        lastSet = Math.Round(Units.NormalizeHeadingDeg(lastSet + delta));
                        eng.Enqueue(SimCommand.Autopilot(true, lastSet));
                    }
                }
            }
        };
    }

    /// <summary>
    /// 延誤:潮窗將過後停俥等候,超過港控所稱的約 30 分鐘才恢復 SAH 進港;仍在沙洲(BAR)外側的航道內 → windowClosed(潮位 6.6 → 6.2 m),
    /// 通過沙洲時 UKC 低於政策(ukcTime/ukcMin 至少 warn)。
    /// </summary>
    [Fact]
    public void E21_DelayedPassageAfterWindowClosesBreachesUkcPolicy()
    {
        var (e, ev) = Run(E21, 3900, E21Script(resumeAt: 3000, stopAfterWindow: true));
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var window = FirstT(ev, SimEventKinds.TimelineTriggered, "tideWindowClosing")!.Value;
        var closed = FirstT(ev, SimEventKinds.TimelineTriggered, "windowClosed");
        Assert.NotNull(closed);
        Assert.Equal(window + 1800, closed!.Value);
        Assert.NotEqual("ok", Band(s, "ukcTime").State);
        Assert.NotEqual("ok", Band(s, "ukcMin").State);
        Assert.True(s.Ukc.MinM < Param("navigation.ukc.channel_fraction") * 5.7, $"UKC {s.Ukc.MinM:F2}");
    }

    /// <summary>放棄進港、在航道外錨泊等候(未進入航道):潮窗過後也不觸發 windowClosed,UKC 評估不誤扣(正確決策沒有 UKC 後果)。</summary>
    [Fact]
    public void E21_WaitingOutsideChannelIsNotPenalisedWhenWindowCloses()
    {
        var (e, ev) = Run(E21, 3900, E21Script(stopAt: 300));
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.Equal(Param("cargo.e21.emergencyAt_s"), FirstT(ev, SimEventKinds.TimelineTriggered, "tideWindowClosing"));   // 未到 OUTER:時間後備
        Assert.Null(FirstT(ev, SimEventKinds.TimelineTriggered, "windowClosed"));
        Assert.Equal("ok", Band(s, "ukcTime").State);
        Assert.Equal("ok", Band(s, "ukcMin").State);
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
    }

    // ------------------------------------------------------------------ E15

    private static (double X, double Y) Xy(double lat, double lon, double lat0, double lon0)
        => ((lon - lon0) * 111320.0 * Math.Cos(Units.DegToRad(lat0)), (lat - lat0) * 110574.0);

    private static double SegmentDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var u = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(p.X - (a.X + u * dx), 2) + Math.Pow(p.Y - (a.Y + u * dy), 2));
    }

    /// <summary>
    /// E15 外錨地:anchorageEntered 由 B 區(航線北側、走廊外約 0.2 nm 起;data/areas/kaohsiung-anchorage-b-e15.json)觸發;
    /// 開場即停俥、在漲潮中向西北漂流時會進入(VTS 提醒),無人操作(未修正流壓)時不會進入;簡報的相對位置與區域一致。
    /// </summary>
    [Fact]
    public void E15_AnchorageZoneIsReachableByDriftAndBriefingMatches()
    {
        var sc = Load(E15);
        var b = sc.Zones.Single(z => z.Id == "anchorageB");
        var area = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "data", "areas", "kaohsiung-anchorage-b-e15.json"))).RootElement;
        Assert.Contains("示意", area.GetProperty("source").GetString());
        Assert.Contains("待 ENC 校正", area.GetProperty("description").GetString());
        Assert.False(area.TryGetProperty("land", out _));
        Assert.Equal(area.GetProperty("anchorages")[0].GetProperty("polygon").EnumerateArray().Select(p => (p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble())),
            b.Polygon.Select(p => (p.Lat!.Value, p.Lon!.Value)));
        // 幾何:B 區最近點離航線 = 走廊半寬 + 0.2–0.5 nm
        var (lat0, lon0) = (sc.Route!.Waypoints[0].Lat!.Value, sc.Route.Waypoints[0].Lon!.Value);
        var route = sc.Route.Waypoints.Select(w => Xy(w.Lat!.Value, w.Lon!.Value, lat0, lon0)).ToList();
        var poly = b.Polygon.Select(p => Xy(p.Lat!.Value, p.Lon!.Value, lat0, lon0)).ToList();
        var samples = poly.Zip(poly.Skip(1).Append(poly[0])).SelectMany(e => Enumerable.Range(0, 101).Select(i => (e.First.X + (e.Second.X - e.First.X) * i / 100.0, e.First.Y + (e.Second.Y - e.First.Y) * i / 100.0)));
        var nearest = samples.Min(p => route.Zip(route.Skip(1)).Min(seg => SegmentDistance(p, seg.First, seg.Second)));
        var corridor = Param("navigation.xtd.pilotage_m");
        Assert.InRange(nearest, corridor + 0.2 * 1852 - 5, corridor + 0.5 * 1852);
        // 時間表:B 區(或 A 區)進入時 VTS 提醒
        var trigger = sc.Timeline.Single(t => t.Id == "anchorageEntered");
        Assert.Contains("anchorageB", JsonSerializer.Serialize(trigger.When, ContractJson.Options));
        Assert.Contains("航線北側", sc.Briefing!.Text);
        Assert.Contains("西北", sc.Briefing.Text);

        // 開場 5 s 停俥、隨漲潮漂流:進入 B 區(1800 s 內)
        var (_, ev) = Run(E15, 1800, (eng, _, t) => { if (t == 5) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); });
        var at = FirstT(ev, SimEventKinds.TimelineTriggered, "anchorageEntered");
        _out.WriteLine($"anchorageEntered @ {at}");
        Assert.NotNull(at);
        Assert.InRange(at!.Value, 1200, 1800);
    }

    /// <summary>E15 檔頭的緊急階段時間後備與參數 bridge.e15.emergencyAt_s 一致(T+21,不是 T+18)。</summary>
    [Fact]
    public void E15_HeaderEmergencyFallbackMatchesParameter()
    {
        var header = string.Join("\n", File.ReadLines(ScenarioPath(E15)).TakeWhile(l => l.StartsWith('#')));
        var minutes = (int)(Param("bridge.e15.emergencyAt_s") / 60);
        Assert.Equal(21, minutes);
        Assert.Contains($"T+{minutes} emergency", header);
        Assert.DoesNotContain("T+18 emergency", header);
    }
}
