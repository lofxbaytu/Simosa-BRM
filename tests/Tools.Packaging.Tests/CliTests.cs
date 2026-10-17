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
    public void MalformedYaml_IsErrorExitCode1_NotCrash()
    {
        // 少一個引號(YAML 語法錯誤)與型別錯誤(sources 為陣列):依約定回 1 並顯示錯誤,不得以未捕捉例外中止
        var badSyntax = _env.PathIn("bad-syntax.yaml");
        File.WriteAllText(badSyntax, "id: x\nversion: \"2026.10.2\nparameters: {}\n");
        var badType = _env.PathIn("bad-type.yaml");
        File.WriteAllText(badType, "id: x\nversion: \"2026.10.2\"\nsources: [1,2]\nparameters: {}\n");

        foreach (var bad in new[] { badSyntax, badType })
        {
            var impact = Run("impact", "--parameters", bad, "--data-root", _env.InstructorRoot);
            Assert.Equal(1, impact.Code);
            Assert.Contains("錯誤", impact.Err);

            var keyBase = _env.PathIn("keys", "k" + Path.GetFileNameWithoutExtension(bad));
            Assert.Equal(0, Run("keygen", "--out", keyBase, "--name", "k", "--role", "primary").Code);
            var pack = Run("pack", "--kind", "parameters", "--version", "2026.10.2", "--summary", "x", "--sms", "DWO-007:4:DPA",
                "--file", $"data/parameters/company-procedures.yaml={bad}", "--data-root", _env.InstructorRoot, "--key", keyBase + ".key.pem", "--out", _env.PathIn("p.sbupd"));
            Assert.Equal(1, pack.Code);

            var scenario = Path.Combine(_env.InstructorRoot, "data", "scenarios", "E01_baseline.yaml");
            var ver = Run("verification", "add", "--file", _env.PathIn("ver.json"), "--scenario", scenario, "--parameters", bad, "--by", "測試");
            Assert.Equal(1, ver.Code);
        }
    }

    [Fact]
    public void BooleanFlagBeforePositional_DoesNotSwallowIt()
    {
        var ship = _env.NewShip();
        using var key = _env.Primary();
        var pkg = _env.SoftwarePackage("2026.10.1", key);
        var verify = Run("verify", "--allow-unverified", pkg, "--root", ship.Root);
        Assert.Equal(0, verify.Code);
        var apply = Run("apply", "--allow-unverified", pkg, "--root", ship.Root);
        Assert.Equal(0, apply.Code);
        Assert.DoesNotContain("用法錯誤", apply.Err);
        Assert.Equal("app 2.0\n", File.ReadAllText(Path.Combine(ship.Root, "app", "version.txt")));

        // --flag=false 形式仍可關閉
        var args = new CliArgs(new[] { "--json=false", "--enroll-device", "x.sbrec", "--store", "st" });
        Assert.False(args.Flag("json"));
        Assert.True(args.Flag("enroll-device"));
        Assert.Equal("x.sbrec", args.Pos(0, "紀錄包"));
        Assert.Equal("st", args.Get("store"));
    }

    [Fact]
    public void UnknownCommand_IsUsageError()
    {
        Assert.Equal(2, Run("frobnicate").Code);
        Assert.Equal(2, Run("pack", "--kind", "software").Code);
    }
}
