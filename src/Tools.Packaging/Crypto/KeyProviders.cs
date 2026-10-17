using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SimosaBRM.Packaging.Crypto;

/// <summary>
/// 簽章金鑰來源(規劃書第 5.5 節):正式環境為訓練負責人保管、以 PIN 保護的硬體金鑰(智慧卡/PIV 安全金鑰),
/// 經 Windows 憑證存放區以憑證指紋取得私鑰,PIN 由作業系統提示;開發/測試用 PEM 檔(CLI 會顯示警告)。
/// 簽章格式固定為 SHA-256 + IEEE P1363(r‖s,64 位元組)。
/// </summary>
public interface IKeyProvider : IDisposable
{
    string KeyId { get; }
    string DisplayName { get; }
    /// <summary>私鑰在硬體中(不可匯出)</summary>
    bool IsHardwareBacked { get; }
    string PublicKeyPem { get; }
    byte[] Sign(byte[] data);
}

/// <summary>PEM 檔私鑰(開發/測試;裝置金鑰在 Linux 測試時亦用此格式)。</summary>
public sealed class PemKeyProvider : IKeyProvider
{
    private readonly ECDsa _key;

    public PemKeyProvider(string pemText, string? displayName = null, string? password = null)
    {
        var meta = KeyFiles.ReadMeta(pemText);
        var block = KeyFiles.PemBlock(pemText);
        _key = ECDsa.Create();
        try
        {
            if (block.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(password)) throw new CryptographicException("私鑰已加密,需提供密碼(--key-password 或環境變數 SIMOSA_KEY_PASSWORD)");
                _key.ImportFromEncryptedPem(block, password);
            }
            else _key.ImportFromPem(block);
            _ = _key.ExportParameters(true); // 確認含私鑰
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            _key.Dispose();
            throw new CryptographicException($"無法讀取 EC 私鑰 PEM:{ex.Message}", ex);
        }
        KeyFiles.EnsureP256(_key, "簽章金鑰");
        KeyId = KeyFiles.KeyId(_key);
        DisplayName = displayName ?? (meta.TryGetValue("name", out var n) ? n : $"PEM {KeyFiles.Short(KeyId)}");
        Meta = meta;
    }

    public static PemKeyProvider FromFile(string path, string? displayName = null, string? password = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到金鑰檔:{path}", path);
        return new PemKeyProvider(File.ReadAllText(path), displayName, password);
    }

    /// <summary>PEM 前的中繼資料(name、role…)</summary>
    public IReadOnlyDictionary<string, string> Meta { get; }
    public string KeyId { get; }
    public string DisplayName { get; }
    public bool IsHardwareBacked => false;
    public string PublicKeyPem => KeyFiles.PublicPem(_key);
    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    public void Dispose() => _key.Dispose();
}

/// <summary>
/// 以 ECDsa 物件直接簽章(裝置金鑰自 DPAPI 解開後使用)。
/// </summary>
public sealed class EcdsaKeyProvider : IKeyProvider
{
    private readonly ECDsa _key;

    public EcdsaKeyProvider(ECDsa key, string displayName, bool hardware = false)
    {
        KeyFiles.EnsureP256(key, "簽章金鑰");
        _key = key;
        DisplayName = displayName;
        IsHardwareBacked = hardware;
        KeyId = KeyFiles.KeyId(key);
    }

    public string KeyId { get; }
    public string DisplayName { get; }
    public bool IsHardwareBacked { get; }
    public string PublicKeyPem => KeyFiles.PublicPem(_key);
    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Windows 憑證存放區(CurrentUser\My 或 LocalMachine\My)中以指紋指定的憑證私鑰。智慧卡/PIV 安全金鑰經 Windows 智慧卡
/// 迷你驅動程式登錄後即出現在 CurrentUser\My;簽章時由作業系統(CNG KSP)提示 PIN,私鑰不離開硬體。
/// 只在 Windows 執行;其他平台建構時丟出 <see cref="PlatformNotSupportedException"/>(Linux 測試改用 PEM)。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CertificateStoreKeyProvider : IKeyProvider
{
    private readonly X509Certificate2 _cert;
    private readonly ECDsa _key;

    public CertificateStoreKeyProvider(string thumbprint, StoreLocation location = StoreLocation.CurrentUser)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("憑證存放區金鑰僅支援 Windows;開發/測試請改用 --key <PEM>");
        var tp = new string(thumbprint.Where(Uri.IsHexDigit).ToArray());
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, tp, validOnly: false);
        if (found.Count == 0) throw new CryptographicException($"憑證存放區 {location}\\My 找不到指紋 {tp} 的憑證(硬體金鑰是否已插入?)");
        _cert = found[0];
        _key = _cert.GetECDsaPrivateKey() ?? throw new CryptographicException($"憑證 {_cert.Subject} 沒有可用的 ECDSA 私鑰");
        KeyFiles.EnsureP256(_key, "硬體簽章金鑰");
        KeyId = KeyFiles.KeyId(_key);
        DisplayName = _cert.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } cn ? cn : _cert.Subject;
        IsHardwareBacked = IsNonExportable(_key);
    }

    private static bool IsNonExportable(ECDsa key)
    {
        if (key is ECDsaCng cng)
        {
            try { return (cng.Key.ExportPolicy & CngExportPolicies.AllowPlaintextExport) == 0; }
            catch (CryptographicException) { return true; }
        }
        return true;
    }

    public string KeyId { get; }
    public string DisplayName { get; }
    public bool IsHardwareBacked { get; }
    public string Thumbprint => _cert.Thumbprint;
    public string PublicKeyPem => KeyFiles.PublicPem(_key);
    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose()
    {
        _key.Dispose();
        _cert.Dispose();
    }
}
