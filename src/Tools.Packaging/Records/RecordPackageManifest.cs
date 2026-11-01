using System.Text.Json;

namespace SimosaBRM.Packaging.Records;

/// <summary>
/// 紀錄包 manifest.json(.sbrec = zip:manifest.json + manifest.sig + records/&lt;相對路徑&gt;;以裝置金鑰簽章)。
/// 內容:船、筆電 ID、練習清單、每檔 SHA-256、訓練時數彙總(規劃書第 5.5 節「休息時間相容」:每位參與者的練習起訖時間)、
/// 練習錄影(records/media/*.webm 與旁檔 *.json;docs/design/screen-recording.md 第 4 節:超過打包上限者只放旁檔並標 omitted)。
/// </summary>
public sealed class RecordPackageManifest
{
    public const string FormatId = "simosa-records/1";
    public const string FilePrefix = "records/";

    public string Format { get; set; } = FormatId;
    public string PackageId { get; set; } = "";
    public string ShipId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>打包時的訪船序號(船上帳冊)</summary>
    public int VisitSeq { get; set; }
    public List<RecordFileEntry> Files { get; set; } = new();
    public List<ExerciseSummary> Exercises { get; set; } = new();
    public TrainingHours TrainingHours { get; set; } = new();
    /// <summary>打包時的船上版次(參數集等;教官端兩船版次清單)</summary>
    public string? InstalledVersion { get; set; }
    public string? ParameterSetVersion { get; set; }
    /// <summary>本包的全部錄影(含未對應到本包任何練習者);對應到練習的同時列在 exercises[].media</summary>
    public List<MediaSummary> Media { get; set; } = new();
    public DeviceKeyRef Device { get; set; } = new();
}

public sealed class RecordFileEntry
{
    /// <summary>包內路徑(records/…;相對船上紀錄目錄)</summary>
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    /// <summary>record(*.jsonl 練習紀錄)、report(報告等其他檔)、media(錄影 media/*.webm)或 media-meta(錄影旁檔 media/*.json)</summary>
    public string Kind { get; set; } = "record";
}

public sealed class ExerciseSummary
{
    public string File { get; set; } = "";
    public string? ScenarioId { get; set; }
    public string? ScenarioName { get; set; }
    public string? ShipId { get; set; }
    public string? Loading { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    /// <summary>牆鐘時長(秒;結束 − 開始)</summary>
    public double? DurationS { get; set; }
    /// <summary>模擬時間長度(footer.finalTick × dt;倍速或凍結時與牆鐘時長不同)</summary>
    public double? SimDurationS { get; set; }
    /// <summary>結束時間為估計值(紀錄沒有 footer.closedUtc,以檔案最後修改時間或模擬時長推算)</summary>
    public bool EndEstimated { get; set; }
    public List<Participant> Participants { get; set; } = new();
    /// <summary>footer 存在且 sha256 與內容一致</summary>
    public bool IntegrityOk { get; set; }
    public bool HasSummary { get; set; }
    public string? ParameterSetVersion { get; set; }
    /// <summary>練習前休息時間核對確認 {confirmedBy, confirmedAtUtc, compliant, note?}(紀錄標頭或最後一行 session 有此欄時原樣帶出)</summary>
    public JsonElement? RestHoursConfirmation { get; set; }
    /// <summary>課程資訊 {id?, title?, instructor?}(同上;選填)</summary>
    public JsonElement? Course { get; set; }
    /// <summary>participants / restHoursConfirmation 的來源:header(紀錄標頭)| session(紀錄開啟後的 session 行)| null(無)</summary>
    public string? SessionInfoSource { get; set; }
    /// <summary>對應本練習的錄影(旁檔 record.file 為本紀錄檔名,或 record.createdUtc 與 header.createdUtc 相同);無則空</summary>
    public List<MediaSummary> Media { get; set; } = new();
}

/// <summary>紀錄包內的一段練習錄影(來自旁檔 &lt;mediaId&gt;.json;docs/design/screen-recording.md)。</summary>
public sealed class MediaSummary
{
    public string MediaId { get; set; } = "";
    /// <summary>錄影檔包內路徑(records/media/&lt;mediaId&gt;.webm);omitted 時檔案不在包內,路徑仍指船上原檔</summary>
    public string File { get; set; } = "";
    /// <summary>旁檔包內路徑(records/media/&lt;mediaId&gt;.json)</summary>
    public string Sidecar { get; set; } = "";
    public string? Station { get; set; }
    public string? Mime { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public double? DurationS { get; set; }
    /// <summary>長度為牆鐘估計(教官站未回報 MediaRecorder 長度)</summary>
    public bool DurationEstimated { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>上傳時超過核心的 --media-max-mb,檔案只含上限內的片段</summary>
    public bool Truncated { get; set; }
    /// <summary>超過紀錄包的錄影上限(--media-max-mb / packaging.json mediaMaxMb)或檔案不在:只放旁檔,錄影留在船上</summary>
    public bool Omitted { get; set; }
    public string? OmitReason { get; set; }
    /// <summary>旁檔記的紀錄檔名(對應 exercises[].file 的檔名部分)</summary>
    public string? Record { get; set; }
    public DateTime? RecordCreatedUtc { get; set; }
    /// <summary>旁檔記的 scenario.id</summary>
    public string? ScenarioId { get; set; }
    /// <summary>旁檔格式錯誤等問題(仍打包旁檔供事後檢視)</summary>
    public string? Error { get; set; }
}

public sealed class Participant
{
    public string Name { get; set; } = "";
    /// <summary>master | oow | pilot | helmsman | lookout | observer | instructor(舊紀錄可能為任意字串)</summary>
    public string? Role { get; set; }
    /// <summary>職級(選填)</summary>
    public string? Rank { get; set; }
    /// <summary>證書號(選填;教官端證書效期追蹤)</summary>
    public string? CertificateNo { get; set; }
}

public sealed class TrainingHours
{
    public int ExerciseCount { get; set; }
    /// <summary>牆鐘總時長(秒)</summary>
    public double TotalS { get; set; }
    public double TotalHours => Math.Round(TotalS / 3600.0, 2);
    /// <summary>模擬時間總長(秒;倍速/凍結時與 totalS 不同,僅供參考)</summary>
    public double SimTotalS { get; set; }
    /// <summary>有練習的結束時間為估計值(見 <see cref="ExerciseSummary.EndEstimated"/>)</summary>
    public bool Estimated { get; set; }
    public List<ParticipantHours> Participants { get; set; } = new();
}

public sealed class ParticipantHours
{
    public string Name { get; set; } = "";
    public string? Role { get; set; }
    /// <summary>職級(最近一次練習填寫者;選填)</summary>
    public string? Rank { get; set; }
    /// <summary>證書號(同上;選填)</summary>
    public string? CertificateNo { get; set; }
    public int Exercises { get; set; }
    public double TotalS { get; set; }
    public double TotalHours => Math.Round(TotalS / 3600.0, 2);
    /// <summary>每次練習的起訖(UTC;供轉錄至船上工時/休息時間紀錄)</summary>
    public List<Session> Sessions { get; set; } = new();
}

public sealed class Session
{
    public string? ScenarioId { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    /// <summary>牆鐘時長(秒)</summary>
    public double? DurationS { get; set; }
    public double? SimDurationS { get; set; }
    public bool EndEstimated { get; set; }
}

public sealed class DeviceKeyRef
{
    public string KeyId { get; set; } = "";
    public string PublicKey { get; set; } = "";
}
