using System.Security.Cryptography;

namespace SimosaBRM.Packaging.Crypto;

/// <summary>信任金鑰角色:主金鑰(訓練負責人)、備援金鑰(DPA/海務主管)、裝置金鑰(船上筆電,教官端紀錄庫登錄)。</summary>
public static class KeyRoles
{
    public const string Primary = "primary";
    public const string Backup = "backup";
    public const string Device = "device";

    public static bool IsSigner(string role) => role is Primary or Backup;
}

/// <summary>信任清單中的一把公鑰(PEM 檔 + 中繼資料)。</summary>
public sealed class TrustedKey : IDisposable
{
    public required string KeyId { get; init; }
    public required string Name { get; init; }
    public required string Role { get; init; }
    public string? Holder { get; init; }
    public string? FilePath { get; init; }
    public required string PublicKeyPem { get; init; }
    public required ECDsa Key { get; init; }
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();

    public string Describe() => $"{Name}({RoleLabel(Role)},金鑰 {KeyFiles.Short(KeyId)}{(Holder is { Length: > 0 } h ? $",保管人 {h}" : "")})";

    public static string RoleLabel(string role) => role switch
    {
        KeyRoles.Primary => "主金鑰",
        KeyRoles.Backup => "備援金鑰",
        KeyRoles.Device => "裝置金鑰",
        _ => role,
    };

    /// <summary>自 PEM 文字解析;<paramref name="defaultRole"/> 用於未標 role 的檔案。</summary>
    public static TrustedKey Parse(string text, string? filePath, string defaultRole)
    {
        var meta = KeyFiles.ReadMeta(text);
        var key = KeyFiles.ImportPublic(text, filePath ?? "公鑰");
        var keyId = KeyFiles.KeyId(key);
        if (meta.TryGetValue("keyid", out var declared) && !string.Equals(declared, keyId, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"{filePath} 標示的 keyId 與公鑰不符");
        var role = meta.TryGetValue("role", out var r) && r.Length > 0 ? r.ToLowerInvariant() : defaultRole;
        if (role is not (KeyRoles.Primary or KeyRoles.Backup or KeyRoles.Device))
            throw new CryptographicException($"{filePath} 的 role 須為 primary、backup 或 device:{role}");
        return new TrustedKey
        {
            KeyId = keyId,
            Name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : Path.GetFileNameWithoutExtension(filePath ?? keyId),
            Role = role,
            Holder = meta.TryGetValue("holder", out var h) ? h : null,
            FilePath = filePath,
            PublicKeyPem = KeyFiles.PemBlock(text).Trim() + "\n",
            Key = key,
            Meta = meta,
        };
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>
/// 信任清單:目錄內所有 *.pem(船上筆電 config/trusted-keys/;主金鑰 + 備援金鑰,系統映像預載)。
/// 檔案格式:PEM 前以 <c># role: primary|backup</c>、<c># name: …</c>、<c># holder: …</c> 標示。
/// </summary>
public sealed class TrustStore : IDisposable
{
    private readonly List<TrustedKey> _keys;

    public TrustStore(IEnumerable<TrustedKey> keys) => _keys = keys.ToList();

    public IReadOnlyList<TrustedKey> Keys => _keys;

    public static TrustStore LoadDirectory(string dir, string defaultRole = KeyRoles.Primary)
    {
        var keys = new List<TrustedKey>();
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.pem").OrderBy(f => f, StringComparer.Ordinal))
                keys.Add(TrustedKey.Parse(File.ReadAllText(f), f, defaultRole));
        }
        return new TrustStore(keys);
    }

    public TrustedKey? Find(string keyId) => _keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        foreach (var k in _keys) k.Dispose();
    }
}
