using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Host;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 埠被占用時的優雅降級(交接紀錄第二十四輪待辦第 1 項;HttpListenerStarter):
/// (a) 以 TcpListener 佔住 0.0.0.0:埠後直接建構三個伺服器類別 → TryStart 回 false 並回報原因、Start 拋 HttpListenerException、
///     DisposeAsync 不拋例外且可重複呼叫;釋放埠後同一埠可再啟動(失敗的嘗試不會在 HttpListener 的靜態端點表留下殘骸);
/// (b) 以子程序啟動建置好的 Host(比照 HostGracefulStopTests):--vhf-port / --media-port 被占 → 結束碼 0、stdout 有「未啟動」與「核心照常執行」、
///     紀錄有 footer;--port 被占 → 結束碼 2、stdout 有訊息、紀錄仍寫完 footer、stderr 無未處理例外。
/// </summary>
public class PortInUseTests
{
    /// <summary>佔住 0.0.0.0:埠(萬用前綴與 localhost/127.0.0.1 後援都綁不上;Windows http.sys 同樣回 ERROR_SHARING_VIOLATION)。</summary>
    private static TcpListener Hold(out int port)
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        return l;
    }

    private static SimulationEngine Engine() => new(TestData.Fsb1(), TestData.Baseline());

    // ---------------------------------------------------------------- (a) 直接建構

    [Fact]
    public async Task WebSocketStateServer_PortInUse_TryStartReportsAndDisposeIsSafe()
    {
        var hold = Hold(out var port);
        try
        {
            var ws = new WebSocketStateServer(Engine(), port);
            Assert.False(ws.TryStart(out var error));
            Assert.NotNull(error);
            Assert.Contains(port.ToString(), error);
            Assert.Equal(error, ws.StartError);
            Assert.False(ws.Started);
            Assert.Null(ws.Prefix);
            await ws.DisposeAsync();
            await ws.DisposeAsync(); // 可重複呼叫
            // Start() 版本:明確的 HttpListenerException(訊息含埠),之後仍可安全釋放
            var ws2 = new WebSocketStateServer(Engine(), port);
            var ex = Assert.Throws<HttpListenerException>(ws2.Start);
            Assert.Contains(port.ToString(), ex.Message);
            await ws2.DisposeAsync();
        }
        finally { hold.Stop(); }
    }

    [Fact]
    public async Task MediaUploadServer_PortInUse_TryStartReportsAndDisposeIsSafe()
    {
        var hold = Hold(out var port);
        var dir = Path.Combine(TestData.TestRecordsDirectory, $"media-portinuse-{Guid.NewGuid():N}");
        try
        {
            var media = new MediaUploadServer(dir, port) { MinFreeBytes = 0 };
            Assert.False(media.TryStart(out var error));
            Assert.NotNull(error);
            Assert.Contains(port.ToString(), error);
            Assert.Equal(error, media.StartError);
            Assert.False(media.Started);
            Assert.Null(media.Prefix);
            await media.DisposeAsync();
            await media.DisposeAsync();
            var media2 = new MediaUploadServer(dir, port) { MinFreeBytes = 0 };
            var ex = Assert.Throws<HttpListenerException>(media2.Start);
            Assert.Contains(port.ToString(), ex.Message);
            await media2.DisposeAsync();
        }
        finally
        {
            hold.Stop();
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task VhfRelayServer_PortInUse_TryStartReportsAndDisposeIsSafe()
    {
        var hold = Hold(out var port);
        try
        {
            var vhf = new VhfRelayServer(port);
            Assert.False(vhf.TryStart(out var error));
            Assert.NotNull(error);
            Assert.Contains(port.ToString(), error);
            Assert.Equal(error, vhf.StartError);
            Assert.False(vhf.Started);
            Assert.Null(vhf.Prefix);
            await vhf.DisposeAsync();
            await vhf.DisposeAsync();
            var vhf2 = new VhfRelayServer(port);
            var ex = Assert.Throws<HttpListenerException>(vhf2.Start);
            Assert.Contains(port.ToString(), ex.Message);
            await vhf2.DisposeAsync();
        }
        finally { hold.Stop(); }
    }

    [Fact]
    public async Task FailedStart_DoesNotPoisonPort_ServersStartOnSamePortAfterRelease()
    {
        // 失敗的嘗試(含 localhost/127.0.0.1 後援,其中一個前綴可能綁上又被收回)不得在 HttpListener 的靜態端點表留下已綁定的 socket:
        // 釋放埠後,三種伺服器都要能在同一埠正常啟動
        var hold = Hold(out var port);
        var ws = new WebSocketStateServer(Engine(), port);
        var vhf = new VhfRelayServer(port);
        var dir = Path.Combine(TestData.TestRecordsDirectory, $"media-portreuse-{Guid.NewGuid():N}");
        var media = new MediaUploadServer(dir, port) { MinFreeBytes = 0 };
        try
        {
            Assert.False(ws.TryStart(out _));
            Assert.False(vhf.TryStart(out _));
            Assert.False(media.TryStart(out _));
        }
        finally
        {
            await ws.DisposeAsync();
            await vhf.DisposeAsync();
            await media.DisposeAsync();
            hold.Stop();
        }
        await using var ws2 = new WebSocketStateServer(Engine(), port);
        Assert.True(ws2.TryStart(out var wsError), wsError);
        Assert.True(ws2.Started);
        Assert.NotNull(ws2.Prefix);
        Assert.Null(ws2.StartError);
        await ws2.DisposeAsync();
        await using var vhf2 = new VhfRelayServer(port);
        Assert.True(vhf2.TryStart(out var vhfError), vhfError);
        await vhf2.DisposeAsync();
        await using var media2 = new MediaUploadServer(dir, port) { MinFreeBytes = 0 };
        Assert.True(media2.TryStart(out var mediaError), mediaError);
        await media2.DisposeAsync();
        try { Directory.Delete(dir, true); } catch (Exception) { }
    }

    [Fact]
    public async Task StartTwice_Throws_AndStartAfterDispose_Throws()
    {
        await using var vhf = new VhfRelayServer(FreePort());
        Assert.True(vhf.TryStart(out var error), error);
        Assert.Throws<InvalidOperationException>(() => vhf.TryStart(out _));
        await vhf.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => vhf.TryStart(out _));
    }

    // ---------------------------------------------------------------- (b) 子程序

    private static string? HostDll()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var config = new DirectoryInfo(baseDir).Parent?.Name ?? "Release";
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

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed record HostRun(int ExitCode, string Stdout, string Stderr);

    /// <summary>啟動 Host(E01、暫代模型、--duration 1、紀錄到 recordPath)並等它自己結束(最多 90 s;逾時則殺掉並失敗)。</summary>
    private static HostRun RunHost(string hostDll, string recordPath, params string[] extra)
    {
        var psi = new ProcessStartInfo(DotnetExe())
        {
            WorkingDirectory = TestData.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(hostDll);
        foreach (var a in new[] { "--scenario", "data/scenarios/E01_baseline.yaml", "--record", recordPath, "--no-udp", "--no-nmea", "--no-stop-file", "--dynamics", "placeholder", "--duration", "1" })
            psi.ArgumentList.Add(a);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(90_000))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
            Assert.Fail($"主控程序 90 s 內未結束\n{stdout.Result}\n{stderr.Result}");
        }
        return new HostRun(p.ExitCode, stdout.Result, stderr.Result);
    }

    private static void AssertFooter(string recordPath)
    {
        Assert.True(File.Exists(recordPath), "應有紀錄檔");
        var rec = RecordReader.Read(recordPath);
        Assert.NotNull(rec.Footer);
        Assert.True(rec.IntegrityOk, "footer sha256 應與內容一致");
        File.Delete(recordPath);
    }

    [Fact]
    public void Host_VhfPortInUse_ContinuesWithoutVhf_ExitZero_RecordHasFooter()
    {
        var hostDll = HostDll();
        if (hostDll is null)
        {
            Console.WriteLine("PortInUseTests:找不到 SimosaBRM.SimCore.Host.dll,略過(請先 dotnet build SimosaBRM.sln)");
            return;
        }
        var hold = Hold(out var vhfPort);
        var recordPath = Path.Combine(TestData.TestRecordsDirectory, $"host-portinuse-vhf-{Guid.NewGuid():N}.jsonl");
        HostRun run;
        try { run = RunHost(hostDll, recordPath, "--port", FreePort().ToString(), "--vhf-port", vhfPort.ToString(), "--no-media"); }
        finally { hold.Stop(); }
        Assert.True(run.ExitCode == 0, $"結束碼 {run.ExitCode}\n{run.Stdout}\n{run.Stderr}");
        Assert.Contains($"埠 {vhfPort} 被占用或無法監聽", run.Stdout);
        Assert.Contains("VHF 角色扮演未啟動", run.Stdout);
        Assert.Contains("核心照常執行", run.Stdout);
        Assert.Contains("  WebSocket:ws://", run.Stdout);
        Assert.Contains("紀錄已關閉", run.Stdout);
        Assert.Contains("結束(原因:duration)", run.Stdout);
        Assert.DoesNotContain("Unhandled exception", run.Stderr);
        Assert.DoesNotContain("Unhandled exception", run.Stdout);
        AssertFooter(recordPath);
    }

    [Fact]
    public void Host_MediaPortInUse_ContinuesWithoutRecording_ExitZero_RecordHasFooter()
    {
        var hostDll = HostDll();
        if (hostDll is null)
        {
            Console.WriteLine("PortInUseTests:找不到 SimosaBRM.SimCore.Host.dll,略過(請先 dotnet build SimosaBRM.sln)");
            return;
        }
        var hold = Hold(out var mediaPort);
        var recordPath = Path.Combine(TestData.TestRecordsDirectory, $"host-portinuse-media-{Guid.NewGuid():N}.jsonl");
        HostRun run;
        try { run = RunHost(hostDll, recordPath, "--port", FreePort().ToString(), "--media-port", mediaPort.ToString(), "--no-vhf"); }
        finally { hold.Stop(); }
        Assert.True(run.ExitCode == 0, $"結束碼 {run.ExitCode}\n{run.Stdout}\n{run.Stderr}");
        Assert.Contains($"埠 {mediaPort} 被占用或無法監聽", run.Stdout);
        Assert.Contains("錄影上傳未啟動", run.Stdout);
        Assert.Contains("核心照常執行", run.Stdout);
        Assert.Contains("  WebSocket:ws://", run.Stdout);
        Assert.Contains("紀錄已關閉", run.Stdout);
        Assert.DoesNotContain("Unhandled exception", run.Stderr);
        Assert.DoesNotContain("Unhandled exception", run.Stdout);
        AssertFooter(recordPath);
    }

    [Fact]
    public void Host_StatePortInUse_ExitsWithCode2_MessageAndFooter_NoUnhandledException()
    {
        var hostDll = HostDll();
        if (hostDll is null)
        {
            Console.WriteLine("PortInUseTests:找不到 SimosaBRM.SimCore.Host.dll,略過(請先 dotnet build SimosaBRM.sln)");
            return;
        }
        var hold = Hold(out var wsPort);
        var recordPath = Path.Combine(TestData.TestRecordsDirectory, $"host-portinuse-ws-{Guid.NewGuid():N}.jsonl");
        HostRun run;
        try { run = RunHost(hostDll, recordPath, "--port", wsPort.ToString(), "--no-vhf", "--no-media"); }
        finally { hold.Stop(); }
        Assert.True(run.ExitCode == 2, $"結束碼 {run.ExitCode}(應為 2)\n{run.Stdout}\n{run.Stderr}");
        Assert.Contains($"埠 {wsPort} 被占用或無法監聽", run.Stdout);
        Assert.Contains("狀態 WebSocket 未啟動", run.Stdout);
        Assert.Contains("結束碼 2", run.Stdout);
        Assert.Contains("紀錄已關閉", run.Stdout);
        Assert.Contains("結束(原因:portInUse)", run.Stdout);
        Assert.DoesNotContain("執行中(", run.Stdout); // 未進入主迴圈
        Assert.DoesNotContain("Unhandled exception", run.Stderr);
        Assert.DoesNotContain("Unhandled exception", run.Stdout);
        Assert.Contains($"埠 {wsPort} 被占用或無法監聽", run.Stderr);
        AssertFooter(recordPath);
    }
}
