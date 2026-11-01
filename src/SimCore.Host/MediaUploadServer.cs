using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimosaBRM.SimCore.Recording;

namespace SimosaBRM.SimCore.Host;

/// <summary>
/// 練習錄影上傳端(docs/design/screen-recording.md;需求 R6 影像擷取、R13 紀錄包與錄影同步/錄影上限):
/// 獨立的 HttpListener(預設 http://0.0.0.0:8768/,與狀態 WebSocket 分開,不影響引擎與狀態雜湊),教官站以瀏覽器
/// getDisplayMedia + MediaRecorder 錄下畫面,片段即時 POST 到這裡,追加寫入 <c>&lt;media-dir&gt;/&lt;mediaId&gt;.webm.part</c>;
/// 停止時改名 <c>.webm</c> 並寫旁檔 <c>&lt;mediaId&gt;.json</c>(<see cref="MediaSidecar"/>:起訖 UTC、長度、大小、SHA-256、對應紀錄檔名與
/// header.createdUtc)。完全離線、不改紀錄檔;紀錄包由 Tools.Packaging 依旁檔關聯到練習。
/// <list type="bullet">
/// <item><c>POST /media/start</c> JSON {exerciseId, recordPath?, station, mime} → 200 {mediaId, file, maxBytes}</item>
/// <item><c>POST /media/chunk/&lt;mediaId&gt;</c> 二進位 body + 標頭 <c>X-Seq</c>(0 起連續):→ 200 {seq, bytes};重送已收到的序號 → 200 {duplicate:true};
///       跳號 → 409 {expectedSeq};累計超過 <see cref="MaxBytes"/>(--media-max-mb)→ 413 並停止接收(旁檔標 truncated)</item>
/// <item><c>POST /media/stop/&lt;mediaId&gt;</c> 可選 JSON {durationS?, reason?} → 200 旁檔內容(重複 stop 回同一份)</item>
/// <item><c>GET /media/status</c> → 進行中的錄影;<c>GET /</c> 健康檢查</item>
/// </list>
/// 只接受本機或直連私有網段的來源(<see cref="HostCommandRouter.IsAllowedShutdownClient"/>;<see cref="ClientFilter"/> 可換),其餘 403。
/// 回應一律帶 CORS 標頭(教官站網頁由 8080 靜態伺服,跨埠 fetch 需 preflight);OPTIONS 回 204。
/// 主控程序結束時尚未 stop 的錄影一併定稿(stopReason = hostShutdown),不留 .part。
/// </summary>
public sealed class MediaUploadServer : IAsyncDisposable
{
    public const int DefaultPort = 8768;
    public const string DefaultRelativeDir = "build/records/media";
    public const long DefaultMaxMb = 2048;
    /// <summary>單一片段上限(MediaRecorder 每 1–5 s 一片,通常數百 KB;防止誤送整檔塞爆記憶體)</summary>
    public const long MaxChunkBytes = 64L * 1024 * 1024;

    private sealed class Session
    {
        public required string MediaId { get; init; }
        public required string PartPath { get; init; }
        public required string FinalPath { get; init; }
        public required FileStream Stream { get; init; }
        public required MediaSidecar Sidecar { get; init; }
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public readonly object Sync = new();
        public long Bytes;
        public int NextSeq;
        public bool Truncated;
        public bool Stopped;
    }

    /// <summary>HTTP 回應用單行 JSON(旁檔本身仍以 MediaSidecar.JsonOptions 縮排寫檔)</summary>
    private static readonly JsonSerializerOptions ReplyJson = new(MediaSidecar.JsonOptions) { WriteIndented = false };

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Session> _active = new(StringComparer.Ordinal);
    /// <summary>已定稿的旁檔(重複 stop 回同一份;最多保留 64 筆)</summary>
    private readonly ConcurrentDictionary<string, MediaSidecar> _finished = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _finishedOrder = new();
    private Task? _acceptLoop;

    public MediaUploadServer(string mediaDir, int port = DefaultPort, long maxBytes = DefaultMaxMb * 1024 * 1024)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), "錄影上限須大於 0");
        MediaDir = Path.GetFullPath(mediaDir);
        Port = port;
        MaxBytes = maxBytes;
    }

    public string MediaDir { get; }
    public int Port { get; }
    /// <summary>每段錄影的上傳上限(位元組;--media-max-mb)</summary>
    public long MaxBytes { get; }
    /// <summary>實際監聽的前綴(所有介面,或僅 localhost)</summary>
    public string? Prefix { get; private set; }
    /// <summary>開始錄影時取得主控程序正在寫的紀錄路徑(null = 未開紀錄);旁檔以其檔名與 header.createdUtc 關聯練習</summary>
    public Func<string?>? CurrentRecordPath { get; set; }
    /// <summary>專案根目錄(旁檔的 record.path 相對此目錄;教官站給的 recordPath 也只能在此目錄內)</summary>
    public string? ProjectRoot { get; set; }
    /// <summary>來源過濾(預設 <see cref="HostCommandRouter.IsAllowedShutdownClient"/>:本機或直連私有網段)</summary>
    public Func<IPEndPoint?, bool> ClientFilter { get; set; } = HostCommandRouter.IsAllowedShutdownClient;
    /// <summary>磁碟剩餘空間低於此值時拒絕開始錄影(507;預設 256 MB,0 = 不檢查)</summary>
    public long MinFreeBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>mediaId 的時間戳與旁檔起訖時間的時鐘(測試可換)</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    public event Action<string>? Log;

    public int ActiveCount => _active.Count;
    public long BytesReceived;
    public long ChunksReceived;

    /// <summary>解析 --media-dir(null = 預設 build/records/media;相對路徑以 root 為準),必須在專案資料夾內(CLAUDE.md)。</summary>
    public static string ResolveDir(string root, string? option = null)
    {
        var rel = string.IsNullOrWhiteSpace(option) ? DefaultRelativeDir : option.Trim();
        var full = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull, comparison))
            throw new ArgumentException($"--media-dir 必須在專案資料夾內({rootFull}):{full}");
        return full;
    }

    /// <summary>同 WebSocketStateServer:先試所有介面,Windows 無 URL ACL 時退回 localhost 並提示。</summary>
    public void Start()
    {
        Directory.CreateDirectory(MediaDir);
        var wildcard = $"http://*:{Port}/";
        try
        {
            _listener.Prefixes.Add(wildcard);
            _listener.Start();
            Prefix = wildcard;
        }
        catch (HttpListenerException ex)
        {
            _listener.Prefixes.Clear();
            var local = $"http://localhost:{Port}/";
            _listener.Prefixes.Add(local);
            _listener.Start();
            Prefix = local;
            Log?.Invoke($"無法監聽 {wildcard}({ex.Message});改為只監聽 {local}。教官筆電直連需設定 URL ACL:netsh http add urlacl url={wildcard} user=Everyone");
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (_cts.IsCancellationRequested) { break; }
            catch (HttpListenerException) { continue; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Seq";
            res.Headers["Access-Control-Max-Age"] = "600";
            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }
            if (!ClientFilter(req.RemoteEndPoint))
            {
                await DrainAsync(req).ConfigureAwait(false);
                await WriteJsonAsync(res, 403, new { error = $"錄影上傳只接受本機或直連網段的教官站(來源 {req.RemoteEndPoint?.Address})" }).ConfigureAwait(false);
                return;
            }
            var path = req.Url?.AbsolutePath ?? "/";
            if (req.HttpMethod == "GET")
            {
                if (path == "/media/status") await WriteJsonAsync(res, 200, Status()).ConfigureAwait(false);
                else await WriteTextAsync(res, 200, "Simosa BRM SimCore media upload endpoint (POST /media/start, /media/chunk/<id>, /media/stop/<id>).\n").ConfigureAwait(false);
                return;
            }
            if (req.HttpMethod != "POST")
            {
                await WriteJsonAsync(res, 405, new { error = "只接受 GET / POST / OPTIONS" }).ConfigureAwait(false);
                return;
            }
            if (path == "/media/start") await StartAsync(req, res).ConfigureAwait(false);
            else if (path.StartsWith("/media/chunk/", StringComparison.Ordinal)) await ChunkAsync(req, res, path["/media/chunk/".Length..]).ConfigureAwait(false);
            else if (path.StartsWith("/media/stop/", StringComparison.Ordinal)) await StopAsync(req, res, path["/media/stop/".Length..]).ConfigureAwait(false);
            else
            {
                await DrainAsync(req).ConfigureAwait(false);
                await WriteJsonAsync(res, 404, new { error = $"未知端點:{path}" }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"錄影上傳處理失敗({req.HttpMethod} {req.Url?.AbsolutePath}):{ex.Message}");
            try { await WriteJsonAsync(res, 500, new { error = ex.Message }).ConfigureAwait(false); }
            catch (Exception) { /* 連線已斷 */ }
        }
    }

    // ---------------------------------------------------------------- start
    private async Task StartAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        var body = await ReadBodyAsync(req, 64 * 1024).ConfigureAwait(false);
        if (body is null)
        {
            await WriteJsonAsync(res, 413, new { error = "start 的 JSON 過大" }).ConfigureAwait(false);
            return;
        }
        string? exerciseId = null, recordPath = null, station = null, mime = null;
        try
        {
            using var doc = JsonDocument.Parse(body.Length == 0 ? "{}"u8.ToArray() : body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("body 須為 JSON 物件");
            exerciseId = Str(root, "exerciseId");
            recordPath = Str(root, "recordPath");
            station = Str(root, "station");
            mime = Str(root, "mime");
        }
        catch (JsonException ex)
        {
            await WriteJsonAsync(res, 400, new { error = $"start 的 JSON 無法解析:{ex.Message}" }).ConfigureAwait(false);
            return;
        }
        if (MinFreeBytes > 0 && FreeBytes() is { } free && free < MinFreeBytes)
        {
            Log?.Invoke($"拒絕開始錄影:磁碟剩餘 {free / (1024.0 * 1024):F0} MB 低於 {MinFreeBytes / (1024.0 * 1024):F0} MB");
            await WriteJsonAsync(res, 507, new { error = $"磁碟剩餘空間不足({free / (1024.0 * 1024):F0} MB),請先取回並清理紀錄包/錄影", freeBytes = free, minFreeBytes = MinFreeBytes }).ConfigureAwait(false);
            return;
        }
        var now = Clock.GetUtcNow().UtcDateTime;
        var stationSafe = SafeName(string.IsNullOrWhiteSpace(station) ? "station" : station!, 24);
        var exerciseSafe = SafeName(string.IsNullOrWhiteSpace(exerciseId) ? "exercise" : exerciseId!, 40);
        string mediaId;
        do mediaId = $"{now:yyyyMMdd-HHmmss}-{stationSafe}-{exerciseSafe}-{Guid.NewGuid().ToString("N")[..6]}";
        while (_active.ContainsKey(mediaId) || File.Exists(Path.Combine(MediaDir, mediaId + ".webm")));
        var record = ResolveRecord(recordPath);
        var sidecar = new MediaSidecar
        {
            MediaId = mediaId,
            File = mediaId + ".webm",
            ExerciseId = exerciseId,
            Station = station ?? "",
            Mime = mime ?? "",
            StartedUtc = now,
            MaxBytes = MaxBytes,
            Record = record,
        };
        Directory.CreateDirectory(MediaDir);
        var partPath = Path.Combine(MediaDir, mediaId + ".webm.part");
        var stream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        var session = new Session { MediaId = mediaId, PartPath = partPath, FinalPath = Path.Combine(MediaDir, mediaId + ".webm"), Stream = stream, Sidecar = sidecar };
        _active[mediaId] = session;
        Log?.Invoke($"開始錄影 {mediaId}(站別 {sidecar.Station},{(record is null ? "無紀錄" : $"紀錄 {record.File}")},上限 {MaxBytes / (1024.0 * 1024):F0} MB)");
        await WriteJsonAsync(res, 200, new { mediaId, file = sidecar.File, maxBytes = MaxBytes, record = record?.File }).ConfigureAwait(false);
    }

    /// <summary>
    /// 對應的練習紀錄:教官站給 recordPath(須在專案根目錄內)優先,否則取主控程序正在寫的紀錄;讀第一行 header 的 createdUtc 與 scenario.id
    /// (RecordWriter 以 FileShare.Read 開檔,可同時讀)。讀不到時只留檔名。
    /// </summary>
    private MediaRecordRef? ResolveRecord(string? requested)
    {
        string? full = null;
        if (!string.IsNullOrWhiteSpace(requested) && ProjectRoot is { } root)
        {
            try
            {
                var candidate = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
                var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(candidate)) full = candidate;
            }
            catch (Exception) { /* 不合法的路徑:忽略 */ }
        }
        full ??= CurrentRecordPath?.Invoke();
        if (full is null) return null;
        var r = new MediaRecordRef { File = Path.GetFileName(full), Path = ProjectRoot is { } pr ? RelativeTo(pr, full) : full };
        try
        {
            using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var line = reader.ReadLine();
            if (!string.IsNullOrEmpty(line))
            {
                using var doc = JsonDocument.Parse(line);
                var h = doc.RootElement;
                if (h.TryGetProperty("createdUtc", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTime(out var dt)) r.CreatedUtc = dt.ToUniversalTime();
                if (h.TryGetProperty("scenario", out var sc) && sc.ValueKind == JsonValueKind.Object) r.ScenarioId = Str(sc, "id");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log?.Invoke($"讀取紀錄標頭失敗({full}):{ex.Message};旁檔只記檔名");
        }
        return r;
    }

    // ---------------------------------------------------------------- chunk
    private async Task ChunkAsync(HttpListenerRequest req, HttpListenerResponse res, string mediaId)
    {
        if (!_active.TryGetValue(mediaId, out var s))
        {
            await DrainAsync(req).ConfigureAwait(false);
            await WriteJsonAsync(res, _finished.ContainsKey(mediaId) ? 410 : 404, new { error = _finished.ContainsKey(mediaId) ? $"錄影 {mediaId} 已停止" : $"未知的錄影 {mediaId}" }).ConfigureAwait(false);
            return;
        }
        if (!int.TryParse(req.Headers["X-Seq"], out var seq) || seq < 0)
        {
            await DrainAsync(req).ConfigureAwait(false);
            await WriteJsonAsync(res, 400, new { error = "缺少或不合法的 X-Seq 標頭" }).ConfigureAwait(false);
            return;
        }
        // 先看序號與上限狀態(不必讀 body 的情況:重送、跳號、已截斷)
        lock (s.Sync)
        {
            if (s.Truncated)
            {
                _ = DrainThenJson(req, res, 413, new { error = "已超過錄影上限,不再接收", truncated = true, bytes = s.Bytes, maxBytes = MaxBytes });
                return;
            }
            if (seq < s.NextSeq)
            {
                _ = DrainThenJson(req, res, 200, new { seq, duplicate = true, bytes = s.Bytes });
                return;
            }
            if (seq > s.NextSeq)
            {
                _ = DrainThenJson(req, res, 409, new { error = $"片段序號不連續:收到 {seq},預期 {s.NextSeq}", expectedSeq = s.NextSeq });
                return;
            }
        }
        var body = await ReadBodyAsync(req, MaxChunkBytes).ConfigureAwait(false);
        if (body is null)
        {
            await WriteJsonAsync(res, 413, new { error = $"單一片段超過 {MaxChunkBytes / (1024 * 1024)} MB" }).ConfigureAwait(false);
            return;
        }
        object reply;
        int status;
        lock (s.Sync)
        {
            if (s.Stopped) { status = 410; reply = new { error = $"錄影 {mediaId} 已停止" }; }
            else if (seq != s.NextSeq) { status = seq < s.NextSeq ? 200 : 409; reply = seq < s.NextSeq ? new { seq, duplicate = true, bytes = s.Bytes } : new { error = "片段序號不連續", expectedSeq = s.NextSeq }; }
            else if (s.Bytes + body.Length > MaxBytes)
            {
                // 超過上限:本片段不寫入,之後也不再接收;檔案只含上限內的片段(旁檔 truncated),教官站收到 413 應停止錄影並送 stop
                s.Truncated = true;
                status = 413;
                reply = new { error = $"錄影累計 {(s.Bytes + body.Length) / (1024.0 * 1024):F1} MB 超過上限 {MaxBytes / (1024.0 * 1024):F0} MB,停止接收", truncated = true, bytes = s.Bytes, maxBytes = MaxBytes };
                Log?.Invoke($"錄影 {mediaId} 超過上限 {MaxBytes / (1024.0 * 1024):F0} MB,停止接收(已存 {s.Bytes} 位元組、{s.NextSeq} 片段)");
            }
            else
            {
                s.Stream.Write(body, 0, body.Length);
                s.Hash.AppendData(body);
                s.Bytes += body.Length;
                s.NextSeq = seq + 1;
                Interlocked.Add(ref BytesReceived, body.Length);
                Interlocked.Increment(ref ChunksReceived);
                status = 200;
                reply = new { seq, bytes = s.Bytes };
            }
        }
        await WriteJsonAsync(res, status, reply).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- stop
    private async Task StopAsync(HttpListenerRequest req, HttpListenerResponse res, string mediaId)
    {
        var body = await ReadBodyAsync(req, 64 * 1024).ConfigureAwait(false);
        double? durationS = null;
        string? reason = null;
        if (body is { Length: > 0 })
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("durationS", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var dv) && dv >= 0 && double.IsFinite(dv)) durationS = dv;
                    reason = Str(doc.RootElement, "reason");
                }
            }
            catch (JsonException) { /* 忽略不合法 body,仍停止 */ }
        }
        if (_active.TryGetValue(mediaId, out var s))
        {
            var sidecar = Finalize(s, durationS, reason ?? "client");
            await WriteJsonAsync(res, 200, sidecar).ConfigureAwait(false);
            return;
        }
        if (_finished.TryGetValue(mediaId, out var done))
        {
            await WriteJsonAsync(res, 200, done).ConfigureAwait(false);
            return;
        }
        await WriteJsonAsync(res, 404, new { error = $"未知的錄影 {mediaId}" }).ConfigureAwait(false);
    }

    /// <summary>定稿:關檔、改名 .webm、寫旁檔;重複呼叫回同一份旁檔。</summary>
    private MediaSidecar Finalize(Session s, double? durationS, string reason)
    {
        lock (s.Sync)
        {
            if (s.Stopped) return s.Sidecar;
            s.Stopped = true;
            var now = Clock.GetUtcNow().UtcDateTime;
            s.Stream.Flush(true);
            s.Stream.Dispose();
            var sc = s.Sidecar;
            sc.StoppedUtc = now;
            sc.SizeBytes = s.Bytes;
            sc.Chunks = s.NextSeq;
            sc.Sha256 = Convert.ToHexString(s.Hash.GetHashAndReset());
            sc.Truncated = s.Truncated;
            sc.StopReason = s.Truncated && reason == "client" ? "limit" : reason;
            if (durationS is { } d)
            {
                sc.DurationS = d;
                sc.DurationEstimated = false;
            }
            else
            {
                sc.DurationS = Math.Max(0, (now - sc.StartedUtc).TotalSeconds);
                sc.DurationEstimated = true;
            }
            try { File.Move(s.PartPath, s.FinalPath, overwrite: true); }
            catch (IOException ex)
            {
                Log?.Invoke($"錄影 {s.MediaId} 改名失敗({ex.Message}),保留 {Path.GetFileName(s.PartPath)}");
                sc.File = Path.GetFileName(s.PartPath);
            }
            sc.Save(Path.Combine(MediaDir, s.MediaId + ".json"));
            s.Hash.Dispose();
            _active.TryRemove(s.MediaId, out _);
            _finished[s.MediaId] = sc;
            _finishedOrder.Enqueue(s.MediaId);
            while (_finishedOrder.Count > 64 && _finishedOrder.TryDequeue(out var old)) _finished.TryRemove(old, out _);
            Log?.Invoke($"錄影已停止 {s.MediaId}:{sc.SizeBytes / (1024.0 * 1024):F1} MB、{sc.Chunks} 片段、{sc.DurationS:F0} s{(sc.Truncated ? "、已截斷(超過上限)" : "")}(原因 {sc.StopReason})→ {sc.File}");
            return sc;
        }
    }

    private object Status() => new
    {
        dir = MediaDir,
        maxBytes = MaxBytes,
        active = _active.Values.Select(s => new { s.MediaId, bytes = Interlocked.Read(ref s.Bytes), chunks = s.NextSeq, startedUtc = s.Sidecar.StartedUtc, truncated = s.Truncated, record = s.Sidecar.Record?.File }).ToList(),
    };

    // ---------------------------------------------------------------- helpers
    private long? FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(MediaDir);
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception) { return null; }
    }

    private static string SafeName(string s, int max)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (sb.Length >= max) break;
            sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) || c == '.' || char.IsControl(c) ? '_' : c);
        }
        return sb.Length == 0 ? "x" : sb.ToString();
    }

    private static string RelativeTo(string root, string full)
    {
        try { return Path.GetRelativePath(root, full).Replace('\\', '/'); }
        catch (Exception) { return full; }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>讀整個 body(最多 <paramref name="max"/> 位元組;超過回 null)。</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest req, long max)
    {
        if (req.ContentLength64 > max) { await DrainAsync(req).ConfigureAwait(false); return null; }
        using var ms = new MemoryStream(req.ContentLength64 > 0 ? (int)Math.Min(req.ContentLength64, int.MaxValue) : 0);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await req.InputStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) { await DrainAsync(req).ConfigureAwait(false); return null; }
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>把尚未讀的 body 讀掉(回錯誤前;否則客戶端可能收到連線重設而不是狀態碼)。</summary>
    private static async Task DrainAsync(HttpListenerRequest req)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (await req.InputStream.ReadAsync(buffer).ConfigureAwait(false) is var n && n > 0)
            {
                total += n;
                if (total > MaxChunkBytes * 2) break;
            }
        }
        catch (Exception) { /* 連線已斷 */ }
    }

    private static async Task DrainThenJson(HttpListenerRequest req, HttpListenerResponse res, int status, object payload)
    {
        await DrainAsync(req).ConfigureAwait(false);
        await WriteJsonAsync(res, status, payload).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ReplyJson);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    private static async Task WriteTextAsync(HttpListenerResponse res, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status;
        res.ContentType = "text/plain; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    /// <summary>停止監聽;進行中的錄影定稿(stopReason = hostShutdown),不留 .part。</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (Exception) { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (Exception) { }
        }
        foreach (var s in _active.Values.ToList())
        {
            try { Finalize(s, null, "hostShutdown"); }
            catch (Exception ex) { Log?.Invoke($"錄影 {s.MediaId} 定稿失敗:{ex.Message}"); }
        }
        _listener.Close();
        _cts.Dispose();
    }
}
