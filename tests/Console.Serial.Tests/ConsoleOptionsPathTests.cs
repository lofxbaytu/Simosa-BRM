using System.Diagnostics;
using SimosaBRM.ConsoleSerial.Core;
using SimosaBRM.SimCore.Ship;
using Xunit;

#pragma warning disable xUnit1031 // 讀 stdout/stderr 的 Task.Result 都在子程序結束之後

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>
/// --log 與 --loopback 的寫入位置(本機資料守則;docs/ops/本機資料位置.md 第 4 節):相對路徑以根目錄(專案或安裝根目錄)為準,
/// 找不到根目錄時才以目前目錄為準;在根目錄外而且位於 Windows 系統磁碟時拒絕,SIMOSA_ALLOW_SYSTEM_DRIVE=1 才照做。
/// 以注入的 Windows + C: 環境在任何作業系統上測試。
/// </summary>
public class ConsoleOptionsPathTests
{
    private static readonly LocalDataEnvironment Win = new(true, "C:", false);
    private const string RootD = @"D:\Simosa BRM";

    private static ResolvedFiles Files(string root, LocalDataEnvironment env, params string[] args)
        => ConsoleOptions.Parse(args).ResolveFiles(root, env);

    [Fact]
    public void Defaults_AndRelativePaths_AreAgainstRoot()
    {
        var f = Files(RootD, Win, "--loopback", @"build\tmp\in.txt,build/tmp/out.txt");
        Assert.Equal(@"D:\Simosa BRM\build\logs\console.log", f.Log);          // 預設 log:<根目錄>\build\logs(不是目前目錄)
        Assert.Equal(@"D:\Simosa BRM\build\tmp\in.txt", f.LoopbackIn);
        Assert.Equal(@"D:\Simosa BRM\build\tmp\out.txt", f.LoopbackOut);
        Assert.Null(Files(RootD, Win, "--port", "COM5", "--log", "-").Log);   // - = 只印主控台
        Assert.Null(Files(RootD, Win, "--loopback", "in.txt").LoopbackOut);
        // 根目錄在系統磁碟(經同意的安裝)時,根目錄內的預設位置照常
        Assert.Equal(@"C:\Simosa BRM\build\logs\console.log", Files(@"C:\Simosa BRM", Win, "--port", "COM5").Log);
    }

    [Theory]
    [InlineData("--log", @"C:\Users\trainee\console.log", "--log")]
    [InlineData("--log", @"c:/temp/console.log", "--log")]
    [InlineData("--loopback", @"C:\temp\in.txt,out.txt", "讀取檔")]
    [InlineData("--loopback", @"in.txt,\\?\C:\temp\out.txt", "寫入檔")]
    public void OutsideRoot_OnSystemDrive_IsRejected(string option, string value, string what)
    {
        var args = option == "--log" ? new[] { "--port", "COM5", "--log", value } : new[] { "--loopback", value, "--log", "-" };
        var ex = Assert.Throws<LocalDataRejectedException>(() => Files(RootD, Win, args));
        Assert.Contains(what, ex.Message);
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", ex.Message);
    }

    [Fact]
    public void AllowedLocations()
    {
        Assert.Equal(@"E:\logs\console.log", Files(RootD, Win, "--port", "COM5", "--log", @"E:\logs\console.log").Log);          // 隨身碟
        Assert.Equal(@"\\server\share\console.log", Files(RootD, Win, "--port", "COM5", "--log", @"\\server\share\console.log").Log); // UNC
        Assert.Equal(@"C:\Simosa BRM\build\logs\x.log", Files(@"C:\Simosa BRM", Win, "--port", "COM5", "--log", @"C:\Simosa BRM\build\logs\x.log").Log);
        var notices = new List<string>();
        var allowed = ConsoleOptions.Parse(new[] { "--port", "COM5", "--log", @"C:\Users\trainee\console.log" })
            .ResolveFiles(RootD, Win with { AllowSystemDrive = true }, notices.Add);
        Assert.Equal(@"C:\Users\trainee\console.log", allowed.Log);
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", Assert.Single(notices));
        // 非 Windows 不檢查
        Assert.NotNull(Files(RootD, new LocalDataEnvironment(false, null, false), "--port", "COM5", "--log", @"C:\Users\trainee\console.log").Log);
    }

    [Fact]
    public void RootNotFound_RelativeIsAgainstCurrentDirectory_AndChecked()
    {
        // 找不到根目錄:相對路徑維持以目前目錄為準;目前目錄在系統磁碟 → 根目錄外 → 拒絕,在其他磁碟照常
        var o = ConsoleOptions.Parse(new[] { "--port", "COM5" });
        Assert.Throws<LocalDataRejectedException>(() => o.ResolveFiles(null, Win, currentDirectory: @"C:\Users\trainee"));
        Assert.Equal(@"E:\work\build\logs\console.log", o.ResolveFiles(null, Win, currentDirectory: @"E:\work").Log);
    }

    [Fact]
    public void NativeSemantics_AndFindRoot()
    {
        var env = LocalDataEnvironment.FromProcess() with { AllowSystemDrive = false };
        var root = ConsoleOptions.FindRoot();
        Assert.Equal(RepositoryPaths.FindRoot(), root);   // 測試執行時由 build/bin 往上找到專案根目錄
        var f = ConsoleOptions.Parse(new[] { "--loopback", "build/test-tmp/in.txt" }).ResolveFiles(root, env);
        Assert.Equal(Path.Combine(root!, "build", "logs", "console.log"), f.Log);
        Assert.Equal(Path.Combine(root!, "build", "test-tmp", "in.txt"), f.LoopbackIn);
    }

    /// <summary>Windows 才有系統磁碟:實際啟動服務,--log 指到系統磁碟上根目錄外 → 結束碼 2、不建立檔案(已設 SIMOSA_ALLOW_SYSTEM_DRIVE=1 的機器略過)。</summary>
    [Fact]
    public void Service_RejectsLogOnSystemDriveOutsideRoot_OnWindows()
    {
        if (!OperatingSystem.IsWindows() || System.Environment.GetEnvironmentVariable(LocalDataGuard.AllowSystemDriveVariable) == "1") return;
        var root = RepositoryPaths.FindRoot();
        var config = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/')).Parent?.Name ?? "Release";
        var dll = Path.Combine(root, "build", "bin", "Console.Serial", config, "net8.0", "SimosaBRM.Console.Serial.dll");
        if (!File.Exists(dll)) return;
        var sys = (System.Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var dir = $@"{sys}\simosa-guard-{Guid.NewGuid():N}";
        if (dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        var dotnet = self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : "dotnet";
        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var a in new[] { dll, "--port", "COM250", "--log", Path.Combine(dir, "console.log"), "--duration", "1" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "服務 60 s 內未結束");
        Assert.True(p.ExitCode == 2, $"結束碼 {p.ExitCode}(應為 2)\n{stdout.Result}\n{stderr.Result}");
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", stderr.Result);
        Assert.False(Directory.Exists(dir), $"不應建立 {dir}");
    }
}
