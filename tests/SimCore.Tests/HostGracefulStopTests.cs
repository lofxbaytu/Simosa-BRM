using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Physics;
using SimosaBRM.SimCore.Recording;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 主控程序層級的優雅停止(交接紀錄待辦第 1 項):以本測試所用的 dotnet 啟動建置好的 SimosaBRM.SimCore.Host.dll(不設 --duration),
/// (1) 寫停止檔 → 核心自行結束並寫完紀錄 footer(雜湊鏈完整、可重播);(2) WebSocket 送 sessionInfo 與 shutdown → 收到 ack 後 ≤ 1 s 內程序結束,
/// 紀錄含 session 行、exerciseEnded 事件、定稿 summary 與 footer。
/// </summary>
public class HostGracefulStopTests
{
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

    private static (Process Process, Task<string> Stdout, Task<string> Stderr) StartHost(string hostDll, params string[] args)
    {
        var psi = new ProcessStartInfo(DotnetExe())
        {
            WorkingDirectory = TestData.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(hostDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        return (p, p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static Func<Ship.ShipParticulars, LoadingCondition, IShipDynamics> Placeholder => (s, l) => new PlaceholderDynamics(s, l);

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    [Fact]
    public void StopFile_EndsHostAndRecordHasFooterWithIntactHashChain()
    {
        var hostDll = HostDll();
        if (hostDll is null)
        {
            Console.WriteLine("HostGracefulStopTests:找不到 SimosaBRM.SimCore.Host.dll,略過(請先 dotnet build SimosaBRM.sln)");
            return;
        }
        var dir = TestData.TestRecordsDirectory;
        var recordPath = Path.Combine(dir, $"host-stopfile-{Guid.NewGuid():N}.jsonl");
        var stopFile = Path.Combine(dir, $"stop-{Guid.NewGuid():N}", "simcore.stop");
        var (p, stdout, stderr) = StartHost(hostDll, "--scenario", "data/scenarios/E05_pilot_traffic.yaml", "--record", recordPath, "--stop-file", stopFile,
            "--no-ws", "--no-udp", "--no-nmea", "--dynamics", "placeholder");
        try
        {
            // 等紀錄檔出現並錄一些狀態後寫停止檔(不設 --duration:只有停止檔能讓它結束)
            var sw = Stopwatch.StartNew();
            while (!File.Exists(recordPath) && sw.Elapsed < TimeSpan.FromSeconds(60) && !p.HasExited) Thread.Sleep(100);
            Assert.True(File.Exists(recordPath), $"主控程序未開始紀錄\n{(p.HasExited ? stdout.Result + stderr.Result : "")}");
            Thread.Sleep(1500);
            Assert.False(p.HasExited, "主控程序不應在寫停止檔前結束");
            File.WriteAllText(stopFile, "");
            var stopAt = Stopwatch.StartNew();
            Assert.True(p.WaitForExit(20_000), "寫停止檔後主控程序 20 s 內未結束");
            Assert.Equal(0, p.ExitCode);
            var output = stdout.Result;
            Assert.Contains("收到停止請求(stopFile)", output);
            Assert.Contains("紀錄已關閉", output);
            Assert.Contains("結束(原因:stopFile)", output);
            Assert.False(File.Exists(stopFile), "核心應刪除已消化的停止檔");
            Console.WriteLine($"停止檔 → 結束耗時 {stopAt.Elapsed.TotalMilliseconds:F0} ms");

            var rec = RecordReader.Read(recordPath);
            Assert.NotNull(rec.Footer);
            Assert.True(rec.IntegrityOk, "footer sha256 應與內容一致");
            Assert.True(rec.Footer!.FinalTick > 0);
            Assert.NotNull(rec.Footer.ClosedUtc);
            Assert.True(rec.StateLines > 0);
            var replay = Replayer.Replay(recordPath, TestData.Ship, Placeholder);
            Assert.True(replay.IntegrityOk);
            Assert.True(replay.HashMatches, $"{replay.ExpectedHash} vs {replay.ActualHash}");
            Assert.True(replay.EventSequenceMatches, replay.FirstEventMismatch);
            File.Delete(recordPath);
        }
        finally { Kill(p); }
    }

    [Fact]
    public async Task ShutdownCommand_AcksThenExitsWithinOneSecond_RecordHasSessionLineAndFooter()
    {
        var hostDll = HostDll();
        if (hostDll is null)
        {
            Console.WriteLine("HostGracefulStopTests:找不到 SimosaBRM.SimCore.Host.dll,略過(請先 dotnet build SimosaBRM.sln)");
            return;
        }
        var dir = TestData.TestRecordsDirectory;
        var recordPath = Path.Combine(dir, $"host-shutdown-{Guid.NewGuid():N}.jsonl");
        var port = FreePort();
        var (p, stdout, stderr) = StartHost(hostDll, "--scenario", "data/scenarios/E01_baseline.yaml", "--record", recordPath, "--port", port.ToString(),
            "--no-udp", "--no-nmea", "--no-stop-file", "--dynamics", "placeholder");
        try
        {
            // 等 WebSocket 埠開啟
            var sw = Stopwatch.StartNew();
            var open = false;
            while (!open && sw.Elapsed < TimeSpan.FromSeconds(60) && !p.HasExited)
            {
                try { using var tc = new TcpClient(); await tc.ConnectAsync(IPAddress.Loopback, port); open = tc.Connected; }
                catch (SocketException) { await Task.Delay(200); }
            }
            Assert.True(open, $"主控程序未開啟埠 {port}\n{(p.HasExited ? stdout.Result + stderr.Result : "")}");

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
            var sessionInfo = SimCommand.SessionInfo(new
            {
                participants = new object[] { new { name = "王大副", role = "oow", rank = "大副" }, new { name = "李舵工", role = "helmsman" } },
                restHoursConfirmation = new { confirmedBy = "船長", confirmedAtUtc = "2026-10-08T01:30:00Z", compliant = true },
                course = new { id = "BRM-2026-03" },
            });
            await SendAsync(ws, sessionInfo.ToJson());
            var ack1 = await ReceiveAckAsync(ws, TimeSpan.FromSeconds(15));
            Assert.Equal("sessionInfo", ack1.GetProperty("command").GetString());
            Assert.True(ack1.GetProperty("ok").GetBoolean(), ack1.ToString());
            Assert.Contains("session 行", ack1.GetProperty("detail").GetString());

            await Task.Delay(500); // 再錄一點狀態
            await SendAsync(ws, SimCommand.Shutdown("test").ToJson());
            var ack2 = await ReceiveAckAsync(ws, TimeSpan.FromSeconds(15));
            var acked = Stopwatch.StartNew();
            Assert.Equal("shutdown", ack2.GetProperty("command").GetString());
            Assert.True(ack2.GetProperty("ok").GetBoolean(), ack2.ToString());
            Assert.True(p.WaitForExit(5000), "shutdown ack 後主控程序 5 s 內未結束");
            var elapsed = acked.Elapsed;
            Console.WriteLine($"shutdown ack → 程序結束耗時 {elapsed.TotalMilliseconds:F0} ms");
            Assert.True(elapsed <= TimeSpan.FromSeconds(1.5), $"ack 後應 ≤ 1 s 內結束(實際 {elapsed.TotalMilliseconds:F0} ms)");
            Assert.Equal(0, p.ExitCode);
            var output = stdout.Result;
            Assert.Contains("收到 shutdown 指令", output);
            Assert.Contains("結束(原因:shutdown)", output);

            var rec = RecordReader.Read(recordPath);
            Assert.True(rec.IntegrityOk);
            Assert.NotNull(rec.Footer?.ClosedUtc);
            Assert.Equal("session", rec.SessionInfoSource);
            Assert.Equal("王大副", rec.SessionInfo!.Participants[0].Name);
            Assert.Contains(rec.Events, e => e.Event.Kind == SimEventKinds.ExerciseEnded);
            Assert.True(rec.Summary?.Summary.Final, "shutdown 應先結束練習並定稿 summary");
            Assert.Contains(rec.Inputs, i => i.Command.Type == SimCommandType.EndExercise);
            Assert.DoesNotContain(rec.Inputs, i => i.Command.Type is SimCommandType.SessionInfo or SimCommandType.Shutdown);
            var replay = Replayer.Replay(recordPath, TestData.Ship, Placeholder);
            Assert.True(replay.HashMatches, $"{replay.ExpectedHash} vs {replay.ActualHash}");
            Assert.True(replay.EventSequenceMatches, replay.FirstEventMismatch);
            File.Delete(recordPath);
        }
        finally { Kill(p); }
    }

    private static Task SendAsync(ClientWebSocket ws, string json)
        => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>讀框直到 {"type":"ack"}(狀態幀與事件框略過)。</summary>
    private static async Task<JsonElement> ReceiveAckAsync(ClientWebSocket ws, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[256 * 1024];
        while (true)
        {
            var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, cts.Token);
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            using var doc = JsonDocument.Parse(ms.ToArray());
            if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ack") return doc.RootElement.Clone();
        }
    }
}
