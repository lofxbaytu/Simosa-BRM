using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SimosaBRM.Packaging.Cli;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Records;
using SimosaBRM.Packaging.Release;
using SimosaBRM.Packaging.Sync;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Tests;

/// <summary>
/// 教官端同步 / 隨身碟匯入時印出並保存船上筆電的發行版與降版提醒(使用者 2026-11-25 的選擇;docs/design/packaging-sync.md 第 5.1 節末、第 7 節):
/// 以指令列(<c>simosa-pkg sync</c> / <c>export</c> / <c>import</c> / <c>store list</c>)的輸出與 <c>fleet-versions.json</c> 的內容斷言 —
/// 有降版、沒有降版、船上沒有回報(舊版軟體)、船上沒有發行清單、惡意內容(控制字元、ANSI 跳脫、超長字串、型別不對、巢狀物件)、
/// 舊的 fleet-versions.json、兩艘船互不影響、降版處理掉之後再同步提醒消失。直連的船端是真的 <see cref="SyncServer"/>;
/// 「舊版船上」與惡意內容用照規格握手、status 回傳指定位元組的假船端(真的 TCP 連線)。
/// </summary>
public sealed class SyncReleaseStatusTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    // ── 做出「安裝版」與降版(與 InstalledVerifyTests 相同的作法)──────────────

    private static void W(string root, string rel, string content)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    /// <summary>發行清單不含的位置(安裝後才建立:信任清單、裝置金鑰、packaging.json、state/、執行期資料)</summary>
    private static bool CreatedAfterInstall(string path)
        => path.StartsWith("state/", StringComparison.Ordinal) || path.StartsWith("config/trusted-keys/", StringComparison.Ordinal)
           || path.StartsWith("config/device/", StringComparison.Ordinal) || path == "config/packaging.json" || path.StartsWith("build/", StringComparison.Ordinal);

    private static void WriteManifest(InstallLayout ship, string version, DateTimeOffset builtUtc)
    {
        var m = ReleaseManifestBuilder.Build(ship.Root, version, "0123456789abcdef", "main", "linux-x64", clock: new ManualClock(builtUtc));
        m.Files.RemoveAll(f => CreatedAfterInstall(f.Path));
        ReleaseManifestBuilder.Save(m, ship.Root);
    }

    private static void WriteProgramFiles(InstallLayout ship, string version)
    {
        W(ship.Root, "app/version.txt", $"app {version}\n");
        W(ship.Root, "app/simcore/SimosaBRM.SimCore.Host.dll", $"fake host {version}\n");
        W(ship.Root, "tools/updater/simosa-pkg.dll", $"fake pkg {version}\n");
    }

    /// <summary>剛以安裝程式安裝好的船上筆電(有 manifest.json)。</summary>
    private InstallLayout NewInstall(string name, string shipId, string version)
    {
        var ship = _env.NewShip(name, shipId);
        WriteProgramFiles(ship, version);
        WriteManifest(ship, version, DateTimeOffset.UtcNow.AddDays(-60));
        return ship;
    }

    /// <summary>以安裝程式重新安裝另一個發行版:[InstallDelete] 先清空程式目錄,再放入該版的檔案與 manifest.json;state\ 與設定不動。</summary>
    private static void Reinstall(InstallLayout ship, string version, DateTimeOffset builtUtc)
    {
        foreach (var prefix in InstalledPaths.ProgramPrefixes)
        {
            var dir = Path.Combine(ship.Root, prefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        WriteProgramFiles(ship, version);
        WriteManifest(ship, version, builtUtc);
    }

    /// <summary>發行版 2026.12.1 上套用軟體更新包 2026.12.2,再以較舊的安裝程式 2026.11.1 重新安裝:降版。</summary>
    private InstallLayout NewDowngradedInstall(string name, string shipId)
    {
        var ship = NewInstall(name, shipId, "2026.12.1");
        using var key = _env.Primary();
        Assert.True(new UpdateApplier(ship).Apply(_env.SoftwarePackage("2026.12.2", key, "app 2026.12.2\n", $"sw-{name}.sbupd")).Ok);
        Reinstall(ship, "2026.11.1", DateTimeOffset.UtcNow.AddDays(-90));
        return ship;
    }

    // ── 執行指令 ─────────────────────────────────────────

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = new CliApp(o, e).Run(args);
        return (code, o.ToString(), e.ToString());
    }

    private string KeyFile()
    {
        var path = _env.PathIn("primary.key.pem");
        if (!File.Exists(path)) File.WriteAllText(path, _env.PrimaryPriv);
        return path;
    }

    private Task<(int Code, string Out, string Err)> SyncCli(int port, string store)
        => Task.Run(() => Run("sync", "--ship", "127.0.0.1", "--port", port.ToString(), "--key", KeyFile(), "--store", store, "--root", _env.InstructorRoot, "--enroll-device"));

    private static async Task<SyncServer> StartServer(InstallLayout ship)
    {
        var server = new SyncServer(new ShipNode(ship), IPAddress.Loopback, 0) { IdleTimeout = TimeSpan.FromSeconds(20) };
        server.Start();
        await Task.Yield();
        return server;
    }

    private static JsonObject FleetEntry(string store, string shipId)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(store, "fleet-versions.json")))!["ships"]![shipId]!.AsObject();

    /// <summary>船上 <c>simosa-pkg status</c> 的降版警告(stderr「警告:降版:…」那一行去掉「警告:」)。</summary>
    private string ShipStatusDowngradeMessage(InstallLayout ship)
    {
        var st = Run("status", "--root", ship.Root);
        Assert.Equal(0, st.Code);
        var line = Assert.Single(st.Err.Split('\n', StringSplitOptions.TrimEntries), l => l.StartsWith("警告:降版:", StringComparison.Ordinal));
        return line["警告:".Length..];
    }

    /// <summary>輸出只能有一般文字:除了換行(CR / LF)以外沒有任何控制字元,也沒有格式字元(雙向控制、零寬字元)。</summary>
    private static void AssertPlainText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\r') continue;
            var category = Rune.GetUnicodeCategory(rune);
            Assert.False(category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                             or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator,
                $"輸出含控制 / 格式字元 U+{rune.Value:X4}:{text.Replace("\u001b", "<ESC>")}");
        }
    }

    // ── 假船端:握手照規格(真的裝置金鑰),status 回傳指定的位元組 ──────────

    /// <summary>
    /// 假的船端同步服務:hello → challenge(以該安裝的裝置金鑰簽章)→ auth(不驗)→ welcome;之後 listRecords 回空清單、
    /// status 回傳 <paramref name="statusJson"/> 的原始位元組(可以是任何內容)、bye 結束。用來模擬舊版船上軟體與惡意 / 損壞的 statusResult。
    /// </summary>
    private sealed class FakeShip : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeShip(InstallLayout ship, Func<byte[]> statusJson)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                        await Handle(client, ship, statusJson, _cts.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException or EndOfStreamException) { }
                }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private static async Task Handle(TcpClient client, InstallLayout ship, Func<byte[]> statusJson, CancellationToken ct)
        {
            await using var s = client.GetStream();
            var hello = SyncProtocol.As<HelloMessage>(await SyncProtocol.ReadAsync(s, ct));
            var (info, key) = DeviceKeyStore.Load(ship);
            using (key)
            {
                var nonce = SyncProtocol.NewNonce();
                await SyncProtocol.WriteAsync(s, new ChallengeMessage
                {
                    ShipId = info.ShipId, DeviceId = info.DeviceId, Nonce = nonce, DeviceKeyId = key.KeyId, DevicePublicKey = key.PublicKeyPem,
                    DeviceSignature = Convert.ToBase64String(key.Sign(SyncProtocol.DeviceAuthPayload(info.ShipId, info.DeviceId, hello.ClientNonce, nonce))),
                }, ct);
            }
            await SyncProtocol.ReadAsync(s, ct);   // auth
            await SyncProtocol.WriteAsync(s, new WelcomeMessage { ShipId = info.ShipId, DeviceId = info.DeviceId, Signer = "假船端(測試)", SignerRole = "primary", VisitSeq = 1 }, ct);
            while (true)
            {
                var msg = await SyncProtocol.ReadAsync(s, ct);
                switch (SyncProtocol.TypeOf(msg))
                {
                    case SyncTypes.ListRecords:
                        await SyncProtocol.WriteAsync(s, new RecordListMessage(), ct);
                        break;
                    case SyncTypes.Status:
                    {
                        var bytes = statusJson();
                        var len = new byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
                        await s.WriteAsync(len, ct);
                        await s.WriteAsync(bytes, ct);
                        await s.FlushAsync(ct);
                        break;
                    }
                    case SyncTypes.Bye:
                        await SyncProtocol.WriteAsync(s, new { type = SyncTypes.Bye }, ct);
                        return;
                    default:
                        return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop; }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            _cts.Dispose();
        }
    }

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    /// <summary>舊版船上軟體(加入 releaseVersion / releaseDowngrade 之前)的 statusResult:沒有這兩個鍵。</summary>
    private const string OldShipStatus = """{"type":"statusResult","shipId":"FSB1","deviceId":"FSB1-LT01","version":"2026.10.1","components":{"software":{"version":"2026.10.1","packageId":"upd-x","appliedUtc":"2026-10-17T05:00:00Z"}},"outstandingRecordPackages":0,"visitSeq":1,"rollbackAvailable":false,"warnings":[]}""";

    // ── 直連同步 ─────────────────────────────────────────

    [Fact]
    public async Task Sync_PrintsAndSavesDowngradeReminder_ThenItDisappearsAfterTheShipIsFixed()
    {
        var ship = NewInstall("ship", "FSB1", "2026.12.1");
        using (var key = _env.Primary())
            Assert.True(new UpdateApplier(ship).Apply(_env.SoftwarePackage("2026.12.2", key, "app 2026.12.2\n")).Ok);
        await using var server = await StartServer(ship);
        var store = _env.PathIn("store");

        // (1) 沒有降版:發行版 2026.12.1 上套用軟體更新包 2026.12.2(常態)— 一行帶出發行版與軟體更新包版次,沒有降版的警告
        var normal = await SyncCli(server.Port, store);
        Assert.Equal(0, normal.Code);
        Assert.Contains("FSB1 船上筆電:發行版 2026.12.1,軟體更新包 2026.12.2\n", normal.Out.Replace("\r\n", "\n"));
        Assert.Contains("同步完成", normal.Out);
        Assert.DoesNotContain("降版", normal.Out + normal.Err);
        Assert.DoesNotContain("沒有回報", normal.Out + normal.Err);
        var entry = FleetEntry(store, "FSB1");
        Assert.True((bool)entry["releaseReported"]!);
        Assert.Equal("2026.12.1", (string?)entry["releaseVersion"]);
        Assert.False(entry.ContainsKey("releaseDowngrade"));
        Assert.False(entry.ContainsKey("releaseUnreadable"));
        Assert.Equal("tcp", (string?)entry["via"]);

        // (2) 以較舊的安裝程式 2026.11.1 重新安裝:降版。sync 印出哪一艘船、與船上 status 相同的說明(目前的發行版、先前的版本、怎麼處理),結束碼不變
        Reinstall(ship, "2026.11.1", DateTimeOffset.UtcNow.AddDays(-90));
        var shipMessage = ShipStatusDowngradeMessage(ship);
        Assert.Contains("目前的發行版 2026.11.1 比先前安裝的發行版 2026.12.1、曾套用的軟體更新包 2026.12.2 舊", shipMessage);
        var down = await SyncCli(server.Port, store);
        Assert.Equal(0, down.Code);
        Assert.Contains("FSB1 船上筆電:發行版 2026.11.1(有降版提醒,見下方的警告)", down.Out);
        Assert.Contains("同步完成", down.Out);
        var warning = Assert.Single(down.Err.Split('\n', StringSplitOptions.TrimEntries), l => l.Contains("有降版提醒"));
        Assert.StartsWith("警告:船上筆電 FSB1(裝置 FSB1-LT01)有降版提醒(與在船上執行 simosa-pkg status 看到的說明相同)— 降版:", warning);
        Assert.Contains(shipMessage, warning);                                     // 與船上 status 的警告一字不差(同一個出處)
        Assert.Contains("版本不低於 2026.12.2 的安裝程式重新安裝", warning);          // 怎麼處理
        Assert.Contains("教官筆電保存的這則提醒才會消失", warning);
        Assert.DoesNotContain("沒有簽章", warning);                                  // 直連不是隨身碟
        entry = FleetEntry(store, "FSB1");
        Assert.True((bool)entry["releaseReported"]!);
        Assert.Equal("2026.11.1", (string?)entry["releaseVersion"]);
        var dg = entry["releaseDowngrade"]!.AsObject();
        Assert.Equal("2026.11.1", (string?)dg["releaseVersion"]);
        Assert.Equal("2026.12.2", (string?)dg["basisVersion"]);
        Assert.Equal("2026.12.1", (string?)dg["previousReleaseVersion"]);
        Assert.Equal("2026.12.2", (string?)dg["softwareVersion"]);
        Assert.True((bool)dg["softwareStale"]!);
        Assert.Equal(shipMessage, (string?)dg["message"]);
        // 與船上 status --json 的 releaseDowngrade 同一份欄位
        var shipJson = JsonNode.Parse(Run("status", "--root", ship.Root, "--json").Out)!["releaseDowngrade"]!.AsObject();
        Assert.Equal(shipJson.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal), dg.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (k, v) in shipJson) Assert.Equal(v?.ToJsonString(), dg[k]?.ToJsonString());

        // store list:保存的提醒也顯示(版次行帶出發行版,警告另列並註明是哪一天怎麼取得的)
        var listed = Run("store", "list", "--store", store);
        Assert.Equal(0, listed.Code);
        Assert.Contains("版次 FSB1:", listed.Out);
        Assert.Contains(";發行版 2026.11.1(有降版提醒))", listed.Out);
        var savedWarning = Assert.Single(listed.Err.Split('\n', StringSplitOptions.TrimEntries), l => l.Contains("有降版提醒"));
        Assert.Contains(shipMessage, savedWarning);
        Assert.Contains("直連同步時保存的提醒", savedWarning);

        // (3) 船上處理掉(以不低於比較基準的安裝程式 2026.12.3 重新安裝):下一次同步提醒消失,保存的也不在了
        Reinstall(ship, "2026.12.3", DateTimeOffset.UtcNow);
        Assert.DoesNotContain("降版", Run("status", "--root", ship.Root).Err);
        var fixedUp = await SyncCli(server.Port, store);
        Assert.Equal(0, fixedUp.Code);
        Assert.Contains("FSB1 船上筆電:發行版 2026.12.3\n", fixedUp.Out.Replace("\r\n", "\n"));
        Assert.DoesNotContain("降版", fixedUp.Out + fixedUp.Err);
        entry = FleetEntry(store, "FSB1");
        Assert.Equal("2026.12.3", (string?)entry["releaseVersion"]);
        Assert.False(entry.ContainsKey("releaseDowngrade"));
        listed = Run("store", "list", "--store", store);
        Assert.Contains(";發行版 2026.12.3)", listed.Out);
        Assert.DoesNotContain("降版", listed.Out + listed.Err);
    }

    [Fact]
    public async Task Sync_TwoShips_EachEntryIsItsOwn()
    {
        var fsb1 = NewDowngradedInstall("ship1", "FSB1");
        var fsb2 = NewInstall("ship2", "FSB2", "2026.11.31");
        await using var s1 = await StartServer(fsb1);
        await using var s2 = await StartServer(fsb2);
        var store = _env.PathIn("store");

        var r1 = await SyncCli(s1.Port, store);
        var r2 = await SyncCli(s2.Port, store);
        Assert.Equal(0, r1.Code);
        Assert.Equal(0, r2.Code);
        Assert.Contains("警告:船上筆電 FSB1(裝置 FSB1-LT01)有降版提醒", r1.Err);
        Assert.Contains("FSB2 船上筆電:發行版 2026.11.31\n", r2.Out.Replace("\r\n", "\n"));
        Assert.DoesNotContain("降版", r2.Out + r2.Err);
        // FSB2 同步之後 FSB1 保存的提醒仍在;FSB2 沒有
        Assert.Equal("2026.12.2", (string?)FleetEntry(store, "FSB1")["releaseDowngrade"]!["basisVersion"]);
        Assert.Equal("2026.11.31", (string?)FleetEntry(store, "FSB2")["releaseVersion"]);
        Assert.False(FleetEntry(store, "FSB2").ContainsKey("releaseDowngrade"));
        var listed = Run("store", "list", "--store", store);
        var warning = Assert.Single(listed.Err.Split('\n', StringSplitOptions.TrimEntries), l => l.Contains("有降版提醒"));
        Assert.Contains("船上筆電 FSB1(裝置 FSB1-LT01)", warning);
        Assert.Contains(";發行版 2026.11.31)", listed.Out);

        // 再同步一次 FSB2(仍沒有降版):FSB1 的項目原封不動
        var before = FleetEntry(store, "FSB1").ToJsonString();
        Assert.Equal(0, (await SyncCli(s2.Port, store)).Code);
        Assert.Equal(before, FleetEntry(store, "FSB1").ToJsonString());
    }

    [Fact]
    public async Task Sync_OldShipSoftware_SaysNotReported_DistinctFromReportedNoDowngrade()
    {
        // 舊版船上(沒有 releaseVersion / releaseDowngrade)↔ 新版教官端:照常同步;輸出與 fleet-versions.json 都寫明「沒有回報」
        var device = _env.NewShip("old", "FSB1");
        await using var old = new FakeShip(device, () => Utf8(OldShipStatus));
        var store = _env.PathIn("store");
        var r = await SyncCli(old.Port, store);
        Assert.Equal(0, r.Code);
        Assert.Contains("FSB1 現行版本 2026.10.1", r.Out);
        Assert.Contains("FSB1 船上筆電:沒有回報發行版(船上的軟體是較舊的版本,還不會回報發行版與降版提醒;船上更新軟體之後,再同步一次就會顯示)", r.Out);
        Assert.Contains("同步完成", r.Out);
        Assert.DoesNotContain("降版提醒,見下方", r.Out);
        Assert.DoesNotContain("船上筆電 FSB1", r.Err);   // 沒有回報不是警告
        var entry = FleetEntry(store, "FSB1");
        Assert.False((bool)entry["releaseReported"]!);
        Assert.False(entry.ContainsKey("releaseVersion"));
        Assert.False(entry.ContainsKey("releaseDowngrade"));
        Assert.Equal("2026.10.1", (string?)entry["version"]);
        Assert.Contains("發行版未回報(船上的軟體較舊)", Run("store", "list", "--store", store).Out);

        // 同一艘船更新軟體之後(回報沒有降版):項目整筆取代,兩種情形在檔案裡分得出來
        var updated = NewInstall("new", "FSB1", "2026.11.31");
        foreach (var f in Directory.GetFiles(device.DeviceDir)) File.Copy(f, Path.Combine(updated.DeviceDir, Path.GetFileName(f)), overwrite: true);   // 同一台筆電(同一把裝置金鑰)
        await using var server = await StartServer(updated);
        var r2 = await SyncCli(server.Port, store);
        Assert.Equal(0, r2.Code);
        Assert.Contains("FSB1 船上筆電:發行版 2026.11.31\n", r2.Out.Replace("\r\n", "\n"));
        entry = FleetEntry(store, "FSB1");
        Assert.True((bool)entry["releaseReported"]!);
        Assert.Equal("2026.11.31", (string?)entry["releaseVersion"]);
        Assert.False(entry.ContainsKey("releaseDowngrade"));
    }

    [Fact]
    public async Task Sync_ShipWithoutReleaseManifest_ReportsNullVersion_NotMistakenForOldSoftware()
    {
        // 這一版的船上軟體在沒有發行清單時把 releaseVersion 寫成 null(鍵一定在):教官端知道「回報了,沒有發行清單」,不是「舊版沒有回報」
        var ship = _env.NewShip();   // 沒有 manifest.json(從原始碼資料夾執行,或發行清單讀不到)
        var json = JsonNode.Parse(Run("status", "--root", ship.Root, "--json").Out)!.AsObject();
        Assert.True(json.ContainsKey("releaseVersion"));
        Assert.Null(json["releaseVersion"]);
        Assert.False(json.ContainsKey("releaseDowngrade"));   // 沒有降版時照舊省略
        await using var server = await StartServer(ship);
        var store = _env.PathIn("store");
        var r = await SyncCli(server.Port, store);
        Assert.Equal(0, r.Code);
        Assert.Contains("FSB1 船上筆電:發行版不明(船上回報安裝根目錄的發行清單 manifest.json 不在或讀不到;在船上執行 simosa-pkg verify-install 可以確認安裝是否完整)", r.Out);
        Assert.DoesNotContain("沒有回報", r.Out);
        var entry = FleetEntry(store, "FSB1");
        Assert.True((bool)entry["releaseReported"]!);
        Assert.False(entry.ContainsKey("releaseVersion"));
        Assert.Contains("發行版不明(船上沒有發行清單 manifest.json)", Run("store", "list", "--store", store).Out);
    }

    // ── 不可信的內容 ──────────────────────────────────────

    /// <summary>JSON 字串裡的 ESC(\u001b 跳脫;JSON 不允許未跳脫的控制字元,那樣整則訊息就不是有效的 JSON)</summary>
    private const string Esc = "\\u001b";
    /// <summary>ESC 字元本身(解析之後字串裡的內容)</summary>
    private const char EscChar = '\u001b';

    /// <summary>惡意 / 損壞的 statusResult(名稱 → JSON 片段:接在其餘正常欄位之後)。</summary>
    public static TheoryData<string, string> HostileReleaseFields()
    {
        var huge = new string('9', 300_000);
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 40)) + "1" + new string('}', 40);
        return new TheoryData<string, string>
        {
            { "ANSI 跳脫與換行(版本與說明)", $"\"releaseVersion\":\"2026.11.1{Esc}[31m\",\"releaseDowngrade\":{{\"releaseVersion\":\"2026.11.1\",\"basisVersion\":\"2026.12.2\",\"softwareVersion\":\"2026.12.2\",\"softwareStale\":true,\"message\":\"{Esc}[2J{Esc}[1;31m偽造的訊息\\r\\n同步完成\\u0007\\u202e\"}}" },
            { "超長字串", $"\"releaseVersion\":\"{huge}\",\"releaseDowngrade\":{{\"releaseVersion\":\"2026.11.1\",\"basisVersion\":\"{huge}\",\"message\":\"{huge}\"}}" },
            { "型別不對(數字、布林)", "\"releaseVersion\":20261101,\"releaseDowngrade\":true" },
            { "型別不對(陣列、字串)", "\"releaseVersion\":[\"2026.11.1\"],\"releaseDowngrade\":\"降版\"" },
            { "型別不對(物件當版本、數字當降版)", "\"releaseVersion\":{\"version\":\"2026.11.1\"},\"releaseDowngrade\":7" },
            { "巢狀物件", $"\"releaseVersion\":\"2026.11.1\",\"releaseDowngrade\":{{\"releaseVersion\":{{\"a\":{{\"b\":[1,2,{{}}]}}}},\"basisVersion\":[],\"message\":{{\"x\":[1,2]}},\"extra\":{deep}}}" },
            { "很深的巢狀", $"\"releaseVersion\":{deep},\"releaseDowngrade\":{deep}" },
            { "降版資訊裡重複的鍵", "\"releaseVersion\":\"2026.11.1\",\"releaseDowngrade\":{\"releaseVersion\":\"2026.11.1\",\"releaseVersion\":\"2026.11.1\",\"basisVersion\":\"2026.12.2\"}" },
            { "鍵名大小寫不同而型別不對", "\"ReleaseVersion\":5,\"RELEASEDOWNGRADE\":\"x\"" },
            { "比較基準與各項對不上", "\"releaseVersion\":\"2026.11.1\",\"releaseDowngrade\":{\"releaseVersion\":\"2026.11.1\",\"basisVersion\":\"2099.1.1\",\"previousReleaseVersion\":\"2026.12.1\"}" },
            { "降版資訊與發行版本對不上", "\"releaseVersion\":\"2026.11.1\",\"releaseDowngrade\":{\"releaseVersion\":\"2026.10.1\",\"basisVersion\":\"2026.12.1\",\"previousReleaseVersion\":\"2026.12.1\"}" },
        };
    }

    private static string StatusWith(string releaseFields)
        => OldShipStatus[..^1] + "," + releaseFields + "}";

    [Theory]
    [MemberData(nameof(HostileReleaseFields))]
    public async Task Sync_HostileReleaseFields_DoNotFailTheSync_NorReachTheTerminalOrTheFleetFile(string name, string releaseFields)
    {
        var device = _env.NewShip("hostile", "FSB1");
        await using var fake = new FakeShip(device, () => Utf8(StatusWith(releaseFields)));
        var store = _env.PathIn("store");
        var r = await SyncCli(fake.Port, store);

        // 同步照常完成(結束碼 0、沒有半途中止),其餘欄位照常寫進兩船版次清單
        Assert.True(r.Code == 0, $"{name}:結束碼 {r.Code};stderr:{r.Err}");
        Assert.Contains("FSB1 現行版本 2026.10.1", r.Out);
        Assert.Contains("同步完成", r.Out);
        var entry = FleetEntry(store, "FSB1");
        Assert.Equal("2026.10.1", (string?)entry["version"]);
        Assert.True((bool)entry["releaseReported"]!);

        // 印到終端機的只有一般文字、長度有限;船上送來的任意文字到不了輸出與檔案
        AssertPlainText(r.Out);
        AssertPlainText(r.Err);
        Assert.True(r.Out.Length + r.Err.Length < 3000, $"{name}:輸出 {r.Out.Length + r.Err.Length} 字");
        Assert.DoesNotContain("偽造", r.Out + r.Err);
        var fleetText = File.ReadAllText(Path.Combine(store, "fleet-versions.json"));
        Assert.True(fleetText.Length < 4000, $"{name}:fleet-versions.json {fleetText.Length} 字");
        Assert.DoesNotContain("偽造", fleetText);
        Assert.DoesNotContain("\\u001b", fleetText, StringComparison.OrdinalIgnoreCase);
        if (entry["releaseVersion"] is { } v) Assert.True(ShipVersion(v.GetValue<string>()), $"{name}:releaseVersion = {v}");

        // 讀不懂的內容要說出來(不把可能存在的降版提醒悄悄丟掉),store list 也一樣
        // (降版資訊本身讀得懂、只有發行版本讀不懂時已列出降版提醒,不說「可能有降版提醒沒有顯示」)
        Assert.Contains("教官筆電讀不懂(格式不符)", r.Err);
        Assert.Contains(";請在船上執行 simosa-pkg status 確認", r.Err);
        Assert.Equal(entry.ContainsKey("releaseDowngrade"), !r.Err.Contains(",可能有降版提醒沒有顯示"));
        Assert.True(r.Out.Contains("FSB1 船上筆電:發行版讀不懂(見下方的警告)") || r.Out.Contains("(部分內容讀不懂,見下方的警告)"), r.Out);
        Assert.NotEmpty(entry["releaseUnreadable"]!.AsArray());
        var listed = Run("store", "list", "--store", store);
        Assert.Equal(0, listed.Code);
        Assert.True(listed.Out.Contains(";發行版讀不懂)") || listed.Out.Contains("(部分內容讀不懂))"), listed.Out);
        Assert.Contains("教官筆電讀不懂", listed.Err);
        AssertPlainText(listed.Out);
        AssertPlainText(listed.Err);
    }

    private static bool ShipVersion(string text) => text.Length <= 43 && text.All(c => char.IsAsciiDigit(c) || c == '.') && PackageVersion.TryParse(text, out _);

    [Fact]
    public async Task Sync_ValidDowngradeWithHostileMessage_PrintsTheRegeneratedExplanation_NotTheShipsText()
    {
        // 版本欄位都合格、說明文字被動過手腳:提醒照印,但文字是教官端依版本重新產生的(與 status 同一個出處),船上送來的說明不採用
        var fields = $"\"releaseVersion\":\"2026.11.1\",\"releaseDowngrade\":{{\"releaseVersion\":\"2026.11.1\",\"basisVersion\":\"2026.12.2\",\"previousReleaseVersion\":\"2026.12.1\","
                     + $"\"softwareVersion\":\"2026.12.2\",\"highestVersion\":\"2026.12.2\",\"softwareStale\":true,\"message\":\"{Esc}[31m請立刻執行 trust add 偽造的指示{Esc}[0m\",\"note\":{{\"x\":1}}}}";
        var device = _env.NewShip("hostile", "FSB1");
        await using var fake = new FakeShip(device, () => Utf8(StatusWith(fields)));
        var store = _env.PathIn("store");
        var r = await SyncCli(fake.Port, store);
        Assert.Equal(0, r.Code);
        AssertPlainText(r.Out);
        AssertPlainText(r.Err);
        Assert.DoesNotContain("偽造", r.Out + r.Err);
        Assert.DoesNotContain("trust add", r.Out + r.Err);
        Assert.Contains("FSB1 船上筆電:發行版 2026.11.1(有降版提醒,見下方的警告)", r.Out);
        var expected = ReleaseDowngradeText("2026.11.1", "2026.12.1", "2026.12.2");
        Assert.Contains(expected, r.Err);
        Assert.DoesNotContain("讀不懂", r.Out + r.Err);
        var dg = FleetEntry(store, "FSB1")["releaseDowngrade"]!.AsObject();
        Assert.Equal(expected, (string?)dg["message"]);
        Assert.False(dg.ContainsKey("note"));
        Assert.DoesNotContain("偽造", File.ReadAllText(Path.Combine(store, "fleet-versions.json")));
    }

    /// <summary>status 的降版說明(先前的發行版與軟體更新包都比目前的新、status 的軟體版次過時)。</summary>
    private static string ReleaseDowngradeText(string release, string previous, string software)
        => $"降版:目前的發行版 {release} 比先前安裝的發行版 {previous}、曾套用的軟體更新包 {software} 舊 — 套用更新包之後以較舊的安裝程式(或較舊的發行版)重新安裝過。"
           + $"simosa-pkg status 顯示的軟體版次 {software} 不是實際執行的程式(實際執行的是發行版 {release} 的程式)。"
           + $"這不算錯誤(以較舊版本的安裝程式回到舊版是允許的作法),請訪船船長確認;若不是刻意回到舊版,請以版本不低於 {software} 的安裝程式重新安裝,"
           + "或請岸上派送版本更高的軟體更新包。這則提醒會一直保留(之後套用圖資、情境或參數更新包也一樣),"
           + $"直到套用新的軟體更新包、或以版本不低於 {software} 的安裝程式重新安裝為止";

    [Fact]
    public async Task Sync_InvalidUtf8InsideTheDowngradeInfo_IsUnreadable_NotAFailure()
    {
        // 說明字串裡有不合法的 UTF-8(System.Text.Json 到取值時才發現):只有降版資訊讀不懂,發行版本與其餘欄位照常
        var head = Utf8(OldShipStatus[..^1] + ",\"releaseVersion\":\"2026.11.1\",\"releaseDowngrade\":{\"releaseVersion\":\"2026.11.1\",\"basisVersion\":\"");
        var bytes = head.Concat(new byte[] { 0xFF, 0xFE, 0x32 }).Concat(Utf8("\",\"message\":\"")).Concat(new byte[] { 0xC3, 0x28 }).Concat(Utf8("\"}}")).ToArray();
        var device = _env.NewShip("hostile", "FSB1");
        await using var fake = new FakeShip(device, () => bytes);
        var store = _env.PathIn("store");
        var r = await SyncCli(fake.Port, store);
        Assert.True(r.Code == 0, r.Err);
        Assert.Contains("FSB1 船上筆電:發行版 2026.11.1(部分內容讀不懂,見下方的警告)", r.Out);
        Assert.Contains("回報的降版資訊,教官筆電讀不懂", r.Err);
        AssertPlainText(r.Out);
        AssertPlainText(r.Err);
        var entry = FleetEntry(store, "FSB1");
        Assert.Equal("2026.11.1", (string?)entry["releaseVersion"]);
        Assert.Equal("releaseDowngrade", (string?)Assert.Single(entry["releaseUnreadable"]!.AsArray()));
    }

    [Fact]
    public async Task Sync_ControlCharactersInOtherStatusFields_AreStrippedFromTheSummaryLines()
    {
        // 同一則 statusResult 裡原本就會印出的文字欄位(現行版本、參數集版次、環境):去掉控制字元並截斷後才印(合法的內容不變)
        var status = JsonNode.Parse(OldShipStatus)!.AsObject();
        status["version"] = $"2026.10.1{EscChar}[2J{EscChar}[31m";
        status["parameterSet"] = new JsonObject { ["id"] = "p", ["version"] = $"2026.10.2\r\n同步完成{EscChar}[0m", ["sha256"] = "00", ["path"] = "data/parameters/x.yaml" };
        status["environment"] = new JsonObject { ["os"] = $"Windows{EscChar}]0;偽造的視窗標題\u0007" + new string('W', 2000), ["dotnet"] = ".NET 8.0.11\u202e", ["collectedUtc"] = "2026-11-09T01:00:00Z" };
        status["releaseVersion"] = "2026.11.31";
        var device = _env.NewShip("hostile", "FSB1");
        await using var fake = new FakeShip(device, () => Utf8(status.ToJsonString()));
        var store = _env.PathIn("store");
        var r = await SyncCli(fake.Port, store);
        Assert.True(r.Code == 0, r.Err);
        AssertPlainText(r.Out);
        AssertPlainText(r.Err);
        Assert.Contains("FSB1 現行版本 2026.10.1[2J[31m,參數集 2026.10.2同步完成[0m,待回傳紀錄包 0;環境 Windows]0;偽造的視窗標題WWW", r.Out);
        Assert.True(r.Out.Length < 2000, $"輸出 {r.Out.Length} 字");
        Assert.Contains("FSB1 船上筆電:發行版 2026.11.31", r.Out);
        var listed = Run("store", "list", "--store", store);
        Assert.Equal(0, listed.Code);
        AssertPlainText(listed.Out);
        Assert.Contains("版次 FSB1:2026.10.1[2J[31m(參數集 2026.10.2同步完成[0m,", listed.Out);
        Assert.True(listed.Out.Length < 2000, $"輸出 {listed.Out.Length} 字");
    }

    [Fact]
    public async Task Sync_StatusMessageThatIsNotValidJson_StillEndsWithExitCode1_AndPlainText()
    {
        // 整則 statusResult 不是有效的 JSON(字串裡有未跳脫的控制字元):這是訊息層的檢查,與修改前相同 — 錯誤、結束碼 1;錯誤訊息不把對方的內容帶到終端機
        var device = _env.NewShip("hostile", "FSB1");
        await using var fake = new FakeShip(device, () => Utf8(OldShipStatus[..^1] + $",\"releaseVersion\":\"2026.11.1{EscChar}[31m\"}}"));
        var r = await SyncCli(fake.Port, _env.PathIn("store"));
        Assert.Equal(1, r.Code);
        Assert.Contains("錯誤:訊息不是有效的 JSON", r.Err);
        AssertPlainText(r.Out);
        AssertPlainText(r.Err);
    }

    // ── 隨身碟 ───────────────────────────────────────────

    private (int Code, string Out, string Err) UsbRoundTrip(InstallLayout ship, string usb, string store)
    {
        var export = Run("export", "--to", usb, "--role", "ship", "--root", ship.Root);
        Assert.Equal(0, export.Code);
        return Run("import", "--from", usb, "--role", "instructor", "--store", store, "--root", _env.InstructorRoot, "--enroll-device");
    }

    private static string StatusJsonPath(string usb, string shipId) => Path.Combine(usb, "simosa-transfer", "from-ship", shipId, "status.json");

    [Fact]
    public void Usb_PrintsAndSavesDowngradeReminder_ThenItDisappearsAfterTheShipIsFixed()
    {
        var ship = NewDowngradedInstall("ship", "FSB1");
        var other = NewInstall("ship2", "FSB2", "2026.11.31");
        var usb = _env.PathIn("usb");
        var store = _env.PathIn("store");
        var shipMessage = ShipStatusDowngradeMessage(ship);
        Assert.Equal(0, Run("export", "--to", usb, "--role", "ship", "--root", other.Root).Code);   // 同一支隨身碟上另一艘船(沒有降版)

        var r = UsbRoundTrip(ship, usb, store);
        Assert.Equal(0, r.Code);
        Assert.Contains("FSB1 船上筆電:發行版 2026.11.1(有降版提醒,見下方的警告)", r.Out);
        Assert.Contains("FSB2 船上筆電:發行版 2026.11.31\n", r.Out.Replace("\r\n", "\n"));
        var warning = Assert.Single(r.Err.Split('\n', StringSplitOptions.TrimEntries), l => l.Contains("有降版提醒"));
        Assert.StartsWith("警告:船上筆電 FSB1(裝置 FSB1-LT01)有降版提醒(與在船上執行 simosa-pkg status 看到的說明相同)— 降版:", warning);
        Assert.Contains(shipMessage, warning);
        Assert.Contains("這是隨身碟上 status.json 的內容;這個檔沒有簽章,以船上的 simosa-pkg status 為準", warning);
        var entry = FleetEntry(store, "FSB1");
        Assert.Equal("usb", (string?)entry["via"]);
        Assert.True((bool)entry["releaseReported"]!);
        Assert.Equal("2026.11.1", (string?)entry["releaseVersion"]);
        Assert.Equal("2026.12.2", (string?)entry["releaseDowngrade"]!["basisVersion"]);
        Assert.Equal(shipMessage, (string?)entry["releaseDowngrade"]!["message"]);
        Assert.Equal("2026.11.31", (string?)FleetEntry(store, "FSB2")["releaseVersion"]);
        Assert.False(FleetEntry(store, "FSB2").ContainsKey("releaseDowngrade"));
        var listed = Run("store", "list", "--store", store);
        var saved = Assert.Single(listed.Err.Split('\n', StringSplitOptions.TrimEntries), l => l.Contains("有降版提醒"));
        Assert.Contains("匯入隨身碟時保存的提醒;隨身碟上的 status.json 沒有簽章", saved);

        // 船上處理掉之後再走一次隨身碟:提醒消失
        Reinstall(ship, "2026.12.3", DateTimeOffset.UtcNow);
        var fixedUp = UsbRoundTrip(ship, usb, store);
        Assert.Equal(0, fixedUp.Code);
        Assert.Contains("FSB1 船上筆電:發行版 2026.12.3\n", fixedUp.Out.Replace("\r\n", "\n"));
        Assert.DoesNotContain("降版", fixedUp.Out + fixedUp.Err);
        Assert.False(FleetEntry(store, "FSB1").ContainsKey("releaseDowngrade"));
        Assert.DoesNotContain("降版", Run("store", "list", "--store", store).Err);
    }

    [Fact]
    public void Usb_OldShipStatusFile_SaysNotReported()
    {
        // 舊版船上寫的 status.json(沒有這兩個鍵):照常匯入,寫明沒有回報
        var ship = NewInstall("ship", "FSB1", "2026.11.31");
        var usb = _env.PathIn("usb");
        var store = _env.PathIn("store");
        Assert.Equal(0, Run("export", "--to", usb, "--role", "ship", "--root", ship.Root).Code);
        var path = StatusJsonPath(usb, "FSB1");
        var status = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(status.Remove("releaseVersion"));
        File.WriteAllText(path, status.ToJsonString());
        var r = Run("import", "--from", usb, "--role", "instructor", "--store", store, "--root", _env.InstructorRoot, "--enroll-device");
        Assert.Equal(0, r.Code);
        Assert.Contains("FSB1 船上筆電:沒有回報發行版(船上的軟體是較舊的版本", r.Out);
        var entry = FleetEntry(store, "FSB1");
        Assert.False((bool)entry["releaseReported"]!);
        Assert.False(entry.ContainsKey("releaseVersion"));
        Assert.Equal("usb", (string?)entry["via"]);
    }

    [Theory]
    [MemberData(nameof(HostileReleaseFields))]
    public void Usb_HostileStatusFile_DoesNotFailTheImport_NorReachTheTerminalOrTheFleetFile(string name, string releaseFields)
    {
        var ship = NewInstall("ship", "FSB1", "2026.11.31");
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", new DateTime(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc), 600);
        var usb = _env.PathIn("usb");
        var store = _env.PathIn("store");
        Assert.Equal(0, Run("export", "--to", usb, "--role", "ship", "--root", ship.Root).Code);
        // 隨身碟上的 status.json 沒有簽章:任何人都能改。船舶識別也塞進 ANSI 跳脫與超長內容
        var hostileShipId = $"FSB1{EscChar}[31m" + new string('X', 5000);
        File.WriteAllText(StatusJsonPath(usb, "FSB1"), StatusWith(releaseFields).Replace("\"shipId\":\"FSB1\"", $"\"shipId\":\"FSB1{Esc}[31m{new string('X', 5000)}\""));

        var r = Run("import", "--from", usb, "--role", "instructor", "--store", store, "--root", _env.InstructorRoot, "--enroll-device");
        Assert.True(r.Code == 0, $"{name}:結束碼 {r.Code};stderr:{r.Err}");
        Assert.Contains("已匯入 rec-FSB1-", r.Out);                 // 紀錄包照常匯入
        AssertPlainText(r.Out);
        AssertPlainText(r.Err);
        Assert.True(r.Out.Length + r.Err.Length < 4000, $"{name}:輸出 {r.Out.Length + r.Err.Length} 字");
        Assert.DoesNotContain("偽造", r.Out + r.Err);
        Assert.Contains("FSB1[31mXXXX", r.Out);                    // 船舶識別去掉 ESC 並截斷(少了 ESC 的「[31m」只是普通文字)
        Assert.Contains("X… 船上筆電:", r.Out);
        Assert.Contains("教官筆電讀不懂(格式不符)", r.Err);
        Assert.Contains("這個檔沒有簽章", r.Err);
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(store, "fleet-versions.json")))!["ships"]![hostileShipId]!.AsObject();
        Assert.True((bool)entry["releaseReported"]!);
        Assert.NotEmpty(entry["releaseUnreadable"]!.AsArray());
        if (entry["releaseVersion"] is { } v) Assert.True(ShipVersion(v.GetValue<string>()), $"{name}:releaseVersion = {v}");
        Assert.DoesNotContain("偽造", File.ReadAllText(Path.Combine(store, "fleet-versions.json")));
        // store list:保存下來的船舶識別(隨身碟途徑沒有簽章)也不能把控制字元帶到終端機
        var listed = Run("store", "list", "--store", store);
        Assert.Equal(0, listed.Code);
        AssertPlainText(listed.Out);
        AssertPlainText(listed.Err);
        Assert.Contains("版次 FSB1[31mXXXX", listed.Out);
        Assert.Contains("教官筆電讀不懂", listed.Err);
    }

    [Fact]
    public void Usb_BrokenStatusFile_IsAWarning_ImportStillSucceeds()
    {
        var ship = NewInstall("ship", "FSB1", "2026.11.31");
        TestEnv.WriteRecord(ship.RecordsDir, "r1.jsonl", "E01_baseline", new DateTime(2026, 10, 12, 1, 30, 0, DateTimeKind.Utc), 600);
        var usb = _env.PathIn("usb");
        Assert.Equal(0, Run("export", "--to", usb, "--role", "ship", "--root", ship.Root).Code);
        var path = StatusJsonPath(usb, "FSB1");
        var cases = new (string Name, byte[] Content, string Expect)[]
        {
            ("不是 JSON", Utf8($"{EscChar}[31mnot json"), "不是有效的 JSON"),
            ("不是物件", Utf8("[1,2,3]"), "不是 JSON 物件"),
            ("頂層重複的鍵", Utf8("{\"type\":\"statusResult\",\"shipId\":\"FSB1\",\"shipId\":\"FSB2\"}"), "無法解析"),
            ("其餘欄位型別不對", Utf8("{\"type\":\"statusResult\",\"shipId\":\"FSB1\",\"components\":\"x\",\"releaseVersion\":\"2026.11.31\"}"), "不是有效的 JSON"),
            ("超過大小上限", Utf8("{\"type\":\"statusResult\",\"shipId\":\"FSB1\",\"pad\":\"" + new string('x', 1_100_000) + "\"}"), "超過上限"),
        };
        var n = 0;
        foreach (var (name, content, expect) in cases)
        {
            File.WriteAllBytes(path, content);
            var store = _env.PathIn($"store{n++}");
            var r = Run("import", "--from", usb, "--role", "instructor", "--store", store, "--root", _env.InstructorRoot, "--enroll-device");
            Assert.True(r.Code == 0, $"{name}:結束碼 {r.Code};stderr:{r.Err}");
            Assert.Contains("rec-FSB1-", r.Out);
            Assert.Contains(expect, r.Err);
            AssertPlainText(r.Err);
            Assert.True(r.Err.Length < 1500, $"{name}:stderr {r.Err.Length} 字");
            Assert.False(File.Exists(Path.Combine(store, "fleet-versions.json")), $"{name}:讀不進來的 status.json 不應寫進兩船版次清單");
            Assert.DoesNotContain("船上筆電:", r.Out);
        }
    }

    // ── 既有的 fleet-versions.json ─────────────────────────

    [Fact]
    public async Task ExistingFleetFileWithoutTheNewFields_StillLoads_AndIsReplacedPerShipOnTheNextSync()
    {
        // 加入這些欄位之前的教官端軟體寫的 fleet-versions.json:照常讀得進來,store list 寫「沒有記錄」而不是「沒有回報」
        var store = _env.PathIn("store");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "fleet-versions.json"), """
            {
              "ships": {
                "FSB1": { "shipId": "FSB1", "deviceId": "FSB1-LT01", "version": "2026.10.1", "packageId": "upd-old", "components": {},
                          "environment": { "os": "Microsoft Windows 10.0.26100", "dotnet": ".NET 8.0.11", "webview2": "130.0.2849.56", "collectedUtc": "2026-11-09T01:00:00Z" },
                          "updatedUtc": "2026-11-09T03:00:00Z", "via": "tcp" },
                "FSB2": { "shipId": "FSB2", "version": "2026.10.2", "components": {}, "updatedUtc": "2026-11-10T03:00:00Z", "via": "usb" }
              }
            }
            """);
        var listed = Run("store", "list", "--store", store);
        Assert.Equal(0, listed.Code);
        Assert.Contains("版次 FSB1:2026.10.1", listed.Out);
        Assert.Contains("WebView2 130.0.2849.56(收集於 2026-11-09);發行版沒有記錄(再同步一次就會有))", listed.Out);
        Assert.Contains("版次 FSB2:2026.10.2", listed.Out);
        Assert.DoesNotContain("未回報(船上的軟體較舊)", listed.Out);
        Assert.DoesNotContain("船上筆電", listed.Err);
        Assert.Equal("Microsoft Windows 10.0.26100", new RecordStore(store).LoadFleet().Ships["FSB1"].Environment?.Os);

        // 同步 FSB1(降版的船):該船項目整筆取代並帶新欄位;FSB2 的舊項目不動、仍讀得進來
        var ship = NewDowngradedInstall("ship", "FSB1");
        await using var server = await StartServer(ship);
        var r = await SyncCli(server.Port, store);
        Assert.Equal(0, r.Code);
        Assert.Contains("有降版提醒", r.Err);
        Assert.Equal("2026.11.1", (string?)FleetEntry(store, "FSB1")["releaseVersion"]);
        var fsb2 = FleetEntry(store, "FSB2");
        Assert.Equal("2026.10.2", (string?)fsb2["version"]);
        Assert.False(fsb2.ContainsKey("releaseReported"));
        listed = Run("store", "list", "--store", store);
        Assert.Equal(0, listed.Code);
        Assert.Contains(";發行版 2026.11.1(有降版提醒))", listed.Out);
        Assert.Contains("經 usb;環境 未回報;發行版沒有記錄(再同步一次就會有))", listed.Out);
    }
}
