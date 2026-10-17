using System.Security.Cryptography;
using System.Text;
using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Crypto;

/// <summary>
/// 分離式簽章(manifest.sig、回報檔):對被簽文件「實際的位元組」以 ES256 簽章。
/// <code>
/// { "format": "simosa-sig/1", "alg": "ES256", "keyId": "…64 hex…", "signer": "訪船船長 王○○",
///   "signedUtc": "2026-10-17T03:00:00Z", "publicKey": "-----BEGIN PUBLIC KEY-----…", "signature": "base64(r‖s)" }
/// </code>
/// 驗證時以 keyId 在信任清單中找公鑰;<c>publicKey</c> 與 <c>signer</c> 只供顯示與首次登錄裝置金鑰,不作為信任依據。
/// </summary>
public sealed record SignatureEnvelope
{
    public const string FormatId = "simosa-sig/1";
    public const string Algorithm = "ES256";

    public string Format { get; init; } = FormatId;
    public string Alg { get; init; } = Algorithm;
    public required string KeyId { get; init; }
    public string? Signer { get; init; }
    public DateTime SignedUtc { get; init; }
    public string? PublicKey { get; init; }
    public required string Signature { get; init; }

    public static SignatureEnvelope Create(IKeyProvider key, byte[] data, DateTime utc) => new()
    {
        KeyId = key.KeyId,
        Signer = key.DisplayName,
        SignedUtc = utc,
        PublicKey = key.PublicKeyPem,
        Signature = Convert.ToBase64String(key.Sign(data)),
    };

    /// <summary>格式與演算法檢查;回傳錯誤訊息或 null。</summary>
    public string? FormatError()
    {
        if (Format != FormatId) return $"簽章格式不支援:{Format}";
        if (Alg != Algorithm) return $"簽章演算法不支援:{Alg}(只接受 ES256)";
        if (string.IsNullOrWhiteSpace(KeyId) || KeyId.Length != 64) return "簽章缺少 keyId";
        return null;
    }

    public bool VerifyWith(ECDsa publicKey, byte[] data)
    {
        if (FormatError() is not null) return false;
        byte[] sig;
        try { sig = Convert.FromBase64String(Signature); }
        catch (FormatException) { return false; }
        if (sig.Length != 64) return false;
        if (!string.Equals(KeyFiles.KeyId(publicKey), KeyId, StringComparison.OrdinalIgnoreCase)) return false;
        return publicKey.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}

/// <summary>
/// 自含簽章文件(備份回報 .sbbak 等小型文件):<c>payload</c> 為被簽 JSON 的原文字串,簽章對其 UTF-8 位元組計算。
/// <code>{ "format": "simosa-signed/1", "payload": "{…JSON 原文…}", "signature": { …SignatureEnvelope… } }</code>
/// </summary>
public sealed record SignedDocument
{
    public const string FormatId = "simosa-signed/1";

    public string Format { get; init; } = FormatId;
    public required string Payload { get; init; }
    public required SignatureEnvelope Signature { get; init; }

    public static SignedDocument Create<T>(T payload, IKeyProvider key, DateTime utc)
    {
        var text = PackageJson.ToText(payload);
        return new SignedDocument { Payload = text, Signature = SignatureEnvelope.Create(key, Encoding.UTF8.GetBytes(text), utc) };
    }

    public byte[] PayloadBytes => Encoding.UTF8.GetBytes(Payload);

    public T Read<T>(string what) => PackageJson.FromText<T>(Payload, what);

    public byte[] ToBytes() => PackageJson.ToBytes(this);

    public static SignedDocument Parse(byte[] bytes, string what)
    {
        var doc = PackageJson.FromBytes<SignedDocument>(bytes, what);
        if (doc.Format != FormatId) throw new InvalidDataException($"{what} 格式不支援:{doc.Format}");
        return doc;
    }
}
