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

        // Inno Setup 安裝程式(tools/installer/SimosaBRM.iss)的解除安裝檔不算多出(安裝後 release verify 仍須通過);其他 unins 字首的檔案照常列為多出
        Directory.CreateDirectory(Path.Combine(dir, "tools", "uninstall"));
        File.WriteAllText(Path.Combine(dir, "tools", "uninstall", "unins000.exe"), "MZ");
        File.WriteAllText(Path.Combine(dir, "tools", "uninstall", "unins000.dat"), "x");
        File.WriteAllText(Path.Combine(dir, "unins001.msg"), "x");
        Assert.True(ReleaseManifestBuilder.Verify(dir).Ok);
        Assert.DoesNotContain(ReleaseManifestBuilder.Scan(dir), f => f.Path.Contains("unins"));
        File.WriteAllText(Path.Combine(dir, "tools", "uninstall", "uninstall-notes.txt"), "x");
        Assert.Contains(ReleaseManifestBuilder.Verify(dir).Errors, e => e.Contains("清單中沒有的檔案") && e.Contains("tools/uninstall/uninstall-notes.txt"));
        File.Delete(Path.Combine(dir, "tools", "uninstall", "uninstall-notes.txt"));
        Directory.Delete(Path.Combine(dir, "tools", "uninstall"), true);
        File.Delete(Path.Combine(dir, "unins001.msg"));

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

    // ── 安裝程式排除的檔案不得進發行清單(審查第二十九輪:Godot 匯出的 .pdb)─────────

    [Fact]
    public void Manifest_FailsWhenDirectoryHasFilesTheInstallerExcludes()
    {
        // 安裝程式 [Files] 的 Excludes 不安裝 *.pdb:清單若列了它,以安裝程式安裝後 release verify --installed 必報「檔案缺少」。
        // Godot .NET 匯出會把 .pdb 放進 app/visual/data_*/(匯出在 build-release 步驟 3 的 *.pdb 刪除之後)→ release manifest 直接失敗並說明
        var dir = NewRelease();
        var pdb = Path.Combine(dir, "app", "visual", "data_SimosaBRM.Visual_x86_64", "SimosaBRM.Visual.pdb");
        Directory.CreateDirectory(Path.GetDirectoryName(pdb)!);
        File.WriteAllText(pdb, "pdb");
        File.WriteAllText(Path.Combine(dir, "app", "simcore", "Other.PDB"), "pdb");   // 檔名比對不分大小寫

        Assert.Equal(new[] { "app/simcore/Other.PDB", "app/visual/data_SimosaBRM.Visual_x86_64/SimosaBRM.Visual.pdb" }, ReleaseManifestBuilder.FindExcludedByInstaller(dir));
        var ex = Assert.Throws<InvalidDataException>(() => ReleaseManifestBuilder.Build(dir, "2026.11.1"));
        Assert.Contains("*.pdb", ex.Message);
        Assert.Contains("app/visual/data_SimosaBRM.Visual_x86_64/SimosaBRM.Visual.pdb", ex.Message);
        Assert.Contains("安裝程式", ex.Message);
        Assert.Contains("檔案缺少", ex.Message);

        // CLI:結束碼 1、說明原因、不留下 manifest.json
        var cli = Run("release", "manifest", "--dir", dir, "--version", "2026.11.1");
        Assert.Equal(1, cli.Code);
        Assert.Contains("安裝程式不會安裝", cli.Err);
        Assert.Contains("Other.PDB", cli.Err);
        Assert.False(File.Exists(Path.Combine(dir, ReleaseManifest.FileName)));

        // 刪掉之後即可產生;同樣被安裝程式排除的 Thumbs.db 是依檔名略過(不列入清單、也不擋)
        File.Delete(pdb);
        File.Delete(Path.Combine(dir, "app", "simcore", "Other.PDB"));
        File.WriteAllText(Path.Combine(dir, "app", "web", "Thumbs.db"), "x");
        var m = ReleaseManifestBuilder.Build(dir, "2026.11.1");
        Assert.Equal(8, m.FileCount);
        Assert.DoesNotContain(m.Files, f => ReleaseManifestBuilder.IsExcludedByInstaller(f.Path));
        ReleaseManifestBuilder.Save(m, dir);
        Assert.True(ReleaseManifestBuilder.Verify(dir).Ok);

        Assert.True(ReleaseManifestBuilder.IsExcludedByInstaller("app/x/a.pdb"));
        Assert.True(ReleaseManifestBuilder.IsExcludedByInstaller("app\\x\\THUMBS.DB"));
        Assert.False(ReleaseManifestBuilder.IsExcludedByInstaller("app/x/a.pdb.txt"));
        Assert.False(ReleaseManifestBuilder.IsExcludedByInstaller("app/pdb/a.dll"));
    }

    [Fact]
    public void Verify_WarnsWhenOldManifestListsFilesTheInstallerExcludes()
    {
        // 舊版 simosa-pkg 產生的清單可能列有 .pdb:目錄本身核對得過(檔案在),但要警告 — 以安裝程式安裝後會少這些檔案
        var dir = NewRelease();
        File.WriteAllText(Path.Combine(dir, "app", "simcore", "SimosaBRM.SimCore.Host.pdb"), "pdb");
        ReleaseManifestBuilder.Save(new ReleaseManifest { Version = "2026.11.1", Files = ReleaseManifestBuilder.Scan(dir) }, dir);

        var v = ReleaseManifestBuilder.Verify(dir);
        Assert.True(v.Ok, string.Join("; ", v.Errors));
        var warning = Assert.Single(v.Warnings);
        Assert.Contains("app/simcore/SimosaBRM.SimCore.Host.pdb", warning);
        Assert.Contains("檔案缺少", warning);
        var cli = Run("release", "verify", "--dir", dir);
        Assert.Equal(0, cli.Code);
        Assert.Contains("警告", cli.Err);
    }

    /// <summary>tools/installer/SimosaBRM.iss 某區段(到下一個區段為止)的非註解行。</summary>
    private IEnumerable<string> IssSection(string section)
    {
        var current = "";
        foreach (var raw in File.ReadAllLines(Path.Combine(_env.RepoRoot, "tools", "installer", "SimosaBRM.iss")))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line;
                continue;
            }
            if (current == section && line.Length > 0 && !line.StartsWith(';') && !line.StartsWith('#')) yield return line;
        }
    }

    [Fact]
    public void InstallerExcludedPatterns_MatchTheInstallerScript()
    {
        // 排除規則只有一份(ReleaseManifestBuilder.InstallerExcludedPatterns);.iss [Files] 主項目的 Excludes 必須相同(另列安裝的 \config\* 除外)。
        // tools/installer/check-iss.py 從另一個方向(讀 C# 原始碼)做同一個對照
        var main = Assert.Single(IssSection("[Files]"), l => l.Contains("DestDir: \"{app}\";"));
        var marker = "Excludes: \"";
        var at = main.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, main);
        var start = at + marker.Length;
        var excludes = main[start..main.IndexOf('"', start)].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\\config\\*", excludes);
        Assert.Equal(ReleaseManifestBuilder.InstallerExcludedPatterns.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            excludes.Where(x => x != "\\config\\*").OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        // 其他 [Files] 項目沒有自己的 Excludes
        Assert.All(IssSection("[Files]").Where(l => l != main), l => Assert.DoesNotContain("Excludes:", l));
    }

    [Theory]
    [InlineData("build-release.sh", "step \"5/7", "step \"6/7", "step \"7/7", "-delete")]
    [InlineData("build-release.ps1", "Step '5/7", "Step '6/7", "Step '7/7", "Remove-Item")]
    public void BuildReleaseScripts_DeletePdbAfterGodotExport_BeforePackAndManifest(string script, string step5, string step6, string step7, string deleteCommand)
    {
        // *.pdb 的刪除必須在 Godot 匯出(步驟 5)之後、pack(步驟 6)與 manifest(步驟 7)之前:
        // 之前只在步驟 3 結尾刪,Godot .NET 匯出放進 app/visual/data_*/ 的 .pdb 會進到軟體更新包與發行清單
        var lines = File.ReadAllLines(Path.Combine(_env.RepoRoot, "tools", script));
        int IndexOf(string marker) => Array.FindIndex(lines, l => l.TrimStart().StartsWith(marker, StringComparison.Ordinal));
        var (i5, i6, i7) = (IndexOf(step5), IndexOf(step6), IndexOf(step7));
        Assert.True(i5 > 0 && i6 > i5 && i7 > i6, $"{script}:找不到步驟 5/6/7 的標記({i5}、{i6}、{i7})");
        bool DeletesPdb(string l) => !l.TrimStart().StartsWith('#') && l.Contains("*.pdb", StringComparison.Ordinal) && l.Contains(deleteCommand, StringComparison.Ordinal);
        var between = Enumerable.Range(i5 + 1, i6 - i5 - 1).Where(i => DeletesPdb(lines[i])).ToList();
        Assert.True(between.Count > 0, $"{script}:步驟 5(Godot 匯出)與步驟 6(pack)之間沒有刪除 *.pdb");
        // 這一段的最後一個動作:它之後、步驟 6 之前不得再有會產生檔案的指令(只允許空行與註解)
        Assert.All(lines[(between[^1] + 1)..i6], l => Assert.True(l.Trim().Length == 0 || l.TrimStart().StartsWith('#'), $"{script}:*.pdb 刪除之後、步驟 6 之前還有指令:{l}"));
        // 刪除範圍涵蓋 app 與 tools(Godot 匯出在 app/visual)
        Assert.Contains("app", lines[between[^1]]);
        Assert.Contains("tools", lines[between[^1]]);
    }

    // ── 清單損毀時一般模式不得以未處理例外中止(與安裝模式一致)────────────────

    [Theory]
    [InlineData("{\"format\":\"simosa-release/1\",\"version\":\"1\",\"files\":[null]}", "清單路徑不合法")]
    [InlineData("{\"format\":\"simosa-release/1\",\"version\":\"1\",\"files\":[{\"path\":null,\"sha256\":null,\"size\":1}]}", "清單路徑不合法")]
    [InlineData("{\"format\":\"simosa-release/1\",\"version\":\"1\",\"files\":null}", "files")]
    public void Verify_ManifestWithNullFilesOrEntries_FailsCleanly(string manifest, string expected)
    {
        var dir = NewRelease("null-files");
        File.WriteAllText(Path.Combine(dir, ReleaseManifest.FileName), manifest);

        var v = ReleaseManifestBuilder.Verify(dir);   // 之前:NullReferenceException(CLI 結束碼 134)
        Assert.False(v.Ok);
        Assert.Contains(v.Errors, e => e.Contains(expected));

        var cli = Run("release", "verify", "--dir", dir);
        Assert.Equal(1, cli.Code);
        Assert.Contains("核對失敗", cli.Err);
        var json = Run("release", "verify", "--dir", dir, "--json");
        Assert.Equal(1, json.Code);
        Assert.False(System.Text.Json.Nodes.JsonNode.Parse(json.Out)!["ok"]!.GetValue<bool>());
        // 安裝模式對同一個檔案同樣是「核對失敗」
        var installed = InstalledVerifier.Verify(dir);
        Assert.False(installed.Ok);
        Assert.Contains(installed.Errors, e => e.Category == InstallFindingCategories.Manifest);
    }

    [Fact]
    public void Verify_ManifestEntryWithNullSha_IsMismatchNotCrash()
    {
        var dir = NewRelease("null-sha");
        File.WriteAllText(Path.Combine(dir, ReleaseManifest.FileName),
            "{\"format\":\"simosa-release/1\",\"version\":\"1\",\"files\":[{\"path\":\"contracts/state.schema.json\",\"sha256\":null,\"size\":3}]}");
        var v = ReleaseManifestBuilder.Verify(dir);
        Assert.Contains(v.Errors, e => e.Contains("SHA-256 不符") && e.Contains("contracts/state.schema.json"));
    }
}
