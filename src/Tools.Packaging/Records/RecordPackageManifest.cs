using System.Text.Json;

namespace SimosaBRM.Packaging.Records;

/// <summary>
/// 紀錄包 manifest.json(.sbrec = zip:manifest.json + manifest.sig + records/&lt;相對路徑&gt;;以裝置金鑰簽章)。
/// 內容:船、筆電 ID、練習清單、每檔 SHA-256、訓練時數彙總(規劃書第 5.5 節「休息時間相容」:每位參與者的練習起訖時間)。
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
    public DeviceKeyRef Device { get; set; } = new();
}

public sealed class RecordFileEntry
{
    /// <summary>包內路徑(records/…;相對船上紀錄目錄)</summary>
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    /// <summary>record(*.jsonl 練習紀錄)或 report(報告等其他檔)</summary>
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
