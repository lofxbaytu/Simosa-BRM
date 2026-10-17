using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Sync;

/// <summary>
/// 兩筆電直連同步協定(TCP,預設埠 7300;設計文件 docs/design/packaging-sync.md 第 4 節)。
/// 框架:每則訊息為 4 位元組大端序長度 + UTF-8 JSON(上限 1 MiB);JSON 一律含 <c>type</c>。
/// 檔案:先送 <c>{"type":"file"| "pushUpdate" | "backupReport", "size":N, "sha256":"…"}</c>,緊接著 N 位元組原始資料(不加框)。
/// </summary>
public static class SyncProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1 << 20;
    public const long DefaultMaxFileBytes = 8L << 30;
    public const string Context = "simosa-sync/1";

    /// <summary>教官端簽章內容(證明是訓練負責人):船端 nonce + 船與裝置識別 + 教官端 nonce。</summary>
    public static byte[] InstructorAuthPayload(string shipId, string deviceId, string shipNonce, string clientNonce)
        => Encoding.UTF8.GetBytes($"{Context}\ninstructor\n{shipId}\n{deviceId}\n{shipNonce}\n{clientNonce}");

    /// <summary>船端簽章內容(證明是登錄的船上筆電):教官端 nonce + 船端 nonce。</summary>
    public static byte[] DeviceAuthPayload(string shipId, string deviceId, string clientNonce, string shipNonce)
        => Encoding.UTF8.GetBytes($"{Context}\ndevice\n{shipId}\n{deviceId}\n{clientNonce}\n{shipNonce}");

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static bool IsValidNonce(string? n)
    {
        if (string.IsNullOrEmpty(n) || n.Length > 64) return false;
        try { return Convert.FromBase64String(n).Length == 32; }
        catch (FormatException) { return false; }
    }

    public static async Task WriteAsync(Stream s, object message, CancellationToken ct)
    {
        var node = message as JsonNode ?? JsonSerializer.SerializeToNode(message, message.GetType(), PackageJson.Compact)!;
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(PackageJson.Compact));
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("訊息過大");
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)bytes.Length);
        await s.WriteAsync(len, ct).ConfigureAwait(false);
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<JsonObject> ReadAsync(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct).ConfigureAwait(false);
        var n = BinaryPrimitives.ReadUInt32BigEndian(len);
        if (n == 0 || n > MaxMessageBytes) throw new InvalidDataException($"訊息長度不合法:{n}");
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        JsonNode? node;
        try { node = JsonNode.Parse(buf); }
        catch (JsonException ex) { throw new InvalidDataException($"訊息不是有效的 JSON:{ex.Message}"); }
        if (node is not JsonObject obj || obj["type"]?.GetValueKind() != JsonValueKind.String) throw new InvalidDataException("訊息缺少 type");
        return obj;
    }

    public static string TypeOf(JsonObject o) => o["type"]!.GetValue<string>();

    public static T As<T>(JsonObject o) => o.Deserialize<T>(PackageJson.Compact) ?? throw new InvalidDataException($"無法解析訊息 {TypeOf(o)}");

    /// <summary>送出檔案內容(檔頭訊息須已送出)。</summary>
    public static async Task WriteFileBytesAsync(Stream s, string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        await fs.CopyToAsync(s, 1 << 16, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>接收恰好 <paramref name="size"/> 位元組到檔案並回傳 SHA-256。</summary>
    public static async Task<string> ReadFileBytesAsync(Stream s, long size, string destPath, long maxBytes, CancellationToken ct)
    {
        if (size < 0 || size > maxBytes) throw new InvalidDataException($"檔案大小不合法:{size}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buf = new byte[1 << 16];
            var remaining = size;
            while (remaining > 0)
            {
                var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("檔案傳輸中斷");
                hash.AppendData(buf, 0, n);
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                remaining -= n;
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

/// <summary>同步訊息型別名稱。</summary>
public static class SyncTypes
{
    public const string Hello = "hello";
    public const string Challenge = "challenge";
    public const string Auth = "auth";
    public const string Welcome = "welcome";
    public const string Error = "error";
    public const string Status = "status";
    public const string StatusResult = "statusResult";
    public const string PushUpdate = "pushUpdate";
    public const string UpdateResult = "updateResult";
    public const string ListRecords = "listRecords";
    public const string RecordList = "recordList";
    public const string GetRecord = "getRecord";
    public const string File = "file";
    public const string BackupReport = "backupReport";
    public const string CleanupResult = "cleanupResult";
    public const string Bye = "bye";
}

/// <summary>錯誤代碼(error.code)。</summary>
public static class SyncErrors
{
    public const string Protocol = "protocol";
    public const string AuthFailed = "auth-failed";
    public const string UntrustedKey = "untrusted-key";
    public const string NotFound = "not-found";
    public const string Transfer = "transfer";
    public const string UnknownType = "unknown-type";
    public const string Internal = "internal";
}

public sealed record HelloMessage
{
    public string Type { get; init; } = SyncTypes.Hello;
    public int Protocol { get; init; } = SyncProtocol.Version;
    public string Role { get; init; } = "instructor";
    public required string ClientNonce { get; init; }
    public string? Client { get; init; }
}

public sealed record ChallengeMessage
{
    public string Type { get; init; } = SyncTypes.Challenge;
    public int Protocol { get; init; } = SyncProtocol.Version;
    public required string ShipId { get; init; }
    public required string DeviceId { get; init; }
    public required string Nonce { get; init; }
    public required string DeviceKeyId { get; init; }
    public required string DevicePublicKey { get; init; }
    /// <summary>裝置金鑰對 DeviceAuthPayload 的簽章(base64)</summary>
    public required string DeviceSignature { get; init; }
}

public sealed record AuthMessage
{
    public string Type { get; init; } = SyncTypes.Auth;
    public required string KeyId { get; init; }
    public string? Signer { get; init; }
    public required string Signature { get; init; }
}

public sealed record WelcomeMessage
{
    public string Type { get; init; } = SyncTypes.Welcome;
    public required string ShipId { get; init; }
    public required string DeviceId { get; init; }
    /// <summary>船上信任清單中的簽章者描述(船上畫面同步顯示)</summary>
    public required string Signer { get; init; }
    public required string SignerRole { get; init; }
    public int VisitSeq { get; init; }
}

public sealed record ErrorMessage
{
    public string Type { get; init; } = SyncTypes.Error;
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record FileHeader
{
    public string Type { get; init; } = SyncTypes.File;
    public string? Name { get; init; }
    public string? PackageId { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record GetRecordMessage
{
    public string Type { get; init; } = SyncTypes.GetRecord;
    public required string PackageId { get; init; }
}

public sealed record RecordListMessage
{
    public string Type { get; init; } = SyncTypes.RecordList;
    public List<Records.OutstandingPackage> Packages { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public sealed record UpdateResultMessage
{
    public string Type { get; init; } = SyncTypes.UpdateResult;
    public required Updates.UpdateApplyResult Result { get; init; }
}

public sealed record CleanupResultMessage
{
    public string Type { get; init; } = SyncTypes.CleanupResult;
    public required Records.CleanupResult Result { get; init; }
}

/// <summary>船上狀態(statusResult;亦為 simosa-pkg status --json 的輸出)。</summary>
public sealed record ShipStatus
{
    public string Type { get; init; } = SyncTypes.StatusResult;
    public string? ShipId { get; init; }
    public string? DeviceId { get; init; }
    public string? Version { get; init; }
    public string? PackageId { get; init; }
    public DateTime? AppliedUtc { get; init; }
    public Updates.InstalledSigner? Signer { get; init; }
    public string? HighestVersion { get; init; }
    public Dictionary<string, Updates.ComponentVersion> Components { get; init; } = new();
    public Updates.ParameterSetRef? ParameterSet { get; init; }
    public List<Updates.SmsDocumentRef> SmsDocuments { get; init; } = new();
    public List<TrustedKeyInfo> TrustedKeys { get; init; } = new();
    public int OutstandingRecordPackages { get; init; }
    public int VisitSeq { get; init; }
    public bool RollbackAvailable { get; init; }
    public List<string> Warnings { get; init; } = new();
}

public sealed record TrustedKeyInfo(string KeyId, string Name, string Role, string? Holder);
