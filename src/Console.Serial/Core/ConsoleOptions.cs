using System.Globalization;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.ConsoleSerial.Core;

/// <summary>解析後的完整路徑(null = 不使用:--log - 或沒有 loopback)。</summary>
public sealed record ResolvedFiles(string? Log, string? LoopbackIn, string? LoopbackOut);

/// <summary>命令列參數(主程式與 tools/launch/start-ship-laptop.* 共用的契約)。</summary>
public sealed class ConsoleOptions
{
    public const string Usage = """
        SimosaBRM.Console.Serial — USB 實體操控台服務(協定 src/Contracts/console-serial.md)
        用法:dotnet SimosaBRM.Console.Serial.dll (--port <COM3|/dev/ttyACM0> | --loopback <讀取路徑>[,<寫入路徑>]) [選項]
          --port <埠>            序列埠(Windows COM5、Linux /dev/ttyACM0;System.IO.Ports,115200 8N1)
          --baud <鮑率>          預設 115200(USB CDC 下僅為形式)
          --loopback <讀>[,<寫>]  測試用假裝置:自檔案/具名管道讀裝置行(讀到檔尾持續等待新行;讀取檔不存在時建立),主機行寫到另一個檔案/管道
                                 (省略寫入路徑 = 不回寫);與 --port 擇一
          --ws <url>             核心狀態/指令 WebSocket,預設 ws://127.0.0.1:8765
          --hb-timeout <秒>      心跳逾時(無任何行即視為操控台斷線),預設 3
          --log <路徑|->         log 檔(追加),預設 <根目錄>/build/logs/console.log;- 只印到主控台
          --duration <秒>        牆鐘秒數到達後結束(煙霧測試);預設持續執行到 Ctrl+C / SIGINT / SIGTERM
          --help
        路徑(--log、--loopback):相對路徑以根目錄為準 — 專案根目錄(Directory.Build.props + data/,或環境變數 SIMOSA_BRM_ROOT)
          或安裝根目錄(本程式所在位置往上同時含 data/ 與 app/ 的目錄);兩者都找不到時才以目前目錄為準。
          本機資料守則:位置在根目錄外而且位於 Windows 系統磁碟(C:)時拒絕(結束碼 2);
          經訓練負責人同意後設環境變數 SIMOSA_ALLOW_SYSTEM_DRIVE=1 才照做(docs/ops/本機資料位置.md 第 4 節)。
        """;

    public string? Port { get; set; }
    public int Baud { get; set; } = 115200;
    public string? LoopbackIn { get; set; }
    public string? LoopbackOut { get; set; }
    public string WsUrl { get; set; } = "ws://127.0.0.1:8765";
    public double HeartbeatTimeoutS { get; set; } = 3.0;
    /// <summary>--log(相對路徑以根目錄為準,見 <see cref="ResolveFiles"/>;"-" = 只印主控台)</summary>
    public string LogPath { get; set; } = Path.Combine("build", "logs", "console.log");
    public double? DurationS { get; set; }
    public bool Help { get; set; }

    public bool Loopback => LoopbackIn is not null;

    /// <summary>
    /// 根目錄:專案根目錄(<see cref="RepositoryPaths.TryFindRoot"/>:SIMOSA_BRM_ROOT 或 Directory.Build.props + data/)→
    /// 安裝根目錄(本程式所在位置往上同時含 data/ 與 app/;安裝版單獨執行時)→ null(找不到;相對路徑以目前目錄為準)。
    /// </summary>
    public static string? FindRoot()
        => RepositoryPaths.TryFindRoot() ?? RepositoryPaths.FindInstalledRoot(AppContext.BaseDirectory);

    /// <summary>
    /// 解析寫入用的路徑(log、loopback 讀/寫檔 — 讀取檔不存在時會被建立,所以也算寫入位置):相對路徑以 <paramref name="root"/> 為準
    /// (null 時以 <paramref name="currentDirectory"/>,預設目前目錄);在根目錄外而且位於 Windows 系統磁碟時丟 <see cref="LocalDataRejectedException"/>,
    /// 設了 SIMOSA_ALLOW_SYSTEM_DRIVE=1 則照做並以 <paramref name="notice"/> 提醒。
    /// </summary>
    public ResolvedFiles ResolveFiles(string? root, LocalDataEnvironment env, Action<string>? notice = null, string? currentDirectory = null)
    {
        var baseDir = root ?? currentDirectory ?? Directory.GetCurrentDirectory();
        var roots = new[] { root };
        string Req(string what, string p) => LocalDataGuard.Require(what, p, baseDir, roots, env, notice);
        return new ResolvedFiles(
            LogPath == "-" ? null : Req("--log", LogPath),
            LoopbackIn is null ? null : Req("--loopback(讀取檔)", LoopbackIn),
            LoopbackOut is null ? null : Req("--loopback(寫入檔)", LoopbackOut));
    }

    public static ConsoleOptions Parse(string[] args)
    {
        var o = new ConsoleOptions();
        string Next(int i, string name) => i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{name} 需要值");
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": o.Port = Next(i, "--port"); i++; break;
                case "--baud":
                    if (!int.TryParse(Next(i, "--baud"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) || b <= 0) throw new ArgumentException($"--baud 須為正整數:{args[i + 1]}");
                    o.Baud = b; i++; break;
                case "--loopback":
                {
                    var spec = Next(i, "--loopback"); i++;
                    var parts = spec.Split(',', 2, StringSplitOptions.TrimEntries);
                    if (parts[0].Length == 0) throw new ArgumentException("--loopback 需要讀取路徑");
                    o.LoopbackIn = parts[0];
                    o.LoopbackOut = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                    break;
                }
                case "--ws": o.WsUrl = Next(i, "--ws"); i++; break;
                case "--hb-timeout":
                    if (!double.TryParse(Next(i, "--hb-timeout"), NumberStyles.Float, CultureInfo.InvariantCulture, out var hb) || hb <= 0) throw new ArgumentException($"--hb-timeout 須為正數秒:{args[i + 1]}");
                    o.HeartbeatTimeoutS = hb; i++; break;
                case "--log": o.LogPath = Next(i, "--log"); i++; break;
                case "--duration":
                    if (!double.TryParse(Next(i, "--duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0) throw new ArgumentException($"--duration 須為正數秒:{args[i + 1]}");
                    o.DurationS = d; i++; break;
                case "-h" or "--help" or "/?": o.Help = true; break;
                default: throw new ArgumentException($"未知參數:{args[i]}(--help 列出用法)");
            }
        }
        if (o.Help) return o;
        if (o.Port is null && o.LoopbackIn is null) throw new ArgumentException("需要 --port <序列埠> 或 --loopback <讀取路徑>[,<寫入路徑>]");
        if (o.Port is not null && o.LoopbackIn is not null) throw new ArgumentException("--port 與 --loopback 只能擇一");
        if (!o.WsUrl.Contains("://", StringComparison.Ordinal)) o.WsUrl = "ws://" + o.WsUrl;
        var uri = new Uri(o.WsUrl);
        if (uri.Scheme is not ("ws" or "wss")) throw new ArgumentException($"--ws 須為 ws:// 或 wss://:{o.WsUrl}");
        return o;
    }
}
