using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimosaBRM.SimCore.Host;
using SimosaBRM.SimCore.Recording;
using SimosaBRM.SimCore.Ship;
using Xunit;

namespace SimosaBRM.SimCore.Tests;

/// <summary>
/// 練習錄影上傳端(MediaUploadServer;docs/design/screen-recording.md):HttpClient 依序上傳三片段 → .webm 內容為片段串接、旁檔
/// (起訖、長度、大小、SHA-256、對應紀錄檔名與 header.createdUtc)正確;重送/跳號的序號語意;超過 --media-max-mb 回 413 並標 truncated;
/// 拒絕外部來源(403);CORS preflight;主控程序結束時進行中的錄影定稿。暫存目錄在專案內 build/test-tmp/media/(CLAUDE.md)。
/// </summary>
public sealed class MediaUploadTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root = RepositoryPaths.FindRoot();

    public MediaUploadTests()
    {
        _dir = Path.Combine(_root, "build", "test-tmp", "media", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private MediaUploadServer NewServer(long maxBytes = 1024 * 1024, string? sub = "media", Action<MediaUploadServer>? configure = null)
    {
        var s = new MediaUploadServer(sub is null ? _dir : Path.Combine(_dir, sub), FreePort(), maxBytes) { ProjectRoot = _root, MinFreeBytes = 0 };
        configure?.Invoke(s);
        s.Start();
        return s;
    }

    private static HttpClient Client(MediaUploadServer s) => new() { BaseAddress = new Uri($"http://127.0.0.1:{s.Port}/") };

    private static async Task<JsonDocument> StartAsync(HttpClient c, object body)
    {
        var res = await c.PostAsync("media/start", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> ChunkAsync(HttpClient c, string mediaId, int seq, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var req = new HttpRequestMessage(HttpMethod.Post, $"media/chunk/{mediaId}") { Content = content };
        req.Headers.Add("X-Seq", seq.ToString());
        return await c.SendAsync(req);
    }

    private static async Task<JsonDocument> StopAsync(HttpClient c, string mediaId, object? body = null)
    {
        var res = await c.PostAsync($"media/stop/{mediaId}", body is null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private static byte[] Bytes(int n, byte seed)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(seed + i);
        return b;
    }

    /// <summary>合成一份紀錄檔(只需 header 行;與 RecordWriter 相同欄位)。</summary>
    private string WriteRecord(string name, DateTime createdUtc, string scenarioId)
    {
        var dir = Path.Combine(_dir, "records");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new { kind = "header", version = 2, createdUtc = createdUtc.ToString("O"), shipId = "FSB1", dt = 0.02, scenario = new { id = scenarioId } }) + "\n{\"kind\":\"state\",\"state\":{\"t\":1}}\n");
        return path;
    }

    [Fact]
    public async Task ThreeChunks_FileIsConcatenation_SidecarLinksRecord()
    {
        var created = new DateTime(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc);
        var record = WriteRecord("20261012-013000-E01_baseline.jsonl", created, "E01_baseline");
        await using var server = NewServer();
        server.CurrentRecordPath = () => record;
        using var c = Client(server);

        using var start = await StartAsync(c, new { exerciseId = "2026-10-12T01:30:05Z", station = "instructor", mime = "video/webm;codecs=vp9,opus" });
        var mediaId = start.RootElement.GetProperty("mediaId").GetString()!;
        Assert.StartsWith("202", mediaId);
        Assert.Contains("instructor", mediaId);
        Assert.Equal("20261012-013000-E01_baseline.jsonl", start.RootElement.GetProperty("record").GetString());
        Assert.True(File.Exists(Path.Combine(server.MediaDir, mediaId + ".webm.part")));
        Assert.Equal(1, server.ActiveCount);

        var parts = new[] { Bytes(1000, 1), Bytes(1500, 7), Bytes(10, 99) };
        for (var i = 0; i < parts.Length; i++)
        {
            var res = await ChunkAsync(c, mediaId, i, parts[i]);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(i, doc.RootElement.GetProperty("seq").GetInt32());
        }
        var all = parts.SelectMany(p => p).ToArray();

        using var stop = await StopAsync(c, mediaId, new { durationS = 12.5, reason = "client" });
        var sc = MediaSidecar.Parse(stop.RootElement.GetRawText());
        Assert.Equal(MediaSidecar.FormatId, sc.Format);
        Assert.Equal(mediaId, sc.MediaId);
        Assert.Equal(mediaId + ".webm", sc.File);
        Assert.Equal(all.Length, sc.SizeBytes);
        Assert.Equal(3, sc.Chunks);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(all)), sc.Sha256);
        Assert.Equal(12.5, sc.DurationS);
        Assert.False(sc.DurationEstimated);
        Assert.False(sc.Truncated);
        Assert.Equal("client", sc.StopReason);
        Assert.Equal("instructor", sc.Station);
        Assert.Equal("video/webm;codecs=vp9,opus", sc.Mime);
        Assert.NotNull(sc.StoppedUtc);
        Assert.True(sc.StoppedUtc >= sc.StartedUtc);
        Assert.NotNull(sc.Record);
        Assert.Equal("20261012-013000-E01_baseline.jsonl", sc.Record!.File);
        Assert.Equal(created, sc.Record.CreatedUtc);
        Assert.Equal("E01_baseline", sc.Record.ScenarioId);
        Assert.Equal(1024 * 1024, sc.MaxBytes);

        var final = Path.Combine(server.MediaDir, mediaId + ".webm");
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(final + ".part"));
        Assert.Equal(all, await File.ReadAllBytesAsync(final));
        var side = MediaSidecar.Load(Path.Combine(server.MediaDir, mediaId + ".json"));
        Assert.Equal(sc.Sha256, side.Sha256);
        Assert.Equal(0, server.ActiveCount);

        // 重複 stop:回同一份旁檔;之後的片段 410
        using var again = await StopAsync(c, mediaId);
        Assert.Equal(sc.Sha256, again.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(HttpStatusCode.Gone, (await ChunkAsync(c, mediaId, 3, Bytes(5, 1))).StatusCode);
    }

    [Fact]
    public async Task SeqSemantics_DuplicateIsIdempotent_GapIsConflict()
    {
        await using var server = NewServer();
        using var c = Client(server);
        using var start = await StartAsync(c, new { station = "instructor" });
        var id = start.RootElement.GetProperty("mediaId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, id, 0, Bytes(100, 1))).StatusCode);
        // 重送(教官站未收到回應而重試):不再寫入、回 duplicate
        var dup = await ChunkAsync(c, id, 0, Bytes(100, 1));
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        Assert.Contains("\"duplicate\":true", await dup.Content.ReadAsStringAsync());
        // 跳號:409 並告知預期序號
        var gap = await ChunkAsync(c, id, 2, Bytes(100, 1));
        Assert.Equal(HttpStatusCode.Conflict, gap.StatusCode);
        Assert.Contains("\"expectedSeq\":1", await gap.Content.ReadAsStringAsync());
        // 缺 X-Seq:400;未知 mediaId:404
        var noSeq = await c.PostAsync($"media/chunk/{id}", new ByteArrayContent(Bytes(3, 1)));
        Assert.Equal(HttpStatusCode.BadRequest, noSeq.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ChunkAsync(c, "nope", 0, Bytes(3, 1))).StatusCode);
        using var stop = await StopAsync(c, id);
        Assert.Equal(100, stop.RootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(1, stop.RootElement.GetProperty("chunks").GetInt32());
        Assert.True(stop.RootElement.GetProperty("durationEstimated").GetBoolean());
        Assert.Null(stop.RootElement.TryGetProperty("record", out var rec) ? rec.GetRawText() : null);
    }

    [Fact]
    public async Task OverLimit_413_StopsReceiving_SidecarTruncated()
    {
        await using var server = NewServer(maxBytes: 1000);
        using var c = Client(server);
        using var start = await StartAsync(c, new { station = "instructor" });
        var id = start.RootElement.GetProperty("mediaId").GetString()!;
        Assert.Equal(1000, start.RootElement.GetProperty("maxBytes").GetInt64());
        var first = Bytes(600, 3);
        Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, id, 0, first)).StatusCode);
        var over = await ChunkAsync(c, id, 1, Bytes(600, 4));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Contains("\"truncated\":true", await over.Content.ReadAsStringAsync());
        // 之後任何片段(含合法序號)都不再收
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await ChunkAsync(c, id, 1, Bytes(10, 4))).StatusCode);
        using var stop = await StopAsync(c, id);
        var sc = MediaSidecar.Parse(stop.RootElement.GetRawText());
        Assert.True(sc.Truncated);
        Assert.Equal(600, sc.SizeBytes);
        Assert.Equal(1, sc.Chunks);
        Assert.Equal("limit", sc.StopReason);
        Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(server.MediaDir, sc.File)));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), sc.Sha256);
        Assert.True(MediaSidecar.Load(Path.Combine(server.MediaDir, id + ".json")).Truncated);
    }

    [Fact]
    public async Task ExternalSource_Rejected403()
    {
        await using var server = NewServer();
        // 模擬來源為公網位址(loopback 連線無法真的從外部來;過濾函式與 shutdown 指令相同)
        server.ClientFilter = ep => HostCommandRouter.IsAllowedShutdownClient(new IPEndPoint(IPAddress.Parse("203.0.113.9"), ep?.Port ?? 0));
        using var c = Client(server);
        var res = await c.PostAsync("media/start", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(0, server.ActiveCount);
        Assert.Empty(Directory.Exists(server.MediaDir) ? Directory.GetFiles(server.MediaDir) : Array.Empty<string>());
        // 直連私有網段與本機允許
        Assert.True(HostCommandRouter.IsAllowedShutdownClient(new IPEndPoint(IPAddress.Parse("192.168.77.21"), 5)));
        Assert.True(HostCommandRouter.IsAllowedShutdownClient(new IPEndPoint(IPAddress.Loopback, 5)));
        Assert.False(HostCommandRouter.IsAllowedShutdownClient(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 5)));
    }

    [Fact]
    public async Task CorsPreflight_And_Status()
    {
        await using var server = NewServer();
        using var c = Client(server);
        var pre = await c.SendAsync(new HttpRequestMessage(HttpMethod.Options, "media/chunk/x"));
        Assert.Equal(HttpStatusCode.NoContent, pre.StatusCode);
        Assert.Equal("*", pre.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("X-Seq", pre.Headers.GetValues("Access-Control-Allow-Headers").Single());
        using var start = await StartAsync(c, new { station = "instructor", exerciseId = "E05" });
        var id = start.RootElement.GetProperty("mediaId").GetString()!;
        await ChunkAsync(c, id, 0, Bytes(50, 1));
        var status = await c.GetStringAsync("media/status");
        using var doc = JsonDocument.Parse(status);
        var active = Assert.Single(doc.RootElement.GetProperty("active").EnumerateArray());
        Assert.Equal(id, active.GetProperty("mediaId").GetString());
        Assert.Equal(50, active.GetProperty("bytes").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("media/other", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.DeleteAsync("media/start")).StatusCode);
    }

    [Fact]
    public async Task Dispose_FinalizesActiveRecording()
    {
        var server = NewServer();
        string id;
        using (var c = Client(server))
        {
            using var start = await StartAsync(c, new { station = "instructor-simple" });
            id = start.RootElement.GetProperty("mediaId").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, id, 0, Bytes(200, 9))).StatusCode);
        }
        await server.DisposeAsync();
        Assert.False(File.Exists(Path.Combine(server.MediaDir, id + ".webm.part")));
        Assert.True(File.Exists(Path.Combine(server.MediaDir, id + ".webm")));
        var sc = MediaSidecar.Load(Path.Combine(server.MediaDir, id + ".json"));
        Assert.Equal("hostShutdown", sc.StopReason);
        Assert.Equal(200, sc.SizeBytes);
        Assert.True(sc.DurationEstimated);
    }

    [Fact]
    public void Options_Parse_And_ResolveDir()
    {
        var o = HostOptions.Parse(new[] { "--media-port", "9001", "--media-dir", "build/tmp/media-x", "--media-max-mb", "10" });
        Assert.Equal(9001, o.MediaPort);
        Assert.Equal("build/tmp/media-x", o.MediaDir);
        Assert.Equal(10, o.MediaMaxMb);
        Assert.True(o.EnableMedia);
        Assert.False(HostOptions.Parse(new[] { "--no-media" }).EnableMedia);
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[] { "--media-max-mb", "0" }));
        var def = new HostOptions();
        Assert.Equal(8768, def.MediaPort);
        Assert.Equal(2048, def.MediaMaxMb);
        Assert.Null(def.MediaDir);
        Assert.Equal(Path.Combine(_root, "build", "records", "media"), MediaUploadServer.ResolveDir(_root, null));
        Assert.Equal(Path.Combine(_root, "build", "tmp", "media-x"), MediaUploadServer.ResolveDir(_root, "build/tmp/media-x"));
        Assert.Throws<ArgumentException>(() => MediaUploadServer.ResolveDir(_root, TestData.OutsideOf(_root, "outside")));
        Assert.Throws<ArgumentException>(() => MediaUploadServer.ResolveDir(_root, "../outside"));
    }

    // ------------------------------------------------------------------ 第三十輪 2 小時搜尋:有上限的資源(host-media-*)

    /// <summary>可手動前進的時鐘(閒置逾時的確定性測試)</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// host-media-stalled-body(minor):上傳到一半對端消失(不送 FIN 也不送 RST)時,修正前 ReadBodyAsync 沒有逾時,永久持有片段大小的緩衝與連線
    /// (Linux 受控 HttpListener 沒有 body 逾時;probeB 20 條 150 s 後仍在)。修正後 BodyIdleTimeout 內沒有進度就 Abort:伺服端關閉連線、連線表回到基準,
    /// 該錄影不受影響(之後同一序號的片段照常寫入)。錯誤回應前的排空(未知端點)同樣有逾時。
    /// </summary>
    [Fact]
    public async Task StalledBody_IsAbortedAfterIdleTimeout_AndTheRecordingContinues()
    {
        await using var server = NewServer(configure: s => s.BodyIdleTimeout = TimeSpan.FromMilliseconds(300));
        using var c = Client(server);
        using var start = await StartAsync(c, new { station = "instructor" });
        var id = start.RootElement.GetProperty("mediaId").GetString()!;
        var baseline = ListenerConnections.Count(server);
        using var chunk = await RawHttp.SendThenHoldAsync(server.Port, RawHttp.PartialPost(server.Port, $"/media/chunk/{id}", 100_000, 1000));
        using var drain = await RawHttp.SendThenHoldAsync(server.Port, RawHttp.PartialPost(server.Port, "/media/other", 100_000, 1000));
        Assert.True(await RawHttp.ServerClosesAsync(chunk, TimeSpan.FromSeconds(10)), "卡住的片段:伺服端應在逾時後中止連線");
        Assert.True(await RawHttp.ServerClosesAsync(drain, TimeSpan.FromSeconds(10)), "卡住的錯誤回應排空:伺服端應在逾時後中止連線");
        Assert.True(Interlocked.Read(ref server.RequestsAborted) >= 2);
        if (baseline is { } b) await ListenerConnections.WaitUntilAtMostAsync(server, b, "body 卡住的連線");
        // 被中止的片段沒有寫入:同一序號照常接收
        var res = await ChunkAsync(c, id, 0, Bytes(500, 3));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("duplicate", await res.Content.ReadAsStringAsync());
        Assert.Equal(1, server.ActiveCount);
    }

    /// <summary>
    /// host-media-rst-midbody-leak(nit):片段上傳中途 RST 時讀 body 丟例外、500 也寫不出去,修正前 Close/Abort 都沒執行,
    /// 連線留在 Linux 受控 HttpListener 的連線表(probeB 100 次 socket +100、受控堆積 +8.7 MB)。修正後每個錯誤路徑最後一定 Close 或 Abort。
    /// </summary>
    [Fact]
    public async Task ResetMidBody_ReleasesTheConnection_AndTheRecordingContinues()
    {
        await using var server = NewServer();
        using var c = Client(server);
        using var start = await StartAsync(c, new { station = "instructor" });
        var id = start.RootElement.GetProperty("mediaId").GetString()!;
        var baseline = ListenerConnections.Count(server);
        for (var i = 0; i < 20; i++)
            await RawHttp.SendThenResetAsync(server.Port, RawHttp.PartialPost(server.Port, $"/media/chunk/{id}", 200_000, 1000), delayMs: 30);
        if (baseline is { } b) await ListenerConnections.WaitUntilAtMostAsync(server, b, "上傳中途 RST 的連線");
        var res = await ChunkAsync(c, id, 0, Bytes(800, 5));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("duplicate", await res.Content.ReadAsStringAsync());
        using var stop = await StopAsync(c, id);
        Assert.Equal(800, stop.RootElement.GetProperty("sizeBytes").GetInt64());
    }

    /// <summary>
    /// host-media-abandoned-sessions(nit)與第三十輪審查 REV-MEDIA:開始後沒有 stop 的錄影(教官站分頁重新整理或當機)修正前持有檔案代號
    /// 到主控程序結束(probeE 200 次:active 200、fd +204)。修正後 SessionIdleTimeout 沒有任何片段請求就暫停寫檔(關閉 .part、釋放檔案代號),
    /// 但不定稿:畫面靜止、沒有音訊時 MediaRecorder 可能數分鐘不吐片段(review/web/mr.mjs 實測 canvas 靜止 14 s 沒有 dataavailable),
    /// 之後的片段必須接續寫入同一份錄影(審查前的版本在此定稿,下一片收到 410,教官站把 410 當成致命錯誤,這次訓練剩下的部分就沒有錄到)。
    /// 接續後內容、片段數與 SHA-256 與沒有暫停時相同;片段正在上傳時不暫停;主控程序結束時暫停中的錄影以 idleTimeout 定稿(stoppedUtc = 最後一次活動)。
    /// </summary>
    [Fact]
    public async Task IdleRecording_IsSuspended_ThenResumesOnTheNextChunk()
    {
        var clock = new ManualClock();
        var server = NewServer(configure: s =>
        {
            s.Clock = clock;
            s.IdleSweepInterval = TimeSpan.FromHours(1);   // 只由測試呼叫 SweepIdleSessions
            s.BodyIdleTimeout = TimeSpan.FromMinutes(10);
        });
        try
        {
            using var c = Client(server);
            using var startA = await StartAsync(c, new { station = "a" });
            var a = startA.RootElement.GetProperty("mediaId").GetString()!;
            var parts = new[] { Bytes(100, 1), Bytes(100, 2), Bytes(100, 3), Bytes(100, 4) };
            Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, a, 0, parts[0])).StatusCode);
            using var startB = await StartAsync(c, new { station = "b" });
            var b = startB.RootElement.GetProperty("mediaId").GetString()!;
            var startedB = clock.Now.UtcDateTime;

            clock.Now += TimeSpan.FromMinutes(4);
            Assert.Equal(0, server.SweepIdleSessions());
            Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, a, 1, parts[1])).StatusCode);   // A 仍在錄
            clock.Now += TimeSpan.FromMinutes(2);
            Assert.Equal(1, server.SweepIdleSessions());   // B 已 6 分鐘沒有活動:暫停寫檔;A 才 2 分鐘
            Assert.Equal(2, server.ActiveCount);
            Assert.Equal(1, server.SuspendedCount);
            Assert.False(HasOpenStream(server, b));
            Assert.True(HasOpenStream(server, a));
            Assert.True(File.Exists(Path.Combine(server.MediaDir, b + ".webm.part")));   // 不定稿
            Assert.False(File.Exists(Path.Combine(server.MediaDir, b + ".json")));
            if (OperatingSystem.IsLinux()) Assert.Equal(0, OpenHandles(Path.Combine(server.MediaDir, b + ".webm.part")));

            // 畫面靜止 30 分鐘後 A 也暫停;再有變化時下一片接續寫入(修正前:A 在 5 分鐘時已定稿,這一片 410)
            clock.Now += TimeSpan.FromMinutes(30);
            Assert.Equal(1, server.SweepIdleSessions());
            Assert.Equal(0, server.SweepIdleSessions());   // 已暫停者不重複計數
            Assert.Equal(2, server.SuspendedCount);
            Assert.False(HasOpenStream(server, a));
            if (OperatingSystem.IsLinux()) Assert.Equal(0, OpenHandles(Path.Combine(server.MediaDir, a + ".webm.part")));
            var res = await ChunkAsync(c, a, 2, parts[2]);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using (var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync())) Assert.Equal(300, doc.RootElement.GetProperty("bytes").GetInt64());
            Assert.True(HasOpenStream(server, a));
            Assert.Equal(1, server.SuspendedCount);
            Assert.Equal(1, Interlocked.Read(ref server.SessionsResumed));
            // 重送已收到的序號(上一個回應遺失)不重新開檔、不重複寫入
            Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, a, 2, parts[2])).StatusCode);

            // 片段正在上傳(body 還沒收完)時不暫停,即使最後一次活動已超過逾時
            using (var held = await RawHttp.SendThenHoldAsync(server.Port, RawHttp.PartialPost(server.Port, $"/media/chunk/{a}", 10_000, 100, seq: 3)))
            {
                await WaitForInFlightAsync(server, a, 1);
                clock.Now += TimeSpan.FromMinutes(30);
                Assert.Equal(0, server.SweepIdleSessions());
                Assert.True(HasOpenStream(server, a));
                held.Close();   // 上傳中斷:讀取失敗,活動時間更新為此刻、該片不寫入
                await WaitForInFlightAsync(server, a, 0);
            }
            Assert.Equal(0, server.SweepIdleSessions());
            Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, a, 3, parts[3])).StatusCode);
            using var stopA = await StopAsync(c, a);
            var all = parts.SelectMany(x => x).ToArray();
            Assert.Equal(all, File.ReadAllBytes(Path.Combine(server.MediaDir, a + ".webm")));
            var scA = MediaSidecar.Load(Path.Combine(server.MediaDir, a + ".json"));
            Assert.Equal("client", scA.StopReason);
            Assert.Equal(4, scA.Chunks);
            Assert.Equal(all.Length, scA.SizeBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(all)), scA.Sha256);
            Assert.Equal(0, Interlocked.Read(ref server.SessionsTimedOut));

            // 主控程序結束:暫停中的 B 以 idleTimeout 定稿,stoppedUtc = 最後一次活動(開始時刻)
            await server.DisposeAsync();
            var scB = MediaSidecar.Load(Path.Combine(server.MediaDir, b + ".json"));
            Assert.Equal(MediaUploadServer.IdleTimeoutReason, scB.StopReason);
            Assert.Equal(startedB, scB.StoppedUtc);
            Assert.True(scB.DurationEstimated);
            Assert.Equal(0.0, scB.DurationS);
            Assert.True(File.Exists(Path.Combine(server.MediaDir, b + ".webm")));
            Assert.False(File.Exists(Path.Combine(server.MediaDir, b + ".webm.part")));
        }
        finally { await server.DisposeAsync(); }
    }

    /// <summary>
    /// 暫停中的錄影有上限(MediaUploadServer.MaxSuspendedSessions):超過時最後一次活動最早者定稿(stopReason idleTimeout、stoppedUtc = 最後一次活動,
    /// .webm + 旁檔);之後的片段 410、stop 回同一份旁檔。正在錄的那份最後活動最晚,不受影響。200 份被放棄的錄影也只保留上限份數的工作階段。
    /// </summary>
    [Fact]
    public async Task SuspendedRecordings_AreBounded_OldestIsFinalized()
    {
        var clock = new ManualClock();
        await using var server = NewServer(configure: s =>
        {
            s.Clock = clock;
            s.IdleSweepInterval = TimeSpan.FromHours(1);
        });
        using var c = Client(server);
        var ids = new List<(string Id, DateTime Last)>();
        for (var i = 0; i < MediaUploadServer.MaxSuspendedSessions + 2; i++)
        {
            using var start = await StartAsync(c, new { station = $"s{i}" });
            var id = start.RootElement.GetProperty("mediaId").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, id, 0, Bytes(50, (byte)i))).StatusCode);
            ids.Add((id, clock.Now.UtcDateTime));
            clock.Now += TimeSpan.FromSeconds(10);
        }
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Equal(ids.Count, server.SweepIdleSessions());
        Assert.Equal(MediaUploadServer.MaxSuspendedSessions, server.ActiveCount);
        Assert.Equal(MediaUploadServer.MaxSuspendedSessions, server.SuspendedCount);
        Assert.Equal(2, Interlocked.Read(ref server.SessionsTimedOut));
        foreach (var (id, last) in ids.Take(2))
        {
            var sc = MediaSidecar.Load(Path.Combine(server.MediaDir, id + ".json"));
            Assert.Equal(MediaUploadServer.IdleTimeoutReason, sc.StopReason);
            Assert.Equal(last, sc.StoppedUtc);
            Assert.Equal(1, sc.Chunks);
            Assert.True(File.Exists(Path.Combine(server.MediaDir, id + ".webm")));
            Assert.Equal(HttpStatusCode.Gone, (await ChunkAsync(c, id, 1, Bytes(10, 1))).StatusCode);
            using var stop = await StopAsync(c, id);
            Assert.Equal(MediaUploadServer.IdleTimeoutReason, stop.RootElement.GetProperty("stopReason").GetString());
        }
        // 最近的一份仍可接續
        Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, ids[^1].Id, 1, Bytes(10, 1))).StatusCode);
        Assert.Equal(MediaUploadServer.MaxSuspendedSessions - 1, server.SuspendedCount);
        // 被放棄的錄影一直增加:工作階段數仍有上限
        for (var i = 0; i < 40; i++)
        {
            using var start = await StartAsync(c, new { station = $"x{i}" });
            clock.Now += TimeSpan.FromMinutes(6);
            server.SweepIdleSessions();
        }
        Assert.InRange(server.ActiveCount, 1, MediaUploadServer.MaxSuspendedSessions + 1);
        Assert.InRange(server.SuspendedCount, 1, MediaUploadServer.MaxSuspendedSessions);
    }

    /// <summary>背景閒置檢查(真實時鐘、短逾時):沒有片段的錄影自動暫停(不定稿),之後的片段接續寫入。</summary>
    [Fact]
    public async Task IdleRecording_BackgroundSweep_SuspendsIt()
    {
        await using var server = NewServer(configure: s =>
        {
            s.SessionIdleTimeout = TimeSpan.FromMilliseconds(200);
            s.IdleSweepInterval = TimeSpan.FromMilliseconds(50);
        });
        using var c = Client(server);
        using var start = await StartAsync(c, new { station = "instructor" });
        var id = start.RootElement.GetProperty("mediaId").GetString()!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (server.SuspendedCount == 0)
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("閒置的錄影未被背景檢查暫停");
            await Task.Delay(20);
        }
        Assert.Equal(1, server.ActiveCount);
        Assert.Equal(HttpStatusCode.OK, (await ChunkAsync(c, id, 0, Bytes(64, 5))).StatusCode);
        using var stop = await StopAsync(c, id);
        Assert.Equal(64, stop.RootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("client", stop.RootElement.GetProperty("stopReason").GetString());
    }

    /// <summary>以反射看某段錄影目前是否開著 .part(Session.Stream)</summary>
    private static bool HasOpenStream(MediaUploadServer server, string mediaId)
    {
        var active = (System.Collections.IDictionary)typeof(MediaUploadServer).GetField("_active", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(server)!;
        var session = active[mediaId]!;
        return session.GetType().GetField("Stream")!.GetValue(session) is not null;
    }

    /// <summary>本程序開著某檔案的檔案代號數(Linux:/proc/self/fd 的符號連結)</summary>
    private static int OpenHandles(string path)
    {
        var full = Path.GetFullPath(path);
        var n = 0;
        foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try { if (new FileInfo(fd).LinkTarget == full) n++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>以反射讀某段錄影正在讀 body 的片段請求數(Session.InFlight)</summary>
    private static async Task WaitForInFlightAsync(MediaUploadServer server, string mediaId, int expected)
    {
        var active = (System.Collections.IDictionary)typeof(MediaUploadServer).GetField("_active", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(server)!;
        var session = active[mediaId]!;
        var field = session.GetType().GetField("InFlight")!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((int)field.GetValue(session)! != expected)
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"InFlight 未變成 {expected}");
            await Task.Delay(10);
        }
    }
}
