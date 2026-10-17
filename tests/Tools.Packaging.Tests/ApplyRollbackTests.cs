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

    /// <summary>提交步驟刪不掉舊的上一版(Windows 檔案鎖定):新版已上線,須回報成功加警告,rollback 仍回到正確的上一版。</summary>
    [Fact]
    public void CommitStep_StaleDeleteFails_ReportsAppliedAndRollbackWorks()
    {
        var ship = _env.NewShip();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(MixedPackage("2026.10.1", "app 2.0\n", "chart A")).Ok);
        var r = applier.Apply(MixedPackage("2026.10.2", "app 3.0\n", "chart B"), new UpdateApplyOptions
        {
            CommitStep = step =>
            {
                if (step == "delete-stale") throw new UnauthorizedAccessException("Access to the path 'state/previous/app/locked/x.dll' is denied.");
            },
        });
        Assert.True(r.Ok, string.Join(";", r.Errors));
        Assert.False(r.RolledBack);
        Assert.NotEmpty(r.Warnings);
        Assert.Equal("app 3.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        Assert.Equal("2026.10.2", InstalledState.LoadOrNew(ship.InstalledStatePath).Version);
        var events = ship.Audit().ReadAll().Select(l => l["event"]!.GetValue<string>()).ToList();
        Assert.Equal(2, events.Count(e => e == AuditEvents.UpdateApplied));
        Assert.DoesNotContain(AuditEvents.UpdateFailed, events);

        var rb = applier.Rollback("測試");
        Assert.True(rb.Ok, string.Join(";", rb.Errors));
        Assert.Equal("app 2.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        Assert.Equal("2026.10.1", InstalledState.LoadOrNew(ship.InstalledStatePath).Version);
    }

    /// <summary>提交步驟連舊的上一版都移不開:新版已上線(回報成功加警告),上一版元件另存不被刪除,rollback 不會回到錯誤的版本。</summary>
    [Fact]
    public void CommitStep_MovePreviousFails_KeepsReplacedComponents_AndRollbackRefusesWrongTarget()
    {
        var ship = _env.NewShip();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(MixedPackage("2026.10.1", "app 2.0\n", "chart A")).Ok);
        var r = applier.Apply(MixedPackage("2026.10.2", "app 3.0\n", "chart B"), new UpdateApplyOptions
        {
            CommitStep = step =>
            {
                if (step == "move-previous") throw new IOException("The process cannot access the file because it is being used by another process.");
            },
        });
        Assert.True(r.Ok, string.Join(";", r.Errors));
        Assert.NotEmpty(r.Warnings);
        Assert.Equal("app 3.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        Assert.Equal("2026.10.2", InstalledState.LoadOrNew(ship.InstalledStatePath).HighestVersion);
        // 被替換掉的 2026.10.1 元件仍保存在 state/ 之下某處
        Assert.Contains(Directory.EnumerateFiles(ship.StateDir, "version.txt", SearchOption.AllDirectories), f => File.ReadAllText(f) == "app 2.0\n");

        var rb = applier.Rollback("測試");
        Assert.False(rb.Ok);
        Assert.Equal("app 3.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        Assert.Equal("2026.10.2", InstalledState.LoadOrNew(ship.InstalledStatePath).Version);
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
