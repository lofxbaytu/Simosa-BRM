using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Release;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 發行清單(tools/build-release.* 產生的 manifest.json)與「發行版 app/ → 簽章更新包 → 船上驗證/套用」往返
/// (規劃書第 5.4–5.5 節、第 14 章「船上部署」驗收列中的軟體更新包途徑)。
/// </summary>
public sealed class ReleaseTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    /// <summary>假的發行目錄:app/simcore、app/web、data/、contracts/、config/。</summary>
    private string NewRelease(string name = "release")
    {
        var dir = _env.PathIn(name);
        void W(string rel, string content)
        {
            var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
        }
        W("app/simcore/SimosaBRM.SimCore.Host.dll", "fake host\n");
        W("app/simcore/SimosaBRM.SimCore.Host.runtimeconfig.json", "{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}\n");
        W("app/web/index.html", "<!doctype html><title>儀器</title>\n");
        W("app/web/assets/index-abc.js", "console.log(1)\n");
        W("app/launch/start-ship-laptop.ps1", "# 啟動\n");
        W("data/parameters/company-procedures.yaml", File.ReadAllText(_env.BaselineParameters));
        W("contracts/state.schema.json", "{}\n");
        W("config/ship-laptop.json", "{\"shipId\":\"FSB1\"}\n");
        return dir;
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = new CliApp(o, e).Run(args);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Manifest_RoundTrip_ListsEveryFileSortedWithSha256()
    {
        var dir = NewRelease();
        var components = new Dictionary<string, ReleaseComponent>
        {
            ["simcore"] = new() { Version = "2026.10.23", Path = "app/simcore" },
            ["visual"] = new() { Version = "2026.10.23", Status = "略過:缺匯出範本" },
        };
        var m = ReleaseManifestBuilder.Build(dir, "2026.10.23", "abc123def456", "claude/test", "linux-x64", components, new[] { "Godot 視景未匯出" });
        var path = ReleaseManifestBuilder.Save(m, dir);
        Assert.True(File.Exists(path));

        var loaded = ReleaseManifestBuilder.Load(dir);
        Assert.Equal(ReleaseManifest.FormatId, loaded.Format);
        Assert.Equal("2026.10.23", loaded.Version);
        Assert.Equal("abc123def456", loaded.GitRevision);
        Assert.Equal("linux-x64", loaded.Runtime);
        Assert.Equal(8, loaded.FileCount);
        Assert.DoesNotContain(loaded.Files, f => f.Path == ReleaseManifest.FileName);
        Assert.Equal(loaded.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal), loaded.Files.Select(f => f.Path));
        Assert.All(loaded.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        Assert.All(loaded.Files, f => Assert.DoesNotContain('\\', f.Path));
        var web = loaded.Files.Single(f => f.Path == "app/web/index.html");
        Assert.Equal(Sha256Hex.OfFile(Path.Combine(dir, "app", "web", "index.html")), web.Sha256);
        Assert.Equal("略過:缺匯出範本", loaded.Components["visual"].Status);
        Assert.Contains("Godot 視景未匯出", loaded.Notes);

        var v = ReleaseManifestBuilder.Verify(dir);
        Assert.True(v.Ok, string.Join("; ", v.Errors));
        Assert.Equal(8, v.Checked);
    }

    [Fact]
    public void Verify_DetectsTamperExtraAndMissingFiles()
    {
        var dir = NewRelease();
        ReleaseManifestBuilder.Save(ReleaseManifestBuilder.Build(dir, "2026.10.23"), dir);

        // 竄改內容(大小相同)
        var js = Path.Combine(dir, "app", "web", "assets", "index-abc.js");
        File.WriteAllText(js, "console.log(2)\n");
        var v1 = ReleaseManifestBuilder.Verify(dir);
        Assert.False(v1.Ok);
        Assert.Contains(v1.Errors, e => e.Contains("SHA-256 不符") && e.Contains("app/web/assets/index-abc.js"));
        File.WriteAllText(js, "console.log(1)\n");
        Assert.True(ReleaseManifestBuilder.Verify(dir).Ok);

        // 多出的檔案
        File.WriteAllText(Path.Combine(dir, "app", "extra.txt"), "x");
        var v2 = ReleaseManifestBuilder.Verify(dir);
        Assert.Contains(v2.Errors, e => e.Contains("清單中沒有的檔案") && e.Contains("app/extra.txt"));
        File.Delete(Path.Combine(dir, "app", "extra.txt"));

        // 缺少的檔案與大小不符
        File.Delete(Path.Combine(dir, "contracts", "state.schema.json"));
        File.WriteAllText(Path.Combine(dir, "config", "ship-laptop.json"), "{\"shipId\":\"FSB2\",\"x\":1}\n");
        var v3 = ReleaseManifestBuilder.Verify(dir);
        Assert.Contains(v3.Errors, e => e.Contains("檔案缺少") && e.Contains("contracts/state.schema.json"));
        Assert.Contains(v3.Errors, e => e.Contains("大小不符") && e.Contains("config/ship-laptop.json"));

        // 沒有清單、格式錯誤、版本不合法
        Assert.False(ReleaseManifestBuilder.Verify(_env.PathIn("nothing-here")).Ok);
        var bad = NewRelease("bad");
        File.WriteAllText(Path.Combine(bad, ReleaseManifest.FileName), "{\"format\":\"other/1\",\"version\":\"1\",\"files\":[]}");
        Assert.Contains(ReleaseManifestBuilder.Verify(bad).Errors, e => e.Contains("格式"));
        Assert.Throws<InvalidDataException>(() => ReleaseManifestBuilder.Build(bad, "v1.0"));
    }

    [Fact]
    public void ReleaseApp_PackedAsSoftwareUpdate_VerifiesAndAppliesOnShip()
    {
        // 發行版 app/ → simosa-pkg pack --kind software --dir app=<release>/app → 船上 verify/apply(信任清單含主金鑰)→ status 版本與 app/ 內容
        var dir = NewRelease();
        ReleaseManifestBuilder.Save(ReleaseManifestBuilder.Build(dir, "2026.10.23"), dir);
        var ship = _env.NewShip();
        var req = new UpdateBuildRequest { Version = "2026.10.23", Kind = UpdateKinds.Software, Summary = "發行版 2026.10.23 軟體" }
            .AddDirectory("app", Path.Combine(dir, "app"));
        var built = UpdatePackageBuilder.Build(req, _env.Primary(), _env.PathIn("sw.sbupd"));
        Assert.Equal(5, built.Manifest.Files.Count);
        Assert.All(built.Manifest.Files, f => Assert.StartsWith("app/", f.Path));
        // 更新包內每檔雜湊與發行清單一致
        var rel = ReleaseManifestBuilder.Load(dir);
        foreach (var f in built.Manifest.Files)
            Assert.Equal(rel.Files.Single(r => r.Path == f.Path).Sha256, f.Sha256);

        var applier = new UpdateApplier(ship);
        var v = applier.Verify(built.OutputPath);
        Assert.True(v.Ok, string.Join("; ", v.Errors));
        var r = applier.Apply(built.OutputPath, new UpdateApplyOptions { Operator = "測試" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal("fake host\n", File.ReadAllText(Path.Combine(ship.Root, "app", "simcore", "SimosaBRM.SimCore.Host.dll")));
        Assert.Equal("2026.10.23", new Sync.ShipNode(ship).Status().Version);
        // 同版本不可重複套用(發行版本與更新包版本規則一致)
        Assert.False(applier.Verify(built.OutputPath).Ok);
    }

    [Fact]
    public void Cli_ReleaseManifestAndVerify()
    {
        var dir = NewRelease();
        var mk = Run("release", "manifest", "--dir", dir, "--version", "2026.10.23", "--git", "0123456789abcdef0123", "--runtime", "linux-x64",
                     "--component", "simcore=2026.10.23=app/simcore", "--component", "visual=2026.10.23==略過:缺匯出範本", "--note", "容器建置");
        Assert.Equal(0, mk.Code);
        Assert.Contains("已寫入發行清單", mk.Out);
        Assert.Contains("8 個檔案", mk.Out);
        var m = ReleaseManifestBuilder.Load(dir);
        Assert.Equal("app/simcore", m.Components["simcore"].Path);
        Assert.Null(m.Components["visual"].Path);
        Assert.Equal("略過:缺匯出範本", m.Components["visual"].Status);

        var ok = Run("release", "verify", "--dir", dir);
        Assert.Equal(0, ok.Code);
        Assert.Contains("核對通過", ok.Out);

        File.AppendAllText(Path.Combine(dir, "app", "web", "index.html"), "<!-- 竄改 -->");
        var bad = Run("release", "verify", "--dir", dir);
        Assert.Equal(1, bad.Code);
        Assert.Contains("核對失敗", bad.Err);
        Assert.Contains("app/web/index.html", bad.Err);

        Assert.Equal(2, Run("release", "manifest", "--dir", dir).Code);           // 缺 --version
        Assert.Equal(2, Run("release", "bogus", "--dir", dir).Code);
        Assert.Equal(2, Run("release", "manifest", "--dir", dir, "--version", "1", "--component", "nameonly").Code);
    }
}
