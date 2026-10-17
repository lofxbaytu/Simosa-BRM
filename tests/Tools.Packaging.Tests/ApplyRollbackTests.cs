using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>套用:原子替換、上一版保留、rollback、中途失敗完整回復。</summary>
public sealed class ApplyRollbackTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string MixedPackage(string version, string appText, string chartText)
    {
        var a = _env.PathIn($"app-{version}.txt");
        var c = _env.PathIn($"chart-{version}.txt");
        File.WriteAllText(a, appText);
        File.WriteAllText(c, chartText);
        using var key = _env.Primary();
        return UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = version, Kind = UpdateKinds.Mixed, Summary = "軟體與圖資",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "BRM-TRN-001", Revision = "1", ApprovedBy = "訪船船長" } },
            Files = { new PackageSource("app/version.txt", a), new PackageSource("data/charts/test.txt", c) },
        }, key, _env.PathIn($"mixed-{version}.sbupd")).OutputPath;
    }

    [Fact]
    public void Apply_ThenRollback_RestoresPreviousVersion()
    {
        var ship = _env.NewShip();
        var applier = new UpdateApplier(ship);
        var r1 = applier.Apply(MixedPackage("2026.10.1", "app 2.0\n", "chart A"));
        Assert.True(r1.Ok, string.Join(";", r1.Errors));
        Assert.Equal(new[] { "app", "data" }, r1.Components);
        var r2 = applier.Apply(MixedPackage("2026.10.2", "app 3.0\n", "chart B"));
        Assert.True(r2.Ok, string.Join(";", r2.Errors));
        Assert.Equal("app 3.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        // 未變更的檔案保留
        Assert.True(File.Exists(Path.Combine(ship.Root, "data", "parameters", "company-procedures.yaml")));
        Assert.True(Directory.Exists(ship.PreviousDir));

        var rb = applier.Rollback("測試");
        Assert.True(rb.Ok, string.Join(";", rb.Errors));
        Assert.Equal("app 2.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        Assert.Equal("chart A", File.ReadAllText(Path.Combine(ship.Root, "data", "charts", "test.txt")));
        var st = InstalledState.LoadOrNew(ship.InstalledStatePath);
        Assert.Equal("2026.10.1", st.Version);
        Assert.Equal("2026.10.2", st.HighestVersion); // 不下降:被撤回的版本不能再套用
        Assert.True(st.History.First(h => h.Version == "2026.10.2").RolledBack);
        Assert.False(Directory.Exists(ship.PreviousDir));
        Assert.False(applier.Rollback().Ok); // 只保留一層
        Assert.Contains(ship.Audit().ReadAll(), l => l["event"]!.GetValue<string>() == AuditEvents.UpdateRolledBack);
    }

    [Fact]
    public void FailureMidSwap_FullyRestores()
    {
        var ship = _env.NewShip();
        var before = File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt"));
        var dataFiles = Directory.EnumerateFiles(Path.Combine(ship.Root, "data"), "*", SearchOption.AllDirectories).Count();
        var r = new UpdateApplier(ship).Apply(MixedPackage("2026.10.1", "app 2.0\n", "chart A"), new UpdateApplyOptions
        {
            AfterComponentSwap = comp =>
            {
                if (comp == "data") throw new IOException("模擬磁碟錯誤");
            },
        });
        Assert.False(r.Ok);
        Assert.True(r.RolledBack);
        Assert.Equal(before, File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        Assert.False(File.Exists(Path.Combine(ship.Root, "data", "charts", "test.txt")));
        Assert.Equal(dataFiles, Directory.EnumerateFiles(Path.Combine(ship.Root, "data"), "*", SearchOption.AllDirectories).Count());
        Assert.False(File.Exists(ship.InstalledStatePath));
        Assert.False(Directory.Exists(Path.Combine(ship.StateDir, "next")));
        Assert.False(Directory.Exists(Path.Combine(ship.StateDir, "previous.pending")));
        Assert.Contains(ship.Audit().ReadAll(), l => l["event"]!.GetValue<string>() == AuditEvents.UpdateFailed);

        // 回復後可正常重新套用同一包
        var again = new UpdateApplier(ship).Apply(MixedPackage("2026.10.1", "app 2.0\n", "chart A"));
        Assert.True(again.Ok, string.Join(";", again.Errors));
    }

    [Fact]
    public void AuditRecordsSignerVersionAndTime()
    {
        var ship = _env.NewShip();
        Assert.True(new UpdateApplier(ship).Apply(MixedPackage("2026.10.1", "app 2.0\n", "chart A"), new UpdateApplyOptions { Operator = "船長" }).Ok);
        var line = Assert.Single(ship.Audit().ReadAll(), l => l["event"]!.GetValue<string>() == AuditEvents.UpdateApplied);
        Assert.NotNull(line["ts"]);
        Assert.Equal("2026.10.1", line["details"]!["version"]!.GetValue<string>());
        Assert.Equal("primary", line["details"]!["signer"]!["role"]!.GetValue<string>());
        Assert.Equal("船長", line["details"]!["operator"]!.GetValue<string>());
    }
}
