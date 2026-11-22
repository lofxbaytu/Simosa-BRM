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

    /// <summary>發行清單不含的位置(安裝後才建立:信任清單、裝置金鑰、packaging.json、state/,以及使用後的執行期資料與岸上放入的離線安裝檔)。</summary>
    private static bool CreatedAfterInstall(string path)
        => path.StartsWith("state/", StringComparison.Ordinal) || path.StartsWith("config/trusted-keys/", StringComparison.Ordinal)
           || path.StartsWith("config/device/", StringComparison.Ordinal) || path == "config/packaging.json"
           || path.StartsWith("build/", StringComparison.Ordinal) || path.StartsWith(".cache/", StringComparison.Ordinal)
           || path.StartsWith("store/", StringComparison.Ordinal) || path.StartsWith("data/records/", StringComparison.Ordinal)
           || path.EndsWith(".exe", StringComparison.Ordinal) || path.EndsWith("desktop.ini", StringComparison.Ordinal);

    /// <param name="notInRelease">磁碟上有、但不屬於這個發行版的檔案(更新包新增的檔案、手動覆蓋後殘留的舊檔)</param>
    private static void WriteManifest(InstallLayout ship, string version, TimeProvider? clock = null, params string[] notInRelease)
    {
        var m = ReleaseManifestBuilder.Build(ship.Root, version, "0123456789abcdef", "main", "linux-x64", clock: clock);
        m.Files.RemoveAll(f => CreatedAfterInstall(f.Path) || notInRelease.Contains(f.Path));
        ReleaseManifestBuilder.Save(m, ship.Root);
    }

    /// <summary>新版發行版的程式檔(與 <see cref="NewInstall"/> 的 2026.10.0 相比:內容不同,網頁資產檔名的雜湊也不同)。</summary>
    private static void WriteNewerProgramFiles(InstallLayout ship)
    {
        W(ship.Root, "app/version.txt", "app 5.0\n");
        W(ship.Root, "app/simcore/SimosaBRM.SimCore.Host.dll", "fake host 5.0\n");
        W(ship.Root, "app/web/index.html", "<!doctype html><title>儀器 5</title>\n");
        W(ship.Root, "app/web/assets/index-xyz.js", "console.log(5)\n");
        W(ship.Root, "tools/updater/simosa-pkg.dll", "fake pkg 5\n");
    }

    /// <summary>
    /// 模擬以安裝程式重新安裝較新的發行版(tools/installer/SimosaBRM.iss):[InstallDelete] 先清空程式目錄 app\ 與 tools\updater\,
    /// 再安裝新版的檔案並換上新的 manifest.json;config\(已存在不覆寫)、data\ 內清單沒有的檔案、state\ 都不動。
    /// </summary>
    private static void ReinstallWithInstaller(InstallLayout ship, string version, params string[] notInRelease)
    {
        foreach (var prefix in InstalledPaths.ProgramPrefixes)
        {
            var dir = P(ship, prefix.TrimEnd('/'));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        WriteNewerProgramFiles(ship);
        WriteManifest(ship, version, null, notInRelease);
    }

    /// <summary>建立目錄連結:Windows 先試 junction(mklink /J,不需特殊權限),否則符號連結;建不出來回傳 false。</summary>
    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                proc.WaitForExit(10000);
                if (proc.HasExited && proc.ExitCode == 0 && Directory.Exists(link)) return true;
            }
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }
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
        // 只有多出的檔案:下一步是依清單刪除(以安裝程式重新安裝也會清除 — 安裝前先清空程式目錄;手動覆蓋不會),不是「檔案損壞」那一段
        var step = Assert.Single(r.NextSteps);
        Assert.Contains("刪除", step);
        Assert.Contains("安裝程式重新安裝也會清除", step);
        Assert.Contains("手動複製覆蓋發行版不會清除", step);
        Assert.DoesNotContain("請先不要用於訓練", step);
    }

    [Fact]
    public void DirectoryLink_InProgramDirIsError_ElsewhereIsNotice_RuntimeDataIgnored()
    {
        // 目錄連結(junction/symlink)不跟進,但不能靜默略過:程式目錄內的連結指到的內容會被載入或被網頁伺服送出,卻查不到多出的檔案
        var ship = NewInstall();
        var outside = _env.PathIn("outside");
        W(outside, "web/index.html", File.ReadAllText(P(ship, "app/web/index.html")));
        W(outside, "web/assets/index-abc.js", File.ReadAllText(P(ship, "app/web/assets/index-abc.js")));
        W(outside, "web/Injected.dll", "x");
        W(outside, "plugins/Plugin.dll", "x");
        W(outside, "docs/n.txt", "x");
        W(outside, "records/a.jsonl", "{}\n");
        W(outside, "updater/simosa-pkg.dll", File.ReadAllText(P(ship, "tools/updater/simosa-pkg.dll")));
        var links = new List<string>();
        bool Link(string rel, string target)
        {
            var link = P(ship, rel);
            if (!TryLinkDirectory(link, Path.Combine(outside, target))) return false;
            links.Add(link);
            return true;
        }
        try
        {
            // app/web 整個換成指到別處的連結(內容相同、另多一個 DLL)、app/ 內多一個連結、其他位置的連結、執行期資料位置的連結
            Directory.Delete(P(ship, "app/web"), recursive: true);
            if (!Link("app/web", "web"))
            {
                // 建不出目錄連結只可能發生在 Windows(junction 與符號連結都被拒);其他平台一定要建得出來,不讓這個測試靜默略過
                Assert.True(OperatingSystem.IsWindows(), "無法建立目錄符號連結");
                return;
            }
            Assert.True(Link("app/plugins", "plugins"));
            Assert.True(Link("linked-docs", "docs"));
            Assert.True(Link("build/records", "records"));

            var r = InstalledVerifier.Verify(ship.Root);
            Assert.False(r.Ok);
            Assert.Equal(new[] { "app/plugins/", "app/web/" }, r.Errors.Select(e => e.Path).ToArray());
            Assert.All(r.Errors, e =>
            {
                Assert.Equal(C.Extra, e.Category);
                Assert.Contains("目錄連結", e.Detail);
                Assert.Contains("無法核對", e.Detail);
            });
            var notice = Assert.Single(r.Notices);
            Assert.Equal(C.Unlisted, notice.Category);
            Assert.Equal("linked-docs/", notice.Path);
            Assert.Contains("目錄連結", notice.Detail);
            Assert.Contains("build/", r.SkippedLocations);                       // 執行期資料位置的連結(例如紀錄移到另一顆磁碟)不算
            Assert.Contains(r.NextSteps, s => s.Contains("目錄連結"));
            // 清單所列的檔案仍依路徑核對(跟著連結走):內容相同所以沒有 missing/mismatch
            Assert.DoesNotContain(r.Errors, e => e.Category is C.Missing or C.Mismatch);

            // 程式目錄的上層本身是連結(tools → 別處):同樣是錯誤
            Directory.Delete(P(ship, "app/plugins"));
            Directory.Delete(P(ship, "app/web"));
            links.RemoveAll(l => l.EndsWith("plugins", StringComparison.Ordinal) || l.EndsWith("web", StringComparison.Ordinal));
            W(ship.Root, "app/web/index.html", File.ReadAllText(Path.Combine(outside, "web", "index.html")));
            W(ship.Root, "app/web/assets/index-abc.js", File.ReadAllText(Path.Combine(outside, "web", "assets", "index-abc.js")));
            Directory.Delete(P(ship, "tools/updater"), recursive: true);
            Assert.True(Link("tools/updater", "updater"));
            var r2 = InstalledVerifier.Verify(ship.Root);
            var e2 = Assert.Single(r2.Errors);
            Assert.Equal("tools/updater/", e2.Path);
            Assert.Equal(C.Extra, e2.Category);
        }
        finally
        {
            // 連結本身先移除(不動它指向的內容),測試目錄才能照常清掉
            foreach (var link in links)
            {
                try { Directory.Delete(link); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
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
        // 套用過更新包:下一步不是「原安裝程式」而是不低於現行版本的安裝程式或更高版本的更新包(同版本不可重複套用)。
        // 版本基準 = 發行清單版本(2026.10.0)與現行更新包版本(2026.10.1)的較大者;更新包須包含受損的檔案
        var next = tampered.NextSteps[0];
        Assert.Contains("版本不低於 2026.10.1 的安裝程式", next);
        Assert.Contains("版本高於 2026.10.1、且包含上列受損檔案的更新包", next);
        Assert.Contains("完整的軟體更新包", next);
        Assert.Contains("不能重複套用", next);
        Assert.Contains("安裝程式會先清除舊的程式檔", next);
        Assert.DoesNotContain("原安裝程式", next);
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
        MakeLegacyPreviousMeta(ship);   // 舊版 simosa-pkg 的 previous.json 沒有發行清單身分:只能靠時間規則
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
        var settled = AssertOk(ship);
        Assert.True(settled.InstalledFiles!.Consistent);
        Assert.Null(settled.InstalledFiles.InterruptedVersion);
        // 已寫進覆蓋清單的「不一致」紀錄:現在兩個狀態檔已一致,說明用過去式,不再指示以 status 確認(沒有待處理的事)
        var gaps = settled.Notices.Where(n => n.Category == C.Unverifiable).ToList();
        Assert.Contains(gaps, n => n.Path == "app/");
        Assert.All(gaps, n =>
        {
            Assert.Contains("曾與 state/installed.json 不一致", n.Detail);
            Assert.Contains("現在兩者已一致", n.Detail);
            Assert.DoesNotContain("先以 simosa-pkg status", n.Detail);
        });
    }

    [Fact]
    public void InterruptedApply_OverlayWrittenButInstalledJsonNot_RetryWithSamePackageConverges()
    {
        // 套用在「覆蓋清單已寫、installed.json 未寫」之間中斷(斷電):元件已換成新版,installed.json 還是舊的、完全沒有這個更新包的紀錄
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/q.dll", "q"))).Ok);
        var stateBeforeP = File.ReadAllBytes(ship.InstalledStatePath);
        var p = SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 3.0\n"), ("app/p.dll", "p"));
        Assert.True(applier.Apply(p).Ok);
        File.WriteAllBytes(ship.InstalledStatePath, stateBeforeP);   // 模擬 installed.json 沒寫進去

        // 重試之前:app/ 列為無法核對,並指出可用同一個更新包重新套用(它沒有被記為已套用,版本檢查會通過)
        var torn = AssertOk(ship);
        Assert.False(torn.InstalledFiles!.Consistent);
        Assert.Equal("2026.10.2", torn.InstalledFiles.InterruptedVersion);
        var gap = Assert.Single(torn.Notices, n => n.Category == C.Unverifiable);
        Assert.Equal("app/", gap.Path);
        Assert.Contains("以同一個更新包檔重新套用", gap.Detail);
        var text = Run("verify-install", "--root", ship.Root);
        Assert.Equal(0, text.Code);
        Assert.Contains("例外:更新包 2026.10.2 的套用沒有完成", text.Out);

        // 原包重試:成功,而且不留下「無法核對 app/」— 之前同一個更新包會同時在 packages 與 untracked(diverged),提醒永久存在
        var again = applier.Apply(p);
        Assert.True(again.Ok, string.Join(";", again.Errors));
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Empty(files.Untracked);
        Assert.Equal(new[] { "2026.10.2", "2026.10.1" }, files.Packages.Select(x => x.Version).ToArray());
        Assert.Equal(new[] { "app/p.dll", "app/q.dll", "app/version.txt" }, files.Files.Select(f => f.Path).ToArray());
        var ok = AssertOk(ship);
        Assert.Empty(ok.Notices);
        Assert.True(ok.InstalledFiles!.Consistent);
        Assert.Null(ok.InstalledFiles.InterruptedVersion);
        // 先前更新包(2026.10.1)的紀錄沒有被丟掉:它寫入的檔案仍在核對範圍
        File.WriteAllText(P(ship, "app/q.dll"), "Q");
        var err = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal("app/q.dll", err.Path);
        Assert.Equal("package:2026.10.1", err.Source);
    }

    [Fact]
    public void Reconcile_RetryRule_OnlyWhenInstalledJsonHasNoTraceOfThePackage()
    {
        // 「原包重試」只認:覆蓋清單的現行更新包 = 正在套用的更新包,且 installed.json 完全沒有它(現行、history 含 rolledBack 都沒有)。
        // rollback 時清單還原失敗的情況(history 有該包、標為 rolledBack)清單不可信,仍照不一致處理
        var overlay = new InstalledFiles { PackageId = "upd-P", Version = "2026.10.2" };
        overlay.Packages.Add(new TrackedPackage { PackageId = "upd-P", Version = "2026.10.2", Kind = UpdateKinds.Software, Prefixes = { "app/" } });
        overlay.Packages.Add(new TrackedPackage { PackageId = "upd-Q", Version = "2026.10.1", Kind = UpdateKinds.Software, Prefixes = { "app/" } });
        overlay.Files.Add(new InstalledFile { Path = "app/q.dll", Sha256 = "00", Size = 1, PackageId = "upd-Q", Version = "2026.10.1" });
        overlay.Files.Add(new InstalledFile { Path = "app/p.dll", Sha256 = "11", Size = 1, PackageId = "upd-P", Version = "2026.10.2" });
        InstalledState State(bool withRolledBackP)
        {
            var st = new InstalledState { PackageId = "upd-Q", Version = "2026.10.1", Kind = UpdateKinds.Software };
            if (withRolledBackP) st.History.Add(new AppliedPackage { PackageId = "upd-P", Version = "2026.10.2", Kind = UpdateKinds.Software, RolledBack = true });
            st.History.Add(new AppliedPackage { PackageId = "upd-Q", Version = "2026.10.1", Kind = UpdateKinds.Software });
            return st;
        }

        var retry = new ReconcileInfo();
        var kept = InstalledFiles.Reconcile(overlay, State(false), null, null, StringComparison.Ordinal, retry, applyingPackageId: "upd-P");
        Assert.True(retry.Retried);
        Assert.False(retry.Diverged);
        Assert.Empty(kept.Untracked);
        Assert.Equal(2, kept.Files.Count);

        // 核對時(沒有正在套用的更新包):不一致,並指出這個更新包的套用沒有完成
        var verify = new ReconcileInfo();
        var gapped = InstalledFiles.Reconcile(overlay, State(false), null, null, StringComparison.Ordinal, verify);
        Assert.True(verify.Diverged);
        Assert.Equal("upd-P", verify.InterruptedPackage!.PackageId);
        Assert.Equal("upd-P", Assert.Single(gapped.Untracked).PackageId);
        Assert.Empty(gapped.Files);

        // 套用的是別的更新包:不是重試
        var other = new ReconcileInfo();
        InstalledFiles.Reconcile(overlay, State(false), null, null, StringComparison.Ordinal, other, applyingPackageId: "upd-R");
        Assert.False(other.Retried);
        Assert.True(other.Diverged);

        // installed.json 有該包標為 rolledBack 的紀錄:清單還原失敗,不是套用中斷
        var rolledBack = new ReconcileInfo();
        var dropped = InstalledFiles.Reconcile(overlay, State(true), null, null, StringComparison.Ordinal, rolledBack, applyingPackageId: "upd-P");
        Assert.False(rolledBack.Retried);
        Assert.True(rolledBack.Diverged);
        Assert.Null(rolledBack.InterruptedPackage);
        Assert.Equal(UntrackedReasons.Diverged, Assert.Single(dropped.Untracked).Reason);
        Assert.Empty(dropped.Files);
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
    public void ReinstallWithInstaller_AfterUpdateAddedProgramFiles_ConvergesWithoutManualCleanup()
    {
        // 更新包在 app/ 新增檔案、刪除檔案 → 以安裝程式重新安裝較新的發行版(安裝前清空 app\ 與 tools\updater\)→ 核對通過。
        // 之前的規則把更新包新增的 app/added.dll 留在預期內容,安裝程式清掉它之後會誤報「檔案缺少」
        var ship = NewInstall();
        AddRuntimeData(ship);
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        var pkg = SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/added.dll", "a"), ("app/plugins/extra.dll", "e"));
        TestEnv.ResignManifest(pkg, key, m => m.Remove.Add("app/web/assets/index-abc.js"));
        Assert.True(applier.Apply(pkg).Ok);
        AssertOk(ship);

        ReinstallWithInstaller(ship, "2026.11.0");
        Assert.False(File.Exists(P(ship, "app/added.dll")));
        Assert.False(File.Exists(P(ship, "app/web/assets/index-abc.js")));

        var r = AssertOk(ship);
        Assert.Empty(r.Errors);
        Assert.True(r.InstalledFiles!.Reinstalled);
        Assert.True(r.InstalledFiles.Consistent);
        var notice = Assert.Single(r.Notices);
        Assert.Equal(C.State, notice.Category);
        Assert.Contains("重新安裝", notice.Detail);
        Assert.Contains("對 1 個檔案的變更已被安裝覆蓋", notice.Detail);          // app/version.txt
        Assert.Contains("新增的 2 個檔案不再列入預期", notice.Detail);            // app/added.dll、app/plugins/extra.dll
        Assert.Contains("status", notice.Detail);
        Assert.Contains("請不要執行 rollback", notice.Detail);                    // state/previous 是重新安裝之前的 app/
        // rollback 拒絕:state/previous 是重新安裝之前的元件,回復會把舊版的 app/version.txt(app 1.0)放回 2026.11.0 的發行版上
        Assert.False(applier.CanRollback(out var refuse));
        Assert.Contains("重新安裝", refuse);
        Assert.Contains("2026.10.0 → 2026.11.0", refuse);
        var rb = applier.Rollback("測試");
        Assert.False(rb.Ok);
        Assert.Equal("app 5.0\n", File.ReadAllText(P(ship, "app/version.txt")));      // 沒有被舊版蓋回
        AssertOk(ship);
        // 程式目錄照常核對(以新的發行清單)
        File.WriteAllText(P(ship, "app/version.txt"), "app 5.x\n");
        var err = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal("release", err.Source);
        File.WriteAllText(P(ship, "app/version.txt"), "app 5.0\n");

        // 核對只讀不寫:覆蓋清單還是舊的,每次核對的結論相同;下一次套用才把調整寫回(程式目錄的舊紀錄全部作廢)
        Assert.Equal("2026.10.0", InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Baseline!.Version);
        Assert.Single(AssertOk(ship).Notices);
        Assert.True(applier.Apply(SoftwarePackage("2026.11.1", key, ("app/web/index.html", "<!doctype html><title>新</title>\n"))).Ok);
        Assert.True(applier.CanRollback(out var again), again);              // 重新安裝之後再套用:state/previous 又是可回復的上一版
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Equal("app/web/index.html", Assert.Single(files.Files).Path);
        Assert.Empty(files.Removed);
        Assert.Empty(files.Untracked);
        Assert.Equal("2026.11.0", files.Baseline!.Version);
        Assert.Empty(AssertOk(ship).Notices);
    }

    [Fact]
    public void ManualOverwriteWithNewerRelease_LeftoverProgramFiles_AreReportedAsExtra()
    {
        // 以壓縮檔或手動複製覆蓋發行版(沒有經過安裝程式的清空):舊版留下、新清單沒有的程式檔 — 舊的網頁資產、先前更新包新增的檔案 —
        // 都是「程式目錄內多出的檔案」,依清單刪除後通過。更新包新增的檔案不再以覆蓋清單「放行」(那樣舊組件會一直留在 app/ 被載入)
        var ship = NewInstall();
        using var key = _env.Primary();
        Assert.True(new UpdateApplier(ship).Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/added.dll", "a"))).Ok);
        AssertOk(ship);

        // 手動覆蓋:新版的檔案蓋上去,舊檔原地不動(app/web/assets/index-abc.js 是舊版的資產,新版改名為 index-xyz.js)
        WriteNewerProgramFiles(ship);
        WriteManifest(ship, "2026.11.0", null, "app/added.dll", "app/web/assets/index-abc.js");

        var r = InstalledVerifier.Verify(ship.Root);
        Assert.False(r.Ok);
        Assert.Equal(new[] { "app/added.dll", "app/web/assets/index-abc.js" }, r.Errors.Select(e => e.Path).ToArray());
        Assert.All(r.Errors, e => Assert.Equal(C.Extra, e.Category));
        Assert.True(r.InstalledFiles!.Reinstalled);
        var step = Assert.Single(r.NextSteps);
        Assert.Contains("依上列清單刪除", step);
        // 套用過更新包(現行 2026.10.1)、發行版已是 2026.11.0:若要改用安裝程式清除,版本基準是兩者較大者
        Assert.Contains("版本不低於 2026.11.0 的安裝程式", step);

        File.Delete(P(ship, "app/added.dll"));
        File.Delete(P(ship, "app/web/assets/index-abc.js"));
        var ok = AssertOk(ship);
        Assert.Equal(C.State, Assert.Single(ok.Notices).Category);
    }

    [Fact]
    public void Reinstall_OutsideProgramDirs_FilesAddedByUpdatesAreStillVerifiedAgainstOverlay()
    {
        // 安裝程式只清空程式目錄:data/ 之下由更新包新增、發行清單沒有的檔案還在,重新安裝後仍以覆蓋清單核對(P1 的規則不變);
        // 清單有列的 data/ 檔案被安裝覆寫,改以新的發行清單為準
        var ship = NewInstall();
        W(ship.Root, "data/charts/base.txt", "chart base 1");
        WriteManifest(ship, "2026.10.0");
        var a = _env.PathIn("app.txt");
        var added = _env.PathIn("chart-added.txt");
        var changed = _env.PathIn("chart-changed.txt");
        File.WriteAllText(a, "app 2.0\n");
        File.WriteAllText(added, "chart A");
        File.WriteAllText(changed, "chart base 2");
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        var pkg = UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "2026.10.1", Kind = UpdateKinds.Mixed, Summary = "軟體與圖資",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "BRM-TRN-001", Revision = "1", ApprovedBy = "訪船船長" } },
            Files =
            {
                new PackageSource("app/version.txt", a), new PackageSource("app/added.dll", a),
                new PackageSource("data/charts/test.txt", added), new PackageSource("data/charts/base.txt", changed),
            },
        }, key, _env.PathIn("mixed.sbupd")).OutputPath;
        var applied = applier.Apply(pkg);
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        Assert.Empty(AssertOk(ship).Notices);

        // 重新安裝 2026.11.0:程式目錄清空重裝;清單所列的 data/charts/base.txt 被新版覆寫;更新包新增的 data/charts/test.txt 原地不動
        File.WriteAllText(P(ship, "data/charts/base.txt"), "chart base 5");
        ReinstallWithInstaller(ship, "2026.11.0", "data/charts/test.txt");

        var r = AssertOk(ship);
        var notice = Assert.Single(r.Notices);
        Assert.Equal(C.State, notice.Category);
        Assert.Contains("對 2 個檔案的變更已被安裝覆蓋", notice.Detail);     // app/version.txt、data/charts/base.txt
        Assert.Contains("新增的 1 個檔案不再列入預期", notice.Detail);       // app/added.dll
        // 更新包新增的資料檔:仍在預期內容,來源是更新包
        File.WriteAllText(P(ship, "data/charts/test.txt"), "chart X");
        var err = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal("data/charts/test.txt", err.Path);
        Assert.Equal("package:2026.10.1", err.Source);
        File.Delete(P(ship, "data/charts/test.txt"));
        Assert.Equal(C.Missing, Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Category);
        File.WriteAllText(P(ship, "data/charts/test.txt"), "chart A");
        // 清單有列的資料檔:以新的發行清單核對
        File.WriteAllText(P(ship, "data/charts/base.txt"), "chart base 2");
        var err2 = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal("data/charts/base.txt", err2.Path);
        Assert.Equal("release", err2.Source);
        File.WriteAllText(P(ship, "data/charts/base.txt"), "chart base 5");

        // 下一次套用寫回:程式目錄的舊紀錄作廢,data/ 之下清單沒有的紀錄保留
        Assert.True(applier.Apply(SoftwarePackage("2026.11.1", key, ("app/version.txt", "app 6.0\n"))).Ok);
        var files = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Equal(new[] { "app/version.txt", "data/charts/test.txt" }, files.Files.Select(f => f.Path).ToArray());
        Assert.Equal("2026.11.0", files.Baseline!.Version);
        Assert.Empty(AssertOk(ship).Notices);
    }

    [Fact]
    public void ReinstallSameRelease_AfterUpdate_IsStillReportedAsReverted()
    {
        // 以「同一個」發行版重新安裝(manifest.json 相同,偵測不到重新安裝):更新包換過的檔案被還原、新增的檔案被清掉 → 仍是錯誤,並說明原因;
        // installed.json 記的現行版次與實際檔案不符,不能當成通過。下一步的安裝程式版本基準因此不能是現行發行版本身
        var ship = NewInstall(version: "2026.12.0");
        using var key = _env.Primary();
        Assert.True(new UpdateApplier(ship).Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/added.dll", "a"))).Ok);
        AssertOk(ship);

        File.WriteAllText(P(ship, "app/version.txt"), "app 1.0\n");   // 發行版 2026.12.0 的內容
        File.Delete(P(ship, "app/added.dll"));                         // [InstallDelete] 清空 app\

        var r = InstalledVerifier.Verify(ship.Root);
        Assert.False(r.InstalledFiles!.Reinstalled);
        Assert.Equal(new[] { C.Missing, C.Mismatch }, r.Errors.Select(e => e.Category).ToArray());
        Assert.Contains("被還原", r.Errors[1].Detail);
        // 發行版 2026.12.0 之上還有套用中的更新包(版本 2026.10.1 較舊):以 2026.12.0 的安裝程式重新安裝只會再還原一次 → 要求更新的發行版
        Assert.Contains("版本高於 2026.12.0 的安裝程式", r.NextSteps[0]);
        Assert.DoesNotContain("版本不低於", r.NextSteps[0]);
        Assert.Contains("版本高於 2026.12.0、且包含上列受損檔案的更新包", r.NextSteps[0]);
    }

    // ── 「下一步」的版本基準與更新包說明 ───────────────────

    [Fact]
    public void NextSteps_VersionBasisIsTheLargerOfReleaseAndCurrentPackage()
    {
        // 套用 2026.10.1 更新包之後又以 2026.11.0 的安裝程式重新安裝,之後檔案損毀:
        // 之前的訊息寫「版本不低於 2026.10.1 的安裝程式」,照字面拿 2026.10.x 的安裝程式會把整個發行版降版
        var ship = NewInstall();
        using var key = _env.Primary();
        Assert.True(new UpdateApplier(ship).Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        ReinstallWithInstaller(ship, "2026.11.0");
        AssertOk(ship);

        File.AppendAllText(P(ship, "app/web/index.html"), "<!-- 損毀 -->");
        var r = InstalledVerifier.Verify(ship.Root);
        var next = r.NextSteps[0];
        Assert.Contains("發行版是 2026.11.0、現行更新包是 2026.10.1", next);
        Assert.Contains("版本不低於 2026.11.0 的安裝程式", next);
        Assert.DoesNotContain("不低於 2026.10.1", next);
        // 更新包那一句:版本要高於基準,而且必須包含受損的檔案(app/ → 完整的軟體更新包;情境或參數包修不好)
        Assert.Contains("版本高於 2026.11.0、且包含上列受損檔案的更新包", next);
        Assert.Contains("完整的軟體更新包", next);
        Assert.Contains("修不好程式檔", next);
        Assert.Contains("磁碟", r.NextSteps[1]);
    }

    [Fact]
    public void NextSteps_UpdatePackageSentence_DependsOnWhereTheDamageIs()
    {
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 3.0\n"))).Ok);
        Assert.True(applier.Rollback("測試").Ok);   // 現行 2026.10.1,曾套用最高版本 2026.10.2

        // 受損的是 data/ 的檔案:要含該檔的更新包,版本須高於「曾套用過的最高版本」(rollback 後不下降;2026.10.2 會被拒絕)
        var particulars = P(ship, "data/ships/FSB1/particulars.json");
        var original = File.ReadAllBytes(particulars);
        File.AppendAllText(particulars, " ");
        var data = InstalledVerifier.Verify(ship.Root).NextSteps[0];
        Assert.Contains("版本不低於 2026.10.1 的安裝程式", data);
        Assert.Contains("版本高於 2026.10.2、且包含上列受損檔案的更新包", data);
        Assert.Contains("軟體更新包不含 data\\ 的檔案", data);
        File.WriteAllBytes(particulars, original);

        // 受損的檔案不在更新包可寫入的位置(tools\、docs\、contracts\):只能以安裝程式修復,不提更新包
        File.AppendAllText(P(ship, "docs/README.md"), "x");
        File.AppendAllText(P(ship, "tools/updater/simosa-pkg.dll"), "x");
        var outside = InstalledVerifier.Verify(ship.Root).NextSteps[0];
        Assert.Contains("只能以安裝程式修復", outside);
        Assert.DoesNotContain("的更新包」套用", outside);

        // 兩種都有:更新包只能修 app\ 的那一部分
        File.AppendAllText(P(ship, "app/web/index.html"), "x");
        var both = InstalledVerifier.Verify(ship.Root).NextSteps[0];
        Assert.Contains("完整的軟體更新包", both);
        Assert.Contains("上列有 2 個檔案不在更新包可寫入的位置", both);
    }

    [Fact]
    public void ManifestListingFilesTheInstallerExcludes_IsExplained_NotReportedAsDamage()
    {
        // 舊版 simosa-pkg 產生的發行清單可能列有 .pdb(安裝程式不安裝):缺少的說明要指出不是損毀,下一步不是「不要用於訓練、重新安裝」
        var ship = NewInstall();
        var m = ReleaseManifestBuilder.Load(ship.Root);
        m.Files.Add(new ReleaseFile { Path = "app/visual/data_SimosaBRM.Visual_x86_64/SimosaBRM.Visual.pdb", Sha256 = new string('0', 64), Size = 3 });
        ReleaseManifestBuilder.Save(m, ship.Root);

        var r = InstalledVerifier.Verify(ship.Root);
        var err = Assert.Single(r.Errors);
        Assert.Equal(C.Missing, err.Category);
        Assert.Contains("安裝程式不安裝這類檔案", err.Detail);
        var step = Assert.Single(r.NextSteps);
        Assert.Contains("不需要重新安裝", step);
        Assert.DoesNotContain("請先不要用於訓練", step);

        // 同時真的有檔案損毀:照常給「損壞」的下一步
        File.AppendAllText(P(ship, "app/web/index.html"), "x");
        Assert.Contains(InstalledVerifier.Verify(ship.Root).NextSteps, s => s.Contains("請先不要用於訓練"));
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

    /// <summary>tools/installer/SimosaBRM.iss 指定區段內 Name: "{app}\…" 的相對路徑('/' 分隔、結尾加 '/')。</summary>
    private List<string> IssAppDirs(string section)
    {
        var result = new List<string>();
        var current = "";
        foreach (var raw in File.ReadAllLines(Path.Combine(_env.RepoRoot, "tools", "installer", "SimosaBRM.iss")))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line;
                continue;
            }
            if (current != section || line.StartsWith(';')) continue;
            const string marker = "Name: \"{app}\\";
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var start = at + marker.Length;
            result.Add(line[start..line.IndexOf('"', start)].Replace('\\', '/').TrimEnd('/') + "/");
        }
        return result;
    }

    [Fact]
    public void InstallerInstallDelete_ClearsExactlyTheProgramDirs()
    {
        // 安裝程式重新安裝前清空的資料夾([InstallDelete])= 程式目錄(InstalledPaths.ProgramPrefixes):
        // 覆蓋清單在重新安裝後作廢的範圍、「多出的檔案 = 錯誤」的範圍、安裝程式清空的範圍,三者必須相同
        var deleted = IssAppDirs("[InstallDelete]");
        Assert.Equal(InstalledPaths.ProgramPrefixes.OrderBy(x => x, StringComparer.Ordinal), deleted.OrderBy(x => x, StringComparer.Ordinal));
        // 不得動到執行期資料、設定與資料檔;安裝程式另建的資料資料夾([Dirs])也不在被清空的範圍內
        Assert.All(deleted, d =>
        {
            Assert.Null(InstalledPaths.FindRuntimeData(d, StringComparison.Ordinal));
            Assert.False(InstalledPaths.IsUserConfig(d, StringComparison.Ordinal));
            Assert.DoesNotContain(InstalledPaths.UpdatePrefixes.Where(p => p != "app/"), p => p.StartsWith(d, StringComparison.Ordinal) || d.StartsWith(p, StringComparison.Ordinal));
        });
        Assert.All(IssAppDirs("[Dirs]").Concat(IssAppDirs("[UninstallDelete]")), d => Assert.False(InstalledPaths.IsProgramOrAbove(d, StringComparison.Ordinal), d));
        Assert.True(InstalledPaths.IsProgramOrAbove("tools/", StringComparison.Ordinal));
        Assert.True(InstalledPaths.IsProgramOrAbove("app/web/", StringComparison.Ordinal));
        Assert.False(InstalledPaths.IsProgramOrAbove("tools/webview2/", StringComparison.Ordinal));
        Assert.True(InstalledPaths.IsAtOrUnderProgram("app", StringComparison.Ordinal));
        Assert.True(InstalledPaths.IsAtOrUnderProgram("app/web/assets", StringComparison.Ordinal));
        Assert.False(InstalledPaths.IsAtOrUnderProgram("data/charts/a", StringComparison.Ordinal));
    }

    [Fact]
    public void InstalledRootAsObservedOnWindowsCi_HasNoErrorsAndNothingUnlisted()
    {
        // Windows CI(run 88)以安裝程式安裝並啟動後,安裝根目錄內實際出現的位置:build\logs、build\records、build\tmp、build\browser、
        // build\downloads、build\webview2、config\device、config\trusted-keys、config\packaging.json、config\ship-laptop.json(安裝程式建立)、
        // data\records、state、store、tools\uninstall\unins000.*。安裝模式下都不是錯誤,也不列為「清單沒有列的檔案」
        var ship = NewInstall();
        foreach (var rel in new[]
                 {
                     "build/logs/simcore.log", "build/logs/simosa.pids", "build/records/20261122T010203Z-E01_baseline.jsonl", "build/tmp/setup.tmp",
                     "build/browser/edge/Default/Preferences", "build/downloads/draft.yaml", "build/webview2/ship/EBWebView/Local State",
                     "config/device/device-extra.json", "config/trusted-keys/installed-by-admin.pem", "data/records/old.jsonl",
                     "state/audit.jsonl", "state/records-ledger.json", "store/records/FSB1/p.sbrec",
                     "tools/uninstall/unins000.exe", "tools/uninstall/unins000.dat", "tools/uninstall/unins000.msg",
                 })
            W(ship.Root, rel, "x");
        // 安裝程式 [Dirs] 建立、還沒有資料的空資料夾
        foreach (var dir in IssAppDirs("[Dirs]")) Directory.CreateDirectory(P(ship, dir.TrimEnd('/')));
        Assert.True(File.Exists(P(ship, "config/packaging.json")));
        Assert.True(File.Exists(P(ship, "config/ship-laptop.json")));   // 安裝程式建立(= 發行版的範例,未修改)

        var r = AssertOk(ship);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Notices);
        foreach (var loc in new[] { "build/", "state/", "store/", "config/device/", "config/trusted-keys/", "config/packaging.json", "data/records/" })
            Assert.Contains(loc, r.SkippedLocations);
        var cli = Run("release", "verify", "--dir", ship.Root, "--installed");
        Assert.Equal(0, cli.Code);
        Assert.Contains("沒有提醒", cli.Out);
        Assert.Equal("", cli.Err);
    }

    // ── CLI ───────────────────────────────────────────────

    [Fact]
    public void Cli_MaxItems_IsParsedBeforeVerifying()
    {
        // --max-items 寫錯:立刻回用法錯誤(結束碼 2),不先跑完整核對。之前先核對再解析 — 加了 --json 時甚至不檢查這個參數
        var ship = NewInstall();
        var bad = Run("verify-install", "--root", ship.Root, "--max-items", "abc");
        Assert.Equal(2, bad.Code);
        Assert.Equal("", bad.Out);
        Assert.Contains("--max-items", bad.Err);
        Assert.DoesNotContain("核對", bad.Err);
        var badJson = Run("release", "verify", "--dir", ship.Root, "--installed", "--json", "--max-items", "abc");
        Assert.Equal(2, badJson.Code);
        Assert.Equal("", badJson.Out);
        Assert.Equal(0, Run("verify-install", "--root", ship.Root, "--max-items", "3").Code);
    }

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
    // ── 審查第二十九輪 P3:降版、重新安裝後的 rollback 保護、更新包新增檔案的下一步、manifest.json 只改編碼 ──

    /// <summary>以安裝程式重新安裝指定版本的發行版,建置時間由 <paramref name="builtUtc"/> 指定(模擬「在套用更新包之前就已建置」的較舊發行版)。</summary>
    private static void ReinstallBuiltAt(InstallLayout ship, string version, DateTimeOffset builtUtc, params string[] notInRelease)
    {
        foreach (var prefix in InstalledPaths.ProgramPrefixes)
        {
            var dir = P(ship, prefix.TrimEnd('/'));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        WriteNewerProgramFiles(ship);
        WriteManifest(ship, version, new ManualClock(builtUtc), notInRelease);
    }

    /// <summary>把 state/previous/previous.json 改成舊版 simosa-pkg 的格式(沒有 release、releaseRecorded)。</summary>
    private static void MakeLegacyPreviousMeta(InstallLayout ship)
    {
        var path = Path.Combine(ship.PreviousDir, "previous.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(node.Remove("releaseRecorded"));
        node.Remove("release");
        File.WriteAllText(path, node.ToJsonString());
    }

    private static string ShipStatusJson(InstallLayout ship) => PackageJson.ToText(new Sync.ShipNode(ship).Status());

    [Fact]
    public void ReinstallWithOlderInstaller_AfterSoftwareUpdate_IsDowngradeNotice_AndStatusWarns()
    {
        // 2026.12.1 上套用軟體更新包 2026.12.2,再以 2026.11.1 的安裝程式重新安裝:以舊版安裝程式回到舊版是允許的作法(rollback 拒絕訊息的建議),
        // 所以不是錯誤,但核對與 status 都要明說「降版」— status 的 software 版次 2026.12.2 不是實際執行的程式
        var ship = NewInstall(version: "2026.12.1");
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.12.2", key, ("app/version.txt", "app 3\n"), ("app/simcore/plugin2.dll", "p2"))).Ok);
        Assert.Null(AssertOk(ship).Downgrade);
        Assert.Null(new Sync.ShipNode(ship).Status().ReleaseDowngrade);

        ReinstallBuiltAt(ship, "2026.11.1", DateTimeOffset.UtcNow.AddDays(-30));
        var r = AssertOk(ship);
        var dg = r.Downgrade;
        Assert.NotNull(dg);
        Assert.Equal("2026.11.1", dg!.ReleaseVersion);
        Assert.Equal("2026.12.2", dg.BasisVersion);
        Assert.Equal("2026.12.1", dg.PreviousReleaseVersion);
        Assert.Equal("2026.12.2", dg.SoftwareVersion);
        Assert.True(dg.SoftwareStale);
        var notice = Assert.Single(r.Notices, n => n.Detail?.Contains("降版") == true);
        Assert.Equal(C.State, notice.Category);
        Assert.Equal("manifest.json", notice.Path);
        Assert.Contains("目前的發行版 2026.11.1 比先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 舊", notice.Detail);
        Assert.Contains("simosa-pkg status 顯示的軟體版次 2026.12.2 不是實際執行的程式", notice.Detail);
        Assert.Contains("版本不低於 2026.12.2 的安裝程式", notice.Detail);
        Assert.False(applier.CanRollback(out _));

        // status:文字(stderr 的警告)與 --json(既有欄位不變,只新增 releaseVersion、releaseDowngrade)
        var st = Run("status", "--root", ship.Root);
        Assert.Equal(0, st.Code);
        Assert.Contains("software    2026.12.2", st.Out);
        Assert.Contains("警告:降版:目前的發行版 2026.11.1", st.Err);
        var json = Run("status", "--root", ship.Root, "--json");
        var node = JsonNode.Parse(json.Out)!;
        Assert.Equal("2026.12.2", (string?)node["version"]);
        Assert.Equal("2026.12.2", (string?)node["components"]!["software"]!["version"]);
        Assert.False((bool)node["rollbackAvailable"]!);
        Assert.Equal("2026.11.1", (string?)node["releaseVersion"]);
        Assert.True((bool)node["releaseDowngrade"]!["softwareStale"]!);
        Assert.Equal("2026.12.2", (string?)node["releaseDowngrade"]!["basisVersion"]);
        // verify-install --json:downgrade 物件(只新增)
        var vj = JsonNode.Parse(Run("verify-install", "--root", ship.Root, "--json").Out)!;
        Assert.True((bool)vj["ok"]!);
        Assert.Equal("2026.11.1", (string?)vj["downgrade"]!["releaseVersion"]);

        // 有錯誤時的下一步不再說「較舊的安裝程式…核對會指出」
        File.AppendAllText(P(ship, "app/web/index.html"), "x");
        var bad = InstalledVerifier.Verify(ship.Root);
        Assert.DoesNotContain(bad.NextSteps, s => s.Contains("核對會指出"));
        Assert.Contains("不要用較舊的安裝程式:那會把整個發行版降版", bad.NextSteps[0]);
    }

    [Fact]
    public void Downgrade_IsOnlyReportedAfterAReinstallToAnOlderRelease()
    {
        using var key = _env.Primary();
        // 正常狀態:發行版 2026.11.1 上套用 2026.11.2 的軟體更新包(software 版次比發行版新是常態,不是降版)
        var normal = NewInstall("normal", "2026.11.1");
        Assert.True(new UpdateApplier(normal).Apply(SoftwarePackage("2026.11.2", key, ("app/version.txt", "app 2\n"))).Ok);
        Assert.Null(AssertOk(normal).Downgrade);
        Assert.Null(new Sync.ShipNode(normal).Status().ReleaseDowngrade);
        Assert.Equal("2026.11.1", new Sync.ShipNode(normal).Status().ReleaseVersion);
        Assert.DoesNotContain("降版", Run("status", "--root", normal.Root).Err);

        // 以較新的安裝程式(2027.1.1)重新安裝:升級,不是降版
        var up = NewInstall("up", "2026.12.1");
        Assert.True(new UpdateApplier(up).Apply(SoftwarePackage("2026.12.2", key, ("app/version.txt", "app 3\n"))).Ok);
        ReinstallWithInstaller(up, "2027.1.1");
        var r = AssertOk(up);
        Assert.True(r.InstalledFiles!.Reinstalled);
        Assert.Null(r.Downgrade);
        Assert.DoesNotContain(r.Notices, n => n.Detail?.Contains("降版") == true);
        Assert.Null(new Sync.ShipNode(up).Status().ReleaseDowngrade);
    }

    [Fact]
    public void Rollback_AfterReinstall_IsRefused_WhenOverlayBaselineIsNull()
    {
        // 套用時 manifest.json 讀不到(覆蓋清單 baseline = null),之後依下一步重新安裝:舊版 previous.json(沒有發行清單身分)與新版 previous.json 都要拒絕
        foreach (var legacyMeta in new[] { true, false })
        {
            var ship = NewInstall(legacyMeta ? "nullbase-legacy" : "nullbase-new");
            using var key = _env.Primary();
            var applier = new UpdateApplier(ship);
            var manifest = P(ship, "manifest.json");
            var saved = File.ReadAllBytes(manifest);
            File.Delete(manifest);
            Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/added.dll", "a"))).Ok);
            File.WriteAllBytes(manifest, saved);
            Assert.Null(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Baseline);
            if (legacyMeta) MakeLegacyPreviousMeta(ship);
            else Assert.True(PackageJson.Load<PreviousMeta>(Path.Combine(ship.PreviousDir, "previous.json"), "previous.json").ReleaseRecorded);

            ReinstallWithInstaller(ship, "2026.11.0");
            var r = AssertOk(ship);
            var notice = Assert.Single(r.Notices);
            Assert.Contains("覆蓋清單寫入時安裝根目錄沒有可讀的發行清單", notice.Detail);
            Assert.Contains("請不要執行 rollback", notice.Detail);
            Assert.False(applier.CanRollback(out var reason), legacyMeta ? "legacy" : "new");
            Assert.Contains("重新安裝", reason);
            Assert.True(applier.CheckRollback().ReinstalledSinceApply);
            Assert.False(new Sync.ShipNode(ship).Status().RollbackAvailable);
            Assert.False(applier.Rollback("測試").Ok);
            Assert.Equal("app 5.0\n", File.ReadAllText(P(ship, "app/version.txt")));
            AssertOk(ship);
        }
    }

    [Fact]
    public void Rollback_AfterReinstall_IsRefused_WhenThereIsNoOverlay_SameRuleAsVerify()
    {
        // 舊安裝:舊版 simosa-pkg 套用過更新包(沒有覆蓋清單、previous.json 沒有發行清單身分),之後以安裝程式裝了較新的發行版 —
        // 與核對相同的 Reconcile 判斷(更新包的套用時間早於發行版的建置時間 → SupersededPackages)
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        File.Delete(ship.InstalledFilesPath);
        MakeLegacyPreviousMeta(ship);
        Assert.True(applier.CanRollback(out var before), before);       // 還沒重新安裝:照常可回復

        ReinstallBuiltAt(ship, "2026.11.0", DateTimeOffset.UtcNow.AddMinutes(5));
        var r = AssertOk(ship);
        var notice = Assert.Single(r.Notices);
        Assert.Contains("之前套用", notice.Detail);
        Assert.Contains("請不要執行 rollback", notice.Detail);           // 由 CheckRollback 的實際結果產生
        Assert.False(applier.CanRollback(out var reason));
        Assert.Contains("套用時間早於目前的發行版 2026.11.0 的建置時間", reason);
        Assert.Contains("時鐘", reason);
        Assert.False(new Sync.ShipNode(ship).Status().RollbackAvailable);
        Assert.Contains("無上一版", Run("status", "--root", ship.Root).Out);
        Assert.False(applier.Rollback("測試").Ok);
        AssertOk(ship);
    }

    [Fact]
    public void Rollback_AfterReinstall_PreviousMetaIdentityIsPreferred()
    {
        // 新版 previous.json 記錄了套用當時的發行清單身分:覆蓋清單遺失、而重新安裝的發行版又在套用之前就已建置(時間規則看不出來)時仍拒絕
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        var meta = PackageJson.Load<PreviousMeta>(Path.Combine(ship.PreviousDir, "previous.json"), "previous.json");
        Assert.True(meta.ReleaseRecorded);
        Assert.Equal("2026.10.0", meta.Release!.Version);
        Assert.False(string.IsNullOrEmpty(meta.Release.ContentSha256));
        File.Delete(ship.InstalledFilesPath);
        Assert.True(applier.CanRollback(out var before), before);

        ReinstallBuiltAt(ship, "2026.9.5", DateTimeOffset.UtcNow.AddDays(-10));
        Assert.False(applier.CanRollback(out var reason));
        Assert.Contains("2026.10.0 → 2026.9.5", reason);
        Assert.False(new Sync.ShipNode(ship).Status().RollbackAvailable);

        // 覆蓋清單「看起來」依據目前的發行清單(例如被手動改過)也一樣:previous.json 的身分優先
        var overlay = new InstalledFiles { PackageId = meta.ReplacedByPackageId, Version = meta.ReplacedByVersion, Baseline = InstalledVerifier.ReadBaseline(ship.Root, out _) };
        overlay.Save(ship.InstalledFilesPath);
        Assert.False(applier.CanRollback(out _));
    }

    [Fact]
    public void Rollback_WithoutReinstall_StillWorks_ForNewAndLegacyState()
    {
        using var key = _env.Primary();
        // 新版狀態檔
        var a = NewInstall("rb-new");
        var applierA = new UpdateApplier(a);
        Assert.True(applierA.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        Assert.True(new Sync.ShipNode(a).Status().RollbackAvailable);
        Assert.True(applierA.Rollback("測試").Ok);
        AssertOk(a);
        // 舊版 previous.json + 沒有覆蓋清單(更新包在發行版建置之後套用:不是重新安裝)
        var b = NewInstall("rb-legacy");
        var applierB = new UpdateApplier(b);
        Assert.True(applierB.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        File.Delete(b.InstalledFilesPath);
        MakeLegacyPreviousMeta(b);
        Assert.True(applierB.CanRollback(out var reason), reason);
        Assert.True(applierB.Rollback("測試").Ok);
        // 沒有 manifest.json 的根目錄(開發/測試):無從判斷,不擋
        var c = _env.NewShip("rb-nomanifest");
        var applierC = new UpdateApplier(c);
        Assert.True(applierC.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"))).Ok);
        Assert.Null(PackageJson.Load<PreviousMeta>(Path.Combine(c.PreviousDir, "previous.json"), "previous.json").Release);
        Assert.True(applierC.CanRollback(out var reasonC), reasonC);
    }

    [Fact]
    public void FileAddedByUpdateOutsideProgramDirs_Damaged_NextStepIsAPackage_NotReinstall()
    {
        // 圖資更新包在 data/ 新增、發行版沒有的檔案損毀:安裝程式修不好(重新安裝後仍是同樣的錯誤),下一步要含這些檔案的更新包,
        // 不叫人重新安裝、也不套用「重新安裝後仍有錯誤 → 換磁碟」
        var ship = NewInstall();
        using var key = _env.Primary();
        var src = _env.PathIn("chart-new.dat");
        File.WriteAllText(src, "chart new");
        var pkg = UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = "2026.10.3", Kind = UpdateKinds.Charts, Summary = "圖資",
            Files = { new PackageSource("data/charts/new.dat", src) },
        }, key, _env.PathIn("charts.sbupd")).OutputPath;
        Assert.True(new UpdateApplier(ship).Apply(pkg).Ok);
        ReinstallWithInstaller(ship, "2026.11.0", "data/charts/new.dat");
        AssertOk(ship);

        File.WriteAllText(P(ship, "data/charts/new.dat"), "corrupted!");
        for (var round = 0; round < 2; round++)
        {
            var r = InstalledVerifier.Verify(ship.Root);
            var err = Assert.Single(r.Errors);
            Assert.Equal("data/charts/new.dat", err.Path);
            Assert.Equal("package:2026.10.3", err.Source);
            Assert.Equal(InstallFinding.RepairByPackage, err.RepairableBy);
            var step = Assert.Single(r.NextSteps);
            Assert.Contains("包含這些檔案、版本高於 2026.10.3 的更新包", step);
            Assert.Contains("安裝程式不會修復", step);
            Assert.DoesNotContain("的安裝程式」重新安裝", step);
            Assert.DoesNotContain("可能故障", step);
            // 照舊的下一步重新安裝:錯誤不變(要求的版本也不會往上推)
            ReinstallWithInstaller(ship, "2026.11.0", "data/charts/new.dat");
            File.WriteAllText(P(ship, "data/charts/new.dat"), "corrupted!");
        }
        var json = JsonNode.Parse(Run("verify-install", "--root", ship.Root, "--json").Out)!;
        Assert.Equal("package", (string?)json["errors"]![0]!["repairableBy"]);

        // 同時有發行清單所列的檔案損毀:兩種下一步分開寫 — 更新包新增檔案那一項緊接在重新安裝那一項之後,換磁碟那一句在最後,
        // 互相參照寫明「見下一步(N)」,而且被指到的那一項真的是更新包新增檔案的說明(審查第二十九輪 P3 第三輪 #3)
        File.AppendAllText(P(ship, "app/web/index.html"), "x");
        var mixed = InstalledVerifier.Verify(ship.Root);
        Assert.Equal(2, mixed.Errors.Count);
        Assert.Null(mixed.Errors.Single(e => e.Path == "app/web/index.html").RepairableBy);
        Assert.Equal(3, mixed.NextSteps.Count);
        Assert.Contains("版本不低於 2026.11.0 的安裝程式", mixed.NextSteps[0]);
        static int Referenced(string step)
        {
            var match = System.Text.RegularExpressions.Regex.Match(step, @"見下一步\((\d+)\)");
            Assert.True(match.Success, step);
            return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        Assert.DoesNotContain("下一項", string.Join("\n", mixed.NextSteps));
        var fromInstaller = Referenced(mixed.NextSteps[0]);
        Assert.Equal(2, fromInstaller);
        Assert.Contains("包含這些檔案", mixed.NextSteps[fromInstaller - 1]);
        Assert.Contains("data/charts/new.dat", mixed.NextSteps[fromInstaller - 1]);
        Assert.DoesNotContain("資料檔損壞或不完整,請先不要用於相關的訓練", mixed.NextSteps[fromInstaller - 1]);
        var disk = mixed.NextSteps[^1];
        Assert.Contains("可能故障", disk);
        Assert.Contains("更新包新增的檔案除外", disk);
        Assert.Equal(fromInstaller, Referenced(disk));
        // 再加上程式目錄內多出的檔案:換磁碟那一句仍在最後,參照的編號不變
        W(ship.Root, "app/stray.dll", "x");
        var withExtra = InstalledVerifier.Verify(ship.Root);
        Assert.Equal(4, withExtra.NextSteps.Count);
        Assert.Contains("包含這些檔案", withExtra.NextSteps[Referenced(withExtra.NextSteps[0]) - 1]);
        Assert.Contains("多出的檔案", withExtra.NextSteps[2]);
        Assert.Contains("可能故障", withExtra.NextSteps[3]);
        Assert.Equal(2, Referenced(withExtra.NextSteps[3]));
        File.Delete(P(ship, "app/stray.dll"));
        // 文字輸出的編號與「見下一步(N)」一致
        var text = Run("verify-install", "--root", ship.Root);
        Assert.Contains("下一步(2):上列標示為更新包 2026.10.3", text.Err);
        Assert.Contains("見下一步(2)", text.Err);

        // 更新包新增的檔案只是缺少:同樣是更新包才修得好
        File.WriteAllText(P(ship, "app/web/index.html"), "<!doctype html><title>儀器 5</title>\n");
        File.Delete(P(ship, "data/charts/new.dat"));
        var missing = Assert.Single(InstalledVerifier.Verify(ship.Root).Errors);
        Assert.Equal(C.Missing, missing.Category);
        Assert.Equal(InstallFinding.RepairByPackage, missing.RepairableBy);
    }

    [Fact]
    public void ManifestOnlyReencoded_IsTheSameRelease_ButARealReinstallIsStillDetected()
    {
        // manifest.json 只改編碼(加 BOM、LF→CRLF;例如以編輯器開過存檔):內容身分相同,不是重新安裝 — 核對通過、rollback 允許
        var ship = NewInstall();
        using var key = _env.Primary();
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.10.1", key, ("app/version.txt", "app 2.0\n"), ("app/added.dll", "a"))).Ok);
        Assert.False(string.IsNullOrEmpty(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Baseline!.ContentSha256));
        var manifest = P(ship, "manifest.json");
        var original = File.ReadAllBytes(manifest);
        void Reencode()
        {
            var text = System.Text.Encoding.UTF8.GetString(original).Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllBytes(manifest, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray());
        }
        Reencode();
        var r = AssertOk(ship);
        Assert.Empty(r.Notices);
        Assert.False(r.InstalledFiles!.Reinstalled);
        Assert.True(applier.CanRollback(out var reason), reason);

        // 舊版 simosa-pkg 寫的覆蓋清單與 previous.json(沒有內容身分):以版本 + 建置時間比較,也是同一個發行版
        var overlay = JsonNode.Parse(File.ReadAllText(ship.InstalledFilesPath))!.AsObject();
        Assert.True(overlay["baseline"]!.AsObject().Remove("contentSha256"));
        File.WriteAllText(ship.InstalledFilesPath, overlay.ToJsonString());
        MakeLegacyPreviousMeta(ship);
        Assert.Empty(AssertOk(ship).Notices);
        Assert.True(applier.CanRollback(out reason), reason);

        // 之後的套用:覆蓋清單的 baseline 換成目前的身分(原始 SHA-256 與內容身分)
        Assert.True(applier.Apply(SoftwarePackage("2026.10.2", key, ("app/version.txt", "app 3.0\n"))).Ok);
        var refreshed = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Baseline!;
        Assert.Equal(Sha256Hex.OfFile(manifest), refreshed.Sha256);
        Assert.False(string.IsNullOrEmpty(refreshed.ContentSha256));
        Assert.Equal(new[] { "app/added.dll", "app/version.txt" }, InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Files.Select(f => f.Path).ToArray());
        Assert.True(applier.Rollback("測試").Ok);
        AssertOk(ship);

        // 真的換了發行版(內容不同):仍是重新安裝,rollback 拒絕;舊紀錄(沒有內容身分)以版本 + 建置時間也判得出來
        Assert.True(applier.Apply(SoftwarePackage("2026.10.3", key, ("app/version.txt", "app 4.0\n"))).Ok);
        var legacyOverlay = JsonNode.Parse(File.ReadAllText(ship.InstalledFilesPath))!.AsObject();
        Assert.True(legacyOverlay["baseline"]!.AsObject().Remove("contentSha256"));
        File.WriteAllText(ship.InstalledFilesPath, legacyOverlay.ToJsonString());
        MakeLegacyPreviousMeta(ship);
        ReinstallWithInstaller(ship, "2026.11.0");
        Assert.True(AssertOk(ship).InstalledFiles!.Reinstalled);
        Assert.False(applier.CanRollback(out reason));
        Assert.Contains("2026.10.0 → 2026.11.0", reason);
    }

    [Fact]
    public void SameRelease_ComparesContent_ThenVersionAndBuildTimeForLegacyRecords()
    {
        var built = new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc);
        var m = new ReleaseManifest { Version = "2026.11.1", BuiltUtc = built, GitRevision = "abc", Files = { new ReleaseFile { Path = "app/a.dll", Sha256 = "AA", Size = 1 } } };
        var current = InstalledBaseline.Of(m, "1111");
        // 原始 SHA-256 不同、內容相同
        Assert.True(InstalledBaseline.SameRelease(InstalledBaseline.Of(m, "2222"), current));
        // 內容不同(某個檔案的雜湊)
        var other = new ReleaseManifest { Version = "2026.11.1", BuiltUtc = built, GitRevision = "abc", Files = { new ReleaseFile { Path = "app/a.dll", Sha256 = "BB", Size = 1 } } };
        Assert.False(InstalledBaseline.SameRelease(InstalledBaseline.Of(other, "3333"), current));
        // 舊紀錄沒有內容身分:版本 + 建置時間
        Assert.True(InstalledBaseline.SameRelease(new InstalledBaseline { Version = "2026.11.1", Sha256 = "9999", BuiltUtc = built }, current));
        Assert.False(InstalledBaseline.SameRelease(new InstalledBaseline { Version = "2026.11.1", Sha256 = "9999", BuiltUtc = built.AddSeconds(1) }, current));
        Assert.False(InstalledBaseline.SameRelease(new InstalledBaseline { Version = "2026.11.2", Sha256 = "9999", BuiltUtc = built }, current));
        Assert.False(InstalledBaseline.SameRelease(new InstalledBaseline { Version = "2026.11.1", Sha256 = "9999" }, current));
        Assert.False(InstalledBaseline.SameRelease(null, current));
        Assert.False(InstalledBaseline.SameRelease(new InstalledBaseline { Sha256 = "" }, new InstalledBaseline { Sha256 = "" }));
        // 檔案順序與雜湊大小寫不影響內容身分
        var reordered = new ReleaseManifest { Version = "2026.11.1", BuiltUtc = built, GitRevision = "abc", Files = { new ReleaseFile { Path = "app/b.dll", Sha256 = "cc", Size = 2 }, new ReleaseFile { Path = "app/a.dll", Sha256 = "aa", Size = 1 } } };
        var sorted = new ReleaseManifest { Version = "2026.11.1", BuiltUtc = built, GitRevision = "abc", Files = { new ReleaseFile { Path = "app/a.dll", Sha256 = "AA", Size = 1 }, new ReleaseFile { Path = "app/b.dll", Sha256 = "CC", Size = 2 } } };
        Assert.Equal(InstalledBaseline.ContentHash(sorted), InstalledBaseline.ContentHash(reordered));
    }

    // ── 審查第二十九輪 P4(P3 第三輪審查):降版提醒的存續、rollback 後的比較基準、manifest.json 套用時讀不到 ──

    /// <summary>圖資更新包:在 data/charts/ 寫入一個檔案。</summary>
    private string ChartsPackage(string version, IKeyProvider key, string path, string content)
    {
        var src = _env.PathIn($"chart-{version}-{Guid.NewGuid().ToString("N")[..6]}");
        File.WriteAllText(src, content);
        return UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = version, Kind = UpdateKinds.Charts, Summary = $"圖資 {version}", Files = { new PackageSource(path, src) },
        }, key, _env.PathIn($"charts-{version}.sbupd")).OutputPath;
    }

    /// <summary>參數更新包(附驗證紀錄;同 <see cref="ParametersUpdate_Verified"/>)。</summary>
    private string ParametersPackage(string version, IKeyProvider key)
    {
        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        var vr = _env.PathIn($"verification-{version}.json");
        var rec = new VerificationRecord();
        TestEnv.Verify(rec, Path.Combine(_env.InstructorRoot, "data", "scenarios", "E01_baseline.yaml"), ParameterSetLoader.Load(p), "E01_baseline");
        rec.Save(vr);
        return UpdatePackageBuilder.Build(new UpdateBuildRequest
        {
            Version = version, Kind = UpdateKinds.Parameters, Summary = $"參數 {version}",
            SmsDocuments = { new SmsDocumentRef { DocumentId = "DWO-007", Revision = "4", ApprovedBy = "DPA 測試" } },
            Files = { new PackageSource("data/parameters/company-procedures.yaml", p) },
            ValidationRoot = _env.InstructorRoot, BaselineParameters = _env.BaselineParameters, VerificationRecordPath = vr,
        }, key, _env.PathIn($"params-{version}.sbupd")).OutputPath;
    }

    /// <summary>2026.12.1 上套用軟體更新包 2026.12.2,再以 2026.11.1 的安裝程式(建置於套用之前)重新安裝:降版。</summary>
    private (InstallLayout Ship, UpdateApplier Applier) Downgraded(string name, IKeyProvider key)
    {
        var ship = NewInstall(name, "2026.12.1");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.12.2", key, ("app/version.txt", "app 3\n"), ("app/simcore/plugin2.dll", "p2"))).Ok);
        ReinstallBuiltAt(ship, "2026.11.1", DateTimeOffset.UtcNow.AddDays(-30));
        return (ship, applier);
    }

    /// <summary>核對、status(物件、文字、--json)都報同一個降版,而且 status 的軟體版次被標為不是實際執行的程式。</summary>
    private void AssertDowngradeEverywhere(InstallLayout ship, string software, string basis, string? previous = "2026.12.1")
    {
        var r = AssertOk(ship);
        var dg = r.Downgrade;
        Assert.True(dg is not null, Describe(r));
        Assert.Equal("2026.11.1", dg!.ReleaseVersion);
        Assert.Equal(basis, dg.BasisVersion);
        Assert.Equal(software, dg.SoftwareVersion);
        Assert.Equal(previous, dg.PreviousReleaseVersion);
        Assert.True(dg.SoftwareStale);
        var notice = Assert.Single(r.Notices, n => n.Detail?.StartsWith("降版:", StringComparison.Ordinal) == true);
        Assert.Equal(C.State, notice.Category);
        Assert.Contains($"simosa-pkg status 顯示的軟體版次 {software} 不是實際執行的程式", notice.Detail);
        Assert.Contains("直到套用新的軟體更新包", notice.Detail);
        var st = new Sync.ShipNode(ship).Status();
        Assert.Equal(dg.Message, st.ReleaseDowngrade?.Message);
        Assert.Contains("警告:降版:目前的發行版 2026.11.1", Run("status", "--root", ship.Root).Err);
        var json = JsonNode.Parse(Run("status", "--root", ship.Root, "--json").Out)!;
        Assert.True((bool)json["releaseDowngrade"]!["softwareStale"]!);
        Assert.Equal(software, (string?)json["components"]!["software"]!["version"]);
    }

    private void AssertNoDowngrade(InstallLayout ship)
    {
        var r = AssertOk(ship);
        Assert.Null(r.Downgrade);
        Assert.DoesNotContain(r.Notices, n => n.Detail?.Contains("降版") == true);
        Assert.Null(new Sync.ShipNode(ship).Status().ReleaseDowngrade);
        Assert.DoesNotContain("降版", Run("status", "--root", ship.Root).Err);
    }

    [Fact]
    public void Downgrade_PersistsAcrossNonSoftwarePackages_UntilANewSoftwarePackageIsApplied()
    {
        // 降版之後再套用圖資、參數更新包:套用會把覆蓋清單對齊到目前的發行版(之後看不到重新安裝),
        // 但 installed.json 的 software 元件仍是 2026.12.2 — 提醒必須保留(覆蓋清單的 downgrade 證據),直到套用新的軟體更新包
        using var key = _env.Primary();
        var (ship, applier) = Downgraded("persist", key);
        AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");

        Assert.True(applier.Apply(ChartsPackage("2026.12.4", key, "data/charts/z.dat", "z")).Ok);
        var overlay = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Equal("2026.11.1", overlay.Baseline!.Version);           // 已對齊到目前的發行版:這一次核對看不到重新安裝
        Assert.Equal("2026.12.2", overlay.Downgrade!.SoftwareVersion);  // 證據寫進檔案
        Assert.Equal("2026.12.1", overlay.Downgrade.PreviousReleaseVersion);
        Assert.False(AssertOk(ship).InstalledFiles!.Reinstalled);
        // 比較基準不因之後套用的圖資包(2026.12.4)而改變:它不是被重新安裝取代的東西
        AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");

        var applied = applier.Apply(ParametersPackage("2026.12.5", key));
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");
        Assert.True(applier.CanRollback(out var reason), reason);       // 參數包本身可以回復(之後沒有重新安裝)

        // 新的軟體更新包:status 的軟體版次又是更新包寫入的程式 → 提醒消失,證據清除
        Assert.True(applier.Apply(SoftwarePackage("2027.1.1", key, ("app/version.txt", "app 9\n"))).Ok);
        Assert.Null(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Downgrade);
        Assert.DoesNotContain("\"downgrade\"", File.ReadAllText(ship.InstalledFilesPath));
        AssertNoDowngrade(ship);

        // 回復那個軟體更新包:套用前的覆蓋清單(含證據)與 installed.json 一起還原 → 提醒回來
        Assert.True(applier.Rollback("測試").Ok);
        AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");
    }

    [Fact]
    public void Downgrade_IsClearedOnlyByReinstallingAReleaseNotOlderThanTheBasis()
    {
        using var key = _env.Primary();
        var (ship, applier) = Downgraded("clear", key);
        Assert.True(applier.Apply(ChartsPackage("2026.12.4", key, "data/charts/z.dat", "z")).Ok);
        AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");

        // 回到先前的發行版 2026.12.1:仍比軟體更新包 2026.12.2 舊(該包的變更已被安裝覆蓋)→ 仍是降版;先前的發行版取兩次重新安裝的最大者
        ReinstallWithInstaller(ship, "2026.12.1", "data/charts/z.dat");
        var r = AssertOk(ship);
        Assert.NotNull(r.Downgrade);
        Assert.Equal("2026.12.1", r.Downgrade!.ReleaseVersion);
        Assert.Equal("2026.12.2", r.Downgrade.SoftwareVersion);
        Assert.Contains("曾套用的軟體更新包 2026.12.2", r.Downgrade.Message);
        Assert.DoesNotContain("先前安裝的發行版", r.Downgrade.Message);
        // 比較基準只計被安裝覆蓋的軟體更新包(審查第二十九輪 P5):圖資包 2026.12.4 放進 data/charts/、發行清單沒有的檔案,
        // 安裝沒有覆蓋它,而且是降版之後才套用的 — 不列入,也不要求「版本不低於 2026.12.4 的安裝程式」
        Assert.Equal("2026.12.2", r.Downgrade.BasisVersion);
        Assert.Equal("2026.12.2", r.Downgrade.HighestVersion);
        Assert.DoesNotContain("2026.12.4", r.Downgrade.Message);
        Assert.Contains("版本不低於 2026.12.2 的安裝程式", r.Downgrade.Message);

        // 以版本不低於比較基準的安裝程式重新安裝:提醒消失;下一次套用時證據從檔案清除
        ReinstallWithInstaller(ship, "2026.12.2", "data/charts/z.dat");
        AssertNoDowngrade(ship);
        Assert.True(applier.Apply(ChartsPackage("2026.12.6", key, "data/charts/y.dat", "y")).Ok);
        Assert.Null(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Downgrade);
        AssertNoDowngrade(ship);
    }

    // ── 審查第二十九輪 P5(P4 第四輪審查):降版的比較基準只計這次重新安裝真的覆蓋到的軟體更新包;套用時讀不到發行清單而覆蓋清單仍有身分 ──

    /// <summary>以安裝程式重新安裝 <paramref name="version"/>(建置於現在;data/charts/ 之下更新包新增的檔案不在發行清單)。</summary>
    private static void ReinstallKeepingCharts(InstallLayout ship, string version, DateTimeOffset? builtUtc = null)
    {
        var charts = Directory.Exists(P(ship, "data/charts"))
            ? Directory.GetFiles(P(ship, "data/charts")).Select(f => "data/charts/" + Path.GetFileName(f)).ToArray()
            : Array.Empty<string>();
        ReinstallBuiltAt(ship, version, builtUtc ?? DateTimeOffset.UtcNow, charts);
    }

    [Fact]
    public void ReturnToTheOriginalRelease_AfterNonSoftwarePackages_IsNotADowngrade()
    {
        // 審查 P4 第四輪 #1(1):2026.12.1 + 圖資 2026.12.4 → 以 2026.11.1 重新安裝(真的降版:先前的發行版較新)→ 參數 2026.12.8
        // → 以原本的 2026.12.1 重新安裝:回到原本的發行版,不是降版。參數包是降版之後才套用的(合併時不納入),圖資包安裝沒有覆蓋
        using var key = _env.Primary();
        var ship = NewInstall("return-original", "2026.12.1");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(ChartsPackage("2026.12.4", key, "data/charts/z.dat", "z")).Ok);
        ReinstallKeepingCharts(ship, "2026.11.1", DateTimeOffset.UtcNow.AddDays(-30));
        var down = AssertOk(ship);
        Assert.NotNull(down.Downgrade);                                  // 發行版本身降版:照報
        Assert.Equal("2026.12.1", down.Downgrade!.BasisVersion);         // 比較基準是先前的發行版,不是圖資包的 2026.12.4
        Assert.Equal("2026.12.1", down.Downgrade.PreviousReleaseVersion);
        Assert.Null(down.Downgrade.HighestVersion);
        Assert.False(down.Downgrade.SoftwareStale);
        Assert.DoesNotContain("2026.12.4", down.Downgrade.Message);

        var applied = applier.Apply(ParametersPackage("2026.12.8", key));
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        Assert.Equal("2026.12.1", InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Downgrade?.BasisVersion);
        Assert.Equal("2026.12.1", AssertOk(ship).Downgrade?.BasisVersion);

        ReinstallKeepingCharts(ship, "2026.12.1");
        var back = AssertOk(ship);
        Assert.True(back.InstalledFiles!.Reinstalled);
        AssertNoDowngrade(ship);
        // 之後再套用圖資包:證據從檔案清除,提醒不會回來
        Assert.True(applier.Apply(ChartsPackage("2026.12.9", key, "data/charts/c9.dat", "c9")).Ok);
        Assert.Null(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Downgrade);
        AssertNoDowngrade(ship);
    }

    [Fact]
    public void UpgradeWithInstaller_AfterHigherVersionedNonSoftwarePackages_IsNotADowngrade()
    {
        // 審查 P4 第四輪 #1(2):2026.11.1 + 版本較高的圖資包 2026.12.4 → 以 2026.12.1 的安裝程式升級:不是降版,之後套用參數包也不會出現
        using var key = _env.Primary();
        var ship = NewInstall("upgrade-charts", "2026.11.1");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(ChartsPackage("2026.12.4", key, "data/charts/z.dat", "z")).Ok);
        ReinstallKeepingCharts(ship, "2026.12.1");
        var r = AssertOk(ship);
        Assert.True(r.InstalledFiles!.Reinstalled);
        AssertNoDowngrade(ship);
        var applied = applier.Apply(ParametersPackage("2026.12.8", key));
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        Assert.Null(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Downgrade);
        Assert.DoesNotContain("\"downgrade\"", File.ReadAllText(ship.InstalledFilesPath));
        AssertNoDowngrade(ship);

        // 參數包換過發行清單所列的檔案(data/parameters/company-procedures.yaml),之後以安裝程式升級:參數包的變更真的被覆蓋,
        // 但它不是軟體更新包 — 由「被安裝覆蓋」的 state 提醒說明,不算降版、不寫證據
        ReinstallKeepingCharts(ship, "2026.12.2");
        var up = AssertOk(ship);
        Assert.Contains(up.Notices, n => n.Category == C.State && n.Detail!.Contains("已被安裝覆蓋"));
        AssertNoDowngrade(ship);
        var info = new ReconcileInfo();
        var reconciled = InstalledFiles.Reconcile(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _), InstalledState.LoadOrNew(ship.InstalledStatePath),
            InstalledVerifier.ReadBaseline(ship.Root, out var paths), paths, StringComparison.Ordinal, info);
        Assert.Null(reconciled.Downgrade);
        var overwritten = Assert.Single(info.OverwrittenPackages);       // 圖資包(data/charts/ 之下發行清單沒有的檔案)不算被覆蓋
        Assert.Equal("2026.12.8", overwritten.Version);
        Assert.False(overwritten.Software);
    }

    [Fact]
    public void RealSoftwareDowngrade_IsStillReported_AndPersists_WhileNonSoftwarePackagesDoNotRaiseTheBasis()
    {
        // 真的降版(軟體更新包被較舊的安裝程式覆蓋)照報、照樣存續;降版之前套用、被安裝覆蓋的參數包(版本較高)不提高比較基準
        using var key = _env.Primary();
        var ship = NewInstall("real-downgrade", "2026.12.1");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.12.2", key, ("app/version.txt", "app 3\n"))).Ok);
        var pa = applier.Apply(ParametersPackage("2026.12.5", key));
        Assert.True(pa.Ok, string.Join(";", pa.Errors));
        ReinstallBuiltAt(ship, "2026.11.1", DateTimeOffset.UtcNow.AddDays(-30));
        AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");
        Assert.DoesNotContain("2026.12.5", AssertOk(ship).Downgrade!.Message);
        // 之後套用圖資包:證據寫進檔案,提醒保留,比較基準不變
        Assert.True(applier.Apply(ChartsPackage("2026.12.6", key, "data/charts/z.dat", "z")).Ok);
        Assert.Equal("2026.12.2", InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Downgrade?.HighestAppliedVersion);
        AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");
        // 以 2026.12.2 的安裝程式重新安裝(不低於比較基準):消失 — 不必找「版本不低於參數包 2026.12.5」的安裝程式
        ReinstallKeepingCharts(ship, "2026.12.2");
        AssertNoDowngrade(ship);
    }

    [Fact]
    public void ManifestUnreadableAtApply_ThenOlderInstaller_RollbackUsesTheOverlaysLastKnownRelease()
    {
        // 審查 P4 第四輪 #2:先套用一包(發行清單讀得到 → 覆蓋清單記下 2026.12.1)→ 發行清單損毀時再套用軟體包(previous.json 記「讀不到」,
        // 覆蓋清單仍保留 2026.12.1)→ 以較舊、建置於套用之前的發行版重新安裝。時間規則看不出來,但覆蓋清單的最後已知身分看得出來:
        // rollback 拒絕(與核對的 Rebase 同一個依據),status 顯示沒有可回復的上一版,核對註明「請不要執行 rollback」
        using var key = _env.Primary();
        var ship = NewInstall("unreadable-older", "2026.12.1");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(ChartsPackage("2026.12.4", key, "data/charts/z.dat", "z")).Ok);
        Assert.Equal("2026.12.1", InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Baseline?.Version);
        File.WriteAllText(P(ship, "manifest.json"), "{\"format\":\"simosa-release/1\",\"version\":");
        var applied = applier.Apply(SoftwarePackage("2027.1.2", key, ("app/version.txt", "app 9\n")));
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        var warning = Assert.Single(applied.Warnings, w => w.Contains("manifest.json"));
        Assert.Contains("最後已知發行版 2026.12.1", warning);
        Assert.True(PackageJson.Load<PreviousMeta>(Path.Combine(ship.PreviousDir, "previous.json"), "previous.json").ReleaseUnreadable);
        var overlay = InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!;
        Assert.Equal("2026.12.1", overlay.Baseline?.Version);
        Assert.False(overlay.BaselineUnreadable);

        ReinstallBuiltAt(ship, "2026.11.1", DateTimeOffset.UtcNow.AddDays(-30), "data/charts/z.dat");
        var check = applier.CheckRollback();
        Assert.False(check.Allowed);
        Assert.True(check.ReinstalledSinceApply);
        Assert.Contains("覆蓋清單記錄的最後已知發行版", check.Reason);
        Assert.Contains("2026.12.1", check.Reason);
        Assert.False(applier.CanRollback(out _));
        Assert.False(new Sync.ShipNode(ship).Status().RollbackAvailable);
        Assert.Contains("無上一版", Run("status", "--root", ship.Root).Out);
        var r = AssertOk(ship);
        Assert.True(r.InstalledFiles!.Reinstalled);
        Assert.Contains(r.Notices, n => n.Detail?.Contains("請不要執行 rollback") == true);
        Assert.NotNull(r.Downgrade);                                     // 2026.12.1 + 軟體 2027.1.2 → 2026.11.1
        Assert.False(applier.Rollback("測試").Ok);
        Assert.Equal("app 5.0\n", File.ReadAllText(P(ship, "app/version.txt")));
        AssertOk(ship);

        // 對照:覆蓋清單仍是同一個發行版(只把清單修好、沒有重新安裝)→ 放行,說明依據
        var same = NewInstall("unreadable-same", "2026.12.1");
        var applierSame = new UpdateApplier(same);
        Assert.True(applierSame.Apply(ChartsPackage("2026.12.4", key, "data/charts/z.dat", "z")).Ok);
        var saved = File.ReadAllBytes(P(same, "manifest.json"));
        File.WriteAllText(P(same, "manifest.json"), "{");
        Assert.True(applierSame.Apply(SoftwarePackage("2027.1.2", key, ("app/version.txt", "app 9\n"))).Ok);
        File.WriteAllBytes(P(same, "manifest.json"), saved);
        var ok = applierSame.CheckRollback();
        Assert.True(ok.Allowed, ok.Reason);
        Assert.Contains("最後已知發行版 2026.12.1", ok.Note);
        Assert.True(new Sync.ShipNode(same).Status().RollbackAvailable);
        AssertOk(same);
        var rolled = applierSame.Rollback("測試");
        Assert.True(rolled.Ok, string.Join(";", rolled.Errors));
        Assert.Contains(rolled.Warnings, w => w.Contains("最後已知發行版"));
        AssertOk(same);
    }

    [Fact]
    public void NextSteps_Order_InstallerExcludedFilesOnly_ThenPackageAddedFiles_MatchesTheDocs()
    {
        // 審查 P4 第四輪 #3:只有「安裝程式不安裝的檔案」(*.pdb)與更新包新增的檔案損毀時,順序是 pdb 的說明 → 更新包新增檔案那一項
        // (docs/design/packaging-sync.md 第 5.1 節「下一步」的順序:重新安裝那一項或 pdb 的說明擇一,再接更新包新增的檔案)
        var ship = NewInstall();
        using var key = _env.Primary();
        Assert.True(new UpdateApplier(ship).Apply(ChartsPackage("2026.10.3", key, "data/charts/new.dat", "chart new")).Ok);
        var m = ReleaseManifestBuilder.Load(ship.Root);
        m.Files.Add(new ReleaseFile { Path = "app/visual/data_SimosaBRM.Visual_x86_64/SimosaBRM.Visual.pdb", Sha256 = new string('0', 64), Size = 3 });
        ReleaseManifestBuilder.Save(m, ship.Root);
        File.WriteAllText(P(ship, "data/charts/new.dat"), "corrupted!");

        var r = InstalledVerifier.Verify(ship.Root);
        Assert.Equal(2, r.Errors.Count);
        Assert.Equal(2, r.NextSteps.Count);
        Assert.Contains("安裝程式不安裝的檔案類型", r.NextSteps[0]);
        Assert.Contains("包含這些檔案", r.NextSteps[1]);
        Assert.Contains("data/charts/new.dat", r.NextSteps[1]);
        Assert.DoesNotContain("可能故障", string.Join("\n", r.NextSteps));
        // 加上程式目錄內多出的檔案:多出的檔案排在更新包那一項之後
        W(ship.Root, "app/stray.dll", "x");
        var withExtra = InstalledVerifier.Verify(ship.Root);
        Assert.Equal(3, withExtra.NextSteps.Count);
        Assert.Contains("安裝程式不安裝的檔案類型", withExtra.NextSteps[0]);
        Assert.Contains("包含這些檔案", withExtra.NextSteps[1]);
        Assert.Contains("多出的檔案", withExtra.NextSteps[2]);
    }

    [Fact]
    public void Downgrade_IsStillReported_WhenTheOverlayIsDeletedOrCorrupt_UsingPreviousJson()
    {
        // 覆蓋清單遺失或損毀時,以 previous.json 的發行清單身分(rollback 判斷用的同一份)作為重新安裝的依據:
        // 降版照樣提醒、rollback 照樣拒絕;套用中的更新包都已被安裝覆蓋,不列為「無法核對」
        using var key = _env.Primary();
        foreach (var variant in new[] { "deleted-before", "corrupt-before", "deleted-after" })
        {
            var ship = NewInstall(variant, "2026.12.1");
            var applier = new UpdateApplier(ship);
            Assert.True(applier.Apply(SoftwarePackage("2026.12.2", key, ("app/version.txt", "app 3\n"))).Ok);
            if (variant == "deleted-before") File.Delete(ship.InstalledFilesPath);
            if (variant == "corrupt-before") File.WriteAllText(ship.InstalledFilesPath, "{x");
            ReinstallBuiltAt(ship, "2026.11.1", DateTimeOffset.UtcNow.AddDays(-30));
            if (variant == "deleted-after") File.Delete(ship.InstalledFilesPath);

            AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");
            var r = AssertOk(ship);
            Assert.DoesNotContain(r.Notices, n => n.Category == C.Unverifiable);
            var superseded = Assert.Single(r.Notices, n => n.Path == "state/installed.json");
            Assert.Contains("套用之後重新安裝過發行版", superseded.Detail);
            Assert.Contains("previous.json 記錄套用時的發行版 2026.12.1", superseded.Detail);
            Assert.Contains("請不要執行 rollback", superseded.Detail);
            Assert.False(applier.CanRollback(out var reason), variant);
            Assert.Contains("2026.12.1 → 2026.11.1", reason);

            // 之後套用圖資包:覆蓋清單重新建立,證據隨之寫入,提醒保留
            var next = applier.Apply(ChartsPackage("2026.12.4", key, "data/charts/z.dat", "z"));
            Assert.True(next.Ok, string.Join(";", next.Errors));
            Assert.Equal("2026.12.2", InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.Downgrade?.SoftwareVersion);
            AssertDowngradeEverywhere(ship, "2026.12.2", "2026.12.2");
        }
    }

    [Fact]
    public void NoDowngrade_ForNormalUpdates_DeletionOnlySoftwarePackage_AndUpgrades()
    {
        using var key = _env.Primary();
        // 只刪除檔案的軟體更新包(覆蓋清單沒有它的 files 紀錄,只有 removed):不是重新安裝,不算降版
        var del = NewInstall("delete-only", "2026.11.1");
        var pkg = SoftwarePackage("2026.11.2", key, ("app/placeholder.txt", "x"));
        TestEnv.ResignManifest(pkg, key, m =>
        {
            m.Files.Clear();
            m.Remove.Add("app/web/assets/index-abc.js");
        });
        using (var zip = System.IO.Compression.ZipFile.Open(pkg, System.IO.Compression.ZipArchiveMode.Update))
            zip.GetEntry(UpdateManifest.PayloadPrefix + "app/placeholder.txt")?.Delete();
        var applied = new UpdateApplier(del).Apply(pkg);
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        var overlay = InstalledFiles.TryLoad(del.InstalledFilesPath, out _)!;
        Assert.Empty(overlay.Files);
        Assert.Single(overlay.Removed);
        Assert.Null(overlay.Downgrade);
        AssertNoDowngrade(del);
        // 之後再套用圖資包也一樣
        Assert.True(new UpdateApplier(del).Apply(ChartsPackage("2026.11.3", key, "data/charts/a.dat", "a")).Ok);
        AssertNoDowngrade(del);
    }

    [Fact]
    public void UpgradeThenApplyThenRollback_IsNotADowngrade()
    {
        // 審查 P3 第三輪 #2:以安裝程式升級(2026.12.1 → 2027.1.1),套用 2027.1.2 再 rollback。highestVersion 不下降(仍是 2027.1.2),
        // 但比較基準只看未 rollback 的套用歷史 — 不報降版,也不要求「版本不低於 2027.1.2 的安裝程式」
        using var key = _env.Primary();
        var ship = NewInstall("upgrade-rollback", "2026.12.1");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(SoftwarePackage("2026.12.3", key, ("app/version.txt", "app 4\n"))).Ok);
        ReinstallWithInstaller(ship, "2027.1.1");
        AssertNoDowngrade(ship);
        Assert.True(applier.Apply(SoftwarePackage("2027.1.2", key, ("app/version.txt", "app 9\n"))).Ok);
        Assert.True(applier.Rollback("測試").Ok);
        Assert.Equal("2027.1.2", InstalledState.LoadOrNew(ship.InstalledStatePath).HighestVersion);
        var r = AssertOk(ship);
        Assert.True(r.InstalledFiles!.Reinstalled);   // 還原的覆蓋清單依據的是 2026.12.1:看得到重新安裝(升級)
        AssertNoDowngrade(ship);
        Assert.DoesNotContain("2027.1.2", string.Join("\n", r.Notices.Select(n => n.Detail)));

        // 比較基準只計這次重新安裝真的覆蓋到的軟體更新包(審查第二十九輪 P5):已 rollback 的不再以 highestVersion 補位,
        // 非軟體更新包(即使被覆蓋)不建立紀錄
        var state = new InstalledState { HighestVersion = "2026.12.9", History = { new AppliedPackage { PackageId = "a", Version = "2026.12.9", RolledBack = true } } };
        Assert.Null(DowngradeRecord.Observe(null, true, "2026.11.1", null, Array.Empty<OverwrittenPackage>(), state, DateTime.UtcNow));
        Assert.Null(DowngradeRecord.Observe(null, true, "2026.11.1", null,
            new[] { new OverwrittenPackage("p", "2026.12.8", UpdateKinds.Parameters, Software: false) }, state, DateTime.UtcNow));
        var sw = DowngradeRecord.Observe(null, true, "2026.11.1", null,
            new[] { new OverwrittenPackage("p", "2026.12.8", UpdateKinds.Parameters, Software: false), new OverwrittenPackage("s", "2026.12.3", UpdateKinds.Software, Software: true) },
            state, DateTime.UtcNow);
        Assert.Equal("2026.12.3", sw?.HighestAppliedVersion);
        Assert.Equal("2026.12.3", sw?.BasisVersion);
        // 先前的發行版比目前新就是降版(沒有被覆蓋的軟體包也一樣)
        Assert.Equal("2026.12.1", DowngradeRecord.Observe(null, true, "2026.11.1", "2026.12.1", Array.Empty<OverwrittenPackage>(), state, DateTime.UtcNow)?.BasisVersion);
    }

    [Fact]
    public void ManifestUnreadableAtApply_Warns_IsRecordedSeparately_AndIsNotTakenAsAReinstall()
    {
        // 審查 P3 第三輪 #4:套用時 manifest.json 在、但讀不到 — 照常套用,但要警告;previous.json 與覆蓋清單記為「讀不到」(與「不在」分開)。
        // 之後只把清單修好(沒有重新安裝):不誤報降版、核對不把更新包的檔案當成「被安裝覆蓋」、rollback 不以「重新安裝過」拒絕(放行並說明)
        using var key = _env.Primary();
        var ship = NewInstall("unreadable", "2026.12.1");
        var applier = new UpdateApplier(ship);
        var manifest = P(ship, "manifest.json");
        var saved = File.ReadAllBytes(manifest);
        File.WriteAllText(manifest, "{\"format\":\"simosa-release/1\",\"version\":");
        var applied = applier.Apply(SoftwarePackage("2026.12.3", key, ("app/version.txt", "app 4\n"), ("app/added.dll", "a")));
        Assert.True(applied.Ok, string.Join(";", applied.Errors));
        var warning = Assert.Single(applied.Warnings, w => w.Contains("manifest.json"));
        Assert.Contains("讀不到", warning);
        Assert.Contains("verify-install", warning);
        Assert.Contains("rollback", warning);
        var meta = PackageJson.Load<PreviousMeta>(Path.Combine(ship.PreviousDir, "previous.json"), "previous.json");
        Assert.True(meta.ReleaseRecorded);
        Assert.True(meta.ReleaseUnreadable);
        Assert.Null(meta.Release);
        Assert.True(InstalledFiles.TryLoad(ship.InstalledFilesPath, out _)!.BaselineUnreadable);
        var cli = Run("verify-install", "--root", ship.Root);
        Assert.Equal(1, cli.Code);   // 清單讀不到本身是錯誤(核對照舊回報)

        File.WriteAllBytes(manifest, saved);
        var r = AssertOk(ship);
        Assert.False(r.InstalledFiles!.Reinstalled);
        Assert.Contains(r.Notices, n => n.Category == C.State && n.Detail!.Contains("讀不到") && n.Detail.Contains("視為同一個發行版"));
        AssertNoDowngrade(ship);
        // 更新包寫入的檔案照覆蓋清單核對(不是被當成已被安裝覆蓋)
        File.WriteAllText(P(ship, "app/version.txt"), "app 4x\n");
        Assert.Equal("app/version.txt", Assert.Single(InstalledVerifier.Verify(ship.Root).Errors).Path);
        File.WriteAllText(P(ship, "app/version.txt"), "app 4\n");
        var check = applier.CheckRollback();
        Assert.True(check.Allowed, check.Reason);
        Assert.False(check.ReinstalledSinceApply);
        Assert.Contains("讀不到", check.Note);
        Assert.True(new Sync.ShipNode(ship).Status().RollbackAvailable);
        var rolled = applier.Rollback("測試");
        Assert.True(rolled.Ok, string.Join(";", rolled.Errors));
        Assert.Contains(rolled.Warnings, w => w.Contains("讀不到"));
        AssertOk(ship);

        // 同樣讀不到,但之後以建置於套用之後的發行版重新安裝:依建置時間判斷為重新安裝過 → rollback 拒絕、覆蓋清單 Rebase
        var later = NewInstall("unreadable-reinstalled", "2026.12.1");
        var applierLater = new UpdateApplier(later);
        File.WriteAllText(P(later, "manifest.json"), "{");
        Assert.True(applierLater.Apply(SoftwarePackage("2026.12.3", key, ("app/version.txt", "app 4\n"))).Ok);
        ReinstallBuiltAt(later, "2027.1.1", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.True(AssertOk(later).InstalledFiles!.Reinstalled);
        Assert.False(applierLater.CanRollback(out var reason));
        Assert.Contains("讀不到", reason);
        Assert.True(applierLater.CheckRollback().ReinstalledSinceApply);

        // 對照:套用時 manifest.json 不在(不是讀不到)→ 照舊視為之後才放進來的發行清單(Rollback_AfterReinstall_IsRefused_WhenOverlayBaselineIsNull)
        var absent = NewInstall("absent", "2026.12.1");
        File.Delete(P(absent, "manifest.json"));
        var noWarn = new UpdateApplier(absent).Apply(SoftwarePackage("2026.12.3", key, ("app/version.txt", "app 4\n")));
        Assert.DoesNotContain(noWarn.Warnings, w => w.Contains("manifest.json"));
        Assert.False(PackageJson.Load<PreviousMeta>(Path.Combine(absent.PreviousDir, "previous.json"), "previous.json").ReleaseUnreadable);
        Assert.DoesNotContain("releaseUnreadable", File.ReadAllText(Path.Combine(absent.PreviousDir, "previous.json")));
        Assert.DoesNotContain("baselineUnreadable", File.ReadAllText(absent.InstalledFilesPath));
    }

    [Fact]
    public void RolledBackSoftwarePackage_IsNotADowngradeBasis_WhenTheOverlayRestoreFailed()
    {
        // 審查第二十九輪第五次 #1:rollback 時「還原覆蓋清單」失敗,覆蓋清單仍記著已撤回的軟體更新包 2027.1.2;
        // 之後以同版本(2026.12.1)、建置於套用之後的重建版重新安裝 — installed.json 已把那個包標為回復,
        // 已撤回的版本不能當降版的比較基準(否則會誤報「降版:比曾套用的軟體更新包 2027.1.2 舊」)
        using var key = _env.Primary();
        var ship = NewInstall("rollback-overlay-stale", "2026.12.1");
        var applier = new UpdateApplier(ship);
        Assert.True(applier.Apply(ChartsPackage("2026.12.5", key, "data/charts/c5.dat", "c5")).Ok);
        Assert.True(applier.Apply(SoftwarePackage("2027.1.2", key, ("app/version.txt", "app 9\n"))).Ok);
        var afterApply = File.ReadAllBytes(ship.InstalledFilesPath);
        var rolled = applier.Rollback("測試");
        Assert.True(rolled.Ok, string.Join(";", rolled.Errors));
        File.WriteAllBytes(ship.InstalledFilesPath, afterApply);   // 模擬還原覆蓋清單失敗:留下套用後的覆蓋清單

        ReinstallKeepingCharts(ship, "2026.12.1", DateTimeOffset.UtcNow.AddMinutes(5));
        var r = AssertOk(ship);
        Assert.True(r.InstalledFiles!.Reinstalled);
        AssertNoDowngrade(ship);
        Assert.DoesNotContain("2027.1.2", Run("status", "--root", ship.Root).Err);

        // 對照:沒有回復的軟體更新包照舊列入比較基準(同樣的重新安裝 → 降版)
        var kept = NewInstall("rollback-overlay-kept", "2026.12.1");
        var keptApplier = new UpdateApplier(kept);
        Assert.True(keptApplier.Apply(SoftwarePackage("2027.1.2", key, ("app/version.txt", "app 9\n"))).Ok);
        ReinstallKeepingCharts(kept, "2026.12.1", DateTimeOffset.UtcNow.AddMinutes(5));
        var down = AssertOk(kept);
        Assert.NotNull(down.Downgrade);
        Assert.Equal("2027.1.2", down.Downgrade!.BasisVersion);
    }
}
