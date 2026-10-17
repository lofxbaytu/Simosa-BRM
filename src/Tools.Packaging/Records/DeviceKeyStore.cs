using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;

namespace SimosaBRM.Packaging.Records;

/// <summary>config/device/device.json:船上筆電識別。</summary>
public sealed class DeviceInfo
{
    public string ShipId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string KeyId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>dpapi(Windows,LocalMachine 範圍)或 pem(開發/測試)</summary>
    public string Protection { get; set; } = "pem";
}

/// <summary>
/// 裝置金鑰(紀錄包簽章;安裝時產生,規劃書第 5.5 節)。私鑰不離開船上筆電:
/// Windows 上以 DPAPI(LocalMachine 範圍 + 應用程式熵)加密存為 config/device/device-key.dpapi;
/// 其他平台(Linux 測試)存為 PEM(檔案權限 600)並於 CLI 警告。公鑰 device-key.pub.pem 於安裝時登錄到教官筆電紀錄庫。
/// </summary>
public static class DeviceKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SimosaBRM/device-key/1");
    public const string InfoFile = "device.json";
    public const string DpapiFile = "device-key.dpapi";
    public const string PemFile = "device-key.pem";
    public const string PublicFile = "device-key.pub.pem";

    public static bool Exists(InstallLayout layout) => File.Exists(Path.Combine(layout.DeviceDir, InfoFile));

    /// <summary>產生裝置金鑰(已存在時須 overwrite)。<paramref name="forcePem"/> 強制以 PEM 保存(測試)。</summary>
    public static DeviceInfo Initialize(InstallLayout layout, string shipId, string? deviceId = null, bool overwrite = false, bool forcePem = false, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(shipId)) throw new ArgumentException("需提供船舶 ID");
        if (Exists(layout) && !overwrite) throw new InvalidOperationException("裝置金鑰已存在(更換須 --overwrite,且須重新登錄到教官筆電)");
        Directory.CreateDirectory(layout.DeviceDir);
        using var key = KeyFiles.CreateP256();
        var info = new DeviceInfo
        {
            ShipId = shipId,
            DeviceId = string.IsNullOrWhiteSpace(deviceId) ? $"{shipId}-{System.Environment.MachineName}" : deviceId!,
            KeyId = KeyFiles.KeyId(key),
            CreatedUtc = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime,
        };
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            if (OperatingSystem.IsWindows() && !forcePem)
            {
                WriteDpapi(Path.Combine(layout.DeviceDir, DpapiFile), pkcs8);
                info.Protection = "dpapi";
                File.Delete(Path.Combine(layout.DeviceDir, PemFile));
            }
            else
            {
                var pemPath = Path.Combine(layout.DeviceDir, PemFile);
                File.WriteAllText(pemPath, KeyFiles.WithMeta(PemEncoding.WriteString("PRIVATE KEY", pkcs8),
                    new Dictionary<string, string?> { ["ship"] = info.ShipId, ["device"] = info.DeviceId, ["keyId"] = info.KeyId },
                    "Simosa BRM 裝置私鑰(PEM;僅供開發/測試,正式船上筆電以 DPAPI 保護)"));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(pemPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                info.Protection = "pem";
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
        File.WriteAllText(Path.Combine(layout.DeviceDir, PublicFile), PublicPemWithMeta(info, KeyFiles.PublicPem(key)));
        PackageJson.SaveAtomic(Path.Combine(layout.DeviceDir, InfoFile), info);
        layout.Audit(clock).Write(AuditEvents.DeviceInitialized, new { info.ShipId, info.DeviceId, info.KeyId, info.Protection });
        return info;
    }

    public static string PublicPemWithMeta(DeviceInfo info, string publicPem)
        => KeyFiles.WithMeta(publicPem, new Dictionary<string, string?>
        {
            ["role"] = KeyRoles.Device, ["name"] = $"{info.ShipId} 船上筆電 {info.DeviceId}", ["ship"] = info.ShipId, ["device"] = info.DeviceId, ["keyId"] = info.KeyId,
        }, "Simosa BRM 裝置公鑰(登錄到教官筆電紀錄庫 devices/)");

    [SupportedOSPlatform("windows")]
    private static void WriteDpapi(string path, byte[] pkcs8)
    {
        var blob = ProtectedData.Protect(pkcs8, Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(path, blob);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ReadDpapi(string path) => ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);

    public static DeviceInfo LoadInfo(InstallLayout layout)
    {
        var p = Path.Combine(layout.DeviceDir, InfoFile);
        if (!File.Exists(p)) throw new InvalidOperationException("尚未產生裝置金鑰(simosa-pkg device init --ship <船舶 ID>)");
        return PackageJson.Load<DeviceInfo>(p, "device.json");
    }

    /// <summary>載入裝置私鑰為簽章提供者。</summary>
    public static (DeviceInfo Info, IKeyProvider Key) Load(InstallLayout layout)
    {
        var info = LoadInfo(layout);
        var ecdsa = ECDsa.Create();
        if (info.Protection == "dpapi")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI 保護的裝置金鑰只能在原 Windows 筆電使用");
            var pkcs8 = ReadDpapi(Path.Combine(layout.DeviceDir, DpapiFile));
            try { ecdsa.ImportPkcs8PrivateKey(pkcs8, out _); }
            finally { CryptographicOperations.ZeroMemory(pkcs8); }
        }
        else ecdsa.ImportFromPem(KeyFiles.PemBlock(File.ReadAllText(Path.Combine(layout.DeviceDir, PemFile))));
        var provider = new EcdsaKeyProvider(ecdsa, $"{info.ShipId} 船上筆電 {info.DeviceId}");
        if (!string.Equals(provider.KeyId, info.KeyId, StringComparison.OrdinalIgnoreCase))
        {
            provider.Dispose();
            throw new CryptographicException("裝置私鑰與 device.json 的 keyId 不符");
        }
        return (info, provider);
    }

    public static string PublicPem(InstallLayout layout) => File.ReadAllText(Path.Combine(layout.DeviceDir, PublicFile));
}
