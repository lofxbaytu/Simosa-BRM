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

    /// <summary>
    /// 離泊後再碰回(berthing.maxReContactSpeed_mps):質點以北向流 0.15 m/s 靠上碰墊(接近接觸,不是再碰回;線性無阻尼碰墊彈性反彈,
    /// 對水速度 −0.30 m/s)→ t = 20 s 流轉南 0.5 kn → 船體距碼頭線 &gt; berth.departedDistance_m(15 m)時「已離開泊位」(departedAt_s)
    /// → t = 120 s 流轉北 0.9 kn,對地以 0.463 − 0.30 ≈ 0.163 m/s 推回 → 再碰回的碰墊接觸標 reContact、指標 = 其接觸點法向速度
    /// (介於 approachSpeed 0.15 與 approachSpeedFail 0.20 → warn)。
    /// 已離開前的離開/反彈(船體距碼頭線 ≤ 門檻)不會成立 departed。紀錄 → 重播 summary 一致;快照 JSON 往返後續跑相同。
    /// </summary>
    [Fact]
    public void ReContactAfterDepartureIsSeparateFromApproachSpeed()
    {
        var e = Alongside(0.15, """
              bands:
                - { id: reContactSpeed, metric: berthing.maxReContactSpeed_mps, warn: "${params:berthing.approachSpeed_mps}", fail: "${params:berthing.approachSpeedFail_mps}", penalty: 10, failPenalty: 25 }
            """, extra: "  departedDistance_m: 15");
        Assert.Equal(15.0, e.Assessment!.Berthing.DepartedDistanceM);
        Assert.Equal(BerthingTracker.DefaultDepartedDistanceM, Alongside(0.15).Assessment!.Berthing.DepartedDistanceM);
        var path = Path.Combine(TestData.TestRecordsDirectory, $"recontact-{Guid.NewGuid():N}.jsonl");
        using (var w = new RecordWriter(path, e))
        {
            RunTo(e, 20);
            var s0 = e.Assessment.BuildSummary(e.Tick, final: false).Berthing!;
            Assert.True(s0.FenderContacts >= 2);
            Assert.All(s0.Contacts, c => Assert.False(c.ReContact));
            Assert.Null(s0.DepartedAtS);
            Apply(e, SimCommand.SetEnvironment(currentSetDeg: 180, currentDriftKn: 0.5));
            RunTo(e, 120);
            var s1 = e.Assessment.BuildSummary(e.Tick, final: false).Berthing!;
            Assert.NotNull(s1.DepartedAtS);
            Assert.Null(s1.MaxReContactSpeedMps);
            Assert.Null(e.Assessment.MetricValue("berthing.maxReContactSpeed_mps", null!));
            Assert.True(e.Assessment.Berthing.State.Departed);
            Apply(e, SimCommand.SetEnvironment(currentSetDeg: 0, currentDriftKn: 0.9));
            RunTo(e, 500);
            w.Close();
        }
        var s = e.Assessment.BuildSummary(e.Tick, final: true);
        var b = s.Berthing!;
        var re = b.Contacts.Where(c => c.ReContact).ToList();
        Assert.Equal(2, re.Count);                       // 兩個碰墊各一次再碰回
        Assert.All(re, c => Assert.True(c.TS > b.DepartedAtS));
        var v = Units.KnToMps(0.9) - 2 * 0.15;
        Assert.Equal(v, b.MaxReContactSpeedMps!.Value, 2);
        Assert.Equal(re.Max(c => c.NormalSpeedMps), b.MaxReContactSpeedMps.Value, 12);
        Assert.Equal(2, b.ReContacts);
        Assert.Equal(b.MaxReContactSpeedMps, e.Assessment.MetricValue("berthing.maxReContactSpeed_mps", null!));
        Assert.Equal(v, b.MaxLateralSpeedMps!.Value, 2); // 再碰回也是接近接觸(maxLateralSpeed 為全部接近接觸;離泊情境以 maxReContactSpeed 評估)
        Assert.Equal("warn", s.Assessment.Bands.Single(x => x.Id == "reContactSpeed").State);
        var json = JsonSerializer.Serialize(b, ContractJson.Options);
        Assert.Contains("\"reContact\":true", json);
        Assert.Contains("\"maxReContactSpeed_mps\"", json);
        var r = Replayer.Replay(path, TestData.Ship, (_, _) => new PointMassDynamics(PointMassKg, PointMassKg * 625.0));
        Assert.True(r.HashMatches);
        Assert.True(r.SummaryMatches);
        Assert.Equal(b.MaxReContactSpeedMps, r.Summary!.Berthing!.MaxReContactSpeedMps);
        // 舊格式:未使用者 summary/快照不寫出新欄位
        var plain = Alongside(0.15);
        RunTo(plain, 30);
        var pj = JsonSerializer.Serialize(plain.Assessment!.BuildSummary(plain.Tick, true).Berthing!, ContractJson.Options);
        Assert.DoesNotContain("reContact", pj);
        Assert.DoesNotContain("departedAt_s", pj);
        Assert.DoesNotContain("\"departed", JsonSerializer.Serialize(plain.Assessment.Berthing.State, ContractJson.Options).ToLowerInvariant());
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
    /// 靠上的定義:拖船持續推靠時在碰墊上反彈再碰回(離開 &lt; 0.5 m)只是同一次靠上的再接觸(approach = false;第三十輪起不逐筆記入 contacts[],
    /// 只計入 heldContacts 與 fenderContacts 等彙總);拉離 ≥ 0.5 m 後再推回才是新的靠上。maxLateralSpeed = 接近接觸的接觸點法向速度最大值(第二次靠上較快)。
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
        Assert.Equal(b1.FenderContacts, b1.Contacts.Count + b1.HeldContacts);       // 反彈再碰回只計數(heldContacts)
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
    /// 第三十輪 ENG-C1:同一碰墊未離開 ≥ 0.5 m 的再接觸(此處為拖船推靠中的反彈,與繫泊中在纜繩上的擺盪同類)不逐筆記入 contacts[],
    /// 但 berthing.* 指標與 summary(次數、法向速度 / 能量 / 能量比最大值、過載數)仍等於「全部 fenderContact 事件」逐筆計算的值
    /// (= 修正前 contacts[] 逐筆記錄時的定義);每碰墊次數合計 = heldContacts;快照 JSON 往返續跑後 summary 相同;
    /// 舊格式狀態(每筆都在 contacts[]、無彙總欄位)還原時由清單重建彙總。
    /// </summary>
    [Fact]
    public void HeldContactsAreCountedNotListedAndMetricsEqualAllContactEvents()
    {
        var e = PointMass(Sc(LinearBerthYaml() + PushPullTugs, x: 0, y: 19.8, headingDeg: 90));
        var events = TestData.Capture(e);
        RunTo(e, 40);
        var snap = EngineSnapshot.FromJson(e.CreateSnapshot().ToJson());
        RunTo(e, 120);
        var a = e.Assessment!;
        var summaryJson = ContractJson.Serialize(a.BuildSummary(e.Tick, final: true));
        var b = a.BuildSummary(e.Tick, final: true).Berthing!;

        var fc = events.Where(x => x.Kind == SimEventKinds.FenderContact).ToList();
        Assert.True(fc.Count > 2, $"推靠中反覆碰回 {fc.Count}");
        static double Speed(SimEvent x) => x.DataNumber("normalSpeed_mps") ?? 0.0;
        static double Energy(SimEvent x) => x.DataNumber("energy_kNm") ?? 0.0;
        static double? Ratio(SimEvent x) => x.DataNumber("ratedEnergy_kNm") is { } r && r > 0 ? Energy(x) / r : null;
        static bool Overload(SimEvent x) => x.DataBool("overload") == true;
        void AssertMetricsEqualEvents(BerthingTracker t)
        {
            Assert.Equal(fc.Count, t.Metric("berthing.fenderContacts", 0));
            Assert.Equal(fc.Count(Overload), t.Metric("berthing.fenderOverloads", 0));
            Assert.Equal(fc.Max(Speed), t.Metric("berthing.maxContactSpeed_mps", 0));
            Assert.Equal(fc.Max(Energy), t.Metric("berthing.maxEnergy_kNm", 0));
            Assert.Equal(fc.Select(Ratio).Max(), t.Metric("berthing.maxEnergyRatio", 0));
        }
        AssertMetricsEqualEvents(a.Berthing);
        Assert.Equal(fc.Count, b.FenderContacts);
        Assert.Equal(fc.Count(Overload), b.FenderOverloads);
        Assert.Equal(fc.Max(Speed), b.MaxContactSpeedMps);
        Assert.Equal(fc.Max(Energy), b.MaxEnergyKNm);
        Assert.Equal(fc.Select(Ratio).Max(), b.MaxEnergyRatio);
        // 逐筆清單只留 F1、F2 的接近接觸;其餘為反彈再碰回,只計數(每碰墊次數合計 = heldContacts)
        Assert.Equal(2, b.Contacts.Count);
        Assert.All(b.Contacts, c => Assert.True(c.Approach));
        Assert.Equal(fc.Count - 2, b.HeldContacts);
        Assert.Equal(b.HeldContacts, b.HeldContactsByFender!.Values.Sum());
        Assert.Equal(new[] { "F1", "F2" }, b.HeldContactsByFender.Keys.ToArray());
        Assert.Equal(b.HeldContacts, a.Berthing.State.HeldContacts);
        Assert.Contains("\"heldContacts\":", ContractJson.Serialize(b));

        // 快照 JSON 往返後續跑:summary 相同
        var r = PointMass(Sc(LinearBerthYaml() + PushPullTugs, x: 0, y: 19.8, headingDeg: 90));
        r.Restore(snap);
        RunTo(r, 120);
        Assert.Equal(e.StateHash, r.StateHash);
        Assert.Equal(summaryJson, ContractJson.Serialize(r.Assessment!.BuildSummary(r.Tick, final: true)));

        // 舊格式狀態(第三十輪以前:每筆接觸都在 contacts[]、無彙總欄位)還原時由清單重建彙總
        var old = ContractJson.Deserialize<BerthingState>(ContractJson.Serialize(a.Berthing.State))!;
        old.Contacts = fc.Select(x => new FenderContactMetric
        {
            FenderId = x.DataString("fenderId") ?? "", TS = x.T, NormalSpeedMps = Speed(x), EnergyKNm = Energy(x), EnergyRatio = Ratio(x), Overload = Overload(x),
        }).ToList();
        old.ContactCount = old.ContactOverloads = old.HeldContacts = 0;
        old.MaxContactSpeedMps = old.MaxContactEnergyKNm = old.MaxContactEnergyRatio = null;
        old.HeldContactsByFender = null;
        var oldJson = ContractJson.Serialize(old);
        Assert.DoesNotContain("contactCount", oldJson);
        a.Berthing.Restore(ContractJson.Deserialize<BerthingState>(oldJson)!);
        AssertMetricsEqualEvents(a.Berthing);
    }

    /// <summary>
    /// 第三十輪審查 REV-ENG-C1:已離開泊位後被壓回碰墊並停在上面(沒有重新繫妥;E14 / E17 開艏時主機起動失敗、被吹攏風壓回碼頭)時,
    /// 同一碰墊未離開 ≥ 0.5 m 的擺盪接觸也不逐筆記入 contacts[]:停在碰墊上的時間越久,contacts[] 與評估快照都不變長,只有 heldContacts 增加;
    /// 每個碰墊第一次再碰回仍逐筆記錄(reContact),reContacts 與 berthing.maxReContactSpeed_mps 仍以「已離開泊位後的全部接觸事件」計算。
    /// 以流把船帶離 &gt; departedDistance_m,再以拖船 TA 推回;擺盪以拖船出力週期切換模擬(TA 持續推靠、TB 每 40 s 短暫拉離,離開 &lt; 0.5 m);
    /// 碰墊加阻尼,使推回時的反彈很快衰減。
    /// </summary>
    [Fact]
    public void PinnedAfterDepartureReContactsAreCountedNotListed()
    {
        var e = PointMass(Sc(LinearBerthYaml(damping: 3000) + "\n  departedDistance_m: 15" + PushPullTugs, x: 0, y: 19.8, headingDeg: 90));
        var events = TestData.Capture(e);
        var st = () => e.Assessment!.Berthing.State;
        RunTo(e, 40);                                                                // 推靠:F1、F2 靠上,反彈衰減後停在碰墊上
        Apply(e, SimCommand.TugControl("TA", "push", 270, 0));
        Apply(e, SimCommand.SetEnvironment(currentSetDeg: 180, currentDriftKn: 0.5));  // 流把船帶離碼頭(質點:對地速度 = 流速)
        while (!st().Departed && e.Time < 400) RunTo(e, e.Time + 1);                 // 船體距碼頭線 > departedDistance_m(15 m)
        Assert.True(st().Departed, $"未離開泊位 t={e.Time}");
        var departedAt = st().DepartedAtS!.Value;
        Assert.Empty(st().HeldFenders);
        Apply(e, SimCommand.SetEnvironment(currentSetDeg: 180, currentDriftKn: 0));
        Apply(e, SimCommand.TugControl("TA", "push", 270, 10));                      // 推回碼頭(沒有纜繩:不會繫妥)
        var t0 = e.Time;
        while (!st().Contacts.Any(c => c.ReContact) && e.Time < t0 + 600) RunTo(e, e.Time + 1);
        Assert.True(st().Contacts.Any(c => c.ReContact), "未再碰回");
        RunTo(e, e.Time + 120);                                                      // 停在碰墊上,反彈衰減
        var c1 = st().Contacts.Count;
        var h1 = st().HeldContacts;
        var r1 = st().ReContacts;
        var state1 = ContractJson.Serialize(st()).Length;
        var summary1 = ContractJson.Serialize(e.Assessment!.BuildSummary(e.Tick, final: false).Berthing).Length;
        Assert.True(st().Departed);                                                  // 沒有重新繫妥:仍是「已離開泊位」
        Assert.All(new[] { "F1", "F2" }, f => Assert.Contains(st().Contacts, c => c.FenderId == f && c.ReContact && c.TS > departedAt));
        // 停在碰墊上擺盪 20 個週期(每 40 s:TB 拉 25 % 8 s,離開 < 0.5 m 再被 TA 推回)
        for (var k = 0; k < 20; k++)
        {
            Apply(e, SimCommand.TugControl("TB", "pull", 90, 25));
            RunTo(e, e.Time + 8);
            Apply(e, SimCommand.TugControl("TB", "pull", 90, 0));
            RunTo(e, e.Time + 32);
        }
        var s = st();
        Assert.True(s.Departed);
        // 擺盪接觸只計數:contacts[] 筆數不變(修正前每筆都以 reContact 逐筆記錄,20 個週期 +40 筆)、評估狀態與 summary 不隨時間變長
        Assert.True(s.HeldContacts - h1 >= 20, $"擺盪接觸 {s.HeldContacts - h1}");
        Assert.Equal(c1, s.Contacts.Count);
        Assert.InRange(ContractJson.Serialize(s).Length - state1, -64, 64);         // 逐筆記錄時每筆約 250 B
        var summary = e.Assessment.BuildSummary(e.Tick, final: true);
        var b = summary.Berthing!;
        Assert.InRange(ContractJson.Serialize(b).Length - summary1, -64, 64);
        // 已離開泊位後的擺盪接觸仍是再碰回:reContacts 與 maxReContactSpeed 以已離開泊位後的全部接觸事件計算
        Assert.Equal(s.HeldContacts - h1, s.ReContacts - r1);
        var fc = events.Where(x => x.Kind == SimEventKinds.FenderContact).ToList();
        var after = fc.Where(x => x.T > departedAt).ToList();
        Assert.Equal(0, b.QuayContacts);
        Assert.Equal(after.Count, b.ReContacts);
        Assert.Equal(after.Max(x => x.DataNumber("normalSpeed_mps")), b.MaxReContactSpeedMps);
        Assert.Equal(b.MaxReContactSpeedMps, e.Assessment.MetricValue("berthing.maxReContactSpeed_mps", null!));
        // 全部接觸的彙總不變:次數、最大法向速度 = 全部 fenderContact 事件;逐筆 + 只計數 = 全部
        Assert.Equal(fc.Count, b.FenderContacts);
        Assert.Equal(fc.Max(x => x.DataNumber("normalSpeed_mps")), b.MaxContactSpeedMps);
        Assert.Equal(b.FenderContacts, b.Contacts.Count + b.HeldContacts);
        Assert.Equal(b.HeldContacts, b.HeldContactsByFender!.Values.Sum());
        Assert.Equal(s.ReContacts, b.ReContacts);
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
