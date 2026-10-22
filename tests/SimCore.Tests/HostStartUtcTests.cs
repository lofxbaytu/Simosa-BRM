using System.Diagnostics;
using System.Globalization;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 主控程序層級(審查發現 utc-mooring):SimCore.Host 必須以 EngineOptions.Clock = TimeProvider.System 建立引擎,
/// 情境未給 startTimeUtc 時 state 才有 utc(state.schema.json 描述:「未給時為載入情境時的實際時鐘(核心寫回情境,重播沿用紀錄標頭)」),
/// 且紀錄標頭 scenario.startTimeUtc 保存實際開始時刻。
/// 做法:以本測試所用的 dotnet 啟動已建置的 SimosaBRM.SimCore.Host.dll(build/bin/SimCore.Host/&lt;組態&gt;/net8.0),
/// 跑去掉 startTimeUtc 的 E01 一秒並紀錄,再讀紀錄檔驗證。
/// </summary>
public class HostStartUtcTests
{
    private static string? HostDll()
    {
        // build/bin/SimCore.Tests/<組態>/net8.0/ → build/bin/SimCore.Host/<組態>/net8.0/SimosaBRM.SimCore.Host.dll(Directory.Build.props 的輸出配置)
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfmDir = new DirectoryInfo(baseDir);
        var config = tfmDir.Parent?.Name ?? "Release";
        var candidates = new[] { config, "Release", "Debug" }
            .Select(c => Path.Combine(TestData.Root, "build", "bin", "SimCore.Host", c, "net8.0", "SimosaBRM.SimCore.Host.dll"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string DotnetExe()
    {
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return self;
        var root = System.Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (root is not null && File.Exists(Path.Combine(root, "dotnet"))) return Path.Combine(root, "dotnet");
        return "dotnet";
    }

    [Fact]
    public void HostWithoutScenarioStartTimeRecordsWallClockUtc()
    {
        var hostDll = HostDll();
        if (hostDll is null)
        {
            // 單獨建置本測試專案時主控程序尚未建置(本專案不引用 SimCore.Host);建置整個 SimosaBRM.sln 後才驗證
            Console.WriteLine("HostStartUtcTests:找不到 SimosaBRM.SimCore.Host.dll,略過(請先 dotnet build SimosaBRM.sln)");
            return;
        }
        var dir = TestData.TestRecordsDirectory;
        // E01 去掉 startTimeUtc(教官自訂情境常見);${params:…} 由載入器自動找參數集(以儲存庫根目錄為準)
        var yaml = File.ReadAllLines(TestData.BaselineScenarioPath).Where(l => !l.TrimStart().StartsWith("startTimeUtc:", StringComparison.Ordinal));
        var scenarioPath = Path.Combine(dir, "E01_nostart.yaml");
        File.WriteAllLines(scenarioPath, yaml);
        var recordPath = Path.Combine(dir, $"host-nostart-{Guid.NewGuid():N}.jsonl");

        var before = DateTime.UtcNow;
        var psi = new ProcessStartInfo(DotnetExe())
        {
            WorkingDirectory = TestData.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { hostDll, "--scenario", scenarioPath, "--record", recordPath, "--duration", "1", "--no-ws", "--no-udp", "--no-nmea", "--dynamics", "placeholder" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "主控程序 120 s 內未結束");
        var after = DateTime.UtcNow;
        Assert.True(p.ExitCode == 0, $"主控程序結束碼 {p.ExitCode}\n{stdout.Result}\n{stderr.Result}");

        var record = RecordReader.Read(recordPath);
        var start = record.Header.Scenario.StartTimeUtc;
        Assert.False(string.IsNullOrEmpty(start), "紀錄標頭 scenario.startTimeUtc 應保存實際開始時刻");
        Assert.True(SimulationEngine.TryParseUtc(start, out var startUtc), $"startTimeUtc 無法解析:{start}");
        Assert.InRange(startUtc, before.AddSeconds(-1), after);

        var states = RecordReader.ReadStates(recordPath).ToList();
        Assert.NotEmpty(states);
        Assert.All(states, s => Assert.False(string.IsNullOrEmpty(s.Utc), $"t={s.T} 的 state 無 utc"));
        var first = states[0];
        Assert.Equal(startUtc.AddSeconds(first.T).ToString(SimulationEngine.UtcFormat, CultureInfo.InvariantCulture), first.Utc);
        File.Delete(recordPath);
    }
}
