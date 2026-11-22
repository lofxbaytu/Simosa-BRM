using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// simosa-pkg 的寫入位置(本機資料守則;docs/ops/本機資料位置.md 第 4 節):寫入用的相對路徑以根目錄(--root 或預設安裝根目錄)為準;
/// 寫入位置在安裝根目錄與專案根目錄之外、而且位於 Windows 系統磁碟時拒絕(結束碼 1),隨身碟(E: 等)、UNC 照常,SIMOSA_ALLOW_SYSTEM_DRIVE=1 才放行。
/// 以注入的 Windows + 系統磁碟 C: 環境在任何作業系統上測試:被拒絕的指令必須在碰到檔案系統之前結束;允許的位置以 WritePath 驗證(不實際寫檔)。
/// </summary>
public sealed class CliWriteLocationTests : IDisposable
{
    private static readonly LocalDataEnvironment Win = new(true, "C:", false);
    private const string Sys = @"C:\Users\trainee\simosa-out";
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static (int Code, string Out, string Err) Run(LocalDataEnvironment? env, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = new CliApp(o, e, env).Run(args);
        return (code, o.ToString(), e.ToString());
    }

    /// <summary>船上安裝目錄(實際的暫存資料夾),packaging.json 可另加 store / recordsDir。</summary>
    private string ShipRoot(string? store = null, string? recordsDir = null)
    {
        var layout = _env.NewShip("ship-" + Guid.NewGuid().ToString("N")[..6]);
        if (store is not null || recordsDir is not null)
        {
            layout.Config.Store = store;
            layout.Config.RecordsDir = recordsDir;
            layout.Config.Save(layout.ConfigPath);
        }
        return layout.Root;
    }

    private static void AssertRejected((int Code, string Out, string Err) r, string what)
    {
        Assert.True(r.Code == 1, $"結束碼 {r.Code}(應為 1)\n{r.Out}\n{r.Err}");
        Assert.Contains(what, r.Err);
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", r.Err);
        Assert.Contains("系統磁碟", r.Err);
    }

    /// <summary>每一個寫入選項:根目錄外的 C:\ → 結束碼 1,訊息說明守則與開關,沒有寫出任何檔案。</summary>
    [Fact]
    public void EveryWriteOption_OutsideRoot_OnSystemDrive_IsRejected()
    {
        var root = ShipRoot();
        var before = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Count();
        AssertRejected(Run(Win, "keygen", "--root", root, "--out", $@"{Sys}\k", "--name", "k", "--role", "primary"), "--out");
        AssertRejected(Run(Win, "verification", "add", "--root", root, "--file", $@"{Sys}\ver.json", "--scenario", "x.yaml", "--parameters", "p.yaml", "--by", "測試"), "--file");
        AssertRejected(Run(Win, "pack", "--root", root, "--kind", "software", "--version", "1", "--summary", "x", "--out", $@"{Sys}\x.sbupd"), "--out");
        AssertRejected(Run(Win, "pack", "--root", root, "--kind", "software", "--version", "1", "--summary", "x", "--out", @"E:\x.sbupd", "--store", $@"{Sys}\store"), "--store");
        AssertRejected(Run(Win, "store", "import", "x.sbrec", "--root", root, "--store", $@"{Sys}\store"), "--store");
        AssertRejected(Run(Win, "store", "list", "--root", root, "--store", @"c:\temp\store"), "--store");
        AssertRejected(Run(Win, "store", "backup", "--root", root, "--to", $@"{Sys}\backup"), "--to");
        AssertRejected(Run(Win, "store", "report", "--root", root, "--ship", "FSB1", "--out", $@"{Sys}\r.sbbak", "--key", "k.pem"), "--out");
        AssertRejected(Run(Win, "sync", "--root", root, "--ship", "127.0.0.1", "--store", $@"{Sys}\store", "--key", "k.pem"), "--store");
        AssertRejected(Run(Win, "release", "manifest", "--root", root, "--dir", $@"{Sys}\release", "--version", "1"), "--dir");
        AssertRejected(Run(Win, "export", "--root", root, "--to", @"C:\usb", "--role", "ship"), "--to");
        AssertRejected(Run(Win, "export", "--root", root, "--to", @"\\?\C:\usb", "--role", "instructor"), "--to");
        AssertRejected(Run(Win, "import", "--root", root, "--from", @"C:/usb", "--role", "ship"), "--from");
        AssertRejected(Run(Win, "import", "--root", root, "--from", @"C:\usb", "--role", "instructor"), "--from");
        // 被拒絕的指令沒有在安裝根目錄內留下任何東西(紀錄庫、稽核紀錄…)
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Count());
        Assert.False(Directory.Exists(Sys));
    }

    [Fact]
    public void PackagingJson_Store_And_RecordsDir_OutsideRoot_OnSystemDrive_AreRejected()
    {
        var withStore = ShipRoot(store: @"C:\SimosaStore");
        AssertRejected(Run(Win, "store", "list", "--root", withStore), "packaging.json 的 store");
        AssertRejected(Run(Win, "store", "mark-backed-up", "--all", "--root", withStore), "packaging.json 的 store");
        AssertRejected(Run(Win, "pack", "--root", withStore, "--kind", "software", "--version", "1", "--summary", "x", "--out", @"E:\x.sbupd"), "packaging.json 的 store");

        // recordsDir:船上依已備份回報刪除舊紀錄的指令(accept-report、import --role ship、serve)
        var withRecords = ShipRoot(recordsDir: @"C:\SimosaRecords");
        AssertRejected(Run(Win, "records", "accept-report", "x.sbbak", "--root", withRecords), "packaging.json 的 recordsDir");
        AssertRejected(Run(Win, "import", "--root", withRecords, "--from", @"E:\usb", "--role", "ship"), "packaging.json 的 recordsDir");
        AssertRejected(Run(Win, "serve", "--root", withRecords, "--listen", "127.0.0.1", "--port", "1"), "packaging.json 的 recordsDir");
        // 只讀的指令不受影響(records list 只讀帳冊)
        Assert.Equal(0, Run(Win, "records", "list", "--root", withRecords).Code);
    }

    /// <summary>允許的位置(以 WritePath 驗證,不實際寫檔):隨身碟、UNC、根目錄內(根目錄在 C: 也可)、專案根目錄內、開關、非 Windows。</summary>
    [Fact]
    public void AllowedLocations_ViaWritePath()
    {
        var err = new StringWriter();
        var app = new CliApp(new StringWriter(), err, Win);
        string W(string root, string path) => app.WritePath(new CliArgs(new[] { "--root", root }), "--out", path);

        Assert.Equal(@"E:\simosa-transfer\x.sbupd", W(@"D:\Simosa BRM", @"E:\simosa-transfer\x.sbupd"));     // 教官筆電派送到隨身碟
        Assert.Equal(@"\\server\share\backup", W(@"D:\Simosa BRM", @"\\server\share\backup"));
        Assert.Equal(@"C:\Simosa BRM\build\packages\x.sbupd", W(@"C:\Simosa BRM", @"C:\Simosa BRM\build\packages\x.sbupd"));
        Assert.Equal(@"C:\Simosa BRM\build\packages\x.sbupd", W(@"C:\Simosa BRM", @"build\packages\x.sbupd"));   // 相對 → --root
        Assert.Equal(@"D:\Simosa BRM\store", W(@"D:\Simosa BRM", "store"));
        Assert.Throws<LocalDataRejectedException>(() => W(@"C:\Simosa BRM", @"..\other\x.sbupd"));
        Assert.Equal(string.Empty, err.ToString());

        var allowed = new CliApp(new StringWriter(), err, Win with { AllowSystemDrive = true });
        Assert.Equal(@"C:\Users\trainee\x.sbupd", allowed.WritePath(new CliArgs(new[] { "--root", @"D:\Simosa BRM" }), "--out", @"C:\Users\trainee\x.sbupd"));
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", err.ToString());

        var linux = new CliApp(new StringWriter(), new StringWriter(), new LocalDataEnvironment(false, null, false));
        Assert.NotNull(linux.WritePath(new CliArgs(new[] { "--root", _env.Dir }), "--out", Path.Combine(_env.Dir, "x")));

        // packaging.json 的位置設定:相對路徑以安裝根目錄為準
        Assert.Equal(@"D:\Simosa BRM\records", app.ConfigWritePath(new CliArgs(new[] { "--root", @"D:\Simosa BRM" }), "recordsDir", "records"));
        Assert.Equal(@"E:\records", app.ConfigWritePath(new CliArgs(new[] { "--root", @"D:\Simosa BRM" }), "recordsDir", @"E:\records"));
        Assert.Throws<LocalDataRejectedException>(() => app.ConfigWritePath(new CliArgs(new[] { "--root", @"D:\Simosa BRM" }), "recordsDir", @"C:\records"));
    }

    [Fact]
    public void ProjectRoot_CountsAsInside()
    {
        // build-release 在 <專案>\build\release\<版本>\tools\updater 執行 simosa-pkg:安裝根目錄是發行目錄,
        // 金鑰寫到 <專案>\build\release\dev-keys(發行目錄之外、專案根目錄之內)— 專案在 C:(經同意)時也不得拒絕
        var app = new CliApp(new StringWriter(), new StringWriter(), new LocalDataEnvironment(OperatingSystem.IsWindows(), OperatingSystem.IsWindows() ? "C:" : null, false));
        var repo = RepositoryPaths.FindRoot();
        var release = Path.Combine(repo, "build", "release", "2026.11.29");
        var keys = Path.Combine(repo, "build", "release", "dev-keys", "dev-primary");
        Assert.Equal(Path.GetFullPath(keys), app.WritePath(new CliArgs(new[] { "--root", release }), "--out", keys));
    }

    /// <summary>寫入用的相對路徑以根目錄為準(不以目前目錄為準):實際執行 keygen、pack、store、verification、export、release manifest。</summary>
    [Fact]
    public void RelativeWritePaths_AreAgainstRoot()
    {
        var root = _env.PathIn("rel-root");
        Directory.CreateDirectory(root);
        var cwd = Directory.GetCurrentDirectory();
        Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(cwd));

        Assert.Equal(0, Run(null, "keygen", "--root", root, "--out", Path.Combine("keys", "k"), "--name", "k", "--role", "primary").Code);
        Assert.True(File.Exists(Path.Combine(root, "keys", "k.key.pem")));
        Assert.False(File.Exists(Path.Combine(cwd, "keys", "k.key.pem")));

        var src = _env.PathIn("v.txt");
        File.WriteAllText(src, "app 9\n");
        var pack = Run(null, "pack", "--root", root, "--kind", "software", "--version", "2026.11.1", "--summary", "相對路徑", "--file", $"app/version.txt={src}",
            "--key", Path.Combine(root, "keys", "k.key.pem"), "--out", Path.Combine("packages", "x.sbupd"));
        Assert.True(pack.Code == 0, pack.Err);
        Assert.True(File.Exists(Path.Combine(root, "packages", "x.sbupd")));

        Assert.Equal(0, Run(null, "store", "list", "--root", root, "--store", "my-store").Code);
        Assert.True(Directory.Exists(Path.Combine(root, "my-store")));
        Assert.False(Directory.Exists(Path.Combine(cwd, "my-store")));

        var scenario = Path.Combine(_env.InstructorRoot, "data", "scenarios", "E01_baseline.yaml");
        var ver = Run(null, "verification", "add", "--root", root, "--file", "ver.json", "--scenario", scenario, "--parameters", _env.BaselineParameters, "--by", "測試");
        Assert.True(ver.Code == 0, ver.Err);
        Assert.True(File.Exists(Path.Combine(root, "ver.json")));
        var list = Run(null, "verification", "list", "--root", root, "--file", "ver.json");
        Assert.Contains("E01_baseline", list.Out);

        Assert.Equal(0, Run(null, "export", "--root", root, "--to", "usb", "--role", "instructor", "--store", "my-store").Code);
        Assert.True(Directory.Exists(Path.Combine(root, "usb", "simosa-transfer")));

        var rel = Path.Combine(root, "release");
        Directory.CreateDirectory(Path.Combine(rel, "app"));
        File.WriteAllText(Path.Combine(rel, "app", "a.txt"), "a\n");
        Assert.Equal(0, Run(null, "release", "manifest", "--root", root, "--dir", "release", "--version", "2026.11.1").Code);
        Assert.True(File.Exists(Path.Combine(rel, "manifest.json")));
    }

    /// <summary>
    /// 成對的指令在目前目錄不是根目錄時以相對路徑互相找得到(審查 F1):寫入用的相對路徑以根目錄為準,
    /// 讀取本工具產生的檔案(--key、--verification、verification list --file、--push、inspect / verify 的檔案)目前目錄沒有時再找根目錄。
    /// 目前目錄 = 測試程序的目前目錄(不改它:其他測試平行執行),根目錄 = 暫存資料夾;檔名帶亂數,目前目錄不會剛好有同名檔。
    /// </summary>
    [Fact]
    public void PairedCommands_WithRelativePaths_FindEachOther_WhenCwdIsNotRoot()
    {
        var root = _env.PathIn("pair-root");
        Directory.CreateDirectory(root);
        var cwd = Directory.GetCurrentDirectory();
        Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(cwd));
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ver = $"ver-{tag}.json";
        var key = $"k-{tag}";
        var pkg = $"p-{tag}.sbupd";
        Assert.False(File.Exists(ver) || File.Exists(key + ".key.pem") || File.Exists(pkg));

        var p = _env.EditParameter("navigation.cpa.coastal_nm", "0.5", "0.6");
        var scenario = Path.Combine(_env.InstructorRoot, "data", "scenarios", "E01_baseline.yaml");
        var add = Run(null, "verification", "add", "--root", root, "--file", ver, "--scenario", scenario, "--parameters", p, "--by", "測試");
        Assert.True(add.Code == 0, add.Err);
        Assert.True(File.Exists(Path.Combine(root, ver)));
        var list = Run(null, "verification", "list", "--root", root, "--file", ver);
        Assert.Equal(0, list.Code);
        Assert.Contains("E01_baseline", list.Out);

        Assert.Equal(0, Run(null, "keygen", "--root", root, "--out", key, "--name", "k", "--role", "primary").Code);
        Assert.True(File.Exists(Path.Combine(root, key + ".key.pem")));

        // 改動前在目前目錄找不到 → 「找不到金鑰檔」「找不到驗證紀錄」;現在 --key 與 --verification 都找到根目錄內的檔
        var pack = Run(null, "pack", "--root", root, "--kind", "parameters", "--version", "2026.10.2", "--summary", "相對路徑成對", "--sms", "DWO-007:4:DPA",
            "--file", $"data/parameters/company-procedures.yaml={p}", "--data-root", _env.InstructorRoot, "--baseline", _env.BaselineParameters,
            "--verification", ver, "--key", key + ".key.pem", "--out", pkg);
        Assert.True(pack.Code == 0, pack.Out + pack.Err);
        Assert.True(File.Exists(Path.Combine(root, pkg)));
        Assert.DoesNotContain("目前目錄與根目錄都有", pack.Err);   // 只有根目錄有:不提醒

        // pack --out 的產物:inspect、export --push 以相對路徑讀得到
        var inspect = Run(null, "inspect", pkg, "--root", root);
        Assert.True(inspect.Code == 0, inspect.Err);
        Assert.Contains("2026.10.2", inspect.Out);
        var export = Run(null, "export", "--root", root, "--to", "usb", "--role", "instructor", "--store", "st", "--push", pkg);
        Assert.True(export.Code == 0, export.Err);
        Assert.True(File.Exists(Path.Combine(root, "usb", "simosa-transfer", "to-ship", pkg)));
        // 目前目錄沒有留下任何檔案
        Assert.False(File.Exists(ver) || File.Exists(key + ".key.pem") || File.Exists(pkg) || File.Exists(Path.Combine("usb", "simosa-transfer", "to-ship", pkg)));
    }

    /// <summary>ReadPath:絕對路徑原樣;相對路徑目前目錄優先(原本的行為),沒有時根目錄;兩處都有時用目前目錄的並提醒;兩處都沒有時原樣。</summary>
    [Fact]
    public void ReadPath_CwdFirst_ThenRoot()
    {
        var root = _env.PathIn("read-root");
        Directory.CreateDirectory(root);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var name = $"read-{tag}.json";
        var err = new StringWriter();
        var app = new CliApp(new StringWriter(), err);
        var a = new CliArgs(new[] { "--root", root });

        Assert.Equal(name, app.ReadPath(a, "--verification", name));                         // 兩處都沒有:原樣(錯誤訊息與原本相同)
        File.WriteAllText(Path.Combine(root, name), "{}");
        Assert.Equal(Path.Combine(root, name), app.ReadPath(a, "--verification", name));     // 只有根目錄有
        var abs = Path.Combine(_env.Dir, "elsewhere.json");
        Assert.Equal(abs, app.ReadPath(a, "--verification", abs));                           // 絕對路徑原樣
        Assert.Equal(string.Empty, err.ToString());

        var inCwd = Path.GetFullPath(name);
        try
        {
            File.WriteAllText(inCwd, "{}");
            Assert.Equal(name, app.ReadPath(a, "--verification", name));                     // 兩處都有:目前目錄(原本的行為)並提醒
            Assert.Contains("注意:--verification", err.ToString());
            Assert.Contains("目前目錄與根目錄都有", err.ToString());
            File.Delete(Path.Combine(root, name));
            var quiet = new StringWriter();
            Assert.Equal(name, new CliApp(new StringWriter(), quiet).ReadPath(a, "--verification", name));   // 只有目前目錄有:不提醒
            Assert.Equal(string.Empty, quiet.ToString());
        }
        finally { File.Delete(inCwd); }
    }

    /// <summary>Windows 才有系統磁碟:以目前程序的環境(未注入)執行,keygen --out 到系統磁碟上根目錄外 → 結束碼 1、不建立檔案(已設開關的機器略過)。</summary>
    [Fact]
    public void RealEnvironment_RejectsSystemDrive_OnWindows()
    {
        if (!OperatingSystem.IsWindows() || System.Environment.GetEnvironmentVariable(LocalDataGuard.AllowSystemDriveVariable) == "1") return;
        var sys = (System.Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var dir = $@"{sys}\simosa-guard-{Guid.NewGuid():N}";
        var repo = RepositoryPaths.FindRoot();
        if (dir.StartsWith(repo, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(_env.Dir, StringComparison.OrdinalIgnoreCase)) return;
        var r = Run(null, "keygen", "--root", _env.Dir, "--out", Path.Combine(dir, "k"), "--name", "k", "--role", "primary");
        AssertRejected(r, "--out");
        Assert.False(Directory.Exists(dir));
    }
}
