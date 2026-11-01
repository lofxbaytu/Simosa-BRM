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

    private MediaUploadServer NewServer(long maxBytes = 1024 * 1024, string? sub = "media")
    {
        var s = new MediaUploadServer(sub is null ? _dir : Path.Combine(_dir, sub), FreePort(), maxBytes) { ProjectRoot = _root, MinFreeBytes = 0 };
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
        Assert.Throws<ArgumentException>(() => MediaUploadServer.ResolveDir(_root, Path.Combine(Path.GetTempPath(), "outside")));
        Assert.Throws<ArgumentException>(() => MediaUploadServer.ResolveDir(_root, "../outside"));
    }
}
