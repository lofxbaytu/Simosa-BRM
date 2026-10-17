using SimosaBRM.Packaging.Impact;
using SimosaBRM.Packaging.Updates;
using SimosaBRM.SimCore.Parameters;

namespace SimosaBRM.Packaging.Tests;

/// <summary>影響分析與驗證把關(規劃書第 9.1 節;第 14 章「SMS 更新流程」:未驗證的更新包無法簽章與套用)。</summary>
public sealed class ImpactAndValidationTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string ScenarioPath(string id) => Path.Combine(_env.InstructorRoot, "data", "scenarios", id + ".yaml");

    private ImpactReport Analyze(string newParams)
        => ImpactAnalyzer.Analyze(new ImpactRequest
        {
            NewSet = ParameterSetLoader.Load(newParams), Baseline = ParameterSetLoader.Load(_env.BaselineParameters),
            ScenarioFiles = Directory.EnumerateFiles(Path.Combine(_env.InstructorRoot, "data", "scenarios"), "*.yaml").ToList(),
            DataRoot = _env.InstructorRoot, DisplayRoot = _env.InstructorRoot,
        });

    private UpdateBuildRequest ParamRequest(string newParams, string? verification = null, bool force = false) => new()
    {
        Version = "2026.10.2", Kind = UpdateKinds.Parameters, Summary = "DWO-007 第 4 版:沿岸 CPA 0.5 → 0.6 nm",
        SmsDocuments = { new SmsDocumentRef { DocumentId = "DWO-007", Revision = "4", ApprovedBy = "DPA 測試", ApprovedOn = "2026-10-15" } },
        Files = { new PackageSource("data/parameters/company-procedures.yaml", newParams) },
        ValidationRoot = _env.InstructorRoot, BaselineParameters = _env.BaselineParameters, VerificationRecordPath = verification,
        Force = force, ForceReason = force ? "緊急派送(測試)" : null,
    };

    [Fact]
    public void Impact_ListsScenariosAndAssessmentRulesReferencingChangedKey()
    {
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        var rep = Analyze(p);
        var change = Assert.Single(rep.Changes);
        Assert.Equal("navigation.cpa.coastal_nm", change.Key);
        Assert.Equal(ParameterChangeKinds.Changed, change.Change);
        var e01 = Assert.Single(rep.Affected);
        Assert.Equal("E01_baseline", e01.ScenarioId);
        Assert.Contains("navigation.cpa.coastal_nm", e01.ChangedKeys);
        Assert.Contains(e01.AssessmentRules, r => r.Contains("bands[") && r.Contains("minCpa"));
        Assert.Contains("E07_steering_failure", rep.Unaffected);
        Assert.False(rep.HasErrors, string.Join(";", e01.Issues.Select(i => i.ToString())));
    }

    [Fact]
    public void Impact_SharedKeyAffectsAllReferencingScenarios()
    {
        var p = _env.EditParameter("manoeuvring.rotLimit_degPerMin", "30", "25");
        var rep = Analyze(p);
        var ids = rep.Affected.Select(a => a.ScenarioId).ToHashSet();
        Assert.Contains("E01_baseline", ids);
        Assert.Contains("E07_steering_failure", ids);
        Assert.Contains("E09_blackout", ids);
    }

    [Fact]
    public void Pack_WithoutVerification_Rejected()
    {
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        using var key = _env.Primary();
        var ex = Assert.Throws<PackageBuildException>(() => UpdatePackageBuilder.Build(ParamRequest(p), key, _env.PathIn("p.sbupd")));
        Assert.Contains(ex.Problems, x => x.Contains("E01_baseline") && x.Contains("未"));
        Assert.False(File.Exists(_env.PathIn("p.sbupd")));
    }

    [Fact]
    public void Pack_WithVerification_Succeeds_AndShipApplies()
    {
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        var set = ParameterSetLoader.Load(p);
        var vr = _env.PathIn("verification.json");
        var rec = new VerificationRecord();
        TestEnv.Verify(rec, ScenarioPath("E01_baseline"), set, "E01_baseline");
        rec.Save(vr);
        using var key = _env.Primary();
        var res = UpdatePackageBuilder.Build(ParamRequest(p, vr), key, _env.PathIn("p.sbupd"));
        Assert.Equal(ValidationStatuses.Verified, res.Manifest.Validation.Status);
        var a = Assert.Single(res.Manifest.AffectedScenarios);
        Assert.NotNull(a.Verification);
        Assert.Equal("2026.10.2", res.Manifest.ParameterSet!.Version);
        Assert.Equal("4", res.Manifest.SmsDocuments[0].Revision);

        var ship = _env.NewShip();
        var r = new UpdateApplier(ship).Apply(res.OutputPath);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        var installed = InstalledState.LoadOrNew(ship.InstalledStatePath);
        Assert.Equal("2026.10.2", installed.ParameterSet!.Version);
        Assert.Equal("2026.10.2", installed.Components[UpdateKinds.Parameters].Version);
        Assert.Contains("value: 0.6,", File.ReadAllText(Path.Combine(ship.Root, "data", "parameters", "company-procedures.yaml")));
    }

    [Fact]
    public void VerificationForOtherParameterContent_DoesNotCount()
    {
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        var stale = ParameterSetLoader.Load(_env.BaselineParameters); // 以舊參數集做的驗證
        var vr = _env.PathIn("verification.json");
        var rec = new VerificationRecord();
        TestEnv.Verify(rec, ScenarioPath("E01_baseline"), stale, "E01_baseline");
        rec.Save(vr);
        using var key = _env.Primary();
        Assert.Throws<PackageBuildException>(() => UpdatePackageBuilder.Build(ParamRequest(p, vr), key, _env.PathIn("p.sbupd")));
    }

    [Fact]
    public void Force_IsRecorded_AndShipRejectsUnlessAllowed()
    {
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        using var key = _env.Primary();
        var res = UpdatePackageBuilder.Build(ParamRequest(p, force: true), key, _env.PathIn("forced.sbupd"));
        Assert.Equal(ValidationStatuses.Forced, res.Manifest.Validation.Status);
        Assert.Contains("緊急派送", res.Manifest.Validation.ForcedReason);
        Assert.NotEmpty(res.Manifest.Validation.Problems);

        var ship = _env.NewShip();
        var applier = new UpdateApplier(ship);
        var r = applier.Apply(res.OutputPath);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("未通過受影響情境驗證"));
        var allowed = applier.Apply(res.OutputPath, new UpdateApplyOptions { AllowUnverified = true, Operator = "船上管理者" });
        Assert.True(allowed.Ok, string.Join(";", allowed.Errors));
        Assert.NotEmpty(allowed.Warnings);
    }

    [Fact]
    public void ChangedParametersWithoutVersionBump_Rejected()
    {
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6", bumpVersion: null);
        var set = ParameterSetLoader.Load(p);
        var vr = _env.PathIn("verification.json");
        var rec = new VerificationRecord();
        TestEnv.Verify(rec, ScenarioPath("E01_baseline"), set, "E01_baseline");
        rec.Save(vr);
        using var key = _env.Primary();
        var ex = Assert.Throws<PackageBuildException>(() => UpdatePackageBuilder.Build(ParamRequest(p, vr), key, _env.PathIn("p.sbupd")));
        Assert.Contains(ex.Problems, x => x.Contains("未大於舊版次"));
    }

    [Fact]
    public void RemovedKeyStillReferenced_IsError()
    {
        var path = _env.PathIn("removed.yaml");
        var lines = File.ReadAllLines(_env.BaselineParameters).Where(l => !l.TrimStart().StartsWith("navigation.cpa.coastal_nm:", StringComparison.Ordinal));
        File.WriteAllLines(path, lines.Select(l => l.Replace("version: \"2026.10.1\"", "version: \"2026.10.2\"")));
        var rep = Analyze(path);
        Assert.Contains(rep.Changes, c => c.Key == "navigation.cpa.coastal_nm" && c.Change == ParameterChangeKinds.Removed);
        var e01 = Assert.Single(rep.Affected, a => a.ScenarioId == "E01_baseline");
        Assert.NotNull(e01.LoadError);
        Assert.True(rep.HasErrors);

        // 即使有驗證紀錄,錯誤仍拒絕
        using var key = _env.Primary();
        Assert.Throws<PackageBuildException>(() => UpdatePackageBuilder.Build(ParamRequest(path), key, _env.PathIn("r.sbupd")));
    }

    [Fact]
    public void ScenarioWithErrors_Rejected()
    {
        // 初始位置的 UKC ≤ 0(水深改為 1 m)→ ScenarioValidator error
        var src = ScenarioPath("E01_baseline");
        var broken = _env.PathIn("E01_baseline.yaml");
        File.WriteAllText(broken, File.ReadAllText(src).Replace("waterDepth: 30", "waterDepth: 1"));
        Assert.NotEqual(File.ReadAllText(src), File.ReadAllText(broken));
        using var key = _env.Primary();
        var ex = Assert.Throws<PackageBuildException>(() => UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "2026.10.3", Kind = UpdateKinds.Scenarios, Summary = "E01 改水深",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "BRM-TRN-001", Revision = "draft", ApprovedBy = "訪船船長" } },
            Files = { new PackageSource("data/scenarios/E01_baseline.yaml", broken) }, ValidationRoot = _env.InstructorRoot,
        }, key, _env.PathIn("s.sbupd")));
        Assert.Contains(ex.Problems, p => p.Contains("E01_baseline") && p.Contains("錯誤"));
    }
}
