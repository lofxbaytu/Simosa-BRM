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
/// E12 錨泊/走錨(高雄外錨地)、E13 拖船協助靠泊(麥寮油碼頭)、E14 無拖船離泊(麥寮小型油碼頭)(規劃書第 8.1 節):
/// 載入/驗證無錯誤、參數引用與示意泊位/錨地檔一致;以固定的學員腳本(與主控程序相同的 MMG 動力學)跑出關鍵事件序列與 summary.berthing / anchoring,
/// 紀錄 → 重播雜湊/事件/summary 一致。事件時刻來自時間表(參數集)者以等值固定,物理事件以順序與範圍固定。
/// </summary>
public class BerthingScenarioTests
{
    private readonly ITestOutputHelper _out;
    public BerthingScenarioTests(ITestOutputHelper output) => _out = output;

    private static readonly Func<ShipParticulars, LoadingCondition, IShipDynamics> Mmg = (s, l) => MmgDynamics.Load(TestData.Root, s, l);
    private static Scenario.Scenario Load(string id) => ScenarioLoader.Load(Path.Combine(TestData.Root, "data", "scenarios", id + ".yaml"));

    /// <summary>每秒呼叫 <paramref name="script"/>(引擎時間 t − 1 s 時送入的指令於該步套用);<paramref name="record"/> 時同時錄製並回傳路徑。</summary>
    private static (SimulationEngine Engine, List<SimEvent> Events, string? Path) Run(string id, double endS, Action<SimulationEngine, List<SimEvent>, int> script, bool record = true)
    {
        var e = new SimulationEngine(TestData.Fsb1(), Load(id), Mmg);
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
            script(e, events, t);
            // endExercise(時間表或教官 allFast)凍結引擎:之後不再推進
            var target = (long)Math.Round(t / e.Dt);
            while (e.Tick < target && !e.Ended) e.Step();
        }
        w?.Close();
        return (e, events, path);
    }

    private static void AssertReplays(string path)
    {
        var r = Replayer.Replay(path, TestData.Ship, Mmg);
        Assert.True(r.IntegrityOk);
        Assert.True(r.HashMatches, $"{r.ExpectedHash} vs {r.ActualHash}");
        Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
        Assert.True(r.SummaryMatches);
        Assert.Null(r.Warning);
    }

    private static void NoErrors(Scenario.Scenario sc)
    {
        var issues = ScenarioValidator.Check(sc, TestData.Fsb1());
        Assert.DoesNotContain(issues, i => i.Severity == ScenarioIssueSeverity.Error);
    }

    private void Dump(List<SimEvent> events)
    {
        foreach (var x in events.Where(x => x.Kind is not (TrafficEventKinds.CpaAlarm or TrafficEventKinds.CpaClear)))
            _out.WriteLine($"{x.T:F2} {x.Source}/{x.Kind} {x.ItemId} {x.Message}");
    }

    // ------------------------------------------------------------------ E12

    [Fact]
    public void E12_LoadsWithParametersAndMatchesAnchorageFile()
    {
        var sc = Load("E12_anchoring_kaohsiung");
        Assert.Equal("FSB1", sc.Ship.Id);
        Assert.Equal(LoadingCondition.Full, sc.Ship.Loading);
        Assert.Equal("mud", sc.Environment.Seabed);
        Assert.Equal(0.9, sc.Anchors!.HoldingFactor);
        Assert.Equal(3, sc.Targets.Count);
        Assert.All(sc.Targets, t => Assert.Equal("anchored", ContractJson.EnumName(t.Behaviour.Mode)));
        Assert.Equal(900, sc.Timeline.Single(t => t.Id == "monsoonSurge").When!.AtLeast);
        Assert.True(sc.Timeline.Single(t => t.Id == "engineFailure").IsManual);
        Assert.Equal(new[] { "anchorReady", "broughtUp", "dragResponse", "dragReport" }, sc.Assessment.Required.Select(r => r.Id));
        Assert.Equal(120, sc.Assessment.Required.Single(r => r.Id == "dragResponse").WithinS);
        Assert.Equal("anchorDragging", sc.Assessment.Required.Single(r => r.Id == "dragResponse").After.Event);
        var resolved = ScenarioLoader.LastResolvedParameters;
        Assert.Contains(resolved, r => r.Key == "anchoring.dragResponse_s" && r.Source == "ASSUMED-E12");
        NoErrors(sc);

        // 錨地多邊形與示意錨地檔一致;錨泊船在檔案列出的錨位
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "data", "areas", "kaohsiung-anchorage.json")));
        var anch = doc.RootElement.GetProperty("anchorages")[0];
        Assert.Contains("示意", doc.RootElement.GetProperty("source").GetString());
        var poly = anch.GetProperty("polygon").EnumerateArray().Select(p => (p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble())).ToList();
        Assert.Equal(poly, sc.Zones.Single(z => z.Id == "anchorage").Polygon.Select(p => (p.Lat!.Value, p.Lon!.Value)));
        var positions = anch.GetProperty("positions").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!, p => (p.GetProperty("lat").GetDouble(), p.GetProperty("lon").GetDouble()));
        Assert.Equal(positions["A12"], (sc.Origin!.Lat, sc.Origin.Lon));
        foreach (var t in sc.Targets) Assert.Equal(positions[t.Id], (t.Initial.Position!.Lat!.Value, t.Initial.Position.Lon!.Value));
    }

    /// <summary>
    /// 學員腳本:停俥、錨備便、倒俥減速後放右錨 6 節(165 m = 5.5 倍水深);季風增強後走錨 → 60 s 後鬆鏈至 7 節並回報。
    /// 固定:時間表時刻(參數集)、放錨 → 帶上 → 季風 → 走錨 → 反應的順序、必要事件全部 met、summary.anchoring 與重播一致。
    /// </summary>
    [Fact]
    public void E12_ScriptedAnchoringDragAndResponseReplays()
    {
        double? dragT = null;
        var (e, events, path) = Run("E12_anchoring_kaohsiung", 1800, (eng, ev, t) =>
        {
            switch (t)
            {
                case 2: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 10: eng.Enqueue(SimCommand.Acknowledge("anchorReady")); break;
                case 60: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAS)); break;
                case 150: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 170: eng.Enqueue(SimCommand.Anchor("starboard", "letGo", 6)); break;
            }
            dragT ??= ev.FirstOrDefault(x => x.Kind == SimEventKinds.AnchorDragging)?.T;
            if (dragT is { } d && t == (int)Math.Ceiling(d) + 60)
            {
                eng.Enqueue(SimCommand.Anchor("starboard", "veer", lengthM: 192.5));
                eng.Enqueue(SimCommand.Acknowledge("report"));
            }
        });
        Dump(events);
        Assert.Equal(5.0, events.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "assignAnchorage").T);
        Assert.Equal(900.0, events.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "monsoonSurge").T);
        var letGo = events.Single(x => x.Kind == SimEventKinds.AnchorLetGo);
        Assert.Equal(169.0, letGo.T);
        Assert.Equal(SimEventSources.Instructor, letGo.Source);
        var broughtUp = events.First(x => x.Kind == SimEventKinds.AnchorBroughtUp);
        var drag = events.First(x => x.Kind == SimEventKinds.AnchorDragging);
        Assert.True(letGo.T < broughtUp.T && broughtUp.T < 900.0, $"帶上 {broughtUp.T}");
        Assert.True(drag.T > 900.0, $"季風增強後才走錨:{drag.T}");
        var veer = events.Single(x => x.Kind == SimEventKinds.AnchorCommand && x.DataString("action") == "veer");
        Assert.Equal(Math.Ceiling(drag.T) + 59, veer.T);
        // 走錨 2 分鐘後 VTS 呼叫(dragWarning delay 120 s)
        Assert.Contains(events, x => x.Kind == SimEventKinds.VoiceScript && x.ItemId == "dragWarning" && Math.Abs(x.T - (Math.Ceiling(drag.T) + 120)) <= 1.0);

        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.False(s.Assessment.HardFailed);
        var an = s.Anchoring!;
        var a = an.Anchors.Single();
        Assert.Equal(165.0, a.TargetLengthM);
        Assert.Equal(5.5, a.PlannedScopeRatio!.Value, 9);
        Assert.Equal(165.0 / 30.0, a.ScopeRatio!.Value, 9);
        Assert.Equal(drag.T, an.FirstDragAtS);
        Assert.Equal(veer.T - drag.T, an.DragResponseTimeS!.Value, 9);
        Assert.Equal("event:anchorCommand:veer", an.DragResponseBy);
        Assert.Equal(veer.T, an.DragDetectedAtS);
        Assert.True(an.DragEvents >= 1 && an.DragTimeS > 0);
        Assert.Equal(192.5, a.MaxLengthM, 9);
        Assert.True(an.MaxChainTensionT > 5.0);
        Assert.True(s.CollisionAvoidance.MinCpaNm > 0.15, $"與錨泊船最小距離 {s.CollisionAvoidance.MinCpaNm}");
        Assert.Null(s.Berthing);
        _out.WriteLine(JsonSerializer.Serialize(an, ContractJson.Indented));
        AssertReplays(path!);
    }

    /// <summary>
    /// 鏈長選擇不得反被扣分(簡報:鏈長 ≥ 水深 × 倍數,倍數為下限):與上例相同的接近(放錨時仍有前進速度),放 7 或 9 節時
    /// 季風增強前錨鏈仍未拉緊、物理上的 anchorBroughtUp 要到走錨並重新抓住後才發生;學員依 VTS 指示於錨妥後回報(acknowledge report)
    /// 即完成 broughtUp。未回報者以物理帶上判定、逾時依 latePenaltyPer_s 漸進扣分(不一次扣滿)。
    /// </summary>
    [Theory]
    [InlineData(7, true)]
    [InlineData(9, true)]
    [InlineData(7, false)]
    public void E12_LongerScopeIsNotPenalised(int shackles, bool report)
    {
        var (e, events, _) = Run("E12_anchoring_kaohsiung", 1800, (eng, ev, t) =>
        {
            switch (t)
            {
                case 2: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 10: eng.Enqueue(SimCommand.Acknowledge("anchorReady")); break;
                case 60: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAS)); break;
                case 150: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 170: eng.Enqueue(SimCommand.Anchor("starboard", "letGo", shackles)); break;
                case 600: if (report) eng.Enqueue(SimCommand.Acknowledge("report")); break;
            }
        }, record: false);
        var broughtUp = events.FirstOrDefault(x => x.Kind == SimEventKinds.AnchorBroughtUp);
        Assert.True(broughtUp is null || broughtUp.T > 900.0, $"本接近方式下長鏈長於季風前不會帶上:{broughtUp?.T}");
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        var r = s.Assessment.Required.Single(x => x.Id == "broughtUp");
        if (report)
        {
            Assert.Equal("met", r.State);
            Assert.Equal(0.0, r.Penalty);
        }
        else
        {
            Assert.Equal("late", r.State);
            Assert.True(r.Penalty > 0 && r.Penalty < 15.0, $"逾時漸進扣分:{r.Penalty}");
        }
    }

    // ------------------------------------------------------------------ E13

    [Fact]
    public void E13_LoadsWithTugsBerthAndParameters()
    {
        var sc = Load("E13_tug_berthing_mailiao");
        Assert.Equal(2, sc.Tugs!.Count);
        Assert.All(sc.Tugs, t => Assert.Equal(40, t.BollardPullT));
        Assert.Equal("MLO-OIL-3", sc.Berth!.Definition!.Id);
        Assert.Equal(12, sc.Berth.Definition.Fenders.Count);
        Assert.Equal(4, sc.Berth.Lines.Count);
        Assert.Equal(360, sc.Timeline.Single(t => t.Id == "berthReady").When!.AtLeast);
        var brk = sc.Timeline.Single(t => t.Id == "tugLineBreak");
        Assert.Equal(240, brk.When!.AtLeast);
        Assert.Equal("tugLineBreak", brk.Actions[0].AsString());
        Assert.Equal("TG2", brk.Actions[0].FaultArgs()!.Value.GetProperty("id").GetString());
        Assert.Contains(sc.Assessment.HardFail, h => h.Rule == "event" && h.Kind == "quayContact");
        Assert.Contains(sc.Assessment.HardFail, h => h.Rule == "event" && h.Kind == "mooringLineParted");
        Assert.Equal(60, sc.Assessment.Required.Single(r => r.Id == "tugLineBreakResponse").WithinS);
        Assert.Equal(5.0, sc.Zones.Single(z => z.Id == "harbour").SpeedLimitKn);
        Assert.Contains(sc.Briefing!.Roles!, r => r.Role == "pilot" && r.Required);
        NoErrors(sc);
    }

    /// <summary>
    /// 學員腳本(兩艘拖船的推拉控制器,每秒依對地速度):
    /// 橫向 — 等待時保持、就緒後依離碰墊面距離控制橫向接近速度 0.25 → 0.15 → 0.07 m/s(過快時換拉,換位約 30 s);
    /// 艏向 — TG1/TG2 出力差(艏向偏差 + 迴轉率)保持與碼頭線平行;縱向 — 推/拉方向前後偏轉(±35°)抵消沿碼頭向後的流,
    /// 船舯保持在泊位中心(碰墊列中點,里程 130 m)附近。拖纜斷裂後 TG2 拉被拒(tugLineParted)、學員續下拖船指令 = 反應;
    /// 靠上碰墊、船位在泊位中心 ± 15 m 且與碼頭線平行(± 5°)時繫 4 條纜(拖船續以低出力推靠、保持艏向與船位),120 s 後教官 allFast 結束。
    /// </summary>
    [Fact]
    public void E13_ScriptedTugBerthingWithLineBreakReplays()
    {
        const double centre = 130.0;   // MLO-OIL-3 碰墊 F01(20 m)–F12(240 m)中點
        const double quayHeading = 85.0;
        var want = "hold";
        var since = 0;
        var mode = "push";
        var fastAt = -1;
        var sent = new Dictionary<string, (string Mode, double Dir, double Power)>();
        List<(string Id, string Status)>? linesAtAllFast = null;
        double? chainAtAllFast = null;
        var (e, events, path) = Run("E13_tug_berthing_mailiao", 1800, (eng, ev, t) =>
        {
            if (t == 10) eng.Enqueue(SimCommand.Acknowledge("checklist"));
            if (t == 35) eng.Enqueue(SimCommand.Acknowledge("report"));
            var q = eng.External.Berth!.Segments[0];
            var m = eng.Motion;
            var env = eng.Environment.SampleAt(m.X, m.Y);
            var (ve, vn) = EnvironmentMath.GroundVelocity(m.U, m.V, m.Psi, env.CurrentEastMps, env.CurrentNorthMps);
            var toward = -(ve * q.Nx + vn * q.Ny);                         // 朝碼頭的對地速度
            var along = ve * q.Tx + vn * q.Ty;                             // 沿碼頭線(里程增加方向)的對地速度
            var dist = (m.X - q.Ax) * q.Nx + (m.Y - q.Ay) * q.Ny - 10.0;  // 左舷至碰墊面(半船寬 9 m + 碰墊 1 m)
            var chain = (m.X - q.Ax) * q.Tx + (m.Y - q.Ay) * q.Ty;         // 船舯里程
            var hdgErr = Units.RadToDeg(Units.WrapRadPi(m.Psi - Units.DegToRad(quayHeading)));
            var rotDegPerMin = Units.RadToDeg(m.R) * 60.0;
            var target = t < 360 ? 0.0 : dist > 30 ? 0.25 : dist > 8 ? 0.15 : 0.07;
            var w = toward < target - 0.04 ? "push" : toward > target + 0.06 ? "pull" : "hold";
            if (w != want) { want = w; since = t; }
            {
                if (t - since >= 8 && want != "hold" && want != mode) mode = want;
                var p = want == "hold" || want != mode ? 10.0 : want == "push" && t < 360 ? 12.0 : 20.0;
                // 艏向:艏偏向碼頭(偏差 < 0)時推艉多於推艏 / 拉艏多於拉艉
                var h = Math.Clamp(2.0 * hdgErr + 4.0 * rotDegPerMin, -25.0, 25.0);
                // 縱向:期望沿碼頭速度(朝泊位中心,上限 0.1 m/s),推拉方向朝前(> 0)或朝後偏轉
                var vd = Math.Clamp(-0.01 * (chain - centre), -0.1, 0.1);
                var d = Math.Clamp(200.0 * (vd - along), -35.0, 35.0);
                var (p1, p2, dir) = mode == "push" ? (p + h, p - h, 270.0 + d) : (p - h, p + h, 90.0 - d);
                foreach (var (id, pw) in new[] { ("TG1", p1), ("TG2", p2) })
                {
                    var cmd = (mode, Math.Round(dir / 5.0) * 5.0, Math.Round(Math.Clamp(pw, 0.0, 100.0) / 2.0) * 2.0);
                    if (sent.TryGetValue(id, out var last) && last == cmd) continue;
                    eng.Enqueue(SimCommand.TugControl(id, cmd.Item1, cmd.Item2, cmd.Item3));
                    sent[id] = cmd;
                }
            }
            if (fastAt < 0 && t > 700 && dist < 3.0 && Math.Abs(chain - centre) < 15.0 && Math.Abs(hdgErr) < 5.0)
            {
                fastAt = t;
                foreach (var l in new[] { "HEAD", "FWDSPRING", "AFTSPRING", "STERN" }) eng.Enqueue(SimCommand.Mooring(l, "makeFast"));
            }
            if (fastAt > 0 && t == fastAt + 120)
            {
                linesAtAllFast = eng.BuildState().Berth!.Lines.Select(l => (l.Id, l.Status)).ToList();
                chainAtAllFast = (m.X - q.Ax) * q.Tx + (m.Y - q.Ay) * q.Ty;
                eng.Enqueue(SimCommand.TriggerTimeline("allFast"));
            }
        });
        // 教官 allFast 前四條纜皆已繫妥(無 bollardOutOfReach),船舯在泊位中心(碰墊列中點,里程 130 m)± 15 m 內
        Assert.NotNull(linesAtAllFast);
        Assert.Equal(4, linesAtAllFast!.Count);
        Assert.All(linesAtAllFast, l => Assert.True(l.Status == "fast", $"{l.Id} {l.Status}"));
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.CommandRejected && x.DataString("reason") == "bollardOutOfReach");
        Assert.InRange(chainAtAllFast!.Value, 130.0 - 15.0, 130.0 + 15.0);
        Dump(events.Where(x => x.Kind != SimEventKinds.TugCommand).ToList());
        double T(string item) => events.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == item).T;
        Assert.Equal(5.0, T("pilotMpx"));
        Assert.Equal(30.0, T("berthNotReady"));
        Assert.Equal(360.0, T("berthReady"));
        Assert.Equal(600.0, T("tugLineBreak"));
        var parted = events.Single(x => x.Kind == SimEventKinds.TugLineParted);
        Assert.Equal(("TG2", 600.0, SimEventSources.Engine), (parted.ItemId, parted.T, parted.Source));
        Assert.Equal(690.0, events.Single(x => x.Kind == SimEventKinds.FaultCleared && x.ItemId == "tugReconnect").T);
        Assert.Contains(events, x => x.Kind == SimEventKinds.CommandRejected && x.DataString("reason") == "tugLineParted" && x.T >= 600 && x.T < 690);
        var contacts = events.Where(x => x.Kind == SimEventKinds.FenderContact).ToList();
        Assert.NotEmpty(contacts);
        Assert.All(contacts, c => Assert.True(c.T > 360.0, "碼頭就緒前不得靠上"));
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.QuayContact);
        Assert.Equal(SimEventKinds.ExerciseEnded, events.Last(x => x.Source == SimEventSources.Timeline).Kind);

        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Assert.False(s.Assessment.HardFailed);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        var b = s.Berthing!;
        Assert.Equal("MLO-OIL-3", b.BerthId);
        Assert.Equal(contacts.Count, b.FenderContacts);
        Assert.Equal(0, b.FenderOverloads);
        Assert.InRange(b.MaxLateralSpeedMps!.Value, 0.05, 0.40);
        Assert.InRange(b.MaxEnergyRatio!.Value, 0.0, 0.5);
        // 橫向接近速度 = 接近接觸的接觸點法向速度最大值;繫妥後的接觸不是接近接觸
        Assert.Equal(b.Contacts.Where(c => c.Approach).Max(c => c.NormalSpeedMps), b.MaxLateralSpeedMps);
        Assert.Equal(b.MaxLateralSpeedMps, b.Landings.Max(l => l.ContactSpeedMps));
        Assert.All(b.Contacts.Where(c => c.TS > b.AllFastAtS), c => Assert.False(c.Approach));
        // 繫妥時船位:泊位中心 = 碰墊列中點 130 m
        Assert.Equal(centre, b.BerthCentreChainageM!.Value, 6);
        Assert.InRange(b.AllFastAtS!.Value, fastAt - 1.0, fastAt + 120.0);
        Assert.InRange(b.AllFastPositionErrorM!.Value, -15.0, 15.0);
        Assert.Equal(b.AllFastMidshipChainageM!.Value - centre, b.AllFastPositionErrorM.Value, 9);
        Assert.Equal(Math.Abs(b.AllFastPositionErrorM.Value), e.Assessment.MetricValue("berthing.allFastPositionError_m", null!));
        Assert.Equal(1, b.TugLineBreaks);
        Assert.True(b.Tugs.Single(x => x.Id == "TG2").LineParted);
        Assert.All(b.Tugs, x => Assert.True(x.WorkingTimeS > 0 && x.MaxForceT > 0));
        Assert.Equal(4, b.Lines.Count);
        Assert.True(b.Landings.Count >= 1 && b.Landings.All(l => !l.Initial && l.MaxMps is not null));
        _out.WriteLine(JsonSerializer.Serialize(b with { Landings = b.Landings.Take(3).ToArray(), Contacts = b.Contacts.Take(3).ToArray() }, ContractJson.Indented));
        AssertReplays(path!);
    }

    // ------------------------------------------------------------------ E14

    [Fact]
    public void E14_LoadsAlongsideWithLinesFastAndStartFault()
    {
        var sc = Load("E14_unberthing_no_tug");
        Assert.Equal(LoadingCondition.Ballast, sc.Ship.Loading);
        Assert.Null(sc.Tugs);
        Assert.Equal("MLO-SOB-1", sc.Berth!.Definition!.Id);
        Assert.Contains("示意", sc.Berth.Definition.Source);
        Assert.All(sc.Berth.Lines, l => Assert.Equal("fast", l.Initial));
        var fault = sc.Timeline.Single(t => t.Id == "engineStartFault");
        Assert.Equal("engineStartFailure", fault.Actions[0].AsString());
        Assert.Equal(2, fault.Actions[0].FaultArgs()!.Value.GetProperty("attempts").GetInt32());
        Assert.Equal(300, sc.Timeline.Single(t => t.Id == "windShift").When!.AtLeast);
        Assert.Contains(sc.Assessment.Required, r => r.Id == "engineStartReport" && r.After.Event == "engineStartFailed");
        NoErrors(sc);
        // 初始:右舷壓在碰墊上(碼頭線 x = 0、水域在西)
        var e = new SimulationEngine(TestData.Fsb1(), sc, Mmg);
        e.Step();
        Assert.Contains(e.External.Berth!.Fenders, f => f.InContact);
        Assert.All(e.BuildState().Berth!.Lines, l => Assert.Equal("fast", l.Status));
    }

    /// <summary>
    /// 學員腳本:簡報 → 解艏纜/艉倒纜/艉纜留艏倒纜、右滿舵微速進車開艉(前 2 次起動失敗、自動再試,第 3 次點火)→ 通知機控室 →
    /// 解艏倒纜後退俥離開碼頭 → 風向改變後停俥、側推穩住艏向 → 左滿舵微速前進離開泊位區(結束)。
    /// </summary>
    [Fact]
    public void E14_ScriptedUnberthingWithStartDelayReplays()
    {
        var (e, events, path) = Run("E14_unberthing_no_tug", 1200, (eng, ev, t) =>
        {
            switch (t)
            {
                case 10: eng.Enqueue(SimCommand.Acknowledge("checklist")); break;
                case 40:
                    foreach (var l in new[] { "HEAD", "AFTSPRING", "STERN" }) eng.Enqueue(SimCommand.Mooring(l, "letGo"));
                    break;
                case 50: eng.Enqueue(SimCommand.Rudder(35)); eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH)); break;
                case 60: eng.Enqueue(SimCommand.Acknowledge("notifyEcr")); break;
                case 150: eng.Enqueue(SimCommand.Mooring("FWDSPRING", "letGo")); eng.Enqueue(SimCommand.Rudder(0)); eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.SAS)); break;
                case 310: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.STOP)); break;
                case 330: eng.Enqueue(SimCommand.Thruster(0.3)); break;
                case 420: eng.Enqueue(SimCommand.Thruster(0)); break;
                case 490: eng.Enqueue(SimCommand.Telegraph(TelegraphOrder.DSAH)); eng.Enqueue(SimCommand.Rudder(-35)); break;
            }
        });
        Dump(events);
        Assert.Equal(1.0, events.Single(x => x.Kind == SimEventKinds.FaultInjected).T);
        var dsah = 49.0; // t = 50 時送入的指令於引擎時間 49 s 套用
        var failed = events.Where(x => x.Kind == SimEventKinds.EngineStartFailed).ToList();
        Assert.Equal(new[] { dsah + 5, dsah + 10 }, failed.Select(x => x.T));
        var started = events.First(x => x.Kind == SimEventKinds.EngineStarted);
        Assert.Equal(dsah + 15, started.T);
        Assert.DoesNotContain(events, x => x.Kind == SimEventKinds.EngineStartBlocked);
        Assert.Equal(dsah + 6, events.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "startFailedVoice").T);
        Assert.Equal(300.0, events.Single(x => x.Kind == SimEventKinds.TimelineTriggered && x.ItemId == "windShift").T);
        Assert.Equal(3, events.Count(x => x.Kind == SimEventKinds.MooringCommand && x.T == 39.0));
        Assert.DoesNotContain(events, x => x.Kind is SimEventKinds.QuayContact or SimEventKinds.MooringLineParted);
        var end = events.Single(x => x.Kind == SimEventKinds.ExerciseEnded);
        Assert.Equal("clearOfBerth", end.ItemId);

        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Assert.False(s.Assessment.HardFailed);
        Assert.All(s.Assessment.Required, r => Assert.Equal("met", r.State));
        Assert.Equal(100, s.Assessment.Score);
        var b = s.Berthing!;
        Assert.Equal("MLO-SOB-1", b.BerthId);
        Assert.True(b.Landings[0].Initial);
        Assert.True(b.FenderContacts > 0);
        Assert.Equal(0, b.QuayContacts);
        Assert.Equal(0, b.LineBreaks);
        Assert.InRange(b.MaxLineTensionRatio!.Value, 0.0, 0.5);
        Assert.Empty(b.Tugs);
        Assert.True(s.Manoeuvring.ThrusterOrders >= 1 && s.Manoeuvring.ThrusterTimeS > 0, "側推使用");
        Assert.Single(s.FaultResponses, f => f.Fault == FaultNames.EngineStartFailure);
        _out.WriteLine(JsonSerializer.Serialize(b with { Landings = b.Landings.Take(2).ToArray(), Contacts = b.Contacts.TakeLast(3).ToArray() }, ContractJson.Indented));
        AssertReplays(path!);
    }

    /// <summary>
    /// E14 不操船 600 s(4 纜已繫、右舷靠在碰墊上;T+300 s 轉吹開風):船在纜繩上離開碰墊再碰回(繫泊中擺盪)仍逐筆記入 contacts[],
    /// 但不是新的靠上(landings 只有開始即接觸的 initial 一筆),也不計入橫向接近速度(berthing.maxLateralSpeed_mps 為 null,不會觸發 approachSpeed 容許帶)。
    /// </summary>
    [Fact]
    public void E14_IdleAlongsideRangingIsNotALanding()
    {
        var (e, _, _) = Run("E14_unberthing_no_tug", 600, (_, _, _) => { }, record: false);
        var b = e.Assessment!.BuildSummary(e.Tick, final: true).Berthing!;
        Assert.True(b.FenderContacts > 20, $"繫泊中擺盪接觸 {b.FenderContacts}");
        var landing = Assert.Single(b.Landings);
        Assert.True(landing.Initial);
        Assert.Null(b.MaxLateralSpeedMps);
        Assert.Null(e.Assessment.MetricValue("berthing.maxLateralSpeed_mps", null!));
        Assert.All(b.Contacts, c => Assert.False(c.Approach));
        Assert.Null(landing.ContactSpeedMps);
        Assert.True(b.MaxContactSpeedMps > 0.10, "contacts[] 仍記錄擺盪接觸(maxContactSpeed 為全部接觸)");
        // 情境開始即繫妥(4 纜 fast、右舷靠在碰墊上;船舯里程 75 m = 碰墊列 15–135 m 中點)
        Assert.Equal(1.0, b.AllFastAtS);                 // 第一個評估步
        Assert.Equal(75.0, b.BerthCentreChainageM!.Value, 6);
        Assert.InRange(b.AllFastPositionErrorM!.Value, -0.5, 0.5);
    }
}
