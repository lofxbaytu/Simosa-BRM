using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 本機資料守則的寫入位置檢查(LocalDataGuard;docs/ops/本機資料位置.md 第 4 節):以注入的 Windows + 系統磁碟 C: 環境在任何作業系統上測試
/// (Linux 上以 Windows 語意的字串運算、Windows 上以 System.IO.Path,兩者結果須相同)。
/// </summary>
public class LocalDataGuardTests
{
    private static readonly LocalDataEnvironment Win = new(IsWindows: true, SystemDrive: "C:", AllowSystemDrive: false);
    private static readonly LocalDataEnvironment WinAllowed = Win with { AllowSystemDrive = true };
    private static readonly LocalDataEnvironment Linux = new(IsWindows: false, SystemDrive: null, AllowSystemDrive: false);
    private const string RootD = @"D:\Simosa BRM";
    private const string RootC = @"C:\Simosa BRM";

    private static LocalDataVerdict V(string path, string root, LocalDataEnvironment? env = null)
        => LocalDataGuard.Evaluate(path, root, new[] { root }, env ?? Win).Verdict;

    [Theory]
    [InlineData(@"C:\Users\trainee\console.log")]
    [InlineData(@"c:\users\trainee\console.log")]             // 大小寫
    [InlineData(@"C:/Users/trainee/console.log")]             // 正斜線
    [InlineData(@"\\?\C:\Users\trainee\console.log")]         // 長路徑前綴
    [InlineData(@"\\.\C:\Users\trainee\console.log")]         // 裝置路徑前綴
    [InlineData(@"C:\Simosa BRM\..\Users\trainee\x.log")]     // .. 走出根目錄
    public void OutsideRoot_OnSystemDrive_IsRejected(string path)
    {
        Assert.Equal(LocalDataVerdict.Rejected, V(path, RootD));
        Assert.Equal(LocalDataVerdict.Rejected, V(path, RootC));   // 根目錄在 C: 也一樣:根目錄外即拒絕
    }

    [Theory]
    [InlineData(@"E:\simosa-transfer\x.sbupd")]                // 隨身碟
    [InlineData(@"G:\Simosa BRM\build\records\x.jsonl")]
    [InlineData(@"\\server\share\backup")]                     // UNC 不是系統磁碟
    [InlineData(@"\\?\UNC\server\share\backup")]
    [InlineData(@"//server/share/backup")]
    [InlineData(@"\\?\E:\x")]
    public void OutsideRoot_NotOnSystemDrive_IsAllowed(string path)
    {
        Assert.Equal(LocalDataVerdict.NotSystemDrive, V(path, RootD));
        Assert.Equal(LocalDataVerdict.NotSystemDrive, V(path, RootC));
    }

    [Theory]
    [InlineData(@"C:\Simosa BRM\build\records\x.jsonl")]
    [InlineData(@"c:\SIMOSA BRM\Build\x")]                     // 大小寫
    [InlineData(@"C:/Simosa BRM/build/x")]
    [InlineData(@"\\?\C:\Simosa BRM\build\x")]
    [InlineData(@"C:\Simosa BRM")]                             // 根目錄本身
    [InlineData(@"C:\Simosa BRM\build\..\state\x")]
    public void InsideRoot_OnSystemDrive_IsAllowed(string path)
    {
        // 根目錄本身在系統磁碟(經同意的安裝)時,根目錄內的位置不額外檢查
        Assert.Equal(LocalDataVerdict.InsideRoot, V(path, RootC));
    }

    [Fact]
    public void SimilarPrefix_IsNotInsideRoot()
    {
        Assert.Equal(LocalDataVerdict.Rejected, V(@"C:\Simosa BRMx\y", RootC));
        Assert.Equal(LocalDataVerdict.Rejected, V(@"C:\Simosa\y", RootC));
        Assert.Equal(LocalDataVerdict.InsideRoot, V(@"C:\Simosa BRM\y", RootC + @"\"));   // 根目錄結尾有 \ 也一樣
    }

    /// <summary>
    /// 段落尾端的空白與句點照 GetFullPathNameW(Microsoft〈File path formats on Windows systems〉):中間段只刪一個結尾句點、空白保留;
    /// 路徑不以 \ 結尾時最後一段刪去全部結尾的空白與句點;以 \ 結尾時最後一段的空白保留。
    /// </summary>
    [Fact]
    public void TrailingSpacesAndDots_FollowWindowsNormalization()
    {
        static LocalDataVerdict C(string path, string root) => LocalDataGuard.Check(path, new[] { root }, Win).Verdict;
        // 中間段結尾的空白不刪:"Simosa BRM \records" 是根目錄旁的另一個資料夾 "Simosa BRM "(根目錄外的 C:)
        Assert.Equal(LocalDataVerdict.Rejected, C(@"C:\Simosa BRM \records\x.jsonl", RootC));
        Assert.Equal(LocalDataVerdict.Rejected, V(@"C:\Simosa BRM \records\x.jsonl", RootC));
        Assert.Equal(LocalDataVerdict.Rejected, V(@"C:\Simosa BRM  \records\x.jsonl", RootC));
        // 中間段只刪一個結尾句點:"Simosa BRM.\records" 即根目錄內
        Assert.Equal(LocalDataVerdict.InsideRoot, V(@"C:\Simosa BRM.\records\x.jsonl", RootC));
        Assert.Equal(LocalDataVerdict.InsideRoot, C(@"C:\Simosa BRM.\records\x.jsonl", RootC));
        Assert.Equal(LocalDataVerdict.Rejected, C(@"C:\Simosa BRM. \records\x.jsonl", RootC));   // 句點後還有空白:不刪
        // 最後一段(路徑不以 \ 結尾):結尾的空白與句點全刪
        Assert.Equal(LocalDataVerdict.InsideRoot, V(@"C:\Simosa BRM\records\x.jsonl. .", RootC));
        Assert.Equal(LocalDataVerdict.InsideRoot, V(@"C:\Simosa BRM. ", RootC));
        Assert.Equal(LocalDataVerdict.InsideRoot, C(@"C:\Simosa BRM \.", RootC));                // . 處理後的最後一段也刪
        // 以 \ 結尾:最後一段的空白保留(資料夾選項,例如 --profile、--store、--to)
        Assert.Equal(LocalDataVerdict.Rejected, V(@"C:\Simosa BRM \", RootC));
        Assert.Equal(LocalDataVerdict.Rejected, C(@"C:\Simosa BRM \", RootC));
        Assert.Equal(LocalDataVerdict.InsideRoot, V(@"C:\Simosa BRM\profile \", RootC));
        Assert.Equal(LocalDataVerdict.InsideRoot, V(@"C:\Simosa BRM\", RootC));
        // 相對路徑組合後的結果相同(Linux 上以字串運算組合,Windows 上以 System.IO.Path)
        Assert.Equal(LocalDataVerdict.Rejected, V(@"..\Simosa BRM \records\x.jsonl", RootC));
        Assert.Equal(LocalDataVerdict.InsideRoot, V(@"..\Simosa BRM.\records\x.jsonl", RootC));
        // 根目錄在 D:(一般情況):根目錄旁的 "Simosa BRM " 在 D: → 不是系統磁碟,照常
        Assert.Equal(LocalDataVerdict.NotSystemDrive, V(@"D:\Simosa BRM \records\x.jsonl", RootD));
    }

    [Fact]
    public void Messages_HaveNoDoubleColonAfterDriveLetter()
    {
        var rejected = LocalDataGuard.Evaluate(@"C:\Users\x\console.log", RootD, new[] { RootD }, Win);
        var message = LocalDataGuard.RejectionMessage("--log", rejected);
        Assert.DoesNotContain("C::", message);
        Assert.Contains("位於系統磁碟 C:。", message);
        var allowed = LocalDataGuard.Evaluate(@"C:\Users\x\console.log", RootD, new[] { RootD }, WinAllowed);
        var notice = LocalDataGuard.ConsentNotice("--log", allowed);
        Assert.DoesNotContain("C::", notice);
        Assert.Contains(@"C:\Users\x\console.log", notice);
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", notice);
    }

    [Fact]
    public void Switch_AllowsWithNotice_OtherwiseRejectedWithGuidance()
    {
        var notices = new List<string>();
        var path = LocalDataGuard.Require("--record", @"C:\Users\trainee\r.jsonl", RootD, new[] { RootD }, WinAllowed, notices.Add);
        Assert.Equal(@"C:\Users\trainee\r.jsonl", path);
        var notice = Assert.Single(notices);
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", notice);
        Assert.Contains("--record", notice);

        var ex = Assert.Throws<LocalDataRejectedException>(() => LocalDataGuard.Require("--record", @"C:\Users\trainee\r.jsonl", RootD, new[] { RootD }, Win, notices.Add));
        Assert.Contains("--record", ex.Message);
        Assert.Contains(@"C:\Users\trainee\r.jsonl", ex.Message);
        Assert.Contains(RootD, ex.Message);
        Assert.Contains("C:", ex.Message);
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", ex.Message);
        Assert.Contains("同意", ex.Message);
        Assert.Equal(LocalDataVerdict.Rejected, ex.Target.Verdict);
        Assert.Single(notices);   // 被拒絕時不另外提醒
        // 允許的位置不提醒
        LocalDataGuard.Require("--record", @"E:\r.jsonl", RootD, new[] { RootD }, WinAllowed, notices.Add);
        Assert.Single(notices);
    }

    [Fact]
    public void NotWindows_OrUnknownSystemDrive_IsNotChecked()
    {
        Assert.Equal(LocalDataVerdict.NotChecked, V(@"C:\Users\trainee\x", RootD, Linux));
        Assert.Equal(LocalDataVerdict.NotChecked, LocalDataGuard.Check("/home/trainee/x", new[] { "/srv/simosa" }, Linux).Verdict);
        Assert.Equal(LocalDataVerdict.NotChecked, V(@"C:\Users\trainee\x", RootD, Win with { SystemDrive = null }));
    }

    [Theory]
    [InlineData("C:", 'C')]
    [InlineData("c:", 'C')]
    [InlineData(@"C:\", 'C')]
    [InlineData("D", 'D')]
    public void SystemDrive_Forms(string systemDrive, char expected)
    {
        Assert.Equal(expected, LocalDataGuard.DriveLetter(systemDrive));
        // 系統磁碟以其他寫法給(小寫、結尾 \)時判定相同
        var env = Win with { SystemDrive = systemDrive };
        Assert.Equal(LocalDataVerdict.Rejected, LocalDataGuard.Check($@"{expected}:\Users\x", new[] { @"Z:\Simosa BRM" }, env).Verdict);
    }

    [Fact]
    public void RelativePath_IsAgainstRoot_NotCurrentDirectory()
    {
        var t = LocalDataGuard.Evaluate(@"build\logs\console.log", RootC, new[] { RootC }, Win);
        Assert.Equal(@"C:\Simosa BRM\build\logs\console.log", t.Path);
        Assert.Equal(LocalDataVerdict.InsideRoot, t.Verdict);
        Assert.Equal(@"D:\Simosa BRM\build\logs\console.log", LocalDataGuard.Evaluate("build/logs/console.log", RootD, new[] { RootD }, Win).Path);
        // 相對路徑以 .. 走出根目錄而落在系統磁碟 → 拒絕
        var up = LocalDataGuard.Evaluate(@"..\..\Users\trainee\x.log", RootC + @"\build", new[] { RootC }, Win);
        Assert.Equal(@"C:\Users\trainee\x.log", up.Path);
        Assert.Equal(LocalDataVerdict.Rejected, up.Verdict);
        // 絕對路徑原樣
        Assert.Equal(@"E:\usb", LocalDataGuard.Evaluate(@"E:\usb", RootD, new[] { RootD }, Win).Path);
        Assert.Equal(@"\\server\share\x", LocalDataGuard.Evaluate(@"\\server\share\x", RootD, new[] { RootD }, Win).Path);
    }

    [Fact]
    public void RelativePath_NativeSemantics_WhenNotEmulating()
    {
        // 目前作業系統自己的語意(Linux 容器、Windows 執行器各自):相對路徑接在基準目錄之後
        var env = new LocalDataEnvironment(OperatingSystem.IsWindows(), OperatingSystem.IsWindows() ? "C:" : null, false);
        var baseDir = Path.GetFullPath(Path.Combine("build", "tmp", "guard-base"));
        Assert.Equal(Path.Combine(baseDir, "build", "logs", "console.log"), LocalDataGuard.FullPath(Path.Combine("build", "logs", "console.log"), baseDir, env));
        Assert.Equal(baseDir, LocalDataGuard.FullPath(baseDir, "/elsewhere", env));
    }

    [Fact]
    public void SeveralRoots_AnyContains_AndMissingRootsAreIgnored()
    {
        var roots = new string?[] { RootD, null, "", @"C:\dev\simosa" };
        Assert.Equal(LocalDataVerdict.InsideRoot, LocalDataGuard.Check(@"C:\dev\simosa\build\release\dev-keys\k", roots, Win).Verdict);
        Assert.Equal(LocalDataVerdict.Rejected, LocalDataGuard.Check(@"C:\dev\other\k", roots, Win).Verdict);
        // 找不到根目錄:所有位置都算根目錄外;訊息說明找不到根目錄
        var none = LocalDataGuard.Check(@"C:\Users\trainee\build\logs\console.log", new string?[] { null }, Win);
        Assert.Equal(LocalDataVerdict.Rejected, none.Verdict);
        Assert.Contains("找不到根目錄", LocalDataGuard.RejectionMessage("--log", none));
        Assert.Equal(LocalDataVerdict.NotSystemDrive, LocalDataGuard.Check(@"E:\work\build\logs\console.log", new string?[] { null }, Win).Verdict);
    }

    [Fact]
    public void FromProcess_MatchesCurrentPlatform()
    {
        var env = LocalDataEnvironment.FromProcess();
        Assert.Equal(OperatingSystem.IsWindows(), env.IsWindows);
        if (OperatingSystem.IsWindows()) Assert.NotNull(LocalDataGuard.DriveLetter(env.SystemDrive));
        Assert.Equal(System.Environment.GetEnvironmentVariable("SIMOSA_ALLOW_SYSTEM_DRIVE") == "1", env.AllowSystemDrive);
    }

    [Fact]
    public void FindInstalledRoot_WalksUpToDataAndApp()
    {
        var root = Path.GetFullPath(Path.Combine("build", "tmp", "installed-root"));
        bool Exists(string d) => d == Path.Combine(root, "data") || d == Path.Combine(root, "app");
        Assert.Equal(root, RepositoryPaths.FindInstalledRoot(Path.Combine(root, "app", "console"), Exists));
        Assert.Equal(root, RepositoryPaths.FindInstalledRoot(root, Exists));
        Assert.Null(RepositoryPaths.FindInstalledRoot(Path.Combine(root, "app", "console"), _ => false));
    }
}
