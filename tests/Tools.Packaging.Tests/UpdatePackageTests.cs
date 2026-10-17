global using Xunit;
using System.Text;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>簽章/驗證:竄改檔案、竄改 manifest、未信任金鑰、降版(規劃書第 14 章「船上部署」驗收列)。</summary>
public sealed class UpdatePackageTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void ValidPackage_VerifiesAndShowsSigner()
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        var v = new UpdateApplier(ship).Verify(pkg);
        Assert.True(v.Ok, string.Join(";", v.Errors));
        Assert.Equal(KeyRoles.Primary, v.SignerRole);
        Assert.Contains("訪船船長(測試)", v.SignerDescription);
        Assert.Equal("2026.10.1", v.Manifest!.Version);
    }

    [Fact]
    public void TamperedPayloadFile_Rejected()
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        TestEnv.RewriteZip(pkg, "payload/app/version.txt", b => Encoding.UTF8.GetBytes("evil 9.9\n"));
        var r = new UpdateApplier(ship).Apply(pkg);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("雜湊不符") || e.Contains("大小不符"));
        Assert.Equal("app 1.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
        Assert.Contains(ship.Audit().ReadAll(), l => l["event"]!.GetValue<string>() == AuditEvents.UpdateRejected);
    }

    [Fact]
    public void ExtraUnlistedFile_Rejected()
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        TestEnv.AddZipEntry(pkg, "payload/app/extra.dll", new byte[] { 1, 2, 3 });
        var v = new UpdateApplier(ship).Verify(pkg);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("未列於 manifest"));
    }

    [Fact]
    public void TamperedManifest_Rejected()
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        TestEnv.RewriteZip(pkg, UpdateManifest.ManifestEntry, b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("軟體 2026.10.1", "軟體 2099.1.1")));
        var v = new UpdateApplier(ship).Verify(pkg);
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains("簽章驗證失敗"));
        Assert.Null(v.Manifest); // 簽章不符時不解讀 manifest
    }

    [Fact]
    public void UntrustedKey_Rejected()
    {
        var ship = _env.NewShip();
        var (priv, _) = TestEnv.NewKey(KeyRoles.Primary, "陌生人");
        using var stranger = new PemKeyProvider(priv);
        var pkg = _env.SoftwarePackage("2026.10.1", stranger);
        var r = new UpdateApplier(ship).Apply(pkg);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("不在信任清單") && e.Contains("陌生人"));
        var audit = ship.Audit().ReadAll().Last();
        Assert.Equal(AuditEvents.UpdateRejected, audit["event"]!.GetValue<string>());
        Assert.Equal("陌生人", audit["details"]!["claimedSigner"]!.GetValue<string>());
    }

    [Fact]
    public void Downgrade_And_Replay_Rejected()
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var v2 = _env.SoftwarePackage("2026.10.2", key, "app 2.0\n");
        var v1 = _env.SoftwarePackage("2026.10.1", key, "app 1.5\n");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(v2).Ok);
        var down = applier.Apply(v1);
        Assert.False(down.Ok);
        Assert.Contains(down.Errors, e => e.Contains("降版"));
        var again = applier.Apply(v2);
        Assert.False(again.Ok);
        Assert.Equal("app 2.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));
    }

    [Fact]
    public void ShipTargeting_Enforced()
    {
        var ship = _env.NewShip(shipId: "FSB1");
        using var key = _env.Primary();
        var src = _env.PathIn("v.txt");
        File.WriteAllText(src, "x");
        var pkg = UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "2026.10.1", Kind = UpdateKinds.Software, Summary = "只給 No.2", Ships = { "FSB2" }, Files = { new PackageSource("app/v.txt", src) },
        }, key, _env.PathIn("fsb2.sbupd")).OutputPath;
        var v = new UpdateApplier(ship).Verify(pkg);
        Assert.Contains(v.Errors, e => e.Contains("限定船舶"));
    }

    [Fact]
    public void PathTraversal_RejectedAtBuild()
    {
        using var key = _env.Primary();
        var src = _env.PathIn("v.txt");
        File.WriteAllText(src, "x");
        Assert.Throws<InvalidDataException>(() => UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "1", Kind = UpdateKinds.Software, Summary = "x", Files = { new PackageSource("app/../../evil.txt", src) },
        }, key, _env.PathIn("evil.sbupd")));
        Assert.Throws<PackageBuildException>(() => UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "1", Kind = UpdateKinds.Software, Summary = "x", Files = { new PackageSource("data/scenarios/x.yaml", src) },
        }, key, _env.PathIn("wrongkind.sbupd")));
    }

    [Fact]
    public void Version_Comparison()
    {
        Assert.True(PackageVersion.Compare("2026.10.10", "2026.10.9") > 0);
        Assert.True(PackageVersion.Compare("2026.11", "2026.10.99") > 0);
        Assert.Equal(0, PackageVersion.Compare("2026.10", "2026.10.0"));
        Assert.False(PackageVersion.TryParse("2026.10.a", out _));
        Assert.False(PackageVersion.TryParse("1.2.3.4.5", out _));
    }
}
