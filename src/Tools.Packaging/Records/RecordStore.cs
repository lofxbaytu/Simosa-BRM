using System.IO.Compression;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Records;

/// <summary>教官筆電紀錄庫帳冊 &lt;store&gt;/store-ledger.json。</summary>
public sealed class StoreLedger
{
    public const string FormatId = "simosa-store-ledger/1";

    public string Format { get; set; } = FormatId;
    public List<StoredPackage> Packages { get; set; } = new();
}

public sealed class StoredPackage
{
    public string PackageId { get; set; } = "";
    public string ShipId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ImportedUtc { get; set; }
    /// <summary>相對紀錄庫的目錄(records/&lt;ship&gt;/&lt;yyyy&gt;/&lt;packageId&gt;)</summary>
    public string Directory { get; set; } = "";
    public int Exercises { get; set; }
    public double TrainingS { get; set; }
    public DateTime? BackedUpUtc { get; set; }
    public string? BackupTarget { get; set; }
    /// <summary>已送出的回報 ID(船上已確認刪除時記錄)</summary>
    public string? ReportedId { get; set; }
    public DateTime? ShipDeletedUtc { get; set; }
}

/// <summary>教官端兩船版次清單 &lt;store&gt;/fleet-versions.json(規劃書第 5.5 節)。</summary>
public sealed class FleetVersions
{
    public Dictionary<string, ShipVersionEntry> Ships { get; set; } = new();
}

public sealed class ShipVersionEntry
{
    public string ShipId { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? Version { get; set; }
    public string? PackageId { get; set; }
    public Dictionary<string, ComponentVersion> Components { get; set; } = new();
    public ParameterSetRef? ParameterSet { get; set; }
    /// <summary>該船筆電的環境版次(statusResult / status.json 的 environment;船端未回報時 UpdateFleet 沿用上次已知值,從未回報過則為 null)</summary>
    public EnvironmentInfo? Environment { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string? Via { get; set; }

    // ── 船上回報的發行版狀態(statusResult / status.json 的 releaseVersion、releaseDowngrade;docs/design/packaging-sync.md 第 7 節)。
    //    由 Sync.ShipReleaseReport 檢查後寫入:只有通過檢查的數字版本與教官端自己產生的說明,沒有船上送來的任意文字。
    //    與其他欄位一樣每次同步整筆取代(不沿用上一次的值:降版處理掉之後,下一次同步保存的提醒就不在了)。

    /// <summary>
    /// 船上這次同步有沒有回報發行版狀態:true = 有;false = 沒有(船上的軟體是加入這兩個欄位之前的版本);
    /// null(檔案裡沒有這個鍵)= 這筆是加入本欄位之前的教官端軟體寫的,不知道。
    /// </summary>
    public bool? ReleaseReported { get; set; }
    /// <summary>船上安裝根目錄 manifest.json 的發行版本(1 至 4 段數字;船上沒有發行清單、沒有回報或讀不懂時 null)</summary>
    public string? ReleaseVersion { get; set; }
    /// <summary>
    /// 船上的降版(套用更新包之後以較舊的安裝程式重新安裝過);欄位與 <c>simosa-pkg status --json</c> 的 releaseDowngrade 相同,
    /// 其中的 <c>message</c> 是教官端依版本欄位重新產生的。沒有降版(或沒有回報)時 null。
    /// </summary>
    public Release.ReleaseDowngrade? ReleaseDowngrade { get; set; }
    /// <summary>船上回報了、但教官端讀不懂(型別或格式不符)的欄位名稱(releaseVersion、releaseDowngrade);都讀得懂時 null</summary>
    public List<string>? ReleaseUnreadable { get; set; }

    /// <summary>船上時鐘比教官筆電快多少以內仍當成可用的時間(時區設錯、時鐘沒對準);超過就當成不知道</summary>
    public static readonly TimeSpan StatusClockTolerance = TimeSpan.FromDays(1);

    /// <summary>
    /// 一份船上狀態在船上產生的時間(船上的時鐘;status 的 <c>environment.collectedUtc</c>)— 隨身碟上的 status.json 是不是比教官筆電已有的舊,
    /// 以及「這則提醒是船上哪個時候回報的」都看它。沒有環境資訊(較舊的船上軟體)或時間不合理時回傳 null(不知道,不拿來比較):
    /// 早於 2000 年(沒有這個欄位時的預設值)、或比教官筆電現在的時間晚超過 <see cref="StatusClockTolerance"/>(船上時鐘超前,或檔案被改過)—
    /// 否則一個寫著很久以後的時間的檔案,會讓之後所有正常的狀態都被當成「比較舊」。
    /// </summary>
    public static DateTime? StatusUtcOf(EnvironmentInfo? environment, DateTime nowUtc)
    {
        if (environment is null) return null;
        var t = environment.CollectedUtc;
        t = t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
        return t.Year < 2000 || t > nowUtc + StatusClockTolerance ? null : t;
    }

    /// <summary>元件、SMS 文件來源這類清單各自最多保存幾項(實際的元件種類只有 5 種)</summary>
    internal const int MaxListedItems = 32;

    /// <summary>
    /// 寫進 fleet-versions.json 之前:船上回報的文字欄位(船舶與裝置識別、現行版本、更新包 ID、元件、參數集、環境)去掉控制 / 格式字元並截斷
    /// (<see cref="UntrustedText.Clean"/>;合法的內容不變),清單項數設上限 — statusResult 沒有自己的簽章、隨身碟的 status.json 完全沒有簽章,
    /// 內容不可信,控制字元、雙向控制字元與超長字串不該原樣留在檔案裡(docs/design/packaging-sync.md 第 5.1 節末)。
    /// 發行版本與降版資訊另由 <see cref="Sync.ShipReleaseReport"/> 檢查(只有數字版本與教官端自己產生的說明)。
    /// </summary>
    internal void Sanitize()
    {
        static string? Opt(string? text, int max) => text is null ? null : UntrustedText.Clean(text, max);
        ShipId = UntrustedText.Clean(ShipId, 64);
        DeviceId = Opt(DeviceId, 64);
        Version = Opt(Version, 64);
        PackageId = Opt(PackageId, 128);
        Via = Opt(Via, 16);
        var components = new Dictionary<string, ComponentVersion>();
        foreach (var (kind, c) in (Components ?? new()).Take(MaxListedItems))
        {
            var key = UntrustedText.Clean(kind, 32);
            if (key.Length == 0 || c is null) continue;
            components.TryAdd(key, new ComponentVersion { Version = UntrustedText.Clean(c.Version, 64), PackageId = UntrustedText.Clean(c.PackageId, 128), AppliedUtc = c.AppliedUtc, Summary = Opt(c.Summary, 300) });
        }
        Components = components;
        if (ParameterSet is { } ps)
        {
            var sources = new Dictionary<string, string?>();
            foreach (var (document, revision) in (ps.SourceRevisions ?? new()).Take(MaxListedItems))
            {
                var key = UntrustedText.Clean(document, 64);
                if (key.Length > 0) sources.TryAdd(key, Opt(revision, 64));
            }
            ParameterSet = new ParameterSetRef
            {
                Id = UntrustedText.Clean(ps.Id, 128), Version = UntrustedText.Clean(ps.Version, 64), Sha256 = UntrustedText.Clean(ps.Sha256, 128),
                Path = UntrustedText.Clean(ps.Path, 260), SourceRevisions = sources,
            };
        }
        if (Environment is { } env) Environment = env with { Os = Opt(env.Os, 200), Dotnet = Opt(env.Dotnet, 100), WebView2 = Opt(env.WebView2, 64) };
    }
}

/// <summary>
/// <see cref="RecordStore.UpdateFleet"/> 的結果。<paramref name="Saved"/> false = 沒有取代:這份狀態在船上產生的時間比清單裡該船已有的舊
/// (只有隨身碟匯入會這樣;直連同步拿到的是當下的狀態,一律取代)。<paramref name="Previous"/>:更新前清單裡該船的項目(沒有時 null)。
/// <paramref name="StatusUtc"/> / <paramref name="PreviousStatusUtc"/>:這份狀態與原有項目在船上產生的時間
/// (<see cref="ShipVersionEntry.StatusUtcOf"/>;不知道時 null)。
/// </summary>
public sealed record FleetUpdate(bool Saved, ShipVersionEntry? Previous, DateTime? StatusUtc, DateTime? PreviousStatusUtc)
{
    /// <summary>有採用,但這份狀態沒有可用的產生時間,無法判斷是不是比原有的項目舊(照原本的作法取代)</summary>
    public bool AgeUnknown => Saved && Previous is not null && StatusUtc is null;
}

public sealed class ImportResult
{
    public bool Ok { get; set; }
    public bool AlreadyImported { get; set; }
    public string? PackageId { get; set; }
    public string? ShipId { get; set; }
    public string? DeviceId { get; set; }
    public string? Directory { get; set; }
    public bool DeviceEnrolled { get; set; }
    public RecordPackageManifest? Manifest { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 教官筆電紀錄庫(規劃書第 5.5 節:教官筆電即為訓練紀錄庫主本)。目錄:
/// <code>
/// &lt;store&gt;/records/&lt;ship&gt;/&lt;yyyy&gt;/&lt;packageId&gt;.sbrec   原始簽章紀錄包(證據保存)
/// &lt;store&gt;/records/&lt;ship&gt;/&lt;yyyy&gt;/&lt;packageId&gt;/…       解開的紀錄與報告
/// &lt;store&gt;/devices/&lt;ship&gt;/&lt;deviceId&gt;.pem             已登錄的船上筆電裝置公鑰
/// &lt;store&gt;/store-ledger.json  &lt;store&gt;/fleet-versions.json  &lt;store&gt;/audit.jsonl
/// </code>
/// 匯入時驗證裝置簽章與每檔雜湊;未登錄的裝置預設拒絕(--enroll-device 首次登錄並記入稽核)。
/// </summary>
public sealed class RecordStore
{
    private readonly TimeProvider _clock;

    public RecordStore(string root, TimeProvider? clock = null)
    {
        Root = Path.GetFullPath(root);
        _clock = clock ?? TimeProvider.System;
        System.IO.Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string RecordsDir => Path.Combine(Root, "records");
    public string DevicesDir => Path.Combine(Root, "devices");
    public string LedgerPath => Path.Combine(Root, "store-ledger.json");
    public string FleetPath => Path.Combine(Root, "fleet-versions.json");
    public AuditLog Audit => new(Path.Combine(Root, "audit.jsonl"), _clock);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private static readonly object Gate = new();

    public StoreLedger LoadLedger() => File.Exists(LedgerPath) ? PackageJson.Load<StoreLedger>(LedgerPath, "store-ledger.json") : new StoreLedger();
    private void SaveLedger(StoreLedger l) => PackageJson.SaveAtomic(LedgerPath, l);

    public FleetVersions LoadFleet() => File.Exists(FleetPath) ? PackageJson.Load<FleetVersions>(FleetPath, "fleet-versions.json") : new FleetVersions();

    /// <summary>這份船上狀態在船上產生的時間(<see cref="ShipVersionEntry.StatusUtcOf"/>,以紀錄庫的時鐘判斷合不合理)</summary>
    public DateTime? StatusUtcOf(EnvironmentInfo? environment) => ShipVersionEntry.StatusUtcOf(environment, Now);

    /// <summary>跨行程鎖定檔(0 位元組,留在紀錄庫裡;見 <see cref="UpdateFleet"/>)</summary>
    public string FleetLockPath => FleetPath + ".lock";

    /// <summary>等另一個行程放開兩船版次清單的時間上限(一次更新只要幾毫秒)</summary>
    internal TimeSpan FleetLockTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 把一艘船的狀態寫進兩船版次清單(整筆取代該船項目;另一艘船的項目不動)。
    /// <list type="bullet">
    /// <item><paramref name="skipIfOlder"/>(隨身碟匯入用):這份狀態在船上產生的時間比清單裡該船已有的舊時<b>不取代</b>,回傳
    /// <see cref="FleetUpdate.Saved"/> false — 隨身碟上會留著以前匯出的 status.json(同一支隨身碟拿去另一艘船用時,前一艘船的資料夾還在),
    /// 每次匯入都會再讀到;不擋的話,較新的直連同步結果(含保存的降版提醒)會被舊資料蓋回去。任一邊的時間不知道時照原本的作法取代。
    /// 直連同步不給這個參數:拿到的是當下的狀態。</item>
    /// <item>寫檔之前先清理船上回報的文字(<see cref="ShipVersionEntry.Sanitize"/>)。</item>
    /// <item>「讀 → 改 → 寫」以鎖定檔 <see cref="FleetLockPath"/>(獨占開啟)跨行程序列化:一個行程在直連同步、另一個同時匯入另一艘船的隨身碟時,
    /// 原本後寫的會把先寫的那艘船整筆蓋掉(兩邊都顯示成功)。等不到鎖定(<see cref="FleetLockTimeout"/>)時丟 <see cref="IOException"/>。</item>
    /// </list>
    /// </summary>
    public FleetUpdate UpdateFleet(ShipVersionEntry entry, bool skipIfOlder = false)
    {
        lock (Gate)
        {
            using var crossProcess = AcquireFleetLock();
            var now = Now;
            entry.Sanitize();
            if (entry.ShipId.Length == 0) throw new InvalidDataException("沒有船舶識別,無法寫進兩船版次清單 fleet-versions.json");
            var f = LoadFleet();
            f.Ships.TryGetValue(entry.ShipId, out var previous);
            // 產生時間要在「沿用上次的 environment」之前算:沿用來的是上一份狀態的時間
            var statusUtc = ShipVersionEntry.StatusUtcOf(entry.Environment, now);
            var previousUtc = ShipVersionEntry.StatusUtcOf(previous?.Environment, now);
            if (skipIfOlder && statusUtc is { } incoming && previousUtc is { } saved && incoming < saved)
                return new FleetUpdate(false, previous, statusUtc, previousUtc);
            entry.UpdatedUtc = now;
            // 船端未回報 environment(舊版船端、舊格式的隨身碟 status.json)時沿用上次已知值:筆電的 OS/.NET/WebView2 不因軟體回復而改變,
            // 陳舊與否由 EnvironmentInfo.CollectedUtc 判斷(docs/design/packaging-sync.md 第 7 節)
            if (entry.Environment is null && previous is not null) entry.Environment = previous.Environment;
            // 發行版狀態(releaseReported / releaseVersion / releaseDowngrade / releaseUnreadable)不沿用上一次的值:
            // 「這次沒有回報」「這次沒有降版」都要如實取代,保存的降版提醒才會在處理掉之後的下一次同步消失
            f.Ships[entry.ShipId] = entry;
            PackageJson.SaveAtomic(FleetPath, f);
            return new FleetUpdate(true, previous, statusUtc, previousUtc);
        }
    }

    /// <summary>
    /// 獨占開啟鎖定檔(FileShare.None;Windows 是共用違規、Linux 是 flock,行程結束時作業系統自動放開,不會留下過期的鎖)。
    /// 被別的行程佔用時每隔幾十毫秒重試,超過 <see cref="FleetLockTimeout"/> 丟 <see cref="IOException"/>。鎖定檔不刪除
    /// (關閉時刪除會讓第三個行程鎖到另一個檔案);<see cref="Backup"/> 不複製它。
    /// </summary>
    private FileStream AcquireFleetLock()
    {
        System.IO.Directory.CreateDirectory(Root);   // 與 SaveAtomic 相同:紀錄庫資料夾被移走時重建,不因為多了鎖定檔而改成失敗
        var deadline = System.Environment.TickCount64 + (long)FleetLockTimeout.TotalMilliseconds;
        while (true)
        {
            try
            {
                return new FileStream(FleetLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (File.Exists(FleetLockPath))   // 被佔用(鎖定檔不存在時的 IOException 是別的原因,例如資料夾不在,不重試)
            {
                if (System.Environment.TickCount64 >= deadline)
                    throw new IOException($"另一個 simosa-pkg 正在更新兩船版次清單({FleetLockPath} 被佔用超過 {FleetLockTimeout.TotalSeconds:0} 秒),這次沒有寫入;請等它結束後再執行一次");
                Thread.Sleep(20 + Random.Shared.Next(40));
            }
        }
    }

    private static string Safe(string s)
    {
        var cleaned = new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) throw new InvalidDataException($"不合法的識別字:{s}");
        return cleaned;
    }

    /// <summary>登錄船上筆電裝置公鑰(安裝時由船上 device show 匯出)。</summary>
    public TrustedKey RegisterDevice(string shipId, string deviceId, string publicPem)
    {
        var key = TrustedKey.Parse(publicPem, null, KeyRoles.Device);
        var dir = Path.Combine(DevicesDir, Safe(shipId));
        System.IO.Directory.CreateDirectory(dir);
        var info = new DeviceInfo { ShipId = shipId, DeviceId = deviceId, KeyId = key.KeyId };
        File.WriteAllText(Path.Combine(dir, Safe(deviceId) + ".pem"), DeviceKeyStore.PublicPemWithMeta(info, key.PublicKeyPem));
        return key;
    }

    public TrustedKey? FindDevice(string shipId, string deviceId)
    {
        var path = Path.Combine(DevicesDir, Safe(shipId), Safe(deviceId) + ".pem");
        if (!File.Exists(path)) return null;
        var k = TrustedKey.Parse(File.ReadAllText(path), path, KeyRoles.Device);
        if (k.Role != KeyRoles.Device) return null;
        return k;
    }

    public bool Has(string packageId, string sha256) => LoadLedger().Packages.Any(p => p.PackageId == packageId && Sha256Hex.Equal(p.Sha256, sha256));

    /// <summary>驗證並匯入紀錄包。</summary>
    public ImportResult Import(string sbrecPath, bool enrollUnknownDevice = false)
    {
        var r = new ImportResult();
        try
        {
            var pkgSha = Sha256Hex.OfFile(sbrecPath);
            using var zip = ZipFile.OpenRead(sbrecPath);
            // 重複項目(尤其是第二個 manifest.json)會讓解開的內容與驗證的簽章不一致:一律拒絕
            if (UpdatePackageReader.FindDuplicateEntry(zip) is { } dup) throw new InvalidDataException($"紀錄包內有重複的項目:{dup}");
            var (mb, sig) = UpdatePackageReader.ReadSigned(zip);
            if (sig.FormatError() is { } fe) throw new InvalidDataException(fe);
            var m = PackageJson.FromBytes<RecordPackageManifest>(mb, "紀錄包 manifest.json");
            if (m.Format != RecordPackageManifest.FormatId) throw new InvalidDataException($"紀錄包格式不支援:{m.Format}");
            r.PackageId = m.PackageId;
            r.ShipId = m.ShipId;
            r.DeviceId = m.DeviceId;
            Safe(m.PackageId);
            Safe(m.ShipId);
            Safe(m.DeviceId);

            using var registered = FindDevice(m.ShipId, m.DeviceId);
            TrustedKey? enrolled = null;
            var key = registered;
            if (key is null)
            {
                if (!enrollUnknownDevice) throw new InvalidDataException($"裝置 {m.ShipId}/{m.DeviceId} 未登錄於紀錄庫(首次請以 --enroll-device 登錄,並核對金鑰 {KeyFiles.Short(sig.KeyId)})");
                if (sig.PublicKey is null) throw new InvalidDataException("紀錄包未附裝置公鑰,無法登錄");
                enrolled = TrustedKey.Parse(sig.PublicKey, null, KeyRoles.Device);
                key = enrolled;
            }
            using var _e = enrolled;
            if (!string.Equals(key.KeyId, sig.KeyId, StringComparison.OrdinalIgnoreCase) || !string.Equals(m.Device.KeyId, sig.KeyId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"紀錄包簽章金鑰 {KeyFiles.Short(sig.KeyId)} 與登錄的裝置金鑰 {KeyFiles.Short(key.KeyId)} 不符");
            if (!sig.VerifyWith(key.Key, mb)) throw new InvalidDataException("紀錄包裝置簽章驗證失敗(manifest 遭竄改)");

            // 檔案清單 ↔ zip 內容與雜湊
            var listed = new Dictionary<string, RecordFileEntry>(StringComparer.Ordinal);
            foreach (var f in m.Files)
            {
                var p = PackagePaths.Normalize(f.Path);
                if (p != f.Path || !p.StartsWith(RecordPackageManifest.FilePrefix, StringComparison.Ordinal)) throw new InvalidDataException($"紀錄包路徑不合法:{f.Path}");
                if (!listed.TryAdd(p, f)) throw new InvalidDataException($"紀錄包檔案重複:{p}");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                if (e.FullName is "manifest.json" or "manifest.sig" || e.FullName.EndsWith('/')) continue;
                if (!listed.TryGetValue(e.FullName, out var f)) throw new InvalidDataException($"紀錄包含未列於 manifest 的檔案:{e.FullName}");
                if (!seen.Add(e.FullName)) throw new InvalidDataException($"紀錄包重複檔案:{e.FullName}");
                using var s = e.Open();
                if (!Sha256Hex.Equal(Sha256Hex.OfStream(s), f.Sha256) || e.Length != f.Size) throw new InvalidDataException($"紀錄檔雜湊不符(遭竄改):{e.FullName}");
            }
            var missing = listed.Keys.Where(k => !seen.Contains(k)).ToList();
            if (missing.Count > 0) throw new InvalidDataException($"紀錄包缺少檔案:{string.Join(", ", missing)}");

            lock (Gate)
            {
                var ledger = LoadLedger();
                var existing = ledger.Packages.FirstOrDefault(p => p.PackageId == m.PackageId);
                if (existing is not null)
                {
                    if (!Sha256Hex.Equal(existing.Sha256, pkgSha)) throw new InvalidDataException($"紀錄包 {m.PackageId} 已存在但內容不同");
                    r.Ok = true;
                    r.AlreadyImported = true;
                    r.Directory = existing.Directory;
                    r.Manifest = m;
                    return r;
                }
                if (enrolled is not null)
                {
                    using var reg = RegisterDevice(m.ShipId, m.DeviceId, enrolled.PublicKeyPem);
                    r.DeviceEnrolled = true;
                    Audit.Write(AuditEvents.StoreDeviceEnrolled, new { m.ShipId, m.DeviceId, keyId = enrolled.KeyId, via = "import" });
                }
                var year = m.CreatedUtc.Year.ToString("D4");
                var relDir = $"records/{Safe(m.ShipId)}/{year}/{Safe(m.PackageId)}";
                var dest = Path.Combine(Root, relDir.Replace('/', Path.DirectorySeparatorChar));
                var tmp = dest + ".importing";
                PackagePaths.DeleteDirectory(tmp);
                System.IO.Directory.CreateDirectory(tmp);
                // manifest.json/manifest.sig 寫入「已驗證的位元組」,不從 zip 另行解出;其餘只解出已核對雜湊的紀錄檔,不覆寫
                File.WriteAllBytes(Path.Combine(tmp, "manifest.json"), mb);
                File.WriteAllBytes(Path.Combine(tmp, "manifest.sig"), UpdatePackageReader.ReadEntry(zip.GetEntry("manifest.sig")!, 64 << 10));
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith('/') || e.FullName is "manifest.json" or "manifest.sig") continue;
                    var target = PackagePaths.Combine(tmp, PackagePaths.Normalize(e.FullName[RecordPackageManifest.FilePrefix.Length..]));
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: false);
                    if (!Sha256Hex.Equal(Sha256Hex.OfFile(target), listed[e.FullName].Sha256)) throw new InvalidDataException($"解開後紀錄檔雜湊不符:{e.FullName}");
                }
                PackagePaths.DeleteDirectory(dest);
                System.IO.Directory.Move(tmp, dest);
                File.Copy(sbrecPath, dest + ".sbrec", overwrite: true);
                ledger.Packages.Add(new StoredPackage
                {
                    PackageId = m.PackageId, ShipId = m.ShipId, DeviceId = m.DeviceId, Sha256 = pkgSha, Size = new FileInfo(sbrecPath).Length,
                    CreatedUtc = m.CreatedUtc, ImportedUtc = Now, Directory = relDir, Exercises = m.Exercises.Count, TrainingS = m.TrainingHours.TotalS,
                });
                SaveLedger(ledger);
                r.Ok = true;
                r.Directory = relDir;
                r.Manifest = m;
                Audit.Write(AuditEvents.StoreImported, new { m.PackageId, m.ShipId, m.DeviceId, sha256 = pkgSha, files = m.Files.Count, exercises = m.Exercises.Count, trainingHours = m.TrainingHours.TotalHours });
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Security.Cryptography.CryptographicException)
        {
            r.Errors.Add(ex.Message);
            Audit.Write(AuditEvents.StoreImportRejected, new { file = Path.GetFileName(sbrecPath), r.PackageId, r.ShipId, r.DeviceId, error = ex.Message });
        }
        return r;
    }

    /// <summary>
    /// 標記已完成岸上備份(加密外接碟或公司檔案共享)。<paramref name="packageIds"/> 為 null 時標記全部已匯入者。
    /// 之後的同步才會把這些紀錄包列入已備份回報。
    /// </summary>
    public List<string> MarkBackedUp(IEnumerable<string>? packageIds, string? target)
    {
        lock (Gate)
        {
            var l = LoadLedger();
            var ids = packageIds?.ToHashSet(StringComparer.Ordinal);
            var marked = new List<string>();
            foreach (var p in l.Packages.Where(p => p.BackedUpUtc is null && (ids is null || ids.Contains(p.PackageId))))
            {
                p.BackedUpUtc = Now;
                p.BackupTarget = target;
                marked.Add(p.PackageId);
            }
            SaveLedger(l);
            if (marked.Count > 0) Audit.Write(AuditEvents.StoreBackedUp, new { packages = marked, target });
            return marked;
        }
    }

    /// <summary>
    /// 備份整個紀錄庫到目的地(加密外接碟掛載點或檔案共享;目的地加密由作業系統 BitLocker To Go 等負責),
    /// 複製後逐檔比對雜湊,成功才標記已備份。
    /// </summary>
    public (string Target, List<string> Marked) Backup(string targetDir)
    {
        var targetFull = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (targetFull.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("備份目的地不可位於紀錄庫之內");
        var stamp = Now.ToString("yyyyMMdd'T'HHmmss");
        var dest = Path.Combine(targetDir, $"simosa-store-backup-{stamp}");
        var importedBefore = LoadLedger().Packages.Select(p => p.PackageId).ToList();
        foreach (var file in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            // 兩船版次清單的鎖定檔(0 位元組)不是資料:另一個行程正在同步時它被獨占開啟,複製會失敗
            if (string.Equals(file, FleetLockPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            var rel = Path.GetRelativePath(Root, file);
            var to = Path.Combine(dest, rel);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
            if (!Sha256Hex.Equal(Sha256Hex.OfFile(file), Sha256Hex.OfFile(to))) throw new IOException($"備份後雜湊不符:{rel}");
        }
        return (dest, MarkBackedUp(importedBefore, dest));
    }

    /// <summary>產生指定船舶的已備份回報(列出已備份、且船上仍保留的紀錄包;<paramref name="onlyIds"/> 為船上回報的清單)。無可回報者回傳 null。</summary>
    public (SignedDocument Document, BackupReport Report)? CreateBackupReport(string shipId, IKeyProvider key, IEnumerable<string>? onlyIds = null)
    {
        var only = onlyIds?.ToHashSet(StringComparer.Ordinal);
        var l = LoadLedger();
        var pkgs = l.Packages.Where(p => string.Equals(p.ShipId, shipId, StringComparison.OrdinalIgnoreCase) && p.BackedUpUtc is not null && p.ShipDeletedUtc is null
                                         && (only is null || only.Contains(p.PackageId))).ToList();
        if (pkgs.Count == 0) return null;
        var now = Now;
        var report = new BackupReport
        {
            ReportId = $"bak-{shipId}-{now:yyyyMMdd'T'HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            ShipId = shipId, IssuedUtc = now, Store = System.Environment.MachineName,
            Packages = pkgs.Select(p => new BackedUpPackage { PackageId = p.PackageId, Sha256 = p.Sha256, ImportedUtc = p.ImportedUtc, BackedUpUtc = p.BackedUpUtc, BackupTarget = p.BackupTarget }).ToList(),
            SignerKeyId = key.KeyId, SignerName = key.DisplayName,
        };
        var doc = SignedDocument.Create(report, key, now);
        Audit.Write(AuditEvents.StoreReportIssued, new { report.ReportId, shipId, packages = report.Packages.Select(p => p.PackageId).ToList(), signer = key.DisplayName });
        return (doc, report);
    }

    /// <summary>船上回報已刪除(直連 cleanupResult,或隨身碟上的裝置簽章清理通知)後記錄;<paramref name="shipId"/> 不為 null 時只標記該船的紀錄包。</summary>
    public void MarkShipDeleted(string reportId, IEnumerable<string> packageIds, string? shipId = null)
    {
        lock (Gate)
        {
            var l = LoadLedger();
            var ids = packageIds.ToHashSet(StringComparer.Ordinal);
            foreach (var p in l.Packages.Where(p => ids.Contains(p.PackageId) && (shipId is null || string.Equals(p.ShipId, shipId, StringComparison.OrdinalIgnoreCase))))
            {
                p.ShipDeletedUtc ??= Now;
                p.ReportedId = reportId;
            }
            SaveLedger(l);
        }
    }
}
