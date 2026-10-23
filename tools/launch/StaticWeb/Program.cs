// simosa-web:極簡靜態網頁伺服(GET/HEAD、HTTP/1.1、Connection: close)。
// 用途:離線的船上/教官筆電以 http://<直連 IP>:<埠>/ 提供 app/web(Vite 建置輸出,相對路徑資產),供 WebView2 或瀏覽器載入;
// 不做目錄列表、不接受 .. 或絕對路徑、不跟隨符號連結到根目錄之外;每個請求一行紀錄到 stdout(啟動腳本導到 build/logs/)。
using System.Net;
using System.Net.Sockets;
using System.Text;

var root = Directory.GetCurrentDirectory();
var port = 8080;
var bind = IPAddress.Any;
var quiet = false;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} 需要參數值");
    switch (args[i])
    {
        case "--root": root = Path.GetFullPath(Next()); break;
        case "--port": port = int.Parse(Next()); break;
        case "--bind": bind = IPAddress.Parse(Next()); break;
        case "--quiet": quiet = true; break;
        case "-h": case "--help":
            Console.WriteLine("用法:simosa-web --root <網頁目錄> [--port 8080] [--bind 0.0.0.0] [--quiet]");
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
var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

var listener = new TcpListener(bind, port);
listener.Start();
Console.WriteLine($"simosa-web 提供 {rootFull} 於 http://{(Equals(bind, IPAddress.Any) ? "0.0.0.0" : bind.ToString())}:{port}/(Ctrl+C 結束)");
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
    }
}

void Log(string line, int status)
{
    if (!quiet) Console.WriteLine($"{DateTime.UtcNow:O} {status} {line}");
}

static async Task WriteStatus(NetworkStream s, int code, string reason, string body)
{
    var b = Encoding.UTF8.GetBytes(body);
    var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n";
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
