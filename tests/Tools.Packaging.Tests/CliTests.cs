using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Tests;

/// <summary>simosa-pkg 指令列冒煙測試(金鑰產生、影響分析、產生/驗證/套用、status --json)。</summary>
public sealed class CliTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = new CliApp(o, e).Run(args);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Keygen_Pack_Verify_Apply_Status()
    {
        var ship = _env.NewShip();
        var keyBase = _env.PathIn("keys", "trainer");
        var kg = Run("keygen", "--out", keyBase, "--name", "CLI 測試金鑰", "--role", "primary");
        Assert.Equal(0, kg.Code);
        Assert.Contains("警告", kg.Err);
        Assert.Equal(0, Run("trust", "add", "--root", ship.Root, "--file", keyBase + ".pub.pem", "--admin", "測試").Code);

        var src = _env.PathIn("v.txt");
        File.WriteAllText(src, "app 9\n");
        var pkg = _env.PathIn("cli.sbupd");
        var pack = Run("pack", "--kind", "software", "--version", "2026.10.9", "--summary", "CLI 測試", "--file", $"app/version.txt={src}", "--key", keyBase + ".key.pem", "--out", pkg);
        Assert.Equal(0, pack.Code);
        Assert.Contains("PEM", pack.Err);

        var verify = Run("verify", pkg, "--root", ship.Root);
        Assert.Equal(0, verify.Code);
        Assert.Contains("CLI 測試金鑰", verify.Out);
        Assert.Equal(0, Run("apply", pkg, "--root", ship.Root, "--operator", "船長").Code);
        Assert.Equal(1, Run("apply", pkg, "--root", ship.Root).Code); // 重複套用拒絕

        var st = Run("status", "--root", ship.Root, "--json");
        Assert.Equal(0, st.Code);
        var status = PackageJson.FromText<Sync.ShipStatus>(st.Out, "status");
        Assert.Equal("2026.10.9", status.Version);
        Assert.Equal("FSB1", status.ShipId);
        Assert.True(status.RollbackAvailable);
    }

    [Fact]
    public void Impact_PrintsAffectedScenarios()
    {
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        var r = Run("impact", "--parameters", p, "--baseline", _env.BaselineParameters, "--data-root", _env.InstructorRoot);
        Assert.Equal(0, r.Code);
        Assert.Contains("E01_baseline", r.Out);
        Assert.Contains("navigation.cpa.coastal_nm", r.Out);
    }

    [Fact]
    public void UnknownCommand_IsUsageError()
    {
        Assert.Equal(2, Run("frobnicate").Code);
        Assert.Equal(2, Run("pack", "--kind", "software").Code);
    }
}
