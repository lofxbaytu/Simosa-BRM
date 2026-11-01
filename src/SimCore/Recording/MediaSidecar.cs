using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimosaBRM.SimCore.Recording;

/// <summary>
/// 練習錄影旁檔 <c>&lt;mediaId&gt;.json</c>(docs/design/screen-recording.md 第 3 節):教官站以瀏覽器 getDisplayMedia + MediaRecorder 錄下畫面,
/// 片段即時上傳到船上核心的 MediaUploadServer,寫成練習紀錄旁的 <c>&lt;mediaId&gt;.webm</c>;停止時寫本旁檔。
/// 紀錄檔本身(header/session/footer 行)不改;紀錄包打包時 Tools.Packaging 的 RecordInspector 依 <see cref="Record"/>
/// 的檔名與 header.createdUtc 把錄影關聯到練習。SimCore.Host 寫、Tools.Packaging 讀,兩邊共用本型別。
/// </summary>
public sealed class MediaSidecar
{
    public const string FormatId = "simosa-media/1";

    public string Format { get; set; } = FormatId;
    public string MediaId { get; set; } = "";
    /// <summary>錄影檔名(與旁檔同目錄;<c>&lt;mediaId&gt;.webm</c>)</summary>
    public string File { get; set; } = "";
    /// <summary>教官站給的練習識別(教官站紀錄 startedUtc 或情境 id;只作關聯提示)</summary>
    public string? ExerciseId { get; set; }
    /// <summary>錄影來源站別(instructor | instructor-simple | …)</summary>
    public string Station { get; set; } = "";
    /// <summary>MediaRecorder 的 MIME(例如 video/webm;codecs=vp9,opus)</summary>
    public string Mime { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? StoppedUtc { get; set; }
    /// <summary>錄影長度(秒):教官站 MediaRecorder 量到的長度;未提供時取牆鐘(stoppedUtc − startedUtc)</summary>
    public double? DurationS { get; set; }
    /// <summary>長度為牆鐘估計值(教官站未提供長度)</summary>
    public bool DurationEstimated { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>收到的片段數(X-Seq 連續 0…n−1)</summary>
    public int Chunks { get; set; }
    /// <summary>錄影檔內容的 SHA-256(十六進位大寫)</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>超過上傳上限(--media-max-mb)時停止接收,檔案只含上限內的片段</summary>
    public bool Truncated { get; set; }
    /// <summary>上傳上限(位元組;--media-max-mb)</summary>
    public long MaxBytes { get; set; }
    /// <summary>停止原因:client(教官站 stop)| hostShutdown(主控程序結束時尚未 stop)| limit(超過上限後教官站 stop)</summary>
    public string? StopReason { get; set; }
    /// <summary>對應的練習紀錄(開始錄影時主控程序正在寫的紀錄;無紀錄時 null)</summary>
    public MediaRecordRef? Record { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MediaSidecar Parse(string json)
        => JsonSerializer.Deserialize<MediaSidecar>(json, JsonOptions) ?? throw new InvalidDataException("錄影旁檔為空");

    /// <summary>讀旁檔;格式不符時丟 <see cref="InvalidDataException"/>。</summary>
    public static MediaSidecar Load(string path)
    {
        MediaSidecar s;
        try { s = Parse(System.IO.File.ReadAllText(path)); }
        catch (JsonException ex) { throw new InvalidDataException($"錄影旁檔 JSON 無法解析:{path}:{ex.Message}"); }
        if (s.Format != FormatId) throw new InvalidDataException($"錄影旁檔格式不支援:{s.Format}({path})");
        return s;
    }

    /// <summary>原子寫入(先寫 .tmp 再改名)。</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, ToJson());
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>旁檔中對應練習紀錄的引用:檔名與 header.createdUtc(RecordInspector 以此關聯 manifest.exercises[])。</summary>
public sealed class MediaRecordRef
{
    /// <summary>紀錄檔名(不含目錄;例如 20261012-013000-E01_baseline.jsonl)</summary>
    public string File { get; set; } = "";
    /// <summary>紀錄檔相對專案根目錄的路徑(診斷用)</summary>
    public string? Path { get; set; }
    /// <summary>紀錄 header.createdUtc(讀不到時 null)</summary>
    public DateTime? CreatedUtc { get; set; }
    /// <summary>header.scenario.id(讀得到時)</summary>
    public string? ScenarioId { get; set; }
}
