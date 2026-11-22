using System.Diagnostics;
using SimosaBRM.SimCore.Host;
using SimosaBRM.SimCore.Ship;
using Xunit;

#pragma warning disable xUnit1031 // 讀 stdout/stderr 的 Task.Result 都在子程序結束之後(同 HostStartUtcTests)

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// SimCore.Host --record &lt;路徑&gt; 的寫入位置(本機資料守則;docs/ops/本機資料位置.md 第 4 節):相對路徑以專案根目錄為準;
/// 在根目錄外而且位於 Windows 系統磁碟時拒絕(結束碼 2),SIMOSA_ALLOW_SYSTEM_DRIVE=1 才照做。以注入的 Windows + C: 環境在任何作業系統上測試。
/// </summary>
public class HostRecordPathTests
{
    private static readonly LocalDataEnvironment Win = new(true, "C:", false);
    private const string RootD = @"D:\Simosa BRM";

    private static string? Resolve(string? recordArg, string root, LocalDataEnvironment env, List<string>? notices = null)
    {
        var args = recordArg is null ? new[] { "--record" } : new[] { "--record", recordArg };
        return HostOptions.Parse(args).ResolveRecordPath(root, env, notices is null ? null : notices.Add);
    }

    [Fact]
    public void OutsideRoot_OnSystemDrive_IsRejected()
    {
        var ex = Assert.Throws<LocalDataRejectedException>(() => Resolve(@"C:\Users\trainee\records\r.jsonl", RootD, Win));
        Assert.Contains("--record", ex.Message);
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", ex.Message);
        Assert.Throws<LocalDataRejectedException>(() => Resolve(@"\\?\c:\temp\r.jsonl", RootD, Win));
        Assert.Throws<LocalDataRejectedException>(() => Resolve(@"..\Users\r.jsonl", @"C:\Simosa BRM", Win));
    }

    [Fact]
    public void AllowedLocations()
    {
        Assert.Equal(@"E:\records\r.jsonl", Resolve(@"E:\records\r.jsonl", RootD, Win));                         // 隨身碟
        Assert.Equal(@"\\server\share\r.jsonl", Resolve(@"\\server\share\r.jsonl", RootD, Win));                 // UNC
        Assert.Equal(@"D:\Simosa BRM\build\records\r.jsonl", Resolve(@"D:\Simosa BRM\build\records\r.jsonl", RootD, Win));
        Assert.Equal(@"C:\Simosa BRM\build\records\r.jsonl", Resolve(@"C:\Simosa BRM\build\records\r.jsonl", @"C:\Simosa BRM", Win)); // 根目錄在 C:、路徑在根目錄內
        Assert.Equal(@"D:\Simosa BRM\build\records\r.jsonl", Resolve("build/records/r.jsonl", RootD, Win));        // 相對路徑 → 根目錄
        var notices = new List<string>();
        Assert.Equal(@"C:\Users\trainee\r.jsonl", Resolve(@"C:\Users\trainee\r.jsonl", RootD, Win with { AllowSystemDrive = true }, notices));
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", Assert.Single(notices));
        Assert.NotNull(Resolve(@"C:\Users\trainee\r.jsonl", RootD, new LocalDataEnvironment(false, null, false)));    // 非 Windows 不檢查
        Assert.Null(Resolve(null, RootD, Win));                                                                    // --record 不帶路徑:預設 build/records(根目錄內)
    }

    [Fact]
    public void RelativePath_IsAgainstProjectRoot_NativeSemantics()
    {
        var env = LocalDataEnvironment.FromProcess() with { AllowSystemDrive = false };
        Assert.Equal(Path.Combine(TestData.Root, "build", "test-records", "x.jsonl"), Resolve(Path.Combine("build", "test-records", "x.jsonl"), TestData.Root, env));
    }

    /// <summary>
    /// Windows 才有系統磁碟:實際以 dotnet 啟動主控程序,--record 指到系統磁碟上根目錄外的位置 → 結束碼 2、不建立任何檔案。
    /// (已設 SIMOSA_ALLOW_SYSTEM_DRIVE=1 的機器略過,否則真的會寫到系統磁碟)
    /// </summary>
    [Fact]
    public void Host_RejectsRecordOnSystemDriveOutsideRoot_OnWindows()
    {
        if (!OperatingSystem.IsWindows() || System.Environment.GetEnvironmentVariable(LocalDataGuard.AllowSystemDriveVariable) == "1") return;
        var hostDll = Path.Combine(TestData.Root, "build", "bin", "SimCore.Host", new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/')).Parent?.Name ?? "Release", "net8.0", "SimosaBRM.SimCore.Host.dll");
        if (!File.Exists(hostDll)) return;
        var sys = (System.Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var dir = $@"{sys}\simosa-guard-{Guid.NewGuid():N}";
        if (dir.StartsWith(TestData.Root, StringComparison.OrdinalIgnoreCase)) return;
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        var dotnet = self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : "dotnet";
        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = TestData.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var a in new[] { hostDll, "--scenario", "data/scenarios/E01_baseline.yaml", "--record", Path.Combine(dir, "r.jsonl"), "--duration", "1",
                     "--no-ws", "--no-udp", "--no-nmea", "--no-vhf", "--no-media", "--no-stop-file", "--dynamics", "placeholder" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "主控程序 60 s 內未結束");
        Assert.True(p.ExitCode == 2, $"結束碼 {p.ExitCode}(應為 2)\n{stdout.Result}\n{stderr.Result}");
        Assert.Contains("SIMOSA_ALLOW_SYSTEM_DRIVE=1", stderr.Result);
        Assert.False(Directory.Exists(dir), $"不應建立 {dir}");
    }
}
