using System.Security.Cryptography;
using System.Text;

namespace SimosaBRM.Packaging.Crypto;

/// <summary>
/// 金鑰 ID 與 PEM 檔讀寫。演算法固定 ECDSA P-256 + SHA-256(ES256;.NET 內建,智慧卡/PIV 安全金鑰普遍支援)。
/// 金鑰 ID = SubjectPublicKeyInfo(DER)的 SHA-256(小寫 hex,64 字元;顯示時取前 16 字元)。
/// PEM 檔可在 <c>-----BEGIN</c> 之前放 <c># 名稱: 值</c> 註解行作為中繼資料(name、role、holder、ship、device…)。
/// </summary>
public static class KeyFiles
{
    public const string P256Oid = "1.2.840.10045.3.1.7";

    public static string KeyId(ECDsa key) => Common.Sha256Hex.Of(key.ExportSubjectPublicKeyInfo());

    public static string Short(string keyId) => keyId.Length > 16 ? keyId[..16] : keyId;

    public static ECDsa CreateP256() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static void EnsureP256(ECDsa key, string what)
    {
        var p = key.ExportParameters(false);
        var oid = p.Curve.Oid?.Value;
        var friendly = p.Curve.Oid?.FriendlyName;
        if (key.KeySize != 256 || !(oid == P256Oid || friendly is "nistP256" or "ECDSA_P256" or "secp256r1"))
            throw new CryptographicException($"{what} 須為 ECDSA P-256 金鑰(實際 {friendly ?? oid ?? "?"} {key.KeySize} bit)");
    }

    public static string PublicPem(ECDsa key) => PemEncoding.WriteString("PUBLIC KEY", key.ExportSubjectPublicKeyInfo());

    /// <summary>讀取 PEM 前的 <c># key: value</c> 中繼資料(鍵名小寫)。</summary>
    public static Dictionary<string, string> ReadMeta(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("-----BEGIN", StringComparison.Ordinal)) break;
            if (!line.StartsWith('#')) continue;
            var body = line.TrimStart('#').Trim();
            var idx = body.IndexOf(':');
            if (idx <= 0) continue;
            meta[body[..idx].Trim().ToLowerInvariant()] = body[(idx + 1)..].Trim();
        }
        return meta;
    }

    /// <summary>取出第一個 PEM 區塊(含 BEGIN/END 行)。</summary>
    public static string PemBlock(string text)
    {
        var begin = text.IndexOf("-----BEGIN", StringComparison.Ordinal);
        if (begin < 0) throw new CryptographicException("找不到 PEM 區塊(-----BEGIN …)");
        return text[begin..];
    }

    public static string WithMeta(string pem, IEnumerable<KeyValuePair<string, string?>> meta, string? title = null)
    {
        var sb = new StringBuilder();
        if (title is not null) sb.Append("# ").Append(title).Append('\n');
        foreach (var (k, v) in meta)
            if (!string.IsNullOrWhiteSpace(v)) sb.Append("# ").Append(k).Append(": ").Append(v!.Replace('\n', ' ')).Append('\n');
        sb.Append(pem.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// <summary>匯入公鑰 PEM(PUBLIC KEY);亦接受私鑰 PEM 並只取公鑰部分。</summary>
    public static ECDsa ImportPublic(string text, string what)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(PemBlock(text));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new CryptographicException($"{what} 不是有效的 EC 公鑰 PEM:{ex.Message}", ex);
        }
        EnsureP256(key, what);
        // 只保留公鑰,避免誤把私鑰帶進信任清單
        var pub = ECDsa.Create(key.ExportParameters(false));
        key.Dispose();
        return pub;
    }

    /// <summary>產生新的 P-256 金鑰對;回傳(私鑰 PEM 文字、公鑰 PEM 文字、金鑰 ID)。<paramref name="password"/> 非空時私鑰以 PBES2/AES-256 加密。</summary>
    public static (string PrivatePem, string PublicPem, string KeyId) Generate(IDictionary<string, string?> meta, string? password = null)
    {
        using var key = CreateP256();
        var keyId = KeyId(key);
        var allMeta = new List<KeyValuePair<string, string?>>(meta) { new("keyId", keyId) };
        string priv;
        if (string.IsNullOrEmpty(password))
            priv = PemEncoding.WriteString("PRIVATE KEY", key.ExportPkcs8PrivateKey());
        else
            priv = PemEncoding.WriteString("ENCRYPTED PRIVATE KEY", key.ExportEncryptedPkcs8PrivateKey(password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000)));
        return (WithMeta(priv, allMeta, "Simosa BRM 簽章私鑰(開發/測試用 PEM;正式金鑰存於硬體金鑰,不得複製或提交儲存庫)"),
                WithMeta(PublicPem(key), allMeta, "Simosa BRM 信任公鑰"),
                keyId);
    }
}
