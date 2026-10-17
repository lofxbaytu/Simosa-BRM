using System.Text.Json;
using SimosaBRM.SimCore.Assessment;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Scenario;
using Xunit;
using static SimosaBRM.SimCore.Tests.ExternalForceTestSupport;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 靠泊與錨泊指標(規劃書第 8.3 節第 4 類;BerthingTracker、summary.berthing / anchoring、MetricNames berthing.* / anchoring.*):
/// 質點 + 線性碰墊的解析接近速度與能量比、碼頭硬接觸 hardFail(event 規則)、斷纜與繫纜張力/MBL、拖船工作時間與最大出力、
/// 容許帶引用參數集 berthing.approachSpeed_mps / anchoring.scopeMultiplier、放錨速度與鏈長倍數、快照還原續跑、舊情境 summary 不變。
/// </summary>
public class BerthingAssessmentTests
{
    private const string ParamsLine = "  parameterSet: data/parameters/company-procedures.yaml\n";

    /// <summary>航向 090、左舷朝北側碼頭(y = 30);以北向均勻流使船以 v0 接近碼頭(質點,解析值)。</summary>
    private static SimulationEngine Alongside(double v0, string assessment = "", bool fenders = true, string lines = "", string extra = "", double y = 19.5)
        => PointMass(Sc(LinearBerthYaml(fenders, lines) + "\n" + extra + (assessment.Length > 0 ? "\nassessment:\n" + ParamsLine + assessment : ""),
            x: 0, y: y, headingDeg: 90, currentSet: 0, currentDriftKn: Units.MpsToKn(v0)));

    [Fact]
    public void FenderContactSpeedsAndEnergyRatioMatchAnalytic()
    {
        const double v0 = 0.15;
        var e = Alongside(v0);
        RunTo(e, 30);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        var b = s.Berthing!;
        Assert.Equal("TB", b.BerthId);
        Assert.Equal(2, b.FenderContacts);
        Assert.Equal(0, b.FenderOverloads);
        Assert.Equal(0, b.QuayContacts);
        // 平行接近:艏、舯、艉的接近速度皆為 v0(接觸前最後一個每秒取樣)
        var landing = b.Landings[0];
        Assert.False(landing.Initial);
        Assert.Equal(v0, landing.BowMps!.Value, 6);
        Assert.Equal(v0, landing.MidshipMps!.Value, 6);
        Assert.Equal(v0, landing.SternMps!.Value, 6);
        Assert.Equal(Units.MpsToKn(v0), landing.MaxKn!.Value, 6);
        Assert.True(landing.SampleAtS < landing.TS);
        Assert.Equal(v0, b.MaxLateralSpeedMps!.Value, 3);
        Assert.Equal(Units.MpsToKn(v0), b.MaxLateralSpeedKn!.Value, 2);
        // 能量比 = ½ m v0² /(2 碰墊)/ 額定能量(½ · 1000 kN · 0.72 m)
        var perFender = 0.25 * PointMassKg * v0 * v0 / 1000.0;
        var rated = 0.5 * 1000.0 * 0.72;
        Assert.All(b.Contacts, c =>
        {
            Assert.Equal(v0, c.NormalSpeedMps, 3);
            Assert.Equal(Units.MpsToKn(c.NormalSpeedMps), c.NormalSpeedKn, 9);
            Assert.Equal(perFender, c.EnergyKNm, perFender * 0.01);
            Assert.Equal(rated, c.RatedEnergyKNm!.Value, 1e-6);
            Assert.Equal(c.EnergyKNm / rated, c.EnergyRatio!.Value, 9);
        });
        Assert.Equal(b.Contacts.Max(c => c.EnergyRatio), b.MaxEnergyRatio);
        // MetricValue 與 summary 一致
        Assert.Equal(b.MaxLateralSpeedMps, e.Assessment.MetricValue("berthing.maxLateralSpeed_mps", null!));
        Assert.Equal(b.MaxEnergyRatio, e.Assessment.MetricValue("berthing.maxEnergyRatio", null!));
        Assert.Equal(2, e.Assessment.MetricValue("berthing.fenderContacts", null!));
        Assert.Null(s.Anchoring); // 情境無 anchors 區段且錨未使用
    }

    [Fact]
    public void ApproachSpeedBandReferencesParameterSet()
    {
        // 0.18 m/s > berthing.approachSpeed_mps(0.15,warn)、< approachSpeedFail_mps(0.20,fail)
        var e = Alongside(0.18, """
              bands:
                - { id: approachSpeed, metric: berthing.maxLateralSpeed_mps, warn: "${params:berthing.approachSpeed_mps}", fail: "${params:berthing.approachSpeedFail_mps}", penalty: 10, failPenalty: 25 }
                - { id: energy, metric: berthing.maxEnergyRatio, warn: 0.8, fail: 1.0, penalty: 10 }
            """);
        var events = TestData.Capture(e);
        RunTo(e, 30);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        var band = s.Assessment.Bands.Single(x => x.Id == "approachSpeed");
        Assert.Equal("warn", band.State);
        Assert.Equal(0.15, band.Warn);
        Assert.Equal(0.20, band.Fail);
        Assert.Equal("berthing.approachSpeed_mps", band.ParamKey);
        Assert.Equal("ASSUMED-BERTH", band.Source);
        Assert.Equal("ok", s.Assessment.Bands.Single(x => x.Id == "energy").State);
        Assert.Contains(s.Assessment.Parameters, p => p.Key == "berthing.approachSpeed_mps" && p.Unit == "mps");
        Assert.Contains(events, x => x.Kind == SimEventKinds.BandWarn && x.ItemId == "approachSpeed");
        Assert.True(MetricNames.IsKnown("berthing.maxLateralSpeed_mps") && MetricNames.IsKnown("anchoring.minScopeRatio"));
        Assert.StartsWith("假設值,待公司程序", TestData.Parameters().Sources["ASSUMED-BERTH"].Title);
    }

    [Fact]
    public void QuayContactIsHardFailViaEventRule()
    {
        var e = Alongside(0.1, "  hardFail:\n    - { id: quayContact, rule: event, kind: quayContact }\n", fenders: false, y: 20.8);
        RunTo(e, 20);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        Assert.True(s.Assessment.HardFailed);
        Assert.Equal("quayContact", Assert.Single(s.Assessment.HardFails).Id);
        Assert.Equal(1, s.Berthing!.QuayContacts);
        Assert.Equal(0.1, s.Berthing.QuayMaxSpeedMps!.Value, 2);
        Assert.True(s.Berthing.QuayMaxPenetrationM > 0);
        Assert.Equal(1, e.Assessment.MetricValue("berthing.quayContacts", null!));
    }

    [Fact]
    public void LineBreakTensionRatioAndTugUse()
    {
        // 纜 L1(MBL 5 t)繫於船舯左舷,拖船自船舯向南(右舷)拉 20 t → 斷纜;拖船工作時間與最大出力
        const string line = "{ id: L1, fairlead: midPort, bollard: B1, mbl_t: 5, breakStrain: 0.18, exponent: 1.5, pretension_t: 1 }";
        var e = PointMass(Sc(LinearBerthYaml(lines: line) + "\ntugs: [ { id: TG, bollardPull_t: 20, attach: { x: 0, y: 0 }, riseTime_s: 10 } ]"
                             + "\nassessment:\n" + ParamsLine + "  hardFail:\n    - { id: lineParted, rule: event, kind: mooringLineParted }\n", x: 0, y: 19.5, headingDeg: 90));
        Apply(e, SimCommand.Mooring("L1", "makeFast"));
        Apply(e, SimCommand.TugControl("TG", "pull", 90, 100));
        RunTo(e, 120);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        var b = s.Berthing!;
        Assert.Equal(1, b.LineBreaks);
        var l = Assert.Single(b.Lines);
        Assert.True(l.Parted);
        Assert.Equal(5.0, l.MblT);
        Assert.True(l.MaxTensionRatio > 1.0, $"斷裂時張力 > MBL:{l.MaxTensionRatio}");
        Assert.Equal(l.MaxTensionRatio, b.MaxLineTensionRatio);
        Assert.True(s.Assessment.HardFailed);
        var tug = Assert.Single(b.Tugs);
        Assert.Equal("TG", tug.Id);
        Assert.Equal(20.0, tug.BollardPullT, 6);
        Assert.InRange(tug.WorkingTimeS, 110, 120);
        Assert.InRange(tug.MaxForceT, 19.0, 20.0);
        Assert.Equal(tug.MaxForceT, e.Assessment.MetricValue("berthing.maxTugForce_t", null!));
        Assert.Equal(tug.WorkingTimeS, e.Assessment.MetricValue("berthing.tugWorkingTime_s", null!));
    }

    /// <summary>兩艘拖船(船舯右舷):TA 推向碼頭、TB 拉離碼頭(出力上升 5 s),以出力切換使船靠上、輕碰、離開再靠回。</summary>
    private const string PushPullTugs = "\ntugs: [ { id: TA, bollardPull_t: 20, attach: midStbd, riseTime_s: 5, initial: { mode: push, direction_deg: 270, power_pct: 10 } },"
                                        + " { id: TB, bollardPull_t: 20, attach: midStbd, riseTime_s: 5, initial: { mode: pull, direction_deg: 90, power_pct: 0 } } ]";

    /// <summary>
    /// 靠上的定義:拖船持續推靠時在碰墊上反彈再碰回(離開 &lt; 0.5 m)只是同一次靠上的再接觸(contacts[] 仍逐筆記錄、approach = false);
    /// 拉離 ≥ 0.5 m 後再推回才是新的靠上。maxLateralSpeed = 接近接觸的接觸點法向速度最大值(第二次靠上較快)。
    /// </summary>
    [Fact]
    public void ReboundIsNotANewLandingButReapproachIs()
    {
        var e = PointMass(Sc(LinearBerthYaml() + PushPullTugs, x: 0, y: 19.8, headingDeg: 90));   // 左舷距碰墊面 0.2 m
        RunTo(e, 120);
        var b1 = e.Assessment!.BuildSummary(e.Tick, final: false).Berthing!;
        var first = Assert.Single(b1.Landings);
        Assert.False(first.Initial);
        Assert.True(b1.FenderContacts > 2, $"推靠中反覆碰回 {b1.FenderContacts}");
        Assert.Equal(2, b1.Contacts.Count(c => c.Approach));                        // F1、F2 第一次接觸
        Assert.All(b1.Contacts.Skip(2), c => Assert.False(c.Approach));
        Assert.Equal(b1.Contacts.Take(2).Max(c => c.NormalSpeedMps), b1.MaxLateralSpeedMps);
        Assert.Equal(b1.MaxLateralSpeedMps, first.ContactSpeedMps);
        // 拉離碼頭再推回
        Apply(e, SimCommand.TugControl("TA", "push", 270, 0));
        Apply(e, SimCommand.TugControl("TB", "pull", 90, 20));
        RunTo(e, 150);
        Apply(e, SimCommand.TugControl("TB", "pull", 90, 0));
        Apply(e, SimCommand.TugControl("TA", "push", 270, 10));
        RunTo(e, 420);
        var b = e.Assessment.BuildSummary(e.Tick, final: true).Berthing!;
        Assert.Equal(2, b.Landings.Count);
        var second = b.Landings[1];
        Assert.True(second.TS > 150.0);
        Assert.NotNull(second.ContactSpeedMps);
        Assert.True(second.ContactSpeedMps > first.ContactSpeedMps, $"{second.ContactSpeedMps} vs {first.ContactSpeedMps}");
        Assert.Equal(second.ContactSpeedMps, b.MaxLateralSpeedMps);
        Assert.Equal(4, b.Contacts.Count(c => c.Approach));
        Assert.Null(b.AllFastAtS);                                                   // 無纜繩:不會繫妥
    }

    /// <summary>
    /// 繫妥(所有纜繩受力且艏艉碰墊皆已靠上)後拉離再靠回不是新的靠上、不計入橫向接近速度;繫妥時船舯與泊位中心(碰墊列中點)的偏差。
    /// </summary>
    [Fact]
    public void ContactsAfterAllFastAreNotApproachAndPositionErrorIsReported()
    {
        const string line = "{ id: L1, fairlead: midPort, bollard: B2, mbl_t: 50 }";
        var e = PointMass(Sc(LinearBerthYaml(lines: line) + PushPullTugs, x: 0, y: 19.8, headingDeg: 90));
        RunTo(e, 60);
        Apply(e, SimCommand.Mooring("L1", "makeFast"));
        RunTo(e, 120);
        var b1 = e.Assessment!.BuildSummary(e.Tick, final: false).Berthing!;
        Assert.NotNull(b1.AllFastAtS);
        Assert.InRange(b1.AllFastAtS!.Value, 60.0, 62.0);
        Assert.Equal(200.0, b1.BerthCentreChainageM!.Value, 9);                     // 碰墊 F1 180 m、F2 220 m
        Assert.Equal(200.0, b1.AllFastMidshipChainageM!.Value, 1);                  // 船舯 x = 0 → 里程 200 m
        Assert.Equal(b1.AllFastMidshipChainageM.Value - 200.0, b1.AllFastPositionErrorM!.Value, 9);
        Assert.Equal(Math.Abs(b1.AllFastPositionErrorM.Value), e.Assessment.MetricValue("berthing.allFastPositionError_m", null!));
        var approach = b1.MaxLateralSpeedMps;
        Apply(e, SimCommand.TugControl("TA", "push", 270, 0));
        Apply(e, SimCommand.TugControl("TB", "pull", 90, 20));
        RunTo(e, 150);
        Apply(e, SimCommand.TugControl("TB", "pull", 90, 0));
        Apply(e, SimCommand.TugControl("TA", "push", 270, 10));
        RunTo(e, 420);
        var b = e.Assessment.BuildSummary(e.Tick, final: true).Berthing!;
        Assert.Single(b.Landings);
        Assert.Contains(b.Contacts, c => c.TS > 150.0);
        Assert.All(b.Contacts.Where(c => c.TS > b.AllFastAtS), c => Assert.False(c.Approach));
        Assert.Equal(approach, b.MaxLateralSpeedMps);
        Assert.True(b.MaxContactSpeedMps > b.MaxLateralSpeedMps, "maxContactSpeed 含繫妥後的接觸");
        Assert.Equal(b1.AllFastAtS, b.AllFastAtS);
    }

    /// <summary>泊位位置容許帶:berthing.allFastPositionError_m(繫妥時船舯與泊位中心距離)引用參數集 berthing.allFastPositionTolerance_m(warn 15 m、fail 2x)。</summary>
    [Fact]
    public void AllFastPositionBandReferencesParameterSet()
    {
        const string line = "{ id: L1, fairlead: midPort, bollard: B2, mbl_t: 50 }";
        var e = PointMass(Sc(LinearBerthYaml(lines: line) + PushPullTugs + "\nassessment:\n" + ParamsLine + """
              bands:
                - { id: berthPosition, metric: berthing.allFastPositionError_m, warn: "${params:berthing.allFastPositionTolerance_m}", fail: "2x", penalty: 10, failPenalty: 20 }
            """, x: 20, y: 19.8, headingDeg: 90));                                          // 船舯里程 220 m(泊位中心 200 m)
        RunTo(e, 30);
        Assert.Equal("ok", e.Assessment!.BuildSummary(e.Tick, final: false).Assessment.Bands.Single().State);  // 未繫妥:指標 null
        Apply(e, SimCommand.Mooring("L1", "makeFast"));
        RunTo(e, 40);
        var s = e.Assessment.BuildSummary(e.Tick, final: true);
        Assert.Equal(20.0, s.Berthing!.AllFastPositionErrorM!.Value, 1);
        var band = s.Assessment.Bands.Single();
        Assert.Equal("warn", band.State);
        Assert.Equal(15.0, band.Warn);
        Assert.Equal(30.0, band.Fail);
        Assert.Equal("berthing.allFastPositionTolerance_m", band.ParamKey);
        Assert.Equal("ASSUMED-E13", band.Source);
    }

    [Fact]
    public void TugLineBreakCountedAndInitialAlongsideLandingHasNoSpeed()
    {
        // 開始即壓在碰墊上(y 20.1:壓縮 0.1 m):第一段接觸為 initial,無接近速度
        var e = PointMass(Sc(LinearBerthYaml() + "\ntugs: [ { id: TG, bollardPull_t: 20, attach: aftStbd, initial: { mode: pull, direction_deg: 90, power_pct: 10 } } ]", x: 0, y: 20.1, headingDeg: 90));
        RunTo(e, 2);
        Apply(e, SimCommand.InjectFault(FaultNames.TugLineBreak, new { id = "TG" }));
        RunTo(e, 10);
        var b = e.Assessment!.BuildSummary(e.Tick, final: true).Berthing!;
        Assert.True(b.Landings[0].Initial);
        Assert.Null(b.Landings[0].BowMps);
        Assert.Null(b.Landings[0].MaxMps);
        Assert.Equal(1, b.TugLineBreaks);
        Assert.True(b.Tugs.Single().LineParted);
        Assert.Equal(2.0, b.Tugs.Single().LinePartedAtS);
    }

    [Fact]
    public void SnapshotRestoreKeepsBerthingSummaryIdentical()
    {
        var a = Alongside(0.12);
        RunTo(a, 3);
        var snap = EngineSnapshot.FromJson(a.CreateSnapshot().ToJson());
        RunTo(a, 30);
        var b = Alongside(0.12);
        b.Restore(snap);
        RunTo(b, 30);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(ContractJson.Serialize(a.Assessment!.BuildSummary(a.Tick, true)), ContractJson.Serialize(b.Assessment!.BuildSummary(b.Tick, true)));
        Assert.NotNull(snap.Assessment!.Berthing);
    }

    [Fact]
    public void ScenariosWithoutBerthOrAnchorsHaveNoNewSummaryBlocks()
    {
        var e = TestData.Engine();
        TestData.RunTo(e, 30);
        var json = ContractJson.Serialize(e.Assessment!.BuildSummary(e.Tick, true));
        Assert.DoesNotContain("\"berthing\"", json);
        Assert.DoesNotContain("\"anchoring\"", json);
        Assert.Null(e.CreateSnapshot().Assessment!.Berthing);
    }

    // ------------------------------------------------------------------ 錨泊

    [Fact]
    public void LetGoSpeedScopeRatioAndScopeBand()
    {
        // MMG 滿載 1 kn 停俥滑行、水深 20 m:3 節(82.5 m)= 4.1 倍水深 < anchoring.scopeMultiplier(5)→ 容許帶 warn
        var sc = Sc("""
            anchors: { holdingFactor: 1.0 }
            assessment:
              parameterSet: data/parameters/company-procedures.yaml
              bands:
                - { id: scope, metric: anchoring.minScopeRatio, direction: below, warn: "${params:anchoring.scopeMultiplier}", fail: 3, penalty: 10 }
                - { id: letGoSpeed, metric: anchoring.maxLetGoSog_kn, warn: "${params:anchoring.letGoSog_kn}", fail: "2x", penalty: 10 }
            """, speedKn: 1.0, depth: 20, seabed: "sand");
        var e = Mmg(sc);
        var events = TestData.Capture(e);
        RunTo(e, 5);
        var sogAtLetGo = e.BuildState().Truth!.Sog;
        Apply(e, SimCommand.Anchor("starboard", "letGo", 3));
        RunTo(e, 900);
        var s = e.Assessment!.BuildSummary(e.Tick, final: true);
        var an = s.Anchoring!;
        var a = Assert.Single(an.Anchors);
        Assert.Equal("starboard", a.Side);
        Assert.Equal(1, a.LetGoCount);
        Assert.Equal(sogAtLetGo, a.LetGoSogKn!.Value, 6);
        Assert.Equal(82.5, a.TargetLengthM!.Value, 6);
        Assert.Equal(82.5 / 20.0, a.PlannedScopeRatio!.Value, 6);
        Assert.Contains(events, x => x.Kind == SimEventKinds.AnchorBroughtUp);
        Assert.Equal(82.5, a.BroughtUpLengthM!.Value, 1e-6);
        Assert.Equal(3.0, a.BroughtUpShackles!.Value, 1e-6);
        Assert.Equal(82.5 / 20.0, a.ScopeRatio!.Value, 6);
        Assert.Equal(a.ScopeRatio, an.MinScopeRatio);
        Assert.True(an.MaxChainTensionT > 0);
        var scope = s.Assessment.Bands.Single(x => x.Id == "scope");
        Assert.Equal("warn", scope.State);
        Assert.Equal("anchoring.scopeMultiplier", scope.ParamKey);
        Assert.Equal(5.0, scope.Warn);
        var speed = s.Assessment.Bands.Single(x => x.Id == "letGoSpeed");
        Assert.Equal(sogAtLetGo > 0.5 ? "warn" : "ok", speed.State);
        Assert.Equal(0.5, speed.Warn);
    }

    [Fact]
    public void DragResponseMeasuredFromFirstDragToFirstAction()
    {
        // 拖船以 40 t 拉動錨泊船(抓地力倍率 0.3)造成走錨;30 s 後學員下車令 = 反應;未反應前指標隨時間增加
        var sc = Sc("""
            anchors: { holdingFactor: 0.3, starboard: { length_shackles: 4, bearing_deg: 0 } }
            tugs: [ { id: TG, bollardPull_t: 40, attach: stern, riseTime_s: 10 } ]
            """, speedKn: 0, depth: 20, seabed: "mud");
        var e = Mmg(sc);
        var events = TestData.Capture(e);
        Apply(e, SimCommand.TugControl("TG", "pull", 180, 100));
        SimEvent? drag = null;
        for (var t = 1; t <= 600 && drag is null; t++)
        {
            RunTo(e, t);
            drag = events.FirstOrDefault(x => x.Kind == SimEventKinds.AnchorDragging);
        }
        Assert.NotNull(drag);
        var t0 = Math.Ceiling(drag!.T) + 10;
        RunTo(e, t0);
        Assert.Equal(t0 - drag.T, e.Assessment!.MetricValue("anchoring.dragResponseTime_s", new Situation
        {
            Tick = e.Tick, Time = e.Time, X = 0, Y = 0, HeadingRad = 0, SogKn = 0, StwKn = 0, RotDegPerMin = 0, RudderDeg = 0, RudderOrderDeg = 0,
            Telegraph = TelegraphOrder.STOP, Rpm = 0, ThrusterOrder = 0, AutopilotEnabled = false, UkcM = 10, SquatM = 0, WaterDepthM = 20, MaxDraftM = 6, LoaM = 105, VelE = 0, VelN = 0,
        })!.Value, 6);
        Apply(e, SimCommand.Telegraph(TelegraphOrder.DSAH));
        RunTo(e, t0 + 30);
        var an = e.Assessment.BuildSummary(e.Tick, true).Anchoring!;
        Assert.Equal(drag.T, an.FirstDragAtS);
        Assert.Equal(t0, an.DragResponseAtS);
        Assert.Equal(t0 - drag.T, an.DragResponseTimeS!.Value, 9);
        Assert.Equal(ActionKinds.TelegraphChange, an.DragResponseBy);
        Assert.True(an.DragEvents >= 1);
        Assert.True(an.DragTimeS >= 1);
    }

    [Fact]
    public void MetricNamesIncludeBerthingAndAnchoringAndSchemaDocumentsBlocks()
    {
        foreach (var n in BerthingTracker.MetricNameList) Assert.True(MetricNames.IsKnown(n), n);
        Assert.Equal(BerthingTracker.MetricNameList.Length, BerthingTracker.MetricNameList.Distinct().Count());
        var schema = File.ReadAllText(Path.Combine(TestData.Root, "src", "Contracts", "scenario.schema.json"));
        Assert.Contains("berthing.maxLateralSpeed_mps", schema);
        Assert.Contains("anchoring.minScopeRatio", schema);
    }

    [Fact]
    public void RecordReplayKeepsBerthingSummary()
    {
        var path = Path.Combine(TestData.TestRecordsDirectory, $"berthing-{Guid.NewGuid():N}.jsonl");
        var e = Alongside(0.15);
        using (var w = new RecordWriter(path, e))
        {
            RunTo(e, 30);
            w.Close();
        }
        var r = Replayer.Replay(path, TestData.Ship, (_, _) => new PointMassDynamics(PointMassKg, PointMassKg * 625.0));
        Assert.True(r.HashMatches);
        Assert.True(r.EventSequenceMatches, r.FirstEventMismatch);
        Assert.True(r.SummaryMatches);
        Assert.Equal(2, r.Summary!.Berthing!.FenderContacts);
        using var doc = JsonDocument.Parse(File.ReadLines(path).Last(l => l.Contains("\"kind\":\"summary\"")));
        Assert.Equal(2, doc.RootElement.GetProperty("summary").GetProperty("berthing").GetProperty("fenderContacts").GetInt32());
    }
}
