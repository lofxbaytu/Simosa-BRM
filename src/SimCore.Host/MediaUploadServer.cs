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
/// <para>
/// 有上限的資源(第三十輪 2 小時搜尋 host-media-*):讀 body 時 <see cref="BodyIdleTimeout"/>(預設 30 s)沒有收到任何位元組就中止該請求並 Abort 連線
/// (對端消失、不送 FIN 也不送 RST 時,Linux 受控 HttpListener 沒有 body 逾時,修正前永久持有片段大小的緩衝與連線);
/// 任何錯誤路徑最後一定 Close 或 Abort 回應(修正前上傳中途 RST 時連 500 都寫不出去,連線留在 listener 的連線表);
/// 開始後 <see cref="SessionIdleTimeout"/>(預設 5 分鐘)沒有任何片段的錄影暫停寫檔:關閉檔案代號與寫入緩衝(被放棄的錄影 —
/// 分頁重新整理或當機、stop 送不出去 — 修正前持有到主控程序結束),工作階段保留,下一個片段到達時接續寫入(畫面靜止、沒有音訊時
/// MediaRecorder 可能數分鐘不吐片段,不能當成放棄而定稿:之後的片段會收到 410,教官站把它當成致命錯誤而停止錄影,第三十輪審查 REV-MEDIA)。
/// 暫停中的錄影超過 <see cref="MaxSuspendedSessions"/> 份時,最後一次活動最早者定稿(stopReason = idleTimeout,stoppedUtc 為最後一次活動);
/// 主控程序結束時暫停中的錄影同樣以 idleTimeout 定稿。
/// </para>
/// </summary>
public sealed class MediaUploadServer : IAsyncDisposable
{
    public const int DefaultPort = 8768;
    public const string DefaultRelativeDir = "build/records/media";
    public const long DefaultMaxMb = 2048;
    /// <summary>單一片段上限(MediaRecorder 每 1–5 s 一片,通常數百 KB;防止誤送整檔塞爆記憶體)</summary>
    public const long MaxChunkBytes = 64L * 1024 * 1024;
    /// <summary>讀 body 時依 Content-Length 預先配置的上限(正常片段 0.3–1 MB 不受影響;宣告 64 MB 卻只送幾 KB 的請求不先占 64 MB)</summary>
    private const int MaxBodyPreallocBytes = 4 * 1024 * 1024;
    /// <summary>定稿時已暫停(閒置超過 <see cref="SessionIdleTimeout"/>:被放棄,或畫面長時間靜止)的錄影的 stopReason;其 stoppedUtc 為最後一次活動</summary>
    public const string IdleTimeoutReason = "idleTimeout";
    /// <summary>
    /// 暫停中(閒置、已關檔)的錄影最多保留幾份;超過時最後一次活動最早者定稿(idleTimeout)。正在錄的錄影只有教官站目前那一份,
    /// 它的最後一次活動一定最晚,所以被定稿的必定是更早被放棄的錄影(每份暫停中的錄影只剩 SHA-256 狀態與旁檔內容,約 1–2 KB)
    /// </summary>
    public const int MaxSuspendedSessions = 16;

    private sealed class Session
    {
        public required string MediaId { get; init; }
        public required string PartPath { get; init; }
        public required string FinalPath { get; init; }
        /// <summary>寫入中的 .part(暫停時關閉為 null,下一個片段到達時以 <see cref="Reopen"/> 接續;<see cref="Sync"/> 內讀寫)</summary>
        public FileStream? Stream;
        public required MediaSidecar Sidecar { get; init; }
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public readonly object Sync = new();
        public long Bytes;
        public int NextSeq;
        public bool Truncated;
        public bool Stopped;
        /// <summary>最後一次活動(start、任何片段請求;<see cref="Sync"/> 內讀寫):閒置逾時由此起算</summary>
        public DateTime LastActivityUtc;
        /// <summary>正在讀 body 的片段請求數(<see cref="Sync"/> 內讀寫;&gt; 0 時不暫停)</summary>
        public int InFlight;
        /// <summary>閒置超過 SessionIdleTimeout 而暫停寫檔(<see cref="Stream"/> 已關閉;<see cref="Sync"/> 內讀寫)</summary>
        public bool Suspended;

        /// <summary>暫停後的第一個片段:重新開啟 .part 接續寫入(檔案長度須等於已收位元組數,否則拋 IOException → 500,教官站重試)。</summary>
        public FileStream Reopen()
        {
            var fs = new FileStream(PartPath, FileMode.Open, FileAccess.Write, FileShare.Read, 1 << 16);
            if (fs.Length != Bytes)
            {
                fs.Dispose();
                throw new IOException($"{Path.GetFileName(PartPath)} 長度 {fs.Length} 與已收 {Bytes} 位元組不符,無法接續寫入");
            }
            fs.Seek(0, SeekOrigin.End);
            Stream = fs;
            Suspended = false;
            return fs;
        }
    }

    /// <summary>讀 body 時超過 <see cref="BodyIdleTimeout"/> 沒有進度(該請求直接 Abort,不回應)</summary>
    private sealed class BodyStalledException : Exception
    {
        public BodyStalledException(TimeSpan timeout) : base($"body 超過 {timeout.TotalSeconds:0.#} s 沒有進度") { }
    }

    /// <summary>HTTP 回應用單行 JSON(旁檔本身仍以 MediaSidecar.JsonOptions 縮排寫檔)</summary>
    private static readonly JsonSerializerOptions ReplyJson = new(MediaSidecar.JsonOptions) { WriteIndented = false };

    /// <summary>已啟動的 listener(TryStart 成功後才有;Start 失敗的 listener 不保留,見 HttpListenerStarter)</summary>
    private HttpListener? _listener;
    private int _disposed;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Session> _active = new(StringComparer.Ordinal);
    /// <summary>已定稿的旁檔(重複 stop 回同一份;最多保留 64 筆)</summary>
    private readonly ConcurrentDictionary<string, MediaSidecar> _finished = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _finishedOrder = new();
    private Task? _acceptLoop;
    private Task? _idleSweep;

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
    /// <summary>實際監聽的前綴(所有介面,或僅 localhost);未啟動/啟動失敗為 null</summary>
    public string? Prefix { get; private set; }
    /// <summary>啟動失敗的原因(埠被占用、無 URL ACL…;<see cref="TryStart"/> 回 false 後可讀);成功或尚未啟動為 null</summary>
    public string? StartError { get; private set; }
    /// <summary>已成功啟動(可接受上傳)</summary>
    public bool Started => _listener is not null;
    /// <summary>開始錄影時取得主控程序正在寫的紀錄路徑(null = 未開紀錄);旁檔以其檔名與 header.createdUtc 關聯練習</summary>
    public Func<string?>? CurrentRecordPath { get; set; }
    /// <summary>專案根目錄(旁檔的 record.path 相對此目錄;教官站給的 recordPath 也只能在此目錄內)</summary>
    public string? ProjectRoot { get; set; }
    /// <summary>來源過濾(預設 <see cref="HostCommandRouter.IsAllowedShutdownClient"/>:本機或直連私有網段)</summary>
    public Func<IPEndPoint?, bool> ClientFilter { get; set; } = HostCommandRouter.IsAllowedShutdownClient;
    /// <summary>磁碟剩餘空間低於此值時拒絕開始錄影(507;預設 256 MB,0 = 不檢查)</summary>
    public long MinFreeBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>mediaId 的時間戳、旁檔起訖時間與閒置判定的時鐘(測試可換)</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
    /// <summary>讀 body(片段、start/stop 的 JSON、錯誤回應前的排空)時,這段時間內沒有收到任何位元組就中止該請求並 Abort 連線</summary>
    public TimeSpan BodyIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// 錄影閒置逾時:最後一次活動(start 或片段請求)後超過此時間、且沒有片段正在上傳,就暫停寫檔(關閉檔案代號與寫入緩衝),
    /// 不定稿;下一個片段到達時接續寫入。被放棄的錄影(分頁重新整理或當機)由 <see cref="MaxSuspendedSessions"/> 或主控程序結束時定稿。
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>閒置檢查的間隔(背景迴圈;TryStart 前設定)</summary>
    public TimeSpan IdleSweepInterval { get; set; } = TimeSpan.FromSeconds(30);
    public event Action<string>? Log;

    /// <summary>未定稿的錄影數(含暫停中者)</summary>
    public int ActiveCount => _active.Count;
    /// <summary>暫停中(閒置、已關檔)的錄影數</summary>
    public int SuspendedCount => _active.Values.Count(s => { lock (s.Sync) return s.Suspended && !s.Stopped; });
    public long BytesReceived;
    public long ChunksReceived;
    /// <summary>因 body 沒有進度或寫不出回應而以 Abort 收尾的請求數(診斷)</summary>
    public long RequestsAborted;
    /// <summary>因閒置而暫停寫檔的次數(診斷;同一份錄影可暫停多次)</summary>
    public long SessionsSuspended;
    /// <summary>暫停後收到片段而接續寫入的次數(診斷)</summary>
    public long SessionsResumed;
    /// <summary>以 idleTimeout 定稿的錄影數(暫停中的錄影超過 MaxSuspendedSessions 份時最舊者;診斷)</summary>
    public long SessionsTimedOut;

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

    /// <summary>
    /// 同 WebSocketStateServer:先試所有介面,Windows 無 URL ACL 時以新的 HttpListener 退回 localhost + 127.0.0.1 並提示;
    /// 兩者皆失敗(典型為埠被占用)回 false 並設定 <see cref="StartError"/>,不拋例外(錄影為選配:主控程序印訊息後照常執行)。
    /// </summary>
    public bool TryStart(out string? error)
    {
        if (_listener is not null) throw new InvalidOperationException("MediaUploadServer 已啟動");
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(MediaUploadServer));
        Directory.CreateDirectory(MediaDir);
        var listener = HttpListenerStarter.StartWithFallback(Port, "", allInterfaces: true, msg => Log?.Invoke(msg), out var prefix, out error);
        if (listener is null)
        {
            StartError = error;
            return false;
        }
        _listener = listener;
        Prefix = prefix;
        StartError = null;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener));
        _idleSweep = Task.Run(IdleSweepLoopAsync);
        return true;
    }

    /// <summary>同 <see cref="TryStart"/>,失敗時拋 <see cref="HttpListenerException"/>(訊息含埠與原因;物件仍可安全釋放)。</summary>
    public void Start()
    {
        if (!TryStart(out var error)) throw new HttpListenerException(0, $"埠 {Port} 無法監聽:{error}");
    }

    /// <summary>接受迴圈:每個請求都在背景處理(HttpListenerStarter.AcceptLoopAsync:單一請求的錯誤不會讓迴圈結束)。</summary>
    private Task AcceptLoopAsync(HttpListener listener)
        => HttpListenerStarter.AcceptLoopAsync(listener, _cts.Token, ctx => _ = Task.Run(() => HandleAsync(ctx)), msg => Log?.Invoke(msg), "錄影上傳端");

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
                HttpListenerStarter.CloseResponse(res, 204);
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
        catch (BodyStalledException ex)
        {
            // 對端消失(拔線、睡眠)或卡住:回應也送不到,直接 Abort 釋放連線與已讀的緩衝
            Log?.Invoke($"錄影上傳中止({req.HttpMethod} {req.Url?.AbsolutePath},來源 {req.RemoteEndPoint}):{ex.Message},中止連線");
            Abort(res);
        }
        catch (Exception ex)
        {
            // 例如上傳中途 RST(讀 body 丟 IOException):試著回 500;寫不出去時 WriteJsonAsync 會 Abort(修正前寫出失敗後什麼都不做,連線留在連線表)
            Log?.Invoke($"錄影上傳處理失敗({req.HttpMethod} {req.Url?.AbsolutePath}):{ex.Message}");
            await WriteJsonAsync(res, 500, new { error = ex.Message }).ConfigureAwait(false);
        }
    }

    /// <summary>以 Abort 收尾一個請求(計數;不拋例外)</summary>
    private void Abort(HttpListenerResponse res)
    {
        Interlocked.Increment(ref RequestsAborted);
        HttpListenerStarter.AbortResponse(res);
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
        var session = new Session { MediaId = mediaId, PartPath = partPath, FinalPath = Path.Combine(MediaDir, mediaId + ".webm"), Stream = stream, Sidecar = sidecar, LastActivityUtc = now };
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
        // 先看序號與上限狀態(不必讀 body 的情況:重送、跳號、已截斷);任何片段請求都算活動(閒置逾時由此重新起算)
        lock (s.Sync)
        {
            s.LastActivityUtc = Clock.GetUtcNow().UtcDateTime;
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
            s.InFlight++;   // 讀 body 期間不當成被放棄(慢速上傳)
        }
        byte[]? body;
        try { body = await ReadBodyAsync(req, MaxChunkBytes).ConfigureAwait(false); }
        finally
        {
            lock (s.Sync)
            {
                s.InFlight--;
                s.LastActivityUtc = Clock.GetUtcNow().UtcDateTime;
            }
        }
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
                var stream = s.Stream;
                if (stream is null)
                {
                    // 暫停後的第一個片段(畫面靜止數分鐘後又有變化):接續寫入同一份 .part,序號與雜湊不中斷
                    stream = s.Reopen();
                    Interlocked.Increment(ref SessionsResumed);
                    Log?.Invoke($"錄影 {mediaId} 收到片段 {seq},接續寫入(暫停後恢復)");
                }
                stream.Write(body, 0, body.Length);
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

    /// <summary>定稿:關檔、改名 .webm、寫旁檔;重複呼叫回同一份旁檔。<paramref name="stoppedUtc"/> 省略時為現在(閒置逾時以最後一次活動為停止時刻)。</summary>
    private MediaSidecar Finalize(Session s, double? durationS, string reason, DateTime? stoppedUtc = null)
    {
        lock (s.Sync)
        {
            if (s.Stopped) return s.Sidecar;
            s.Stopped = true;
            var now = stoppedUtc ?? Clock.GetUtcNow().UtcDateTime;
            if (s.Stream is { } stream)
            {
                stream.Flush(true);
                stream.Dispose();
                s.Stream = null;
            }
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

    /// <summary>背景閒置檢查(每 <see cref="IdleSweepInterval"/>;權杖取消即結束,Task.Delay 完成時釋放其取消註冊)。</summary>
    private async Task IdleSweepLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(IdleSweepInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try { SweepIdleSessions(); }
            catch (Exception ex) { Log?.Invoke($"錄影閒置檢查失敗:{ex.Message}"); }
        }
    }

    /// <summary>
    /// 閒置檢查:最後一次活動超過 <see cref="SessionIdleTimeout"/>、且沒有片段正在上傳的錄影暫停寫檔(Flush 後關閉 .part,釋放檔案代號與寫入緩衝;
    /// 序號、已收位元組與 SHA-256 狀態保留,下一個片段到達時接續寫入)。暫停中的錄影超過 <see cref="MaxSuspendedSessions"/> 份時,
    /// 最後一次活動最早者定稿(stopReason = idleTimeout,stoppedUtc = 最後一次活動;.webm + 旁檔,紀錄包照常打包;之後的片段 410、stop 回同一份旁檔)。
    /// 回傳本次暫停的錄影數。
    /// </summary>
    internal int SweepIdleSessions()
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var count = 0;
        var suspended = new List<(Session S, DateTime Last)>();
        foreach (var s in _active.Values.ToList())
        {
            DateTime last;
            bool newly;
            lock (s.Sync)
            {
                if (s.Stopped || s.InFlight > 0) continue;
                last = s.LastActivityUtc;
                newly = !s.Suspended && now - last > SessionIdleTimeout;
                if (newly)
                {
                    if (s.Stream is { } stream)
                    {
                        stream.Flush(true);
                        stream.Dispose();
                        s.Stream = null;
                    }
                    s.Suspended = true;
                }
                if (!s.Suspended) continue;
            }
            suspended.Add((s, last));
            if (!newly) continue;
            count++;
            Interlocked.Increment(ref SessionsSuspended);
            Log?.Invoke($"錄影 {s.MediaId} 已 {SessionIdleTimeout.TotalMinutes:0.#} 分鐘沒有片段(最後活動 {last:HH:mm:ss}Z):暫停寫檔、關閉檔案,下一個片段到達時接續寫入");
        }
        // 暫停中的錄影有上限:最後一次活動最早者定稿(被放棄的錄影;正在錄的那份最後活動最晚)
        foreach (var (s, last) in suspended.OrderBy(x => x.Last).ThenBy(x => x.S.MediaId, StringComparer.Ordinal).Take(Math.Max(0, suspended.Count - MaxSuspendedSessions)))
        {
            lock (s.Sync)
            {
                if (s.Stopped || !s.Suspended || s.InFlight > 0) continue;
                Finalize(s, null, IdleTimeoutReason, s.LastActivityUtc);   // 同一把鎖(Monitor 可重入):檢查與定稿之間不會插進新片段
            }
            Interlocked.Increment(ref SessionsTimedOut);
            Log?.Invoke($"暫停中的錄影超過 {MaxSuspendedSessions} 份:最早的 {s.MediaId}(最後活動 {last:HH:mm:ss}Z)視為教官站已放棄,定稿");
        }
        return count;
    }

    private object Status() => new
    {
        dir = MediaDir,
        maxBytes = MaxBytes,
        active = _active.Values.Select(s => new { s.MediaId, bytes = Interlocked.Read(ref s.Bytes), chunks = s.NextSeq, startedUtc = s.Sidecar.StartedUtc, truncated = s.Truncated, suspended = s.Suspended, record = s.Sidecar.Record?.File }).ToList(),
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

    /// <summary>
    /// 讀一次 body,最多等 <see cref="BodyIdleTimeout"/>;逾時拋 <see cref="BodyStalledException"/>(呼叫端 Abort 連線,未完成的讀取隨之結束)。
    /// 不以 Task.WhenAny + Task.Delay 實作(會留下計時器);WaitAsync 完成時即釋放計時器。HttpListener 的請求串流不理會取消權杖,所以不用權杖。
    /// </summary>
    private async Task<int> ReadWithIdleTimeoutAsync(Stream input, byte[] buffer)
    {
        var read = input.ReadAsync(buffer).AsTask();
        try { return await read.WaitAsync(BodyIdleTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Abort 後這個讀取會以例外結束:先掛上觀察,避免 UnobservedTaskException
            _ = read.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new BodyStalledException(BodyIdleTimeout);
        }
    }

    /// <summary>讀整個 body(最多 <paramref name="max"/> 位元組;超過回 null)。每次讀取有閒置逾時(<see cref="BodyIdleTimeout"/>,逾時拋 BodyStalledException)。</summary>
    private async Task<byte[]?> ReadBodyAsync(HttpListenerRequest req, long max)
    {
        if (req.ContentLength64 > max) { await DrainAsync(req).ConfigureAwait(false); return null; }
        using var ms = new MemoryStream(req.ContentLength64 > 0 ? (int)Math.Min(req.ContentLength64, MaxBodyPreallocBytes) : 0);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await ReadWithIdleTimeoutAsync(req.InputStream, buffer).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > max) { await DrainAsync(req).ConfigureAwait(false); return null; }
            ms.Write(buffer, 0, n);
        }
        // 防禦:宣告的長度沒收滿就結束(Linux 受控實作在提前 EOF 時本來就丟 HttpListenerException 400)不當成完整片段寫入
        if (req.ContentLength64 > 0 && ms.Length < req.ContentLength64) throw new IOException($"body 不完整:收到 {ms.Length} / {req.ContentLength64} 位元組");
        return ms.ToArray();
    }

    /// <summary>
    /// 把尚未讀的 body 讀掉(回錯誤前;否則客戶端可能收到連線重設而不是狀態碼)。連線已斷時略過(之後的回應寫不出去會 Abort);
    /// 超過 <see cref="BodyIdleTimeout"/> 沒有進度時拋 BodyStalledException(呼叫端 Abort,不再回應)。
    /// </summary>
    private async Task DrainAsync(HttpListenerRequest req)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (await ReadWithIdleTimeoutAsync(req.InputStream, buffer).ConfigureAwait(false) is var n && n > 0)
            {
                total += n;
                if (total > MaxChunkBytes * 2) break;
            }
        }
        catch (BodyStalledException) { throw; }
        catch (Exception) { /* 連線已斷 */ }
    }

    /// <summary>背景執行(呼叫端不 await):排空後回 JSON;body 卡住時 Abort。不拋例外。</summary>
    private async Task DrainThenJson(HttpListenerRequest req, HttpListenerResponse res, int status, object payload)
    {
        try { await DrainAsync(req).ConfigureAwait(false); }
        catch (BodyStalledException)
        {
            Abort(res);
            return;
        }
        await WriteJsonAsync(res, status, payload).ConfigureAwait(false);
    }

    /// <summary>回 JSON 並關閉;寫出或關閉失敗(客戶端已 RST、已斷線)時 Abort。不拋例外:每個錯誤路徑最後一定 Close 或 Abort。</summary>
    private async Task WriteJsonAsync(HttpListenerResponse res, int status, object payload)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ReplyJson);
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            res.Close();
        }
        catch (Exception) { Abort(res); }
    }

    /// <summary>回純文字並關閉;失敗時 Abort。不拋例外。</summary>
    private async Task WriteTextAsync(HttpListenerResponse res, int status, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.StatusCode = status;
            res.ContentType = "text/plain; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            res.Close();
        }
        catch (Exception) { Abort(res); }
    }

    /// <summary>
    /// 停止監聽;進行中的錄影定稿(stopReason = hostShutdown;暫停中者 idleTimeout、stoppedUtc = 最後一次活動),不留 .part。
    /// 可重複呼叫、啟動失敗後也可呼叫,不拋例外。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var listener = _listener;
        // 取消權杖與中止監聽同時在背景進行(同步段不做可能阻塞的事,見 HttpListenerStarter.StopClientsAsync;不用 Stop():
        // Windows 上仍有連線時會卡住,見 HttpListenerStarter.AbortNow);等接受迴圈有上限,之後一定執行下面的定稿(尚未 stop 的錄影在紀錄關閉前定稿)
        var wait = TimeSpan.FromSeconds(1.5);
        var cancelling = HttpListenerStarter.StopClientsAsync(_cts, Array.Empty<System.Net.WebSockets.WebSocket>(), wait);
        var listenerStopping = HttpListenerStarter.ShutdownAsync(listener, wait);
        var cancelled = await cancelling.ConfigureAwait(false);
        if (!await listenerStopping.ConfigureAwait(false))
            Log?.Invoke("錄影上傳端的監聽未在 1.5 s 內關閉,不再等待");
        if (!await HttpListenerStarter.WaitBoundedAsync(_acceptLoop, wait).ConfigureAwait(false))
            Log?.Invoke("錄影上傳端的接受迴圈未在 1.5 s 內結束,不再等待");
        await HttpListenerStarter.WaitBoundedAsync(_idleSweep, wait).ConfigureAwait(false);
        foreach (var s in _active.Values.ToList())
        {
            try
            {
                lock (s.Sync)
                {
                    if (s.Suspended) Finalize(s, null, IdleTimeoutReason, s.LastActivityUtc);
                    else Finalize(s, null, "hostShutdown");
                }
            }
            catch (Exception ex) { Log?.Invoke($"錄影 {s.MediaId} 定稿失敗:{ex.Message}"); }
        }
        _listener = null;
        if (cancelled) _cts.Dispose();   // 取消仍卡在背景時不釋放
    }
}
