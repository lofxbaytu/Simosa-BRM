using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 更新包、紀錄包、回報與同步訊息共用的 JSON 設定:camelCase、略過 null、中文不跳脫(稽核與人工檢視可讀)。
/// 簽章一律對「檔案中實際的位元組」計算,不做正規化,因此序列化設定改變不影響既有簽章的驗證。
/// </summary>
public static class PackageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>單行(JSONL 稽核紀錄、同步訊息)。</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static byte[] ToBytes<T>(T value, bool indented = true)
        => JsonSerializer.SerializeToUtf8Bytes(value, indented ? Options : Compact);

    public static string ToText<T>(T value, bool indented = true)
        => JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T FromBytes<T>(ReadOnlySpan<byte> bytes, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException($"{what} 為空");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{what} 不是有效的 JSON:{ex.Message}", ex);
        }
    }

    public static T FromText<T>(string text, string what) => FromBytes<T>(Encoding.UTF8.GetBytes(text), what);

    public static T Load<T>(string path, string what) => FromBytes<T>(File.ReadAllBytes(path), $"{what}({path})");

    /// <summary>先寫暫存檔再取代,避免中途斷電留下半個檔案。</summary>
    public static void SaveAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ToBytes(value));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>SHA-256(小寫 hex)。</summary>
public static class Sha256Hex
{
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string OfFile(string path)
    {
        using var s = File.OpenRead(path);
        return OfStream(s);
    }

    public static string OfStream(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

    public static bool Equal(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
