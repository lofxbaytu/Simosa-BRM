using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Geo;
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
/// 情境庫 E02–E04/E06 審查修正的回歸測試(docs/design/scenario-library.md 第 7.1 節「審查修正」):
/// 以合理的學員/引水人腳本(與主控程序相同的 MMG 動力學、每秒迴圈與 <see cref="ScenarioLibraryTests"/> 相同)固定
/// E06 出港船左舷對左舷且不擦過防波堤、E06 依簡報計畫進港可及格、E03 對 R2 的合理應變不被 minCpa 容許帶判 fail、
/// E03 以安全速度操作時仍聽得到 R1 霧號、E02 以自動舵改向的直航船行動可由教官確認、E04 先確認下風舷再登船仍算數、
/// E04 引水船最後停在自船左舷後方。
/// </summary>
public class ScenarioLibraryReviewTests
{
    private readonly ITestOutputHelper _out;
    public ScenarioLibraryReviewTests(ITestOutputHelper output) => _out = output;

    private const string E02 = "E02_coastal_traffic_strait";
    private const string E03 = "E03_restricted_visibility_strait";
    private const string E04 = "E04_pilot_boarding_kaohsiung";
    private const string E06Flood = "E06_taichung_channel_flood";
    private const string E06Ebb = "E06_taichung_channel_ebb";

    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));
    private static Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg => (s, l) => MmgDynamics.Load(TestData.Root, s, l);

    /// <summary>每秒呼叫 <paramref name="script"/>(t − 1 s 送入的指令於該步套用),之後呼叫 <paramref name="probe"/>(取樣);<paramref name="record"/> 時同時錄製。</summary>
    private static (SimulationEngine Engine, List<SimEvent> Events, string? Path) Run(string id, double endS, Action<SimulationEngine, List<SimEvent>, int>? script = null,
        Action<SimulationEngine, int>? probe = null, bool record = false)
    {
        var sc = Load(id);
        var e = new SimulationEngine(TestData.Ship(sc.Ship.Id), sc, Mmg);
        var events = TestData.Capture(e);
        string? path = null;
        RecordWriter? w = null;
        if (record)
        {
            path = Path.Combine(TestData.TestRecordsDirectory, $"{id}-review-{Guid.NewGuid():N}.jsonl");
            w = new RecordWriter(path, e);
        }
        for (var t = 1; t <= endS && !e.Ended; t++)
        {
            script?.Invoke(e, events, t);
            var target = (long)Math.Round(t / e.Dt);
            while (e.Tick < target && !e.Ended) e.Step();
            probe?.Invoke(e, t);
        }
        w?.Close();
        return (e, events, path);
    }

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

    private void Dump(List<SimEvent> events, AssessmentSummary s)
    {
        foreach (var x in events.Where(x => x.Kind is not (TrafficEventKinds.CpaAlarm or TrafficEventKinds.CpaClear or TrafficEventKinds.Sound or SimEventKinds.Message or SimEventKinds.VoiceScript)))
            _out.WriteLine($"{x.T:F1} {x.Source}/{x.Kind} {x.ItemId} {x.TargetId} {x.Message}");
        foreach (var r in s.Assessment.Required) _out.WriteLine($"required {r.Id}: {r.State} dt={r.DtS} by={r.By}");
        foreach (var b in s.Assessment.Bands) _out.WriteLine($"band {b.Id}: {b.State} value={b.Value}");
        foreach (var h in s.Assessment.HardFails) _out.WriteLine($"hardFail {h.Id} @ {h.TS}");
        foreach (var c in s.CollisionAvoidance.Targets) _out.WriteLine($"enc {c.TargetId} det={c.DetectedAtS} act={c.FirstActionAtS} minRange={c.MinRangeNm:F3} dC={c.CourseChangeDeg:F0} dV={c.SpeedChangeKn:F1}");
        _out.WriteLine($"score {s.Assessment.Score} passed {s.Assessment.Passed}");
    }

    private static double Param(string key) => TestData.Parameters().Parameters[key].NumberValue!.Value;
    private static RequiredResult Req(AssessmentSummary s, string id) => s.Assessment.Required.Single(r => r.Id == id);
    private static BandResult Band(AssessmentSummary s, string id) => s.Assessment.Bands.Single(r => r.Id == id);
    private static double? FirstT(List<SimEvent> ev, string kind, string? itemId = null, string? targetId = null)
        => ev.FirstOrDefault(x => x.Kind == kind && (itemId is null || x.ItemId == itemId) && (targetId is null || x.TargetId == targetId))?.T;

    // ------------------------------------------------------------------ E06

    private static readonly double Ux = Math.Sin(Units.DegToRad(125));
    private static readonly double Uy = Math.Cos(Units.DegToRad(125));
    /// <summary>相對 WP2(情境原點 = 口門中心)的沿航道距離(正 = 港內)與 XTD(正 = 進港船右側,即航道南側)。</summary>
    private static (double Along, double Xtd) Channel(double x, double y) => (x * Ux + y * Uy, x * Uy - y * Ux);

    /// <summary>
    /// 引水人腳本(同 ScenarioLibraryTests.E06_ScriptedCrabbingThroughEntrance):每 10 s 以偏航角估計 + XTD 修正下自動舵航向;
    /// <paramref name="offsetM"/> 為期望 XTD(正 = 靠航道右側,第 9 條);出港船通報後 15 s 確認會船計畫,過口門 5 s 後 SAH;
    /// <paramref name="dsahAlongM"/> 有值時,口門內沿航道超過該距離再減至 DSAH(迴船池前減速)。
    /// </summary>
    private static Action<SimulationEngine, List<SimEvent>, int> E06Pilot(double offsetM, double? dsahAlongM = null)
    {
        double? outbound = null, inside = null;
        var slowed = false;
        double px = double.NaN, py = double.NaN;
        return (eng, events, t) =>
        {
            if (t == 20) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            if (events.Any(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "leadingLightsObscured") && !events.Any(x => x.Kind == SimEventKinds.Acknowledge && x.DataString("kind") == "report"))
                eng.Enqueue(SimCommand.Acknowledge("report", note: "導燈被遮蔽,改用雷達平行指標"));
            outbound ??= FirstT(events, SimEventKinds.TimelineTriggered, "outboundInChannel");
            inside ??= FirstT(events, SimEventKinds.TimelineTriggered, "insideBreakwater");
            if (outbound is { } o && t == (int)o + 15) eng.Enqueue(SimCommand.Acknowledge("verbal", note: "與出港船左舷對左舷,靠航道右側"));
            if (inside is { } i && t == (int)i + 5) eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH));
            var m = eng.Motion;
            if (dsahAlongM is { } da && !slowed && inside is not null && Channel(m.X, m.Y).Along > da)
            {
                eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH));
                slowed = true;
            }
            if (t % 10 != 0) return;
            if (!double.IsNaN(px))
            {
                var cog = Math.Atan2(m.X - px, m.Y - py);
                var drift = Units.RadToDeg(Units.WrapRadPi(m.Psi - cog));
                var xtd = Channel(m.X, m.Y).Xtd;
                var cmd = 125 + drift - Math.Clamp(0.2 * (xtd - offsetM), -20, 20);
                eng.Enqueue(SimCommand.Autopilot(true, Math.Round(Units.NormalizeHeadingDeg(cmd))));
            }
            px = m.X; py = m.Y;
        };
    }

    /// <summary>
    /// 審查發現 1:OUT1(waypoints 模式)不得被均勻流壓離「中心線北側、左舷對左舷」的設計航跡。自船依第 9 條靠右 40 m 或沿中心線時:
    /// 不碰撞、minCpa 容許帶 ok(≥ navigation.cpa.channel_nm);最近距離時 OUT1 在自船左舷且位於中心線北側;OUT1 船中心與防波堤多邊形保持 &gt; 1 倍船寬。
    /// </summary>
    [Theory]
    [InlineData(E06Ebb, 40.0)]
    [InlineData(E06Ebb, 0.0)]
    [InlineData(E06Flood, 40.0)]
    [InlineData(E06Flood, 0.0)]
    public void E06_OutboundPassesPortToPortAndClearsBreakwaters(string id, double offsetM)
    {
        var minRange = double.MaxValue;
        (double OwnXtd, double OutXtd, double T) atCpa = default;
        var minBreakwater = double.MaxValue;
        var minBreakwaterT = 0;
        var outXtdInChannel = new List<double>();
        List<(double X, double Y)[]>? polys = null;
        var (e, ev, _) = Run(id, 1800, E06Pilot(offsetM), (eng, t) =>
        {
            polys ??= eng.Scenario.Zones.Where(z => z.Id.EndsWith("Breakwater", StringComparison.Ordinal))
                .Select(z => z.Polygon.Select(p => eng.Projection.ToLocal(p.Lat!.Value, p.Lon!.Value)).ToArray()).ToList();
            if (eng.Traffic.Find("OUT1") is not { Active: true } o) return;
            var m = eng.Motion;
            var r = Math.Sqrt((o.X - m.X) * (o.X - m.X) + (o.Y - m.Y) * (o.Y - m.Y));
            if (r < minRange) { minRange = r; atCpa = (Channel(m.X, m.Y).Xtd, Channel(o.X, o.Y).Xtd, t); }
            var (along, xtd) = Channel(o.X, o.Y);
            if (along > -1500 && along < 1200) outXtdInChannel.Add(xtd);
            foreach (var p in polys)
            {
                var d = Polygon2D.Contains(p, o.X, o.Y) ? -Polygon2D.DistanceToBoundary(p, o.X, o.Y) : Polygon2D.DistanceToBoundary(p, o.X, o.Y);
                if (d < minBreakwater) { minBreakwater = d; minBreakwaterT = t; }
            }
        });
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        _out.WriteLine($"minRange {minRange:F0} m @ {atCpa.T}: own XTD {atCpa.OwnXtd:F0}, OUT1 XTD {atCpa.OutXtd:F0}; OUT1 XTD {outXtdInChannel.Min():F0}..{outXtdInChannel.Max():F0}; breakwater {minBreakwater:F1} m @ {minBreakwaterT}");
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.Equal("arrived", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.Equal("ok", Band(s, "minCpa").State);
        Assert.True(s.CollisionAvoidance.MinCpaNm >= Param("navigation.cpa.channel_nm"), $"minCpa {s.CollisionAvoidance.MinCpaNm:F3} nm");
        Assert.True(atCpa.OutXtd < atCpa.OwnXtd - 80, $"最近距離時 OUT1 XTD {atCpa.OutXtd:F0} m、自船 {atCpa.OwnXtd:F0} m");
        Assert.All(outXtdInChannel, x => Assert.InRange(x, -150.0, -70.0));
        var beam = e.Traffic.Find("OUT1")!.Spec.Beam;
        Assert.True(minBreakwater > beam, $"OUT1 距防波堤 {minBreakwater:F1} m @ {minBreakwaterT}");
    }

    /// <summary>
    /// 減速區與出港船設計航跡的示意區域檔(data/areas/taichung-inner-harbour.json)與兩個版本一致:zones[innerSpeedLimit] 多邊形相同、
    /// OUT1 起點與流壓消失後的航點在 outboundTrack 上、OUT1 對水航向為各版本的橫流修正角(對地航跡 305°)。
    /// </summary>
    [Theory]
    [InlineData(E06Flood, 85.0, 2.5)]
    [InlineData(E06Ebb, 265.0, 3.0)]
    public void E06_InnerSpeedZoneAndOutboundTrackMatchAreaFile(string id, double setDeg, double driftKn)
    {
        static (double, double) LL(System.Text.Json.JsonElement p) => (Math.Round(p.GetProperty("lat").GetDouble(), 5), Math.Round(p.GetProperty("lon").GetDouble(), 5));
        var area = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "data", "areas", "taichung-inner-harbour.json"))).RootElement;
        Assert.Contains("示意", area.GetProperty("source").GetString());
        Assert.Contains("待 ENC 校正", area.GetProperty("description").GetString());
        var sc = Load(id);
        var zone = sc.Zones.Single(z => z.Id == "innerSpeedLimit");
        var az = area.GetProperty("speedLimitZones")[0];
        Assert.Equal(ZoneType.SpeedLimit, zone.Type);
        Assert.Equal(az.GetProperty("speedLimit_kn").GetDouble(), zone.SpeedLimitKn);
        Assert.Equal(az.GetProperty("polygon").EnumerateArray().Select(LL), zone.Polygon.Select(p => (Math.Round(p.Lat!.Value, 5), Math.Round(p.Lon!.Value, 5))));
        Assert.Equal(TestData.Parameters().Parameters["port.taichung.channelSpeedLimit_kn"].NumberValue, sc.Route!.Waypoints.Single(w => w.Name == "WP3").SpeedLimitKn);
        var track = area.GetProperty("outboundTrack").GetProperty("points").EnumerateArray().Select(LL).ToList();
        var out1 = sc.Targets.Single(t => t.Id == "OUT1");
        Assert.Equal(track[0], (out1.Initial.Position!.Lat!.Value, out1.Initial.Position.Lon!.Value));
        var sw = sc.Timeline.Single(t => t.Id == "insideBreakwater").Actions.Single(a => a.Kind == "targetControl").Payload;
        Assert.Equal("OUT1", sw.GetProperty("id").GetString());
        var wps = sw.GetProperty("behaviour").GetProperty("waypoints").EnumerateArray().Select(LL).ToList();
        Assert.Equal(track.Skip(2), wps);
        // 對水航向 h:7 kn·sin(h − 305°) 抵銷橫流 drift·sin(set − 305°)
        var cross = driftKn * Math.Sin(Units.DegToRad(setDeg - 305));
        var h = 305 - Units.RadToDeg(Math.Asin(cross / 7.0));
        Assert.Equal(TargetBehaviourMode.Scripted, out1.Behaviour.Mode);
        Assert.Equal(Math.Round(h), out1.Behaviour.Script![0].Heading!.Value, 0);
        Assert.Equal(Math.Round(h), out1.Initial.Heading, 0);
    }

    /// <summary>
    /// 審查發現 2:依簡報計畫(漲潮 HAH、退潮 FAH 接近,過口門後 SAH)的引水人腳本必須及格,且 speed 容許帶不為 fail(內港速限從防波堤內的減速區起算,
    /// 不是從口門起算);過口門後再於口門內 400 m 減至 DSAH 的計畫,speed 容許帶須為 ok。同時固定 minCpa 容許帶狀態。
    /// </summary>
    [Theory]
    [InlineData(E06Flood, null, "ok")]
    [InlineData(E06Ebb, null, "warn")]
    [InlineData(E06Flood, 400.0, "ok")]
    [InlineData(E06Ebb, 400.0, "ok")]
    public void E06_BriefedPlanPassesWithSpeedBand(string id, double? dsahAlongM, string speedState)
    {
        var (e, ev, path) = Run(id, 1800, E06Pilot(0, dsahAlongM), record: dsahAlongM is null);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.Equal("arrived", ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).ItemId);
        Assert.False(s.Assessment.HardFailed);
        Assert.Equal(speedState, Band(s, "speed").State);
        Assert.Equal("zone:innerSpeedLimit", s.Manoeuvring.SpeedLimitSource ?? "zone:innerSpeedLimit");
        Assert.Equal("ok", Band(s, "minCpa").State);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.True(s.Assessment.Passed, $"score {s.Assessment.Score}");
        if (path is not null) AssertReplays(path);
    }

    // ------------------------------------------------------------------ E03

    /// <summary>
    /// E03 學員腳本:交班檢查表、t = 60 減速(<paramref name="initial"/>)、R1 會遇開始後 30 s 瞭望報告、進霧區通知船長與檢查表;
    /// R2 出現 <paramref name="delay"/> s 後下達 <paramref name="telegraph"/> 與自動舵 <paramref name="heading"/>,900 s 後回 SAH、020。
    /// </summary>
    private static Action<SimulationEngine, List<SimEvent>, int> E03Script(int delay, TelegraphOrder telegraph, double heading, TelegraphOrder initial = TelegraphOrder.SAH)
    {
        double? r1 = null, fog = null, r2 = null;
        return (eng, events, t) =>
        {
            if (t == 30) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            if (t == 60) eng.Enqueue(SimCommand.Telegraph(initial));
            r1 ??= FirstT(events, SimEventKinds.EncounterStarted, targetId: "R1");
            fog ??= FirstT(events, SimEventKinds.TimelineTriggered, "fogThickens");
            r2 ??= FirstT(events, TrafficEventKinds.Activated, targetId: "R2");
            if (r1 is { } a && t == (int)a + 30) eng.Enqueue(SimCommand.Acknowledge("report", role: "lookout", note: "R1 雷達回波,無 AIS"));
            if (fog is { } f)
            {
                if (t == (int)f + 20) eng.Enqueue(SimCommand.Acknowledge("callMaster"));
                if (t == (int)f + 60) eng.Enqueue(SimCommand.Acknowledge("checklist", note: "霧號、主機備便、加派瞭望、手操舵"));
            }
            if (r2 is { } b)
            {
                if (t == (int)b + delay) { eng.Enqueue(SimCommand.Telegraph(telegraph)); eng.Enqueue(SimCommand.Autopilot(true, heading)); }
                if (t == (int)b + 900) { eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAH)); eng.Enqueue(SimCommand.Autopilot(true, 20)); }
            }
        };
    }

    /// <summary>
    /// 審查發現 3:R2 近距離出現並轉向自船;依第 19 條 (d)(e) 的合理應變(減至最低舵效速度、停俥、向右大幅避讓)不得被 minCpa 容許帶判 fail,
    /// 也不得逼近硬性失敗(cpaHard)。停俥(第 19 條 (e) 必要時完全停止前進)須與避讓同等不扣 fail。
    /// </summary>
    [Theory]
    [InlineData(25, TelegraphOrder.DSAH, 80.0)]     // 作者腳本的主要動作:DSAH + 向右 060°
    [InlineData(10, TelegraphOrder.STOP, 20.0)]     // 第 19 條 (e):停俥、保持航向
    [InlineData(10, TelegraphOrder.DSAH, 110.0)]
    [InlineData(10, TelegraphOrder.STOP, 110.0)]
    [InlineData(10, TelegraphOrder.DSAH, 20.0)]     // 只減速
    public void E03_ReasonableResponsesToR2DoNotFailMinCpa(int delay, TelegraphOrder telegraph, double heading)
    {
        var (e, ev, _) = Run(E03, 3600, E03Script(delay, telegraph, heading));
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var r2 = s.CollisionAvoidance.Targets.First(c => c.TargetId == "R2");
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
        Assert.True(r2.MinRangeNm >= Param("navigation.cpa.coastal_nm"), $"R2 最近距離 {r2.MinRangeNm:F3} nm");
        Assert.NotEqual("fail", Band(s, "minCpa").State);
        Assert.Equal("met", Req(s, "closeQuartersAction").State);
    }

    /// <summary>
    /// 審查發現 6:R1(無 AIS 漁船,一長二短霧號,第 35 條 (c))須位於航段 1(霧區 degraded);以安全速度操作時 R1 霧號至少鳴放一次,
    /// 「聽到 R1 霧號」在 denseFog(航段 2)之前觸發。
    /// </summary>
    [Theory]
    [InlineData(25, TelegraphOrder.DSAH, 80.0, TelegraphOrder.SAH)]
    [InlineData(10, TelegraphOrder.STOP, 20.0, TelegraphOrder.DSAH)]
    [InlineData(10, TelegraphOrder.DSAH, 40.0, TelegraphOrder.HAH)]
    public void E03_R1FogSignalHeardAtSafeSpeed(int delay, TelegraphOrder telegraph, double heading, TelegraphOrder initial)
    {
        var (e, ev, _) = Run(E03, 3600, E03Script(delay, telegraph, heading, initial));
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var heard = FirstT(ev, SimEventKinds.TimelineTriggered, "r1FogSignalHeard");
        var dense = FirstT(ev, SimEventKinds.TimelineTriggered, "denseFog");
        Assert.NotNull(heard);
        Assert.True(dense is null || heard < dense, $"heard {heard} dense {dense}");
        Assert.True(ev.Count(x => x.Kind == TrafficEventKinds.Sound && x.TargetId == "R1" && x.Message == "prolongedTwoShort") >= 1);
        Assert.Equal("met", Req(s, "lookoutReportR1").State);
        Assert.False(s.Assessment.HardFailed, string.Join(",", s.Assessment.HardFails.Select(h => h.Id)));
    }

    // ------------------------------------------------------------------ E02

    /// <summary>
    /// 審查發現 4:直航船在自動舵下以改設定航向向右改向(第 17 條 (a)(ii)),核心目前不把 autopilotHeading 列為 expect;
    /// 情境改為接受教官的「口頭」確認(acknowledge verbal),教官提示寫明此用法。腳本同作者 E02 腳本,只把 t = 620 的 HAH 換成自動舵 020°(右轉 30°)。
    /// </summary>
    [Fact]
    public void E02_StandOnAutopilotAlterationCountsWithInstructorConfirmation()
    {
        var reported = false;
        var (e, ev, path) = Run(E02, 3400, (eng, events, t) =>
        {
            switch (t)
            {
                case 30: eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case 90: eng.Enqueue(SimCommand.Autopilot(true, 60)); break;
                case 420: eng.Enqueue(SimCommand.Autopilot(true, 350)); break;
                case 610: eng.Enqueue(SimCommand.Acknowledge("callMaster")); break;
                case 620: eng.Enqueue(SimCommand.Autopilot(true, 20)); break;      // 直航船以自動舵向右改向 30°
                case 630: eng.Enqueue(SimCommand.Acknowledge("verbal", note: "直航船行動:自動舵 350 → 020 向右改向")); break;   // 教官確認
                case 1510: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.HAH)); break;
                case 1650: eng.Enqueue(SimCommand.Autopilot(true, 40)); break;
                case 2410: eng.Enqueue(SimCommand.Acknowledge("verbal", note: "VHF 干擾:不同意右舷對右舷")); break;
                case 2550: eng.Enqueue(SimCommand.Autopilot(true, 20)); break;
                case 3150: eng.Enqueue(SimCommand.Autopilot(true, 5)); break;
            }
            if (!reported && events.Any(x => x.Kind == SimEventKinds.ReportPointPassed))
            {
                eng.Enqueue(SimCommand.Acknowledge("report"));
                reported = true;
            }
        }, record: true);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        var r = Req(s, "standOnAction");
        Assert.Equal("met", r.State);
        Assert.Equal("acknowledge:verbal", r.By);
        Assert.InRange(r.DtS!.Value, 25, 35);
        Assert.Equal("met", Req(s, "vhfChallenge").State);
        // 情境提示:教官訊息寫明自動舵改向時以「口頭」確認
        var msg = Load(E02).Timeline.Single(t => t.Id == "crossingNoAction").Actions.Single(a => a.Kind == "message").Payload.GetProperty("text").GetString();
        Assert.Contains("口頭", msg);
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E04

    /// <summary>E04 學員腳本(同 ScenarioLibraryTests.E04_ScriptedApproachAndBoarding 減速版);<paramref name="extra"/> 加入教官動作。</summary>
    private static Action<SimulationEngine, List<SimEvent>, int> E04Script(Action<SimulationEngine, int> extra)
    {
        var reported = false;
        return (eng, events, t) =>
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
                case 3000: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH)); break;
            }
            extra(eng, t);
            if (!reported && events.Any(x => x.Kind == SimEventKinds.ReportPointPassed))
            {
                eng.Enqueue(SimCommand.Acknowledge("report"));
                reported = true;
            }
        };
    }

    /// <summary>
    /// 審查發現 5:實務上常在引水船靠上時先確認引水梯在下風舷,引水人才登船。教官於 pilotOnBoard 前 30 s 按「下風舷確認」時 boardingLee 仍須 met;
    /// 太早確認(登船前超過容許時間)可於登船後再按一次(leeConfirmed 可重複)。
    /// </summary>
    [Theory]
    [InlineData(-30, null)]
    [InlineData(-900, 20)]
    public void E04_LeeConfirmedBeforeBoardingCounts(int leeOffset, int? secondPress)
    {
        const int boardAt = 3800;
        var (e, ev, path) = Run(E04, 4800, E04Script((eng, t) =>
        {
            if (t == boardAt + leeOffset) eng.Enqueue(SimCommand.TriggerTimeline("leeConfirmed"));
            if (t == boardAt) eng.Enqueue(SimCommand.TriggerTimeline("pilotOnBoard"));
            if (secondPress is { } p && t == boardAt + p) eng.Enqueue(SimCommand.TriggerTimeline("leeConfirmed"));
            if (t == boardAt + 300) eng.Enqueue(SimCommand.Acknowledge("checklist", note: "MPX 完成"));
        }), record: leeOffset == -30);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Dump(ev, s);
        Assert.DoesNotContain(ev, x => x.Kind == SimEventKinds.TimelineManual && x.Message.Contains("已觸發過"));
        Assert.Equal("met", Req(s, "boardingLee").State);
        Assert.Equal(100.0, s.Assessment.Score);
        if (path is not null) AssertReplays(path);
    }

    /// <summary>
    /// 審查發現 7:引水船依引水站要求在自船「左舷後方」待命(follow bearing 225°、120 m;站位在跟隨避讓矩形之後,自艏前或右舷接近時也能繞到左舷);
    /// 不按「引水人登船」時,引水船出發 1000 s(T+71)內進站,其後至練習結束都在左舷後方(相對方位 190°–260°)、距離 0.04–0.09 nm。
    /// </summary>
    [Fact]
    public void E04_PilotBoatEndsOnPortQuarter()
    {
        var samples = new List<(int T, double Rel, double RangeNm)>();
        var (e, ev, _) = Run(E04, 4800, E04Script((_, _) => { }), (eng, t) =>
        {
            if (t < 3900 || t % 20 != 0 || eng.Traffic.Find("PILOT") is not { Active: true } p) return;
            var m = eng.Motion;
            var brg = Units.NormalizeHeadingDeg(Units.RadToDeg(Math.Atan2(p.X - m.X, p.Y - m.Y)));
            var rel = Units.NormalizeHeadingDeg(brg - Units.RadToDeg(m.Psi));
            samples.Add((t, rel, Math.Sqrt((p.X - m.X) * (p.X - m.X) + (p.Y - m.Y) * (p.Y - m.Y)) / Units.MetresPerNauticalMile));
        });
        foreach (var x in samples.Where(x => x.T % 100 == 0)) _out.WriteLine($"PILOT t={x.T} rel={x.Rel:F0} r={x.RangeNm:F3}");
        Assert.NotEmpty(samples);
        var settled = samples.Where(x => x.T >= 3300 + 1000).ToList();
        Assert.True(settled.Count > 20);
        Assert.All(settled, x => Assert.InRange(x.Rel, 190.0, 260.0));
        Assert.All(settled, x => Assert.InRange(x.RangeNm, 0.04, 0.09));
        Assert.Equal(4800.0, ev.Single(x => x.Kind == SimEventKinds.ExerciseEnded).T);
    }
}
