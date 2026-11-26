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

    /// <summary>
    /// 讀本機的設定/狀態檔。開頭的 UTF-8 BOM 略過:人工以「UTF-8 with BOM」存檔,或 Windows PowerShell 5.1 的
    /// <c>Set-Content -Encoding UTF8</c> 都會加 BOM(System.Text.Json 不接受)。簽章文件與更新包內容走 <see cref="FromBytes{T}"/>,不放寬。
    /// </summary>
    public static T Load<T>(string path, string what)
    {
        ReadOnlySpan<byte> bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        return FromBytes<T>(bytes, $"{what}({path})");
    }

    /// <summary>
    /// 先寫暫存檔再取代,避免中途斷電留下半個檔案;讀取端(不取鎖的 status、store list…)看到的不是舊的完整內容、就是新的完整內容。
    /// <para>
    /// 暫存檔名每次不同(<c>&lt;檔&gt;.&lt;8 位十六進位&gt;.tmp</c>)。原本是固定的 <c>&lt;檔&gt;.tmp</c>:兩個行程同時寫同一個檔時寫進同一個暫存檔 —
    /// 實測其中一個以「Could not find file '….tmp'」失敗(先改名的那一個把暫存檔帶走了),也出現過兩份內容交錯、之後讀不進來的檔。
    /// 現在各寫各的暫存檔,最後改名的那一份完整地留下。這只保證檔案完整;「讀 → 改 → 寫」之間不被別的行程插隊要靠 <see cref="StateLock"/>。
    /// 失敗時刪掉自己的暫存檔(行程在寫到一半時被強制結束才會留下一個 .tmp,可以直接刪除)。
    /// </para>
    /// </summary>
    public static void SaveAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        try
        {
            File.WriteAllBytes(tmp, ToBytes(value));
            MoveOver(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// 以暫存檔取代目的檔。Linux 的改名是原子的,不受讀取端影響,失敗就是失敗。Windows 上目的檔正被別的行程開著的瞬間
    /// (另一個視窗的 status 正在讀、防毒或索引程式正在掃描、另一個寫入者剛換上去)取代會得到「存取被拒」或共用違規:
    /// 短暫重試(最多約 2 秒)再當成真的寫不進去。Windows 的這一段只有程式寫法與單元測試,沒有在實機觀察過(docs/design/packaging-sync.md 第 5.2 節)。
    /// </summary>
    private static void MoveOver(string tmp, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(tmp, path, overwrite: true);
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && clock.ElapsedMilliseconds < 2000 && File.Exists(tmp) && !Directory.Exists(path))
            {
                Thread.Sleep(15 + Random.Shared.Next(35));
            }
        }
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
