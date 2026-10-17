using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>金鑰輪替:以備援金鑰簽章的信任更新包更換主金鑰(規劃書第 5.5 節),並記入稽核。</summary>
public sealed class KeyRotationTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string TrustPackage(IKeyProvider signer, string version, string newPubPem, IEnumerable<string> removeKeyIds, string name)
    {
        var src = _env.PathIn(name + ".pem");
        File.WriteAllText(src, newPubPem);
        var req = new UpdateBuildRequest
        {
            Version = version, Kind = UpdateKinds.Trust, Summary = "主金鑰遺失,更換", TrustReason = "主金鑰遺失(測試)",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "SMS-CYB-001", Revision = "2", ApprovedBy = "DPA" } },
            Files = { new PackageSource($"config/trusted-keys/{name}.pem", src) },
        };
        req.RemoveKeyIds.AddRange(removeKeyIds);
        return UpdatePackageBuilder.Build(req, signer, _env.PathIn(name + ".sbupd")).OutputPath;
    }

    [Fact]
    public void BackupSignedTrustPackage_ReplacesPrimary()
    {
        var ship = _env.NewShip();
        using var oldPrimary = _env.Primary();
        using var backup = _env.Backup();
        var (newPriv, newPub) = TestEnv.NewKey(KeyRoles.Primary, "新任訓練負責人");
        using var newPrimary = new PemKeyProvider(newPriv);

        var pkg = TrustPackage(backup, "2026.10.5", newPub, new[] { oldPrimary.KeyId }, "primary-2026");
        var applier = new UpdateApplier(ship);
        var r = applier.Apply(pkg);
        Assert.True(r.Ok, string.Join(";", r.Errors));
        Assert.Equal(KeyRoles.Backup, r.SignerRole);

        using (var trust = TrustStore.LoadDirectory(ship.TrustedKeysDir))
        {
            Assert.Null(trust.Find(oldPrimary.KeyId));
            Assert.NotNull(trust.Find(newPrimary.KeyId));
            Assert.NotNull(trust.Find(backup.KeyId));
        }
        var audit = ship.Audit().ReadAll();
        var rotated = Assert.Single(audit, l => l["event"]!.GetValue<string>() == AuditEvents.TrustRotated);
        Assert.Contains("主金鑰遺失", rotated["details"]!["reason"]!.GetValue<string>());

        // 舊主金鑰簽章者被拒;新主金鑰可套用
        var byOld = _env.SoftwarePackage("2026.10.6", oldPrimary, name: "by-old.sbupd");
        Assert.False(applier.Apply(byOld).Ok);
        var byNew = _env.SoftwarePackage("2026.10.7", newPrimary, name: "by-new.sbupd");
        var ok = applier.Apply(byNew);
        Assert.True(ok.Ok, string.Join(";", ok.Errors));
        Assert.Contains("新任訓練負責人", ok.Signer);
    }

    [Fact]
    public void PrimarySignedTrustPackage_Rejected()
    {
        var ship = _env.NewShip();
        using var primary = _env.Primary();
        var (_, pub) = TestEnv.NewKey(KeyRoles.Backup, "冒充的備援");
        var pkg = TrustPackage(primary, "2026.10.5", pub, Array.Empty<string>(), "fake-backup");
        var r = new UpdateApplier(ship).Apply(pkg);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("須以備援金鑰簽章"));
    }

    [Fact]
    public void TrustPackageRemovingLastBackup_Rejected()
    {
        var ship = _env.NewShip();
        using var backup = _env.Backup();
        var (_, pub) = TestEnv.NewKey(KeyRoles.Primary, "另一把主金鑰");
        var pkg = TrustPackage(backup, "2026.10.5", pub, new[] { backup.KeyId }, "primary-b");
        var r = new UpdateApplier(ship).Apply(pkg);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("沒有備援金鑰"));
        using var trust = TrustStore.LoadDirectory(ship.TrustedKeysDir);
        Assert.NotNull(trust.Find(backup.KeyId));
    }

    [Fact]
    public void TrustFilesInNonTrustPackage_Rejected()
    {
        using var key = _env.Primary();
        var src = _env.PathIn("k.pem");
        File.WriteAllText(src, _env.PrimaryPub);
        Assert.Throws<PackageBuildException>(() => UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "1", Kind = UpdateKinds.Mixed, Summary = "x", SmsDocuments = { new SmsDocumentRef { DocumentId = "A", Revision = "1", ApprovedBy = "B" } },
            Files = { new PackageSource("config/trusted-keys/k.pem", src) },
        }, key, _env.PathIn("x.sbupd")));
    }
}
