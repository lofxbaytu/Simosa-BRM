using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SimosaBRM.ConsoleSerial.Bridge;
using SimosaBRM.ConsoleSerial.Core;
using SimosaBRM.SimCore.Ship;

// USB 實體操控台服務(規劃書第 5.4 節精簡版操控台;協定 src/Contracts/console-serial.md):
// Teensy USB CDC 序列埠(或 --loopback 檔案/具名管道的假裝置)的文字行 ↔ SimCore.Host 的狀態/指令 WebSocket。
// 只做轉換,不改核心、不進狀態雜湊;log 寫 build/logs/console.log(與主控台)。Ctrl+C / SIGINT / SIGTERM 優雅結束(結束碼 0)。
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;
ConsoleOptions options;
try { options = ConsoleOptions.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
if (options.Help)
{
    Console.WriteLine(ConsoleOptions.Usage);
    var names = SerialDeviceLink.PortNames();
    Console.WriteLine(names.Length > 0 ? $"目前的序列埠:{string.Join(", ", names)}" : "目前沒有偵測到序列埠");
    return 0;
}

var root = RepositoryPaths.TryFindRoot() ?? Directory.GetCurrentDirectory();
string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(root, p);

StreamWriter? logFile = null;
if (options.LogPath != "-")
{
    var path = Resolve(options.LogPath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    logFile = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
}
var logGate = new object();
void Log(string message)
{
    var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {message}";
    lock (logGate)
    {
        Console.WriteLine(line);
        logFile?.WriteLine(line);
    }
}

using var cts = new CancellationTokenSource();
var stopReason = "";
void RequestStop(string reason)
{
    if (cts.IsCancellationRequested) return;
    stopReason = reason;
    Log($"收到停止請求({reason})");
    cts.Cancel();
}
Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestStop("Ctrl+C"); };
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; RequestStop("SIGINT"); });
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; RequestStop("SIGTERM"); });
if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => { c.Cancel = true; RequestStop("SIGHUP"); });
    return await RunAsync();
}
return await RunAsync();

async Task<int> RunAsync()
{
    Log($"SimosaBRM.Console.Serial 啟動:核心 {options.WsUrl};{(options.Loopback ? $"loopback 讀 {options.LoopbackIn} 寫 {options.LoopbackOut ?? "(無)"}" : $"序列埠 {options.Port} {options.Baud}")};心跳逾時 {options.HeartbeatTimeoutS:0.#} s;log {(logFile is null ? "(僅主控台)" : Resolve(options.LogPath))}");
    if (options.DurationS is { } d) cts.CancelAfter(TimeSpan.FromSeconds(d));
    var session = new ConsoleSession(options.HeartbeatTimeoutS * 1000.0);
    var core = new CoreLink(new Uri(options.WsUrl));
    core.Log += m => Log($"[核心] {m}");
    var exit = 0;
    try
    {
        // 序列埠:拔線/讀寫失敗時每秒重新開啟,直到停止;loopback:串流結束即結束
        while (!cts.IsCancellationRequested)
        {
            IDeviceLink device;
            try
            {
                device = options.Loopback
                    ? StreamDeviceLink.OpenFiles(Resolve(options.LoopbackIn!), options.LoopbackOut is null ? null : Resolve(options.LoopbackOut))
                    : SerialDeviceLink.Open(options.Port!, options.Baud);
            }
            catch (Exception ex)
            {
                Log($"無法開啟裝置({(options.Loopback ? options.LoopbackIn : options.Port)}):{ex.Message}{(options.Loopback ? "" : $";目前的序列埠:{string.Join(", ", SerialDeviceLink.PortNames())}。每秒重試")}");
                if (options.Loopback) { exit = 1; break; }
                try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
                continue;
            }
            Log($"裝置已開啟:{device.Description}");
            var bridge = new ConsoleBridge(device, core, session, Log);
            try { await bridge.RunAsync(cts.Token); }
            catch (Exception ex) when (!cts.IsCancellationRequested) { Log($"裝置連結錯誤:{ex.Message}{(options.Loopback ? "" : "(每秒重新開啟)")}"); }
            finally { await bridge.DisposeAsync(); } // 解除 session/core 訂閱並釋放裝置;core 於外層 finally 關閉
            if (options.Loopback) { if (!cts.IsCancellationRequested && options.DurationS is null) Log("loopback 串流已結束"); break; }
            if (!cts.IsCancellationRequested) { try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; } }
        }
    }
    finally
    {
        await core.DisposeAsync();
        Log($"結束(原因:{(stopReason.Length > 0 ? stopReason : options.DurationS is not null && cts.IsCancellationRequested ? "duration" : "裝置結束")});行 {session.Stats.Lines}、接受 {session.Stats.Accepted}、拒絕 {session.Stats.Rejected}、指令 {session.CommandsProduced}、心跳逾時 {session.HeartbeatTimeouts} 次、核心送出 {core.CommandsSent}、狀態幀 {core.FramesReceived}");
        logFile?.Dispose();
    }
    return exit;
}
