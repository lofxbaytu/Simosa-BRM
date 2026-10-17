using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 更新包的安全邊界(審查發現的回歸測試):manifest.remove 不得繞過「信任清單只能以 trust 包變更」與種類前綴、
/// packageId 白名單、trust 包變更後須仍有備援金鑰(含覆寫同名檔、同名替換)、zip 內重複 manifest、trust 包不可 rollback。
/// </summary>
public sealed class UpdateSecurityTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static string[] TrustFiles(InstallLayout ship)
        => Directory.EnumerateFiles(ship.TrustedKeysDir, "*.pem").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray()!;

    private string TrustPackage(IKeyProvider signer, string version, string installName, string pubPem, IEnumerable<string> removeKeyIds)
    {
        var src = _env.PathIn($"src-{version}-{installName}");
        File.WriteAllText(src, pubPem);
        var req = new UpdateBuildRequest
        {
            Version = version, Kind = UpdateKinds.Trust, Summary = "信任清單變更", TrustReason = "測試",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "SMS-CYB-001", Revision = "2", ApprovedBy = "DPA" } },
            Files = { new PackageSource($"config/trusted-keys/{installName}", src) },
        };
        req.RemoveKeyIds.AddRange(removeKeyIds);
        return UpdatePackageBuilder.Build(req, signer, _env.PathIn($"trust-{version}-{Guid.NewGuid():N}.sbupd")).OutputPath;
    }

    // ── manifest.remove ────────────────────────────────────

    [Theory]
    [InlineData("config/trusted-keys")]
    [InlineData("config/trusted-keys/backup.pem")]
    [InlineData("config\\trusted-keys\\backup.pem")]
    [InlineData("config//trusted-keys/backup.pem")]
    [InlineData("config/Trusted-Keys/backup.pem")]
    [InlineData("config/device")]
    [InlineData("config/device/device-key.pem")]
    [InlineData("config/packaging.json")]
    [InlineData("data/parameters/company-procedures.yaml")]
    [InlineData("app/")]
    public void SoftwarePackage_RemoveOutsideItsKind_Rejected(string remove)
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        TestEnv.ResignManifest(pkg, key, m => m.Remove.Add(remove));
        var filesBefore = TrustFiles(ship);
        var deviceFiles = Directory.EnumerateFiles(ship.DeviceDir).Count();

        var r = new UpdateApplier(ship).Apply(pkg, new UpdateApplyOptions { Via = "tcp" });
        Assert.False(r.Ok);
        Assert.NotEmpty(r.Errors);
        Assert.Equal(filesBefore, TrustFiles(ship));
        Assert.Equal(deviceFiles, Directory.EnumerateFiles(ship.DeviceDir).Count());
        Assert.True(File.Exists(ship.ConfigPath));
        Assert.True(File.Exists(Path.Combine(ship.Root, "data", "parameters", "company-procedures.yaml")));
        Assert.Equal("app 1.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
    }

    [Fact]
    public void SoftwarePackage_RemoveWithinApp_Applies()
    {
        var ship = _env.NewShip();
        File.WriteAllText(Path.Combine(ship.Root, "app", "old.dll"), "old");
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        TestEnv.ResignManifest(pkg, key, m => m.Remove.Add("app/old.dll"));
        var r = new UpdateApplier(ship).Apply(pkg);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        Assert.False(File.Exists(Path.Combine(ship.Root, "app", "old.dll")));
    }

    [Fact]
    public void TrustPackage_ManifestRemove_Rejected()
    {
        // trust 包只能以 trust.remove(金鑰 ID)移除金鑰,manifest.remove 不算入「仍有備援金鑰」檢查,一律拒絕
        var ship = _env.NewShip();
        using var backup = _env.Backup();
        var (_, p2) = TestEnv.NewKey(KeyRoles.Primary, "第二把主金鑰");
        var pkg = TrustPackage(backup, "2026.10.5", "p2.pem", p2, Array.Empty<string>());
        TestEnv.ResignManifest(pkg, backup, m => m.Remove.Add("config/trusted-keys/backup.pem"));
        var r = new UpdateApplier(ship).Apply(pkg);
        Assert.False(r.Ok);
        using var trust = TrustStore.LoadDirectory(ship.TrustedKeysDir);
        Assert.NotNull(trust.Find(backup.KeyId));
    }

    // ── packageId ──────────────────────────────────────────

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void InvalidPackageId_RejectedByVerifyAndBuilder(string packageId)
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var v1 = new UpdateApplier(ship).Apply(_env.SoftwarePackage("2026.10.1", key));
        Assert.True(v1.Ok, string.Join(";", v1.Errors));
        TestEnv.WriteRecord(ship.OutboxDir, "rec-FSB1-unbacked.sbrec", "E01_baseline", DateTime.UtcNow.AddHours(-1), 60);

        var pkg = _env.SoftwarePackage("2026.10.2", key, "app 3.0\n");
        TestEnv.ResignManifest(pkg, key, m => m.PackageId = packageId);
        var r = new UpdateApplier(ship).Apply(pkg);
        Assert.False(r.Ok);
        Assert.True(Directory.Exists(ship.StateDir));
        Assert.True(File.Exists(ship.InstalledStatePath));
        Assert.True(File.Exists(Path.Combine(ship.OutboxDir, "rec-FSB1-unbacked.sbrec")));
        Assert.Contains(ship.Audit().ReadAll(), l => l["event"]!.GetValue<string>() == AuditEvents.UpdateApplied);
        Assert.Equal("app 2.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));

        if (packageId.Length > 0)
        {
            var src = _env.PathIn("pid.txt");
            File.WriteAllText(src, "x");
            Assert.Throws<PackageBuildException>(() => UpdatePackageBuilder.Build(new UpdateBuildRequest
            {
                PackageId = packageId, Version = "2026.10.3", Kind = UpdateKinds.Software, Summary = "x", Files = { new PackageSource("app/v.txt", src) },
            }, key, _env.PathIn("pid.sbupd")));
        }
    }

    // ── trust 包:變更後須仍有備援金鑰 ──────────────────────

    [Fact]
    public void TrustAdd_OverwritingExistingBackupFile_Rejected()
    {
        var ship = _env.NewShip();
        using var backup = _env.Backup();
        var (_, p2) = TestEnv.NewKey(KeyRoles.Primary, "覆寫備援的主金鑰");
        var pkg = TrustPackage(backup, "2026.10.5", "backup.pem", p2, Array.Empty<string>());
        var r = new UpdateApplier(ship).Apply(pkg);
        Assert.False(r.Ok);
        using var trust = TrustStore.LoadDirectory(ship.TrustedKeysDir);
        Assert.NotNull(trust.Find(backup.KeyId));
        Assert.Contains(trust.Keys, k => k.Role == KeyRoles.Backup);
    }

    [Fact]
    public void TrustAdd_SameFileNameAsRemovedKey_ReplacesKey()
    {
        // 沿用 backup.pem 檔名更換備援金鑰:新公鑰不得被當成移除項刪掉
        var ship = _env.NewShip();
        using var oldBackup = _env.Backup();
        var (newBackupPriv, newBackupPub) = TestEnv.NewKey(KeyRoles.Backup, "新備援金鑰");
        using var newBackup = new PemKeyProvider(newBackupPriv);
        var applier = new UpdateApplier(ship);
        var r = applier.Apply(TrustPackage(oldBackup, "2026.10.5", "backup.pem", newBackupPub, new[] { oldBackup.KeyId }));
        Assert.True(r.Ok, string.Join(";", r.Errors));
        Assert.Equal(new[] { "backup.pem", "primary.pem" }, TrustFiles(ship));
        using (var trust = TrustStore.LoadDirectory(ship.TrustedKeysDir))
        {
            Assert.Null(trust.Find(oldBackup.KeyId));
            Assert.Equal(KeyRoles.Backup, trust.Find(newBackup.KeyId)?.Role);
        }

        // 新備援金鑰之後可再簽 trust 包
        var (_, p3) = TestEnv.NewKey(KeyRoles.Primary, "第三把主金鑰");
        var next = applier.Apply(TrustPackage(newBackup, "2026.10.6", "p3.pem", p3, Array.Empty<string>()));
        Assert.True(next.Ok, string.Join(";", next.Errors));
    }

    [Fact]
    public void TrustAdd_SameFileNameAsRemovedPrimary_ReplacesPrimary()
    {
        var ship = _env.NewShip();
        using var backup = _env.Backup();
        using var oldPrimary = _env.Primary();
        var (newPriv, newPub) = TestEnv.NewKey(KeyRoles.Primary, "新主金鑰");
        using var newPrimary = new PemKeyProvider(newPriv);
        var applier = new UpdateApplier(ship);
        var r = applier.Apply(TrustPackage(backup, "2026.10.5", "primary.pem", newPub, new[] { oldPrimary.KeyId }));
        Assert.True(r.Ok, string.Join(";", r.Errors));
        Assert.True(applier.Apply(_env.SoftwarePackage("2026.10.6", newPrimary)).Ok);
    }

    // ── rollback 不得撤回信任清單輪替 ───────────────────────

    [Fact]
    public void Rollback_OfTrustPackage_Refused()
    {
        var ship = _env.NewShip();
        using var backup = _env.Backup();
        using var oldPrimary = _env.Primary();
        var (_, newPub) = TestEnv.NewKey(KeyRoles.Primary, "primary-2027");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(TrustPackage(backup, "2026.10.5", "primary-2027.pem", newPub, new[] { oldPrimary.KeyId })).Ok);
        Assert.Equal(new[] { "backup.pem", "primary-2027.pem" }, TrustFiles(ship));

        var rb = applier.Rollback("測試");
        Assert.False(rb.Ok);
        Assert.Equal(new[] { "backup.pem", "primary-2027.pem" }, TrustFiles(ship));
        Assert.False(applier.Apply(_env.SoftwarePackage("2026.10.9", oldPrimary, name: "by-revoked.sbupd")).Ok);
        Assert.False(new Sync.ShipNode(ship).Status().RollbackAvailable);
    }

    // ── zip 內重複項目 ──────────────────────────────────────

    [Fact]
    public void DuplicateManifestEntry_InUpdatePackage_Rejected()
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        TestEnv.AddZipEntry(pkg, UpdateManifest.ManifestEntry, "{\"format\":\"simosa-update/1\",\"summary\":\"偽造\"}"u8.ToArray());
        var v = new UpdateApplier(ship).Verify(pkg);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("重複"));
    }
}
