using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 稽核紀錄(JSON Lines,追加寫入;規劃書第 5.5 節「記入稽核紀錄」)。每行:
/// <c>{"ts":"…Z","event":"update.applied","host":"…","details":{…}}</c>。事件名稱見 <see cref="AuditEvents"/>。
/// 船上筆電:state/audit.jsonl;教官筆電紀錄庫:&lt;store&gt;/audit.jsonl。
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
        lock (Gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes(text);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
    }

    /// <summary>讀回所有行(測試與 status 顯示用)。</summary>
    public IReadOnlyList<JsonObject> ReadAll()
    {
        if (!File.Exists(Path)) return Array.Empty<JsonObject>();
        return File.ReadLines(Path, Encoding.UTF8).Where(l => l.Length > 0)
            .Select(l => JsonNode.Parse(l) as JsonObject).Where(o => o is not null).Cast<JsonObject>().ToList();
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
    public const string StoreImported = "store.imported";
    public const string StoreImportRejected = "store.importRejected";
    public const string StoreDeviceEnrolled = "store.deviceEnrolled";
    public const string StoreBackedUp = "store.backedUp";
    public const string StoreReportIssued = "store.reportIssued";
    public const string PackageBuilt = "package.built";
    public const string PackageForced = "package.forced";
}
