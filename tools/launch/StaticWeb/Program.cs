// simosa-web:極簡靜態網頁伺服(GET/HEAD、HTTP/1.1、Connection: close)。
// 用途:離線的船上/教官筆電以 http://<直連 IP>:<埠>/ 提供 app/web(Vite 建置輸出,相對路徑資產),供 WebView2 或瀏覽器載入;
// 不做目錄列表、不接受 .. 或絕對路徑、不跟隨符號連結到根目錄之外;每個請求一行紀錄到 stdout(啟動腳本導到 build/logs/)。
// 發行版狀態(--status-file <檔>;docs/design/packaging-sync.md 第 5.1 節):啟動腳本把 `simosa-pkg status --json` 的輸出存成
// <安裝根目錄>/build/run/release-status.json,本程式以固定網址 /_status/release.json 提供「教官站需要的欄位」
// (發行版本、軟體更新包版次、降版資訊、檔案時間)。只讀、Cache-Control: no-store。
// 有 --status-file 而檔案不存在(開發模式、status 逾時或失敗)或不是 status 的輸出時回 **200 與各欄皆為 null 的同一格式**(「沒有發行版狀態」;
// 原因記在 log):教官站每次載入都會取這個網址,回 404 的話瀏覽器每次都記一筆 console error(Failed to load resource),網頁端無法消掉。
// 沒有 --status-file 時回 404(這個伺服器不提供發行版狀態);/_status/ 之下的其他路徑一律 404。
// 網址是固定的(請求內容不影響讀哪個檔),輸出只含白名單欄位,所以不會變成任意讀檔,也不會把 status 的其他內容(裝置、金鑰名稱、環境版次、警告)送出去。
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// 主控台與重導向輸出一律 UTF-8:Windows 預設 OEM 字碼頁會把中文日誌印成「?」(英文系統)或 Big5(繁體中文系統),
// 啟動腳本收下來的 log 檔與其他程式讀取時都以 UTF-8 為準(與 SimCore.Host 相同)
try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
catch (Exception) { /* 無主控台或不支援時維持預設 */ }
var root = Directory.GetCurrentDirectory();
var port = 8080;
var bind = IPAddress.Any;
var quiet = false;
string? statusFile = null;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} 需要參數值");
    switch (args[i])
    {
        case "--root": root = Path.GetFullPath(Next()); break;
        case "--port": port = int.Parse(Next()); break;
        case "--bind": bind = IPAddress.Parse(Next()); break;
        case "--status-file": statusFile = Path.GetFullPath(Next()); break;
        case "--quiet": quiet = true; break;
        case "-h": case "--help":
            Console.WriteLine("用法:simosa-web --root <網頁目錄> [--port 8080] [--bind 0.0.0.0] [--status-file <simosa-pkg status --json 的輸出檔.json>] [--quiet]");
            Console.WriteLine($"  --status-file:以固定網址 {ReleaseStatusFile.Url} 提供該檔的發行版本、軟體更新包版次與降版資訊(只含教官站需要的欄位;檔案不存在或不是 status 的輸出時各欄為 null)");
            return 0;
        default:
            Console.Error.WriteLine($"未知參數:{args[i]}");
            return 2;
    }
}
if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"找不到網頁目錄:{root}");
    return 1;
}
// 狀態檔可以還不存在(開發模式沒有;請求時才讀),但必須是 .json 檔案路徑,不可以是資料夾
if (statusFile is not null && (!Path.GetExtension(statusFile).Equals(".json", StringComparison.OrdinalIgnoreCase) || Directory.Exists(statusFile)))
{
    Console.Error.WriteLine($"--status-file 必須是 .json 檔的路徑(simosa-pkg status --json 的輸出檔):{statusFile}");
    return 2;
}
var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
// 狀態檔不可放在網頁目錄內:那樣它會被當成一般的靜態檔整份送出(status 的完整輸出含裝置、金鑰名稱、環境版次)
if (statusFile is not null && statusFile.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
{
    Console.Error.WriteLine($"--status-file 不可放在網頁目錄 {rootFull} 之內(會被當成一般檔案整份送出):{statusFile}");
    return 2;
}

var listener = new TcpListener(bind, port);
listener.Start();
Console.WriteLine($"simosa-web 提供 {rootFull} 於 http://{(Equals(bind, IPAddress.Any) ? "0.0.0.0" : bind.ToString())}:{port}/(Ctrl+C 結束)");
if (statusFile is not null) Console.WriteLine($"發行版狀態:{statusFile} → {ReleaseStatusFile.Url}");
// 關閉流程:Ctrl+C / SIGINT(Console.CancelKeyPress)與 ProcessExit 都只呼叫 Shutdown();cts 在主迴圈結束後才手動 Dispose,
// 且 Shutdown() 對已取消/已釋放的 cts 不再動作 — 否則 Main 返回時 `using var cts` 已釋放,接著 ProcessExit 再 Cancel() 會以未處理
// ObjectDisposedException 崩潰(exit 134/SIGABRT),每次正常關機都在 build/logs/web.log 留下崩潰堆疊;Windows 上還可能卡在 WER 對話框。
var cts = new CancellationTokenSource();
var shutdownLock = new object();
void Shutdown()
{
    lock (shutdownLock)
    {
        try { if (!cts.IsCancellationRequested) cts.Cancel(); }
        catch (ObjectDisposedException) { }
        try { listener.Stop(); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }
}
Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

try
{
    while (!cts.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(cts.Token); }
        catch (OperationCanceledException) { break; }
        catch (SocketException) when (cts.IsCancellationRequested) { break; }
        _ = Task.Run(() => Serve(client));
    }
}
catch (ObjectDisposedException) { }
Shutdown();
lock (shutdownLock) { cts.Dispose(); }
if (!quiet) Console.WriteLine("simosa-web 結束。");
return 0;

async Task Serve(TcpClient client)
{
    using (client)
    {
        client.ReceiveTimeout = 10_000;
        client.SendTimeout = 30_000;
        var stream = client.GetStream();
        string? requestLine = null;
        try
        {
            // 只讀標頭(最多 16 KiB),請求本體一律忽略
            var buf = new byte[16384];
            var n = 0;
            while (n < buf.Length)
            {
                var read = await stream.ReadAsync(buf.AsMemory(n, buf.Length - n));
                if (read <= 0) break;
                n += read;
                if (IndexOf(buf, n, "\r\n\r\n"u8) >= 0) break;
            }
            if (n == 0) return;
            var text = Encoding.ASCII.GetString(buf, 0, n);
            var eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            requestLine = eol >= 0 ? text[..eol] : text;
            var parts = requestLine.Split(' ');
            if (parts.Length < 3 || (parts[0] != "GET" && parts[0] != "HEAD"))
            {
                await WriteStatus(stream, 405, "Method Not Allowed", "GET/HEAD only\n");
                Log(requestLine, 405);
                return;
            }
            var target = parts[1];
            var q = target.IndexOf('?');
            if (q >= 0) target = target[..q];
            var path = Uri.UnescapeDataString(target);
            if (path.EndsWith('/')) path += "index.html";
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => s is "." or ".." || s.Contains('\\') || s.Contains(':')))
            {
                await WriteStatus(stream, 400, "Bad Request", "bad path\n");
                Log(requestLine, 400);
                return;
            }
            // 發行版狀態:/_status/ 是保留的字首(不分大小寫),不落到網頁目錄;只有固定網址 /_status/release.json 有內容,其餘 404
            if (segments.Length > 0 && segments[0].Equals(ReleaseStatusFile.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (path != ReleaseStatusFile.Url || statusFile is null)
                {
                    await WriteStatus(stream, 404, "Not Found", "not found\n", noStore: true);
                    Log($"{requestLine}({(path != ReleaseStatusFile.Url ? "不是發行版狀態的網址" : "未指定 --status-file")})", 404);
                    return;
                }
                // 狀態檔不存在或不合格:照樣回 200,內容是「沒有發行版狀態」(各欄 null;網頁端不顯示任何東西),原因記在 log
                var json = ReleaseStatusFile.TryBuild(statusFile, out var why) ?? ReleaseStatusFile.Empty();
                var statusHead = $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {json.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(statusHead));
                if (parts[0] == "GET") await stream.WriteAsync(json);
                Log(why.Length == 0 ? requestLine : $"{requestLine}({why};回覆「沒有發行版狀態」)", 200);
                return;
            }
            var file = Path.GetFullPath(Path.Combine(rootFull, Path.Combine(segments)));
            if (!file.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(file))
            {
                await WriteStatus(stream, 404, "Not Found", "not found\n");
                Log(requestLine, 404);
                return;
            }
            var info = new FileInfo(file);
            var head = $"HTTP/1.1 200 OK\r\nContent-Type: {Mime(file)}\r\nContent-Length: {info.Length}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            if (parts[0] == "GET")
            {
                await using var fs = File.OpenRead(file);
                await fs.CopyToAsync(stream);
            }
            Log(requestLine, 200);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
        {
            if (!quiet) Console.WriteLine($"{DateTime.UtcNow:O} {requestLine ?? "(無請求行)"} 連線錯誤:{ex.Message}");
        }
        catch (Exception ex)
        {
            // 其餘例外不可悄悄消失(本方法在 Task.Run 裡,沒有人等它:例外不接住的話連線直接關閉、log 沒有任何一行):
            // 記下來,盡量回 500(標頭可能已送出一部分,寫不進去就算了)
            Log($"{requestLine ?? "(無請求行)"}(內部錯誤:{ex.GetType().Name})", 500);
            try { await WriteStatus(stream, 500, "Internal Server Error", "internal error\n", noStore: true); }
            catch (Exception inner) when (inner is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
        }
    }
}

void Log(string line, int status)
{
    if (!quiet) Console.WriteLine($"{DateTime.UtcNow:O} {status} {line}");
}

static async Task WriteStatus(NetworkStream s, int code, string reason, string body, bool noStore = false)
{
    var b = Encoding.UTF8.GetBytes(body);
    var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {b.Length}\r\n{(noStore ? "Cache-Control: no-store\r\n" : "")}Connection: close\r\n\r\n";
    await s.WriteAsync(Encoding.ASCII.GetBytes(head));
    await s.WriteAsync(b);
}

static int IndexOf(byte[] buf, int len, ReadOnlySpan<byte> pattern) => buf.AsSpan(0, len).IndexOf(pattern);

static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
{
    ".html" or ".htm" => "text/html; charset=utf-8",
    ".js" or ".mjs" => "text/javascript; charset=utf-8",
    ".css" => "text/css; charset=utf-8",
    ".json" or ".map" => "application/json; charset=utf-8",
    ".svg" => "image/svg+xml",
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".gif" => "image/gif",
    ".ico" => "image/x-icon",
    ".webp" => "image/webp",
    ".woff" => "font/woff",
    ".woff2" => "font/woff2",
    ".ttf" => "font/ttf",
    ".wasm" => "application/wasm",
    ".mp3" => "audio/mpeg",
    ".ogg" => "audio/ogg",
    ".wav" => "audio/wav",
    ".txt" or ".md" or ".yaml" or ".yml" => "text/plain; charset=utf-8",
    ".pdf" => "application/pdf",
    _ => "application/octet-stream",
};

/// <summary>
/// 發行版狀態檔(--status-file)→ 教官站讀的 JSON(固定網址 <see cref="Url"/>;格式 <see cref="Format"/>)。
/// 輸入是啟動腳本存下的 <c>simosa-pkg status --json</c> 輸出(type = statusResult);輸出只有白名單欄位:
/// <code>
/// { "format": "simosa-release-status/1", "generatedUtc": "<狀態檔的修改時間,UTC>" | null,
///   "releaseVersion": "2026.11.30" | null,
///   "softwareVersion": "2026.12.2" | null,
///   "releaseDowngrade": null | { "releaseVersion", "basisVersion", "previousReleaseVersion", "softwareVersion", "highestVersion", "softwareStale", "message" } }
/// </code>
/// releaseVersion 是安裝根目錄 manifest.json 的發行版本(安裝程式的版本;套用軟體更新包不會改它);softwareVersion 是 status 的
/// <c>components.software.version</c>(最近套用的軟體更新包版次;沒套用過為 null)—— 沒有降版而且比發行版新時,實際執行的是它的程式,教官站一併顯示。
/// 版本欄位只接受 1 至 4 段數字(與 PackageVersion 相同),其他內容一律不輸出;檔案不存在、太大、不是 JSON 或不是 status 的輸出時回傳 null
/// (伺服器改送 <see cref="Empty"/>:各欄 null)。每次請求都重讀(檔案很小;啟動腳本在啟動網頁伺服之前寫好,之後不變)。
/// </summary>
static class ReleaseStatusFile
{
    public const string Prefix = "_status";
    public const string Url = "/_status/release.json";
    public const string Format = "simosa-release-status/1";
    /// <summary>status --json 的輸出約 1–3 KiB(信任金鑰與 SMS 文件多時稍大);超過這個大小就不是它</summary>
    private const int MaxBytes = 256 * 1024;
    private const int MaxMessageChars = 4000;
    private static readonly string[] DowngradeVersionFields = { "releaseVersion", "basisVersion", "previousReleaseVersion", "softwareVersion", "highestVersion" };

    public static byte[]? TryBuild(string? file, out string reason)
    {
        if (file is null) { reason = "未指定 --status-file"; return null; }
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) { reason = "狀態檔不存在"; return null; }
            if (info.Length > MaxBytes) { reason = $"狀態檔 {info.Length} bytes 超過上限 {MaxBytes}"; return null; }
            ReadOnlyMemory<byte> bytes = File.ReadAllBytes(file);
            if (bytes.Length >= 3 && bytes.Span[0] == 0xEF && bytes.Span[1] == 0xBB && bytes.Span[2] == 0xBF) bytes = bytes[3..];   // 容許 UTF-8 BOM
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var rootEl = doc.RootElement;
            if (rootEl.ValueKind != JsonValueKind.Object || Str(rootEl, "type") != "statusResult") { reason = "狀態檔不是 simosa-pkg status --json 的輸出"; return null; }
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("format", Format);
                w.WriteString("generatedUtc", info.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                WriteVersion(w, "releaseVersion", Str(rootEl, "releaseVersion"));
                WriteVersion(w, "softwareVersion",
                    rootEl.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Object
                    && comps.TryGetProperty("software", out var sw) && sw.ValueKind == JsonValueKind.Object ? Str(sw, "version") : null);
                if (rootEl.TryGetProperty("releaseDowngrade", out var dg) && dg.ValueKind == JsonValueKind.Object)
                {
                    w.WriteStartObject("releaseDowngrade");
                    foreach (var name in DowngradeVersionFields) WriteVersion(w, name, Str(dg, name));
                    w.WriteBoolean("softwareStale", dg.TryGetProperty("softwareStale", out var stale) && stale.ValueKind == JsonValueKind.True);
                    var message = Str(dg, "message") ?? "";
                    w.WriteString("message", message.Length > MaxMessageChars ? message[..MaxMessageChars] : message);
                    w.WriteEndObject();
                }
                else w.WriteNull("releaseDowngrade");
                w.WriteEndObject();
            }
            reason = "";
            return ms.ToArray();
        }
        catch (JsonException) { reason = "狀態檔不是有效的 JSON"; return null; }   // 不把例外訊息(可能含檔案內容片段)寫進 log
        // 字串裡有不合法的 UTF-8(寫到一半斷電、磁碟錯誤):JsonDocument.Parse 不檢查字串內容,GetString() 這時才擲 InvalidOperationException
        catch (InvalidOperationException) { reason = "狀態檔不是有效的 JSON(字串含不合法的 UTF-8)"; return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            reason = $"狀態檔讀不到({ex.GetType().Name})";
            return null;
        }
    }

    /// <summary>「沒有發行版狀態」:同一格式、各欄 null(狀態檔不存在或不合格時送出;教官站不顯示任何東西)</summary>
    public static byte[] Empty()
        => Encoding.UTF8.GetBytes($"{{\"format\":\"{Format}\",\"generatedUtc\":null,\"releaseVersion\":null,\"softwareVersion\":null,\"releaseDowngrade\":null}}");

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void WriteVersion(Utf8JsonWriter w, string name, string? value)
    {
        if (IsVersion(value)) w.WriteString(name, value);
        else w.WriteNull(name);
    }

    /// <summary>1 至 4 段、每段 1 至 9 位數字(例:2026.11.30);與 Tools.Packaging 的 PackageVersion 相同的形式</summary>
    public static bool IsVersion(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 40) return false;
        var segs = text.Split('.');
        return segs.Length is >= 1 and <= 4 && segs.All(seg => seg.Length is >= 1 and <= 9 && seg.All(char.IsAsciiDigit));
    }
}
