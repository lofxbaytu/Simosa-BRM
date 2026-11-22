using System.Text.Json.Nodes;
using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Impact;
using SimosaBRM.Packaging.Release;
using SimosaBRM.Packaging.Updates;
using SimosaBRM.SimCore.Parameters;
using C = SimosaBRM.Packaging.Release.InstallFindingCategories;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 安裝模式核對(<c>simosa-pkg release verify --installed</c> / <c>verify-install</c>;規劃書第 5.5 節、R13;packaging-sync.md 第 5.1 節):
/// 使用中的安裝根目錄上,執行期資料不算多出、本機修改的設定只列提醒、程式/資料檔缺少或雜湊不符與 app/ 內多出檔案是錯誤;
/// 套用更新包後以覆蓋清單 state/installed-files.json 核對,回復後一起還原;舊安裝(沒有覆蓋清單)與清單不一致列為「無法核對」提醒。
/// </summary>
public sealed class InstalledVerifyTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static void W(string root, string rel, string content)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private static string P(InstallLayout ship, string rel) => Path.Combine(ship.Root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>發行清單不含的位置(安裝後才建立:信任清單、裝置金鑰、packaging.json、state/)。</summary>
    private static bool CreatedAfterInstall(string path)
        => path.StartsWith("state/", StringComparison.Ordinal) || path.StartsWith("config/trusted-keys/", StringComparison.Ordinal)
           || path.StartsWith("config/device/", StringComparison.Ordinal) || path == "config/packaging.json";

    private static void WriteManifest(InstallLayout ship, string version, TimeProvider? clock = null)
    {
        var m = ReleaseManifestBuilder.Build(ship.Root, version, "0123456789abcdef", "main", "linux-x64", clock: clock);
        m.Files.RemoveAll(f => CreatedAfterInstall(f.Path));
        ReleaseManifestBuilder.Save(m, ship.Root);
    }

    /// <summary>
    /// 剛安裝好的根目錄:發行版內容(app/、tools/updater/、data/、contracts/、docs/、設定範例)+ manifest.json,
    /// 加上安裝後建立的信任清單、裝置金鑰與 packaging.json(不在清單內)。
    /// </summary>
    private InstallLayout NewInstall(string name = "install", string version = "2026.10.0")
    {
        var ship = _env.NewShip(name);
        W(ship.Root, "app/simcore/SimosaBRM.SimCore.Host.dll", "fake host 1.0\n");
        W(ship.Root, "app/web/index.html", "<!doctype html><title>儀器</title>\n");
        W(ship.Root, "app/web/assets/index-abc.js", "console.log(1)\n");
        W(ship.Root, "tools/updater/simosa-pkg.dll", "fake pkg\n");
        W(ship.Root, "tools/webview2/README.txt", "離線安裝檔放這裡\n");
        W(ship.Root, "contracts/state.schema.json", "{}\n");
        W(ship.Root, "docs/README.md", "# 說明\n");
        W(ship.Root, "config/ship-laptop.json", "{\"shipId\":\"FSB1\"}\n");
        W(ship.Root, "config/instructor-laptop.json", "{\"role\":\"instructor\"}\n");
        WriteManifest(ship, version);
        return ship;
    }

    /// <summary>使用一陣子之後的執行期資料(docs/ops/本機資料位置.md 第 2 節;安裝程式 [Dirs])。</summary>
    private static void AddRuntimeData(InstallLayout ship)
    {
        W(ship.Root, "build/logs/simcore.log", "steps/s 50\n");
        W(ship.Root, "build/records/20261101T010203Z-E01_baseline.jsonl", "{}\n");
        W(ship.Root, "build/records/media/a.webm", "webm");
        W(ship.Root, "build/browser/edge/Default/Preferences", "{}");
        W(ship.Root, "build/browser/edge/Default/Cache/data_0", "x");
        W(ship.Root, "build/webview2/instructor/EBWebView/Local State", "{}");
        W(ship.Root, "build/downloads/draft.yaml", "id: x\n");
        W(ship.Root, "build/tmp/t.tmp", "x");
        W(ship.Root, "build/visual/quality.json", "{}");
        W(ship.Root, "state/outbox/p.sbrec", "x");
        W(ship.Root, "store/records/FSB1/2026/p.sbrec", "x");
        W(ship.Root, "data/records/old.jsonl", "{}\n");
        W(ship.Root, ".cache/godot/appdata/roaming/SimosaBRM-Visual/logs/godot.log", "x");
        W(ship.Root, "tools/webview2/MicrosoftEdgeWebView2RuntimeInstallerX64.exe", "MZ");
        W(ship.Root, "tools/uninstall/unins000.exe", "MZ");
        W(ship.Root, "tools/uninstall/unins000.dat", "x");
        W(ship.Root, "app/web/desktop.ini", "[.ShellClassInfo]\n");
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = new CliApp(o, e).Run(args);
        return (code, o.ToString(), e.ToString());
    }

    private static string Describe(InstallCheckReport r)
        => string.Join("; ", r.Errors.Select(e => $"[{e.Category}] {e.Path} {e.Detail}")) + " | " + string.Join("; ", r.Notices.Select(e => $"[{e.Category}] {e.Path} {e.Detail}"));

    private static InstallCheckReport AssertOk(InstallLayout ship)
    {
        var r = InstalledVerifier.Verify(ship.Root);
        Assert.True(r.Ok, Describe(r));
        return r;
    }

    /// <summary>software 更新包:多個檔案(安裝路徑 → 內容)。</summary>
    private string SoftwarePackage(string version, IKeyProvider key, params (string Path, string Content)[] files)
    {
        var req = new UpdateBuildRequest { Version = version, Kind = UpdateKinds.Software, Summary = $"軟體 {version}" };
        foreach (var (path, content) in files)
        {
            var src = _env.PathIn($"src-{version}-{Guid.NewGuid().ToString("N")[..8]}");
            File.WriteAllText(src, content);
            req.Files.Add(new PackageSource(path, src));
        }
        return UpdatePackageBuilder.Build(req, key, _env.PathIn($"sw-{version}-{Guid.NewGuid().ToString("N")[..6]}.sbupd")).OutputPath;
    }

    // ── 分級:執行期資料、設定檔、程式檔 ─────────────────────

    [Fact]
    public void FreshInstall_WithRuntimeData_Passes_AndPlainVerifyStillFails()
    {
        var ship = NewInstall();
        AddRuntimeData(ship);

        var r = AssertOk(ship);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Notices);
        Assert.True(r.Checked > 10);
        Assert.Equal("2026.10.0", r.Release!.Version);
        Assert.Null(r.Installed!.Version);
        Assert.True(r.InstalledFiles!.Consistent);
        Assert.False(r.InstalledFiles.Present);
        foreach (var loc in new[] { "build/", "state/", "store/", "config/device/", "config/trusted-keys/", "config/packaging.json", "data/records/", ".cache/", "tools/webview2/" })
            Assert.Contains(loc, r.SkippedLocations);

        // 一般模式(安裝前核對)的行為不變:執行期資料與安裝後建立的檔案都是「清單中沒有的檔案」
        var plain = ReleaseManifestBuilder.Verify(ship.Root);
        Assert.False(plain.Ok);
        Assert.Contains(plain.Errors, e => e.Contains("清單中沒有的檔案") && e.Contains("build/logs/simcore.log"));
        Assert.Contains(plain.Errors, e => e.Contains("清單中沒有的檔案") && e.Contains("config/packaging.json"));
    }

    [Fact]
    public void ModifiedOrMissingConfig_IsNotice_NotError()
    {
        var ship = NewInstall();
        File.WriteAllText(P(ship, "config/ship-laptop.json"), "{\"shipId\":\"FSB1\",\"deviceId\":\"FSB1-LT01\",\"network\":{\"directLinkIp\":\"192.168.77.11\"}}\n");
        File.Delete(P(ship, "config/instructor-laptop.json"));

        var r = AssertOk(ship);
        var modified = Assert.Single(r.Notices, n => n.Category == C.ConfigModified);
        Assert.Equal("config/ship-laptop.json", modified.Path);
        var missing = Assert.Single(r.Notices, n => n.Category == C.ConfigMissing);
        Assert.Equal("config/instructor-laptop.json", missing.Path);
        Assert.Equal(1, r.Counts[C.ConfigModified]);
        Assert.Empty(r.NextSteps);
    }

    [Fact]
    public void ProgramOrDataFile_ChangedByOneByte_IsError()
    {
        var ship = NewInstall();
        // 大小相同、只差一個位元組
        var dll = P(ship, "app/simcore/SimosaBRM.SimCore.Host.dll");
        var bytes = File.ReadAllBytes(dll);
        bytes[5] ^= 0x01;
        File.WriteAllBytes(dll, bytes);
        // 資料檔(船模)大小改變
        File.AppendAllText(P(ship, "data/ships/FSB1/particulars.json"), " ");

        var r = InstalledVerifier.Verify(ship.Root);
        Assert.False(r.Ok);
        var e1 = Assert.Single(r.Errors, e => e.Path == "app/simcore/SimosaBRM.SimCore.Host.dll");
        Assert.Equal(C.Mismatch, e1.Category);
        Assert.Contains("SHA-256 不符", e1.Detail);
        Assert.Equal("release", e1.Source);
        var e2 = Assert.Single(r.Errors, e => e.Path == "data/ships/FSB1/particulars.json");
        Assert.Equal(C.Mismatch, e2.Category);
        Assert.Contains("大小不符", e2.Detail);
        Assert.Equal(2, r.Errors.Count);
        // 沒套用過更新包:下一步是以原安裝程式重新安裝
        Assert.Contains(r.NextSteps, s => s.Contains("重新安裝") && s.Contains("原安裝程式"));
    }

    [Fact]
    public void MissingProgramFile_IsError()
    {
        var ship = NewInstall();
        File.Delete(P(ship, "app/web/assets/index-abc.js"));
        File.Delete(P(ship, "contracts/state.schema.json"));

        var r = InstalledVerifier.Verify(ship.Root);
        Assert.False(r.Ok);
        Assert.Equal(new[] { "app/web/assets/index-abc.js", "contracts/state.schema.json" }, r.Errors.Select(e => e.Path).ToArray());
        Assert.All(r.Errors, e => Assert.Equal(C.Missing, e.Category));
    }

    [Fact]
    public void ExtraFile_InProgramDirIsError_ElsewhereIsNotice()
    {
        var ship = NewInstall();
        W(ship.Root, "app/simcore/Injected.dll", "x");
        W(ship.Root, "tools/updater/stray.dll", "x");
        W(ship.Root, "docs/note.txt", "x");
        W(ship.Root, "primary.pub.pem", "x");                         // 安裝時放在根目錄的公鑰檔
        W(ship.Root, "data/scenarios/custom_local.yaml", "id: x\n");  // 本機自編的情境

        var r = InstalledVerifier.Verify(ship.Root);
        Assert.False(r.Ok);
        Assert.Equal(new[] { "app/simcore/Injected.dll", "tools/updater/stray.dll" }, r.Errors.Select(e => e.Path).ToArray());
        Assert.All(r.Errors, e => Assert.Equal(C.Extra, e.Category));
        Assert.Equal(new[] { "data/scenarios/custom_local.yaml", "docs/note.txt", "primary.pub.pem" },
            r.Notices.Where(n => n.Category == C.Unlisted).Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        // 多出的檔案不會因重新安裝而消失:下一步是刪除
        Assert.Contains(r.NextSteps, s => s.Contains("刪除"));
        Assert.DoesNotContain(r.NextSteps, s => s.Contains("原安裝程式"));
    }

    [Fact]
    public void MissingOrBrokenManifest_IsError()
    {
        var ship = _env.NewShip();   // 沒有 manifest.json(不是以發行版安裝的目錄)
        var r = InstalledVerifier.Verify(ship.Root);
        Assert.False(r.Ok);
        Assert.Equal(C.Manifest, Assert.Single(r.Errors).Category);
        Assert.Contains(r.NextSteps, s => s.Contains("安裝根目錄"));

        File.WriteAllText(P(ship, "manifest.json"), "{\"format\":\"other/1\",\"version\":\"1\",\"files\":[]}");
        Assert.Contains(InstalledVerifier.Verify(ship.Root).Errors, e => e.Category == C.Manifest && e.Detail!.Contains("格式"));
        File.WriteAllText(P(ship, "manifest.json"), "{ not json");
        Assert.Equal(C.Manifest, Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Category);
        Assert.False(InstalledVerifier.Verify(_env.PathIn("nothing-here")).Ok);
    }

    [Fact]
    public void ListedFileUnderRuntimeLocation_IsStillVerified()
    {
        // 略過的是「清單沒有列的檔案」:清單有列的檔案即使位於執行期資料位置(此例 tools/webview2/README.txt)仍要核對
        var ship = NewInstall();
        W(ship.Root, "tools/webview2/MicrosoftEdgeWebView2RuntimeInstallerX64.exe", "MZ");
        AssertOk(ship);
        File.WriteAllText(P(ship, "tools/webview2/README.txt"), "被改過\n");
        var r = InstalledVerifier.Verify(ship.Root);
        Assert.Equal("tools/webview2/README.txt", Assert.Single(r.Errors).Path);
    }

    [Fact]
    public void CustomRecordsDirFromPackagingJson_IsRuntimeData_ButCannotHideProgramDir()
    {
        var ship = NewInstall();
        ship.Config.RecordsDir = "records";
        ship.Config.Store = "app";          // 設定錯誤:不得讓程式目錄變成執行期資料
        ship.Config.Save(ship.ConfigPath);
        W(ship.Root, "records/20261101T010203Z-E01_baseline.jsonl", "{}\n");
        W(ship.Root, "app/extra.txt", "x");

        var r = InstalledVerifier.Verify(ship.Root);
        Assert.Contains("records/", r.SkippedLocations);
        Assert.DoesNotContain(r.Notices, n => n.Path.StartsWith("records/", StringComparison.Ordinal));
        Assert.Equal("app/extra.txt", Assert.Single(r.Errors).Path);
    }

    [Fact]
    public void IgnoreCaseOption_MatchesWindowsFileNameSemantics()
    {
        var ship = NewInstall();
        // 清單寫 app/Web/…,磁碟上是 app/web/…(Windows 上是同一個檔案)
        var m = ReleaseManifestBuilder.Load(ship.Root);
        foreach (var f in m.Files.Where(f => f.Path.StartsWith("app/web/", StringComparison.Ordinal))) f.Path = "app/Web/" + f.Path["app/web/".Length..];
        ReleaseManifestBuilder.Save(m, ship.Root);

        var insensitive = InstalledVerifier.Verify(ship.Root, new InstalledVerifyOptions { IgnoreCase = true });
        Assert.True(insensitive.Ok, Describe(insensitive));
        if (OperatingSystem.IsWindows()) return;   // Windows 的檔案系統本來就不分大小寫,下面的對照只在 Linux 有意義
        var sensitive = InstalledVerifier.Verify(ship.Root, new InstalledVerifyOptions { IgnoreCase = false });
        Assert.Contains(sensitive.Errors, e => e.Category == C.Missing && e.Path == "app/Web/index.html");
        Assert.Contains(sensitive.Errors, e => e.Category == C.Extra && e.Path == "app/web/index.html");
    }

    // ── 更新包覆蓋清單 ─────────────────────────────────────

    [Fact]
    public void SoftwareUpdate_VerifiedAgainstInstalledFiles()
    {
        var ship = NewInstall();
        AddRuntimeData(ship);
        using var key = _env.Primary();
        var pkg = SoftwarePackage("2026.10.1", key,
            ("app/version.txt", "app 2.0\n"), ("app/simcore/SimosaBRM.SimCore.Host.dll", "fake host 2.0\n"), ("app/web/assets/index-def.js", "console.log(2)\n"));
        TestEnv.ResignManifest(pkg, key, m => m.Remove.Add("app/web/assets/index-abc.js"));
        var applied = new UpdateApplier(ship).Apply(pkg);
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        Assert.False(File.Exists(P(ship, "app/web/assets/index-abc.js")));

        // 覆蓋清單:格式、現行更新包、每個寫入路徑的雜湊/大小/來源、刪除的路徑、發行清單身分
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out var loadError);
        Assert.Null(loadError);
        Assert.NotNull(files);
        Assert.Equal(InstalledFiles.FormatId, files!.Format);
        Assert.Equal(InstalledFiles.FileName, Path.GetFileName(ship.InstalledFilesPath));
        Assert.Equal(applied.PackageId, files.PackageId);
        Assert.Equal("2026.10.1", files.Version);
        Assert.Equal(new[] { "app/simcore/SimosaBRM.SimCore.Host.dll", "app/version.txt", "app/web/assets/index-def.js" }, files.Files.Select(f => f.Path).ToArray());
        var host = files.Files[0];
        Assert.Equal(Sha256Hex.OfFile(P(ship, "app/simcore/SimosaBRM.SimCore.Host.dll")), host.Sha256);
        Assert.Equal("fake host 2.0\n".Length, host.Size);
        Assert.Equal(applied.PackageId, host.PackageId);
        Assert.Equal("2026.10.1", host.Version);
        Assert.Equal("app/web/assets/index-abc.js", Assert.Single(files.Removed).Path);
        Assert.Equal(new[] { "app/" }, Assert.Single(files.Packages).Prefixes);
        Assert.Empty(files.Untracked);
        Assert.Equal(Sha256Hex.OfFile(P(ship, "manifest.json")), files.Baseline!.Sha256);

        // 套用後:更新包換過的檔案與清單不同、被刪除的檔案不在,都不是錯誤
        var ok = AssertOk(ship);
        Assert.Empty(ok.Notices);
        Assert.True(ok.InstalledFiles!.Present && ok.InstalledFiles.Consistent);
        Assert.Equal("2026.10.1", ok.Installed!.Version);

        // 更新後的檔案被改動:抓得到,來源標示為更新包
        File.WriteAllText(P(ship, "app/simcore/SimosaBRM.SimCore.Host.dll"), "fake host 2.x\n");
        var tampered = InstalledVerifier.Verify(ship.Root);
        var err = Assert.Single(tampered.Errors);
        Assert.Equal(C.Mismatch, err.Category);
        Assert.Equal("package:2026.10.1", err.Source);
        // 套用過更新包:下一步不是「原安裝程式」而是不低於現行版本的安裝程式或更高版本的更新包(同版本不可重複套用)
        Assert.Contains(tampered.NextSteps, s => s.Contains("2026.10.1") && s.Contains("不能重複套用"));
        File.WriteAllText(P(ship, "app/simcore/SimosaBRM.SimCore.Host.dll"), "fake host 2.0\n");

        // 更新包新增的檔案不見了:缺少
        File.Delete(P(ship, "app/web/assets/index-def.js"));
        Assert.Equal(C.Missing, Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Category);
        File.WriteAllText(P(ship, "app/web/assets/index-def.js"), "console.log(2)\n");

        // 被更新包刪除的檔案又出現在 app/:多出的檔案
        File.WriteAllText(P(ship, "app/web/assets/index-abc.js"), "console.log(1)\n");
        var back = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal(C.Extra, back.Category);
        Assert.Equal("app/web/assets/index-abc.js", back.Path);
        File.Delete(P(ship, "app/web/assets/index-abc.js"));

        // 更新包的檔案被還原成發行版的內容(以原安裝程式覆蓋安裝):仍是錯誤,並說明原因
        File.WriteAllText(P(ship, "app/simcore/SimosaBRM.SimCore.Host.dll"), "fake host 1.0\n");
        var reverted = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Contains("被還原", reverted.Detail);
    }

    [Fact]
    public void RemovedDirectory_ThenFileWrittenBack_FollowsApplyOrder()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        // 第一包:刪除整個 app/web/assets 目錄,同時寫入其下的一個新檔(套用順序:先刪後寫)
        var p1 = SoftwarePackage("2026.10.1", key, ("app/web/assets/index-new.js", "console.log(9)\n"));
        TestEnv.ResignManifest(p1, key, m => m.Remove.Add("app/web/assets"));
        Assert.True(applier.Apply(p1).Ok);
        Assert.False(File.Exists(P(ship, "app/web/assets/index-abc.js")));
        Assert.True(File.Exists(P(ship, "app/web/assets/index-new.js")));
        AssertOk(ship);

        // 第二包:把先前寫入的檔案刪掉 → 覆蓋清單不再預期它
        var p2 = SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 3.0\n"));
        TestEnv.ResignManifest(p2, key, m => m.Remove.Add("app/web/assets/index-new.js"));
        Assert.True(applier.Apply(p2).Ok);
        AssertOk(ship);
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.DoesNotContain(files.Files, f => f.Path == "app/web/assets/index-new.js");
        Assert.Equal(2, files.Packages.Count);
        Assert.Equal("2026.10.2", files.Packages[0].Version);
    }

    [Fact]
    public void Rollback_RestoresInstalledFiles_AndVerifyPasses()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);

        // 第一包套用後回復:套用前沒有覆蓋清單 → 回復後也沒有
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/new.dll", "n1"))).Ok);
        Assert.True(File.Exists(ship.InstalledFilesPath));
        AssertOk(ship);
        Assert.True(applier.Rollback("測試").Ok);
        Assert.False(File.Exists(ship.InstalledFilesPath));
        Assert.False(File.Exists(P(ship, "app/new.dll")));
        var afterFirst = AssertOk(ship);
        Assert.Empty(afterFirst.Notices);   // 被回復的更新包不算「套用中」:不是舊安裝

        // 兩包之後回復第二包:覆蓋清單回到第一包套用後的內容(逐位元組相同)
        Assert.True(applier.Apply(SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 2.0\n"), ("app/new.dll", "n1"))).Ok);
        var afterP2 = File.ReadAllBytes(ship.InstalledFilesPath);
        var r3 = applier.Apply(SoftwarePackage("2026.10.3", key, ("app/version.txt", "app 3.0\n"), ("app/newer.dll", "n2")));
        Assert.True(r3.Ok, string.Join(";", r3.Errors));
        Assert.True(File.Exists(Path.Combine(ship.PreviousDir, InstalledFiles.FileName)));
        AssertOk(ship);
        var rb = applier.Rollback("測試");
        Assert.True(rb.Ok, string.Join(";", rb.Errors));
        Assert.Empty(rb.Warnings);
        Assert.Equal(afterP2, File.ReadAllBytes(ship.InstalledFilesPath));
        Assert.Equal("app 2.0\n", File.ReadAllText(P(ship, "app/version.txt")));
        var after = AssertOk(ship);
        Assert.Empty(after.Notices);
        Assert.Equal("2026.10.2", after.Installed!.Version);
        Assert.True(after.InstalledFiles!.Consistent);
    }

    [Theory]
    [InlineData("installed-files")]
    [InlineData("installed")]
    public void StateWriteFailure_RestoresComponentsAndInstalledFiles(string failAt)
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        var before = File.ReadAllBytes(ship.InstalledFilesPath);
        var stateBefore = File.ReadAllText(ship.InstalledStatePath);

        var pkg = SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 3.0\n"), ("app/new.dll", "n"));
        var r = applier.Apply(pkg, new UpdateApplyOptions
        {
            StateStep = step =>
            {
                if (step == failAt) throw new IOException("模擬狀態檔寫入失敗");
            },
        });
        Assert.False(r.Ok);
        Assert.True(r.RolledBack);
        Assert.Equal("套用失敗,已完整回復上一版", r.Message);
        Assert.Equal("app 2.0\n", File.ReadAllText(P(ship, "app/version.txt")));
        Assert.False(File.Exists(P(ship, "app/new.dll")));
        Assert.Equal(before, File.ReadAllBytes(ship.InstalledFilesPath));
        Assert.Equal(stateBefore, File.ReadAllText(ship.InstalledStatePath));
        Assert.False(File.Exists(ship.InstalledFilesPath + ".tmp"));
        Assert.Empty(AssertOk(ship).Notices);

        // 未提交:同一包可以重試
        var again = applier.Apply(pkg);
        Assert.True(again.Ok, string.Join(";", again.Errors));
        AssertOk(ship);
    }

    [Fact]
    public void FirstApply_StateWriteFailure_LeavesNoInstalledFiles()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var r = new UpdateApplier(ship).Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n")), new UpdateApplyOptions
        {
            StateStep = step =>
            {
                if (step == "installed") throw new IOException("模擬磁碟已滿");
            },
        });
        Assert.False(r.Ok);
        Assert.True(r.RolledBack);
        Assert.False(File.Exists(ship.InstalledFilesPath));
        Assert.False(File.Exists(ship.InstalledStatePath));
        Assert.Empty(AssertOk(ship).Notices);
    }

    // ── 各種類的更新包 ─────────────────────────────────────

    [Fact]
    public void ScenariosUpdate_Verified()
    {
        var ship = NewInstall();
        var src = Path.Combine(_env.InstructorRoot, "data", "scenarios", "E01_baseline.yaml");
        var edited = _env.PathIn("E01_baseline.yaml");
        File.WriteAllText(edited, File.ReadAllText(src) + "\n# 安裝模式核對測試:內容變更\n");
        var vr = _env.PathIn("verification-scenarios.json");
        var rec = new VerificationRecord();
        TestEnv.Verify(rec, edited, ParameterSetLoader.Load(_env.BaselineParameters), "E01_baseline");
        rec.Save(vr);
        using var key = _env.Primary();
        var pkg = UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "2026.10.3", Kind = UpdateKinds.Scenarios, Summary = "E01 修訂",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "BRM-TRN-001", Revision = "2", ApprovedBy = "訪船船長" } },
            Files = { new PackageSource("data/scenarios/E01_baseline.yaml", edited) }, ValidationRoot = _env.InstructorRoot, VerificationRecordPath = vr,
        }, key, _env.PathIn("s.sbupd")).OutputPath;
        var applied = new UpdateApplier(ship).Apply(pkg);
        Assert.True(applied.Ok, string.Join(";", applied.Errors));

        Assert.Empty(AssertOk(ship).Notices);
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Equal("data/scenarios/E01_baseline.yaml", Assert.Single(files.Files).Path);
        Assert.Equal(new[] { "data/scenarios/" }, files.Packages[0].Prefixes);

        File.AppendAllText(P(ship, "data/scenarios/E01_baseline.yaml"), "# 船上被改\n");
        var err = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal("data/scenarios/E01_baseline.yaml", err.Path);
        Assert.Equal("package:2026.10.3", err.Source);
    }

    [Fact]
    public void ParametersUpdate_Verified()
    {
        var ship = NewInstall();
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        var vr = _env.PathIn("verification-params.json");
        var rec = new VerificationRecord();
        TestEnv.Verify(rec, Path.Combine(_env.InstructorRoot, "data", "scenarios", "E01_baseline.yaml"), ParameterSetLoader.Load(p), "E01_baseline");
        rec.Save(vr);
        using var key = _env.Primary();
        var pkg = UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "2026.10.2", Kind = UpdateKinds.Parameters, Summary = "DWO-007 第 4 版",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "DWO-007", Revision = "4", ApprovedBy = "DPA 測試" } },
            Files = { new PackageSource("data/parameters/company-procedures.yaml", p) },
            ValidationRoot = _env.InstructorRoot, BaselineParameters = _env.BaselineParameters, VerificationRecordPath = vr,
        }, key, _env.PathIn("p.sbupd")).OutputPath;
        var applied = new UpdateApplier(ship).Apply(pkg);
        Assert.True(applied.Ok, string.Join(";", applied.Errors));

        Assert.Empty(AssertOk(ship).Notices);
        Assert.Equal(new[] { "data/parameters/" }, InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Packages[0].Prefixes);
        // 參數集被改回發行版的內容(與發行清單相同、與更新包不同)= 錯誤
        File.Copy(_env.BaselineParameters, P(ship, "data/parameters/company-procedures.yaml"), overwrite: true);
        var err = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal("data/parameters/company-procedures.yaml", err.Path);
        Assert.Contains("被還原", err.Detail);
    }

    [Fact]
    public void MixedUpdate_ChartsAndSoftware_Verified()
    {
        var ship = NewInstall();
        var a = _env.PathIn("app.txt");
        var c = _env.PathIn("chart.txt");
        File.WriteAllText(a, "app 2.0\n");
        File.WriteAllText(c, "chart A");
        using var key = _env.Primary();
        var pkg = UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "2026.10.1", Kind = UpdateKinds.Mixed, Summary = "軟體與圖資",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "BRM-TRN-001", Revision = "1", ApprovedBy = "訪船船長" } },
            Files = { new PackageSource("app/version.txt", a), new PackageSource("data/charts/test.txt", c) },
        }, key, _env.PathIn("mixed.sbupd")).OutputPath;
        Assert.True(new UpdateApplier(ship).Apply(pkg).Ok);

        Assert.Empty(AssertOk(ship).Notices);
        Assert.Equal(new[] { "app/", "data/" }, InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Packages[0].Prefixes);
        File.WriteAllText(P(ship, "data/charts/test.txt"), "chart X");
        Assert.Equal("data/charts/test.txt", Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Path);
    }

    private string TrustPackage(IKeyProvider signer, string version, string installName, string pubPem, IEnumerable<string> removeKeyIds)
    {
        var src = _env.PathIn($"trust-src-{version}.pem");
        File.WriteAllText(src, pubPem);
        var req = new UpdateBuildRequest
        {
            Version = version, Kind = UpdateKinds.Trust, Summary = "更換主金鑰", TrustReason = "測試",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "SMS-CYB-001", Revision = "2", ApprovedBy = "DPA" } },
            Files = { new PackageSource($"config/trusted-keys/{installName}", src) },
        };
        req.RemoveKeyIds.AddRange(removeKeyIds);
        return UpdatePackageBuilder.Build(req, signer, _env.PathIn($"trust-{version}.sbupd")).OutputPath;
    }

    [Fact]
    public void TrustUpdate_RecordedAndVerified_ManualChangeIsNotice()
    {
        var ship = NewInstall();
        using var oldPrimary = _env.Primary();
        using var backup = _env.Backup();
        var (_, newPub) = TestEnv.NewKey(KeyRoles.Primary, "新任訓練負責人");
        var applied = new UpdateApplier(ship).Apply(TrustPackage(backup, "2026.10.5", "primary-2027.pem", newPub, new[] { oldPrimary.KeyId }));
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        Assert.False(File.Exists(Path.Combine(ship.TrustedKeysDir, "primary.pem")));

        // 覆蓋清單:寫入的新公鑰、trust.remove 推得的被移除金鑰檔
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Equal("config/trusted-keys/primary-2027.pem", Assert.Single(files.Files).Path);
        Assert.Equal("config/trusted-keys/primary.pem", Assert.Single(files.Removed).Path);
        Assert.Equal(new[] { "config/trusted-keys/" }, files.Packages[0].Prefixes);
        Assert.Empty(AssertOk(ship).Notices);

        // 信任公鑰屬 config/:與更新包安裝的不同(管理者手動 trust add/remove)只列提醒
        File.AppendAllText(Path.Combine(ship.TrustedKeysDir, "primary-2027.pem"), "\n");
        var r = AssertOk(ship);
        var n = Assert.Single(r.Notices);
        Assert.Equal(C.ConfigModified, n.Category);
        Assert.Contains("trust list", n.Detail);
        File.Delete(Path.Combine(ship.TrustedKeysDir, "primary-2027.pem"));
        Assert.Equal(C.ConfigMissing, Assert.Single(AssertOk(ship).Notices).Category);
    }

    // ── 舊安裝、不一致、重新安裝 ───────────────────────────

    [Fact]
    public void LegacyInstall_HistoryWithoutInstalledFiles_IsNoticeNotError()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/added-by-old.dll", "old"))).Ok);
        // 模擬由舊版 simosa-pkg 套用:有套用歷史、沒有覆蓋清單
        File.Delete(ship.InstalledFilesPath);

        var r = AssertOk(ship);
        var gap = Assert.Single(r.Notices);
        Assert.Equal(C.Unverifiable, gap.Category);
        Assert.Equal("app/", gap.Path);
        Assert.Contains("2026.10.1", gap.Detail);
        Assert.Contains("沒有留下覆蓋清單", gap.Detail);
        Assert.True(gap.Count > 0);
        Assert.False(r.InstalledFiles!.Consistent);
        // 其他路徑照常核對
        File.AppendAllText(P(ship, "data/ships/FSB1/particulars.json"), " ");
        Assert.Equal("data/ships/FSB1/particulars.json", Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Path);
        File.WriteAllBytes(P(ship, "data/ships/FSB1/particulars.json"), File.ReadAllBytes(Path.Combine(_env.InstructorRoot, "data", "ships", "FSB1", "particulars.json")));

        // 之後套用的更新包開始記錄:它寫入的檔案納入核對,先前未記錄的仍是提醒
        Assert.True(applier.Apply(SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 3.0\n"))).Ok);
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        var untracked = Assert.Single(files.Untracked);
        Assert.Equal("2026.10.1", untracked.Version);
        Assert.Equal(UntrackedReasons.NotRecorded, untracked.Reason);
        var r2 = AssertOk(ship);
        Assert.Equal("app/", Assert.Single(r2.Notices, n => n.Category == C.Unverifiable).Path);
        File.WriteAllText(P(ship, "app/version.txt"), "app 3.x\n");
        Assert.Equal("app/version.txt", Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Path);
        File.WriteAllText(P(ship, "app/version.txt"), "app 3.0\n");

        // 完整的軟體更新包(app/ 的每個檔案)套用後,app/ 之下已沒有未核對的檔案:提醒消失
        var full = new UpdateBuildRequest { Version = "2026.10.3", Kind = UpdateKinds.Software, Summary = "完整 app/" }.AddDirectory("app", Path.Combine(ship.Root, "app"));
        Assert.True(applier.Apply(UpdatePackageBuilder.Build(full, key, _env.PathIn("full.sbupd")).OutputPath).Ok);
        Assert.Empty(AssertOk(ship).Notices);
        File.AppendAllText(P(ship, "app/added-by-old.dll"), "!");
        Assert.Equal("app/added-by-old.dll", Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Path);
    }

    [Fact]
    public void LegacyPackagesAppliedBeforeCurrentReleaseWasBuilt_AreSupersededByTheInstall()
    {
        // 實際的舊安裝:舊版 simosa-pkg(不寫覆蓋清單)套用過更新包,之後才重新安裝含新版 simosa-pkg 的發行版。
        // 更新包的套用時間早於發行版的建置時間 → 安裝已覆寫清單所列的檔案 → 以發行清單核對,不是「無法核對」
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/left-by-old.dll", "old"))).Ok);
        File.Delete(ship.InstalledFilesPath);
        Assert.Contains(AssertOk(ship).Notices, n => n.Category == C.Unverifiable);   // 清單比更新包舊:仍是無法核對

        // 重新安裝較新的發行版(清單所列檔案被覆寫;舊更新包新增的檔案留在 app/)
        File.WriteAllText(P(ship, "app/version.txt"), "app 5.0\n");
        var leftover = File.ReadAllBytes(P(ship, "app/left-by-old.dll"));
        File.Delete(P(ship, "app/left-by-old.dll"));
        WriteManifest(ship, "2026.11.0", new ManualClock(DateTimeOffset.UtcNow.AddDays(1)));
        File.WriteAllBytes(P(ship, "app/left-by-old.dll"), leftover);

        // 舊更新包留下、新發行清單沒有的檔案:程式目錄內多出的檔案(須手動刪除)
        var withLeftover = InstalledVerifier.Verify(ship.Root);
        var extra = Assert.Single(withLeftover.Errors);
        Assert.Equal(C.Extra, extra.Category);
        Assert.Equal("app/left-by-old.dll", extra.Path);
        File.Delete(P(ship, "app/left-by-old.dll"));

        var r = AssertOk(ship);
        Assert.DoesNotContain(r.Notices, n => n.Category == C.Unverifiable);
        var note = Assert.Single(r.Notices);
        Assert.Equal(C.State, note.Category);
        Assert.Contains("2026.10.1", note.Detail);
        Assert.Contains("之前套用", note.Detail);
        Assert.True(r.InstalledFiles!.Consistent);
        // app/ 照常核對(不是略過)
        File.WriteAllText(P(ship, "app/version.txt"), "app 5.x\n");
        Assert.Equal("app/version.txt", Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Path);
        File.WriteAllText(P(ship, "app/version.txt"), "app 5.0\n");

        // 之後的更新包:覆蓋清單從這裡開始,沒有未記錄的更新包
        Assert.True(applier.Apply(SoftwarePackage("2026.11.1", key, ("app/version.txt", "app 6.0\n"))).Ok);
        Assert.Empty(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Untracked);
        Assert.Empty(AssertOk(ship).Notices);
    }

    [Fact]
    public void BrokenInstalledFilesContent_DoesNotCrashApplyOrVerify()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);

        // 清單欄位為 null、項目為 null、路徑不合法:視同損毀(提醒 + 無法核對),不得以未處理例外中止
        foreach (var broken in new[]
                 {
                     "{\"format\":\"simosa-installed-files/1\",\"files\":null,\"removed\":null,\"packages\":[null],\"untracked\":null}",
                     "{\"format\":\"simosa-installed-files/1\",\"files\":[{\"path\":\"../outside\",\"sha256\":\"00\",\"size\":1}]}",
                     "{\"format\":\"other/9\"}",
                 })
        {
            File.WriteAllText(ship.InstalledFilesPath, broken);
            var r = AssertOk(ship);
            Assert.Contains(r.Notices, n => n.Category == C.Unverifiable && n.Path == "app/");
        }

        // 套用不受影響:重新建立清單並警告,先前的更新包記為未記錄
        var next = applier.Apply(SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 3.0\n")));
        Assert.True(next.Ok, string.Join(";", next.Errors));
        Assert.Contains(next.Warnings, w => w.Contains("覆蓋清單"));
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out var error)!;
        Assert.Null(error);
        Assert.Equal("2026.10.1", Assert.Single(files.Untracked).Version);
        Assert.True(AssertOk(ship).InstalledFiles!.Consistent);
    }

    [Fact]
    public void LegacyTrustUpdate_WithoutInstalledFiles_IsNotice()
    {
        var ship = NewInstall();
        using var oldPrimary = _env.Primary();
        using var backup = _env.Backup();
        var (_, newPub) = TestEnv.NewKey(KeyRoles.Primary, "新任訓練負責人");
        Assert.True(new UpdateApplier(ship).Apply(TrustPackage(backup, "2026.10.5", "primary-2027.pem", newPub, new[] { oldPrimary.KeyId })).Ok);
        File.Delete(ship.InstalledFilesPath);

        var r = AssertOk(ship);
        var gap = Assert.Single(r.Notices);
        Assert.Equal(C.Unverifiable, gap.Category);
        Assert.Equal("config/trusted-keys/", gap.Path);
    }

    [Fact]
    public void InstalledFilesDisagreesWithInstalledJson_IsUnverifiable_NotAllGood()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        var afterP1 = File.ReadAllBytes(ship.InstalledFilesPath);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 3.0\n"))).Ok);
        AssertOk(ship);

        // 覆蓋清單停在上一包(installed.json 已是 2026.10.2;例如中途當機或檔案被舊備份蓋回)
        File.WriteAllBytes(ship.InstalledFilesPath, afterP1);
        var r = AssertOk(ship);
        Assert.False(r.InstalledFiles!.Consistent);
        var gap = Assert.Single(r.Notices, n => n.Category == C.Unverifiable);
        Assert.Equal("app/", gap.Path);
        Assert.Contains("不一致", gap.Detail);
        Assert.Contains("2026.10.2", gap.Detail);
        // 不一致時 app/ 之下的紀錄不可信:不比對(不誤報錯誤),但也不是「沒有提醒的通過」
        File.WriteAllText(P(ship, "app/version.txt"), "???\n");
        Assert.True(InstalledVerifier.Verify(ship.Root).Ok);

        // 反方向:覆蓋清單比 installed.json 新(覆蓋清單已寫、installed.json 未寫就中斷)
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        files.PackageId = "upd-future";
        files.Version = "2026.10.9";
        files.Packages.Insert(0, new TrackedPackage { PackageId = "upd-future", Version = "2026.10.9", Kind = UpdateKinds.Scenarios, Prefixes = { "data/scenarios/" } });
        files.Save(ship.InstalledFilesPath);
        var r2 = AssertOk(ship);
        Assert.Contains(r2.Notices, n => n.Category == C.Unverifiable && n.Path == "data/scenarios/" && n.Detail!.Contains("2026.10.9"));

        // 下一次套用把對不上的更新包記為 untracked,之後恢復一致
        var next = applier.Apply(SoftwarePackage("2026.10.3", key, ("app/version.txt", "app 4.0\n")));
        Assert.True(next.Ok, string.Join(";", next.Errors));
        var after = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Equal(next.PackageId, after.PackageId);
        Assert.Contains(after.Untracked, u => u.Reason == UntrackedReasons.Diverged);
        Assert.DoesNotContain(after.Packages, p => p.PackageId == "upd-future");
        Assert.True(AssertOk(ship).InstalledFiles!.Consistent);
    }

    [Fact]
    public void UnreadableStateFiles_AreNotices_AndPrefixesUnverifiable()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        Assert.True(new UpdateApplier(ship).Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);

        // 覆蓋清單損毀:視同遺失 → 舊安裝的處理
        File.WriteAllText(ship.InstalledFilesPath, "{ broken");
        var r = AssertOk(ship);
        Assert.Contains(r.Notices, n => n.Category == C.State && n.Path == "state/installed-files.json");
        Assert.Contains(r.Notices, n => n.Category == C.Unverifiable && n.Path == "app/");

        // installed.json 損毀:不知道套用過什麼 → 更新包能寫入的前綴全部無法核對;其他路徑照常
        File.WriteAllText(ship.InstalledStatePath, "{ broken");
        var r2 = AssertOk(ship);
        Assert.Contains(r2.Notices, n => n.Category == C.State && n.Path == "state/installed.json");
        Assert.Equal(new[] { "app/", "config/trusted-keys/", "data/" }, r2.Notices.Where(n => n.Category == C.Unverifiable).Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        File.WriteAllText(P(ship, "docs/README.md"), "改\n");
        Assert.Equal("docs/README.md", Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Path);
    }

    [Fact]
    public void ReinstallAfterUpdate_ManifestWinsForListedFiles_AndNextApplyRebases()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/added.dll", "a"))).Ok);
        AssertOk(ship);

        // 以較新的發行版覆蓋安裝:清單所列檔案全部被安裝程式覆寫、manifest.json 換新;更新包新增的 app/added.dll 留在原處
        File.WriteAllText(P(ship, "app/version.txt"), "app 5.0\n");
        File.WriteAllText(P(ship, "app/simcore/SimosaBRM.SimCore.Host.dll"), "fake host 5.0\n");
        var keep = File.ReadAllBytes(P(ship, "app/added.dll"));
        File.Delete(P(ship, "app/added.dll"));
        WriteManifest(ship, "2026.11.0");
        File.WriteAllBytes(P(ship, "app/added.dll"), keep);

        var r = AssertOk(ship);
        Assert.True(r.InstalledFiles!.Reinstalled);
        var notice = Assert.Single(r.Notices);
        Assert.Equal(C.State, notice.Category);
        Assert.Contains("重新安裝", notice.Detail);
        Assert.Contains("1 個檔案", notice.Detail);
        // 清單所列檔案以新清單核對;更新包新增而清單沒有的檔案仍以覆蓋清單核對
        File.WriteAllText(P(ship, "app/version.txt"), "app 5.x\n");
        var err = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal("release", err.Source);
        File.WriteAllText(P(ship, "app/version.txt"), "app 5.0\n");
        File.WriteAllText(P(ship, "app/added.dll"), "b");
        Assert.Equal("package:2026.10.1", Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Source);
        File.WriteAllBytes(P(ship, "app/added.dll"), keep);

        // 下一次套用:覆蓋清單改依新的發行清單(被取代的紀錄丟掉),不再提醒
        Assert.True(applier.Apply(SoftwarePackage("2026.11.1", key, ("app/web/index.html", "<!doctype html><title>新</title>\n"))).Ok);
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Equal(new[] { "app/added.dll", "app/web/index.html" }, files.Files.Select(f => f.Path).ToArray());
        Assert.Equal("2026.11.0", files.Baseline!.Version);
        Assert.Empty(AssertOk(ship).Notices);
    }

    // ── 路徑分類與安裝程式、InstallLayout 對照 ─────────────

    [Fact]
    public void RuntimeDataLocations_CoverInstallLayoutAndInstallerDirs()
    {
        // InstallLayout 管理的位置
        var root = _env.PathIn("layout-root");
        var layout = new InstallLayout(root, new PackagingConfig());
        string Rel(string full, bool dir) => PackagePaths.Relative(root, full) + (dir ? "/" : "");
        foreach (var dir in new[] { layout.StateDir, layout.StagingDir, layout.PreviousDir, layout.IncomingDir, layout.OutboxDir, layout.DeviceDir, layout.TrustedKeysDir, layout.RecordsDir, Path.Combine(root, "store") })
            Assert.True(InstalledPaths.FindRuntimeData(Rel(dir, true), StringComparison.Ordinal) is not null, dir);
        foreach (var file in new[] { layout.ConfigPath, layout.InstalledStatePath, layout.InstalledFilesPath, layout.AuditPath, layout.LedgerPath, layout.LockPath })
            Assert.True(InstalledPaths.FindRuntimeData(Rel(file, false), StringComparison.Ordinal) is not null, file);
        Assert.Equal(Path.Combine(layout.StateDir, InstalledFiles.FileName), layout.InstalledFilesPath);

        // 安裝程式 [Dirs] 建立的資料資料夾與 [UninstallDelete] 清除的位置(tools/installer/SimosaBRM.iss)
        var iss = File.ReadAllLines(Path.Combine(_env.RepoRoot, "tools", "installer", "SimosaBRM.iss"));
        var section = "";
        var seen = new List<string>();
        foreach (var raw in iss)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line;
                continue;
            }
            if (section is not ("[Dirs]" or "[UninstallDelete]") || line.StartsWith(';')) continue;
            var marker = "Name: \"{app}\\";
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var start = at + marker.Length;
            var rel = line[start..line.IndexOf('"', start)].Replace('\\', '/') + "/";
            seen.Add(rel);
            Assert.True(InstalledPaths.FindRuntimeData(rel, StringComparison.Ordinal) is not null, $"安裝程式 {section} 的 {rel} 不在 InstalledPaths.RuntimeData");
        }
        Assert.Contains("build/records/", seen);
        Assert.Contains("state/", seen);
        Assert.Contains("config/device/", seen);

        // 程式目錄與設定檔不是執行期資料;更新包的元件都有分類
        Assert.Null(InstalledPaths.FindRuntimeData("app/simcore/x.dll", StringComparison.Ordinal));
        Assert.Null(InstalledPaths.FindRuntimeData("tools/updater/simosa-pkg.dll", StringComparison.Ordinal));
        Assert.Null(InstalledPaths.FindRuntimeData("config/ship-laptop.json", StringComparison.Ordinal));
        Assert.Null(InstalledPaths.FindRuntimeData("data/ships/FSB1/particulars.json", StringComparison.Ordinal));
        Assert.True(InstalledPaths.IsProgram("app/web/index.html", StringComparison.Ordinal));
        Assert.True(InstalledPaths.IsUserConfig("config/ship-laptop.json", StringComparison.Ordinal));
        Assert.True(InstalledPaths.IsAtOrUnder("app/web", "app/web/", StringComparison.Ordinal));
        Assert.False(InstalledPaths.IsAtOrUnder("app/webx/a", "app/web", StringComparison.Ordinal));
        Assert.True(InstalledPaths.IsAtOrUnder("APP/Web/a", "app/web", StringComparison.OrdinalIgnoreCase));
    }

    // ── CLI ───────────────────────────────────────────────

    [Fact]
    public void Cli_ReleaseVerifyInstalled_ExitCodes_TextAndJson()
    {
        var ship = NewInstall();
        AddRuntimeData(ship);
        File.WriteAllText(P(ship, "config/ship-laptop.json"), "{\"shipId\":\"FSB2\"}\n");

        // 通過(有提醒):結束碼 0
        var ok = Run("release", "verify", "--dir", ship.Root, "--installed");
        Assert.Equal(0, ok.Code);
        Assert.Contains("安裝模式核對", ok.Out);
        Assert.Contains("核對通過", ok.Out);
        Assert.Contains("1 項提醒", ok.Out);
        Assert.Contains("config/ship-laptop.json", ok.Out);
        Assert.Contains("build/", ok.Out);
        Assert.Equal("", ok.Err);
        // 等效的短指令
        var alias = Run("verify-install", "--root", ship.Root);
        Assert.Equal(0, alias.Code);
        Assert.Equal(ok.Out, alias.Out);

        var json = Run("release", "verify", "--installed", "--json", "--dir", ship.Root);
        Assert.Equal(0, json.Code);
        var node = JsonNode.Parse(json.Out)!;
        Assert.Equal("simosa-install-check/1", node["format"]!.GetValue<string>());
        Assert.Equal("installed", node["mode"]!.GetValue<string>());
        Assert.True(node["ok"]!.GetValue<bool>());
        Assert.Equal(1, node["counts"]!["configModified"]!.GetValue<int>());
        Assert.Equal("config/ship-laptop.json", node["notices"]![0]!["path"]!.GetValue<string>());
        Assert.Empty(node["errors"]!.AsArray());

        // 錯誤:結束碼 1、說明下一步;每類預設只列前 10 筆再給總數
        File.AppendAllText(P(ship, "app/web/index.html"), "<!-- 損毀 -->");
        for (var i = 0; i < 12; i++) W(ship.Root, $"app/stray-{i:D2}.dll", "x");
        var bad = Run("release", "verify", "--dir", ship.Root, "--installed");
        Assert.Equal(1, bad.Code);
        Assert.Contains("核對失敗", bad.Err);
        Assert.Contains("app/web/index.html", bad.Err);
        Assert.Contains("下一步", bad.Err);
        Assert.Contains("重新安裝", bad.Err);
        Assert.Contains("12 筆", bad.Err);
        Assert.Contains("另有 2 筆", bad.Err);
        Assert.Contains("app/stray-09.dll", bad.Err);
        Assert.DoesNotContain("app/stray-10.dll", bad.Err);
        var all = Run("verify-install", "--root", ship.Root, "--max-items", "0");
        Assert.Contains("app/stray-11.dll", all.Err);
        var badJson = Run("verify-install", "--root", ship.Root, "--json");
        Assert.Equal(1, badJson.Code);
        var badNode = JsonNode.Parse(badJson.Out)!;
        Assert.False(badNode["ok"]!.GetValue<bool>());
        Assert.Equal(13, badNode["errors"]!.AsArray().Count);
        Assert.Equal(12, badNode["counts"]!["extra"]!.GetValue<int>());
        Assert.NotEmpty(badNode["nextSteps"]!.AsArray());

        // 沒有清單的目錄:結束碼 1
        Assert.Equal(1, Run("verify-install", "--root", _env.PathIn("empty-root")).Code);
    }

    [Fact]
    public void Cli_PlainReleaseVerify_Unchanged_ButHintsOnInUseRoot()
    {
        // 剛複製好的發行版:與之前完全相同(沒有提示)
        var ship = NewInstall();
        var release = _env.PathIn("release-copy");
        foreach (var rel in new[] { "app", "tools", "contracts", "docs" }) PackagePaths.CopyDirectory(Path.Combine(ship.Root, rel), Path.Combine(release, rel));
        ReleaseManifestBuilder.Save(ReleaseManifestBuilder.Build(release, "2026.10.0"), release);
        var fresh = Run("release", "verify", "--dir", release);
        Assert.Equal(0, fresh.Code);
        Assert.Contains("核對通過", fresh.Out);
        Assert.Equal("", fresh.Err);
        var freshJson = JsonNode.Parse(Run("release", "verify", "--dir", release, "--json").Out)!;
        Assert.Equal("release", freshJson["mode"]!.GetValue<string>());
        Assert.True(freshJson["ok"]!.GetValue<bool>());
        Assert.Null(freshJson["hint"]);

        // 使用中的安裝根目錄而未加 --installed:結果與結束碼不變(失敗),多一行提示
        AddRuntimeData(ship);
        var inUse = Run("release", "verify", "--dir", ship.Root);
        Assert.Equal(1, inUse.Code);
        Assert.Contains("核對失敗", inUse.Err);
        Assert.Contains("清單中沒有的檔案", inUse.Err);
        Assert.Contains("提示", inUse.Err);
        Assert.Contains("--installed", inUse.Err);
        var inUseJson = JsonNode.Parse(Run("release", "verify", "--dir", ship.Root, "--json").Out)!;
        Assert.False(inUseJson["ok"]!.GetValue<bool>());
        Assert.Contains("--installed", inUseJson["hint"]!.GetValue<string>());
    }

    [Fact]
    public void Cli_Status_PointsToInstalledVerify_AndApplyThenVerify()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var pkg = SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"));
        Assert.Equal(0, Run("apply", pkg, "--root", ship.Root, "--operator", "船長").Code);
        var st = Run("status", "--root", ship.Root);
        Assert.Equal(0, st.Code);
        Assert.Contains("verify-install", st.Out);
        Assert.Contains("--installed", st.Out);

        var v = Run("verify-install", "--root", ship.Root);
        Assert.Equal(0, v.Code);
        Assert.Contains("現行更新包:2026.10.1", v.Out);
        Assert.Contains("更新包寫入 1 個檔案", v.Out);
        Assert.Equal(0, Run("rollback", "--root", ship.Root).Code);
        var after = Run("verify-install", "--root", ship.Root);
        Assert.Equal(0, after.Code);
        Assert.Contains("初始安裝", after.Out);
        Assert.Contains("沒有提醒", after.Out);
    }
}
