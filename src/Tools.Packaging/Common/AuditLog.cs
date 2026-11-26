using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 稽核紀錄(JSON Lines,追加寫入;規劃書第 5.5 節「記入稽核紀錄」)。每行:
/// <c>{"ts":"…Z","event":"update.applied","host":"…","details":{…}}</c>。事件名稱見 <see cref="AuditEvents"/>。
/// 船上筆電:state/audit.jsonl;教官筆電紀錄庫:&lt;store&gt;/audit.jsonl。
/// <para>
/// 兩個行程同時附加(同步服務與另一個視窗的指令;兩個教官端行程同時匯入)時,每一行的寫入以鎖定檔 <see cref="LockPath"/>(audit.jsonl.lock;
/// 共用的 <see cref="StateLock"/>)跨行程序列化:.NET 的附加寫入是「開檔時記下檔尾位置,再從那裡寫」,不是作業系統的附加模式,
/// 兩個行程在同一瞬間開檔會寫在同一個位置 — 後寫的蓋掉先寫的那一行,長度不同時留下讀不進來的半行(兩個行程各寫 300 行,實測每回合少 7 至 144 行)。
/// 這把鎖永遠最後取得(可以在船上的狀態鎖、紀錄庫的帳冊或版次清單的鎖裡取得,反過來不行),只持有一次寫入的時間。
/// 稽核紀錄不能因為鎖而寫不進去:等不到(<see cref="LockTimeout"/>)或鎖定檔建立不了時照原本的作法直接附加。讀取(<see cref="ReadAll"/>)不取鎖。
/// </para>
/// </summary>
public sealed class AuditLog
{
    private static readonly object Gate = new();
    private readonly TimeProvider _clock;

    public AuditLog(string path, TimeProvider? clock = null)
    {
        Path = path;
        _clock = clock ?? TimeProvider.System;
    }

    public string Path { get; }

    /// <summary>附加寫入的跨行程鎖定檔(0 位元組,留在稽核紀錄旁邊不刪;紀錄庫的 store backup 不複製它)</summary>
    public string LockPath => Path + ".lock";

    /// <summary>等另一個行程寫完一行的時間上限(一次寫入只要幾毫秒;備份紀錄庫複製稽核紀錄的那一刻也持有);等不到就不等了,直接附加</summary>
    internal TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 取得附加寫入的鎖(同一條執行緒放開)。拿不到 — 被佔用超過 <see cref="LockTimeout"/>、鎖定檔建立不了(資料夾唯讀、磁碟滿)— 時回傳 null,
    /// 呼叫端照沒有鎖的作法繼續:多一個鎖定檔不能讓原本寫得進去的稽核紀錄變成寫不進去。
    /// </summary>
    internal IDisposable? TryLock()
    {
        try
        {
            return StateLock.Acquire(new StateLockSpec
            {
                Path = LockPath, Timeout = LockTimeout, OtherErrorGrace = TimeSpan.FromMilliseconds(500),
                BusyMessage = t => $"稽核紀錄的鎖定檔 {LockPath} 被佔用超過 {t.TotalSeconds:0} 秒",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Write(string evt, object? details = null)
    {
        var line = new JsonObject
        {
            ["ts"] = _clock.GetUtcNow().UtcDateTime.ToString("O"),
            ["event"] = evt,
            ["host"] = System.Environment.MachineName,
            ["details"] = details is null ? null : JsonSerializer.SerializeToNode(details, details.GetType(), PackageJson.Compact),
        };
        var text = line.ToJsonString(PackageJson.Compact) + "\n";
        // 「開檔(記下檔尾)→ 寫入」整段在跨行程的鎖裡;稽核紀錄本身不以獨占開啟(讀取端會開不了)。
        // 先取跨行程的鎖、再進行程內的 Gate:等另一個行程時不佔著 Gate(同一個行程裡讀稽核紀錄、寫別的稽核紀錄的執行緒不必跟著等)
        using var appendLock = TryLock();
        lock (Gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            // FileShare.ReadWrite:讓其他行程(船上 status 指令、教官端檢視)在同步服務寫入中仍可讀取;Windows 上以 FileShare.Read 開檔會使讀取端失敗
            using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(text);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
    }

    /// <summary>讀回所有行(測試與 status 顯示用)。</summary>
    public IReadOnlyList<JsonObject> ReadAll()
    {
        // 與 Write 同一把鎖(同行程不讀到半行),並以 FileShare.ReadWrite | Delete 開檔:寫入端仍開著檔案時,
        // Windows 上 File.ReadLines(只允許共用 Read)會丟 IOException(CI windows-latest 的 SyncTests 曾因此失敗)
        lock (Gate)
        {
            if (!File.Exists(Path)) return Array.Empty<JsonObject>();
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var list = new List<JsonObject>();
            while (reader.ReadLine() is { } l)
                if (l.Length > 0 && JsonNode.Parse(l) is JsonObject o) list.Add(o);
            return list;
        }
    }
}

public static class AuditEvents
{
    public const string UpdateApplied = "update.applied";
    public const string UpdateRejected = "update.rejected";
    public const string UpdateFailed = "update.failed";
    public const string UpdateRolledBack = "update.rolledBack";
    public const string TrustRotated = "trust.rotated";
    public const string TrustManualChange = "trust.manualChange";
    public const string DeviceInitialized = "device.initialized";
    public const string RecordsPacked = "records.packed";
    public const string RecordsPulled = "records.pulled";
    public const string RecordsDeleted = "records.deleted";
    public const string BackupReportAccepted = "backupReport.accepted";
    public const string BackupReportRejected = "backupReport.rejected";
    public const string SyncSessionStart = "sync.session.start";
    public const string SyncSessionEnd = "sync.session.end";
    public const string SyncAuthFailed = "sync.auth.failed";
    /// <summary>未驗證的失敗嘗試超過速率上限:之後只計數(phase start/end,end 含被略過的次數)</summary>
    public const string SyncAuthThrottled = "sync.auth.throttled";
    public const string SyncSessionError = "sync.session.error";
    public const string StoreImported = "store.imported";
    public const string StoreImportRejected = "store.importRejected";
    public const string StoreDeviceEnrolled = "store.deviceEnrolled";
    public const string StoreBackedUp = "store.backedUp";
    public const string StoreReportIssued = "store.reportIssued";
    public const string PackageBuilt = "package.built";
    public const string PackageForced = "package.forced";
}
