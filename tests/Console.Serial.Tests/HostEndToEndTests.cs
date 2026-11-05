using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SimosaBRM.ConsoleSerial.Bridge;
using SimosaBRM.ConsoleSerial.Core;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>
/// 端對端(比照 SimCore.Tests/HostGracefulStopTests 的啟動方式):以 dotnet 啟動建置好的 SimosaBRM.SimCore.Host.dll(E05、--no-udp --no-nmea --no-vhf --no-media),
/// 裝置端為記憶體管線(AnonymousPipe:測試寫入裝置行、讀取主機回寫行),<see cref="ConsoleBridge"/> + 真 <see cref="CoreLink"/> 轉送到核心;
/// 送 HELLO → 收 ACK 與 LED/DISP;送 RUD,20 與 TEL,SAH/HAH → 核心狀態幀 rudderOrder/telegraph 改變,且 DISP,TEL 回寫跟著變。
/// </summary>
public class HostEndToEndTests
{
    private static readonly string Root = RepositoryPaths.FindRoot();

    private static string? HostDll()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var config = new DirectoryInfo(baseDir).Parent?.Name ?? "Release";
        return new[] { config, "Release", "Debug" }
            .Select(c => Path.Combine(Root, "build", "bin", "SimCore.Host", c, "net8.0", "SimosaBRM.SimCore.Host.dll"))
            .FirstOrDefault(File.Exists);
    }

    private static string DotnetExe()
    {
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return self;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
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

    private static async Task<bool> WaitPortAsync(int port, Process p, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !p.HasExited)
        {
            try { using var tc = new TcpClient(); await tc.ConnectAsync(IPAddress.Loopback, port); if (tc.Connected) return true; }
            catch (SocketException) { await Task.Delay(200); }
        }
        return false;
    }

    private static Task<T> WaitUntilAsync<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class => WaitUntilAsync(probe, timeout, () => what);

    private static async Task<T> WaitUntilAsync<T>(Func<T?> probe, TimeSpan timeout, Func<string> what) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (probe() is { } v) return v;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"逾時 {timeout.TotalSeconds:0} s:{what()}");
    }

    [Fact]
    public async Task Loopback_Hello_Rudder_Telegraph_ReachCoreAndFeedbackReturns()
    {
        var hostDll = HostDll();
        if (hostDll is null)
        {
            Console.WriteLine("HostEndToEndTests:找不到 SimosaBRM.SimCore.Host.dll,略過(請先 dotnet build SimosaBRM.sln)");
            return;
        }
        var port = FreePort();
        var psi = new ProcessStartInfo(DotnetExe())
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var a in new[] { hostDll, "--scenario", "data/scenarios/E05_pilot_traffic.yaml", "--port", port.ToString(), "--no-udp", "--no-nmea", "--no-vhf", "--no-media", "--no-stop-file", "--dynamics", "placeholder" })
            psi.ArgumentList.Add(a);
        var host = Process.Start(psi)!;
        var stdout = host.StandardOutput.ReadToEndAsync();
        var stderr = host.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(await WaitPortAsync(port, host, TimeSpan.FromSeconds(60)), $"主控程序未開啟埠 {port}\n{(host.HasExited ? await stdout + await stderr : "")}");

            // 記憶體管線裝置:toService(測試寫 → 服務讀)、fromService(服務寫 → 測試讀)
            using var toServiceW = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            using var toServiceR = new AnonymousPipeClientStream(PipeDirection.In, toServiceW.ClientSafePipeHandle);
            using var fromServiceW = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            using var fromServiceR = new AnonymousPipeClientStream(PipeDirection.In, fromServiceW.ClientSafePipeHandle);
            var device = new StreamDeviceLink(toServiceR, fromServiceW, "記憶體管線", pollAtEof: false);
            var session = new ConsoleSession(heartbeatTimeoutMs: 30000); // Windows CI 的 Host 啟動較慢,預設 3 s 會先逾時一次
            var core = new CoreLink(new Uri($"ws://127.0.0.1:{port}/"));
            var logs = new List<string>();
            var frames = new List<ConsoleStateView>();
            var sentLines = new List<string>();
            var sentCommands = new List<SimCommand>();
            core.FrameReceived += f => { if (ConsoleStateView.TryParse(f) is { } v) lock (frames) frames.Add(v); };
            core.Log += m => { lock (logs) logs.Add($"[core] {m}"); };
            var bridge = new ConsoleBridge(device, core, session, m => { lock (logs) logs.Add(m); });
            bridge.DeviceLineSent += l => { lock (sentLines) sentLines.Add(l); };
            bridge.CommandSent += c => { lock (sentCommands) sentCommands.Add(c); };
            using var cts = new CancellationTokenSource();
            var run = bridge.RunAsync(cts.Token);
            var deviceWriter = new StreamWriter(toServiceW, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var deviceReader = new StreamReader(fromServiceR, Encoding.ASCII);
            var received = new List<string>();
            var readTask = Task.Run(async () =>
            {
                while (await deviceReader.ReadLineAsync() is { } l) lock (received) received.Add(l);
            });
            string? Received(Func<string, bool> pred) { lock (received) return received.FirstOrDefault(pred); }

            // 等核心連線後握手(CS:之後每行帶校驗和,主機回寫亦帶)
            string Diag() { lock (logs) return string.Join("\n", logs.TakeLast(12)); }
            await WaitUntilAsync(() => core.Connected ? "ok" : null, TimeSpan.FromSeconds(60), () => $"核心 WebSocket 連線(Host 已開埠 {port};log:\n{Diag()}\nHost stdout:\n{(host.HasExited ? stdout.Result : "(執行中)")})");
            await deviceWriter.WriteLineAsync(LineChecksum.Append("HELLO,1.0.0,RUD+TEL+BTN+NFU+LED+DISP+BUZZ+CS"));
            var ack = await WaitUntilAsync(() => Received(l => l.StartsWith("ACK")), TimeSpan.FromSeconds(10), "ACK");
            Assert.Equal(LineChecksum.Append("ACK"), ack);
            await WaitUntilAsync(() => Received(l => l == LineChecksum.Append("LED,LINK,1")), TimeSpan.FromSeconds(10), "LED,LINK,1");
            await WaitUntilAsync(() => Received(l => l == LineChecksum.Append("DISP,TEL,HAH")), TimeSpan.FromSeconds(10), "DISP,TEL,HAH(E05 初始 HAH)");
            await WaitUntilAsync(() => Received(l => l.StartsWith("DISP,HDG,")), TimeSpan.FromSeconds(10), "DISP,HDG");
            Assert.NotNull(Received(l => l == LineChecksum.Append("LED,FU,1")));

            // RUD,20 → 核心 rudderOrder 20
            await deviceWriter.WriteLineAsync(LineChecksum.Append("RUD,20"));
            await WaitUntilAsync(() => { lock (frames) return frames.LastOrDefault(f => Math.Abs(RudderOrderOf(f) - 20.0) < 1e-6); }, TimeSpan.FromSeconds(15), "state.rudderOrder = 20");
            // TEL,SAH → telegraph SAH;TEL,HAH → 回 HAH;DISP,TEL 跟著變
            await deviceWriter.WriteLineAsync(LineChecksum.Append("TEL,SAH"));
            await WaitUntilAsync(() => { lock (frames) return frames.LastOrDefault(f => f.Telegraph == "SAH"); }, TimeSpan.FromSeconds(15), "state.telegraph = SAH");
            await WaitUntilAsync(() => Received(l => l == LineChecksum.Append("DISP,TEL,SAH")), TimeSpan.FromSeconds(10), "DISP,TEL,SAH");
            await deviceWriter.WriteLineAsync(LineChecksum.Append("TEL,HAH"));
            await WaitUntilAsync(() => { lock (frames) return frames.LastOrDefault(f => f.Telegraph == "HAH" && f.Tick > 0); }, TimeSpan.FromSeconds(15), "state.telegraph = HAH");
            // 舵機追隨舵令:DISP,RUD 值朝 20 增加
            await WaitUntilAsync(() => Received(l => l.StartsWith("DISP,RUD,") && double.Parse(l.Split(',')[2].Split('*')[0], System.Globalization.CultureInfo.InvariantCulture) >= 2.0), TimeSpan.FromSeconds(20), "DISP,RUD ≥ 2.0(舵隨舵令轉動)");
            // 缺校驗和的行被拒絕且不中斷
            await deviceWriter.WriteLineAsync("RUD,-10");
            await WaitUntilAsync(() => session.Stats.ChecksumMissing > 0 ? "ok" : null, TimeSpan.FromSeconds(5), "checksumMissing 計數");
            lock (sentCommands)
            {
                Assert.Contains(sentCommands, c => c.Type == SimCommandType.Rudder && c.ValueAsDouble() == 20.0);
                Assert.Contains(sentCommands, c => c.Type == SimCommandType.Telegraph && c.ValueAsString() == "SAH");
                Assert.DoesNotContain(sentCommands, c => c.Type == SimCommandType.Rudder && c.ValueAsDouble() == -10.0);
            }
            Assert.True(session.DeviceConnected);
            Assert.Equal(0, session.HeartbeatTimeouts);

            cts.Cancel();
            await run;
            await bridge.DisposeAsync();
            await core.DisposeAsync(); // bridge 不再擁有 core(審查第二十三輪)
            deviceWriter.Dispose();
            await Task.WhenAny(readTask, Task.Delay(2000));
            Console.WriteLine($"HostEndToEndTests:狀態幀 {core.FramesReceived}、裝置行送出 {bridge.LinesOut}、指令 {bridge.CommandsOut}");
        }
        finally
        {
            try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (Exception) { }
        }

        static double RudderOrderOf(ConsoleStateView v) => v.RudderOrder;
    }
}
