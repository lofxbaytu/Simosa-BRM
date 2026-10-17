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
    public double? DurationS { get; set; }
    public List<Participant> Participants { get; set; } = new();
    /// <summary>footer 存在且 sha256 與內容一致</summary>
    public bool IntegrityOk { get; set; }
    public bool HasSummary { get; set; }
    public string? ParameterSetVersion { get; set; }
    /// <summary>練習前休息時間核對確認(紀錄標頭有此欄時原樣帶出)</summary>
    public JsonElement? RestHoursConfirmation { get; set; }
}

public sealed class Participant
{
    public string Name { get; set; } = "";
    public string? Role { get; set; }
}

public sealed class TrainingHours
{
    public int ExerciseCount { get; set; }
    public double TotalS { get; set; }
    public double TotalHours => Math.Round(TotalS / 3600.0, 2);
    public List<ParticipantHours> Participants { get; set; } = new();
}

public sealed class ParticipantHours
{
    public string Name { get; set; } = "";
    public string? Role { get; set; }
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
    public double? DurationS { get; set; }
}

public sealed class DeviceKeyRef
{
    public string KeyId { get; set; } = "";
    public string PublicKey { get; set; } = "";
}
