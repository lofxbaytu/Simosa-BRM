using SimosaBRM.Packaging.Impact;

namespace SimosaBRM.Packaging.Updates;

/// <summary>更新包種類(規劃書第 5.5 節:軟體、情境庫、圖資、SMS 訓練程序與公司程序參數;trust 為更換信任公鑰)。</summary>
public static class UpdateKinds
{
    public const string Software = "software";
    public const string Scenarios = "scenarios";
    public const string Parameters = "parameters";
    public const string Charts = "charts";
    public const string Trust = "trust";
    public const string Mixed = "mixed";

    public static readonly string[] All = { Software, Scenarios, Parameters, Charts, Trust, Mixed };

    /// <summary>單一種類更新包允許的路徑前綴(mixed 允許 app/、data/,但不含信任清單)。</summary>
    public static string[] AllowedPrefixes(string kind) => kind switch
    {
        Software => new[] { "app/" },
        Scenarios => new[] { "data/scenarios/" },
        Parameters => new[] { "data/parameters/" },
        Charts => new[] { "data/charts/" },
        Trust => new[] { Common.PackagePaths.TrustedKeysPrefix },
        Mixed => new[] { "app/", "data/" },
        _ => Array.Empty<string>(),
    };
}

/// <summary>
/// 更新包 manifest.json(.sbupd = zip:manifest.json + manifest.sig + payload/&lt;安裝路徑&gt;)。
/// 簽章對 manifest.json 的原始位元組計算;manifest 列出每個檔案的 SHA-256,因此簽章涵蓋全部內容。
/// </summary>
public sealed class UpdateManifest
{
    public const string FormatId = "simosa-update/1";
    public const string ManifestEntry = "manifest.json";
    public const string SignatureEntry = "manifest.sig";
    public const string PayloadPrefix = "payload/";

    /// <summary>packageId 白名單:英數字開頭,只含英數字 . _ -,最多 128 字元(用於 state/staging/&lt;packageId&gt; 等目錄名,不得為 ".." 之類)。</summary>
    public static bool IsValidPackageId(string? id)
        => id is { Length: >= 1 and <= 128 } && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public string Format { get; set; } = FormatId;
    public string PackageId { get; set; } = "";
    /// <summary>單調遞增(1–4 段數字);船上拒絕不大於曾套用最高版本者</summary>
    public string Version { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Kind { get; set; } = UpdateKinds.Mixed;
    /// <summary>變更摘要(一句話)</summary>
    public string Summary { get; set; } = "";
    /// <summary>變更明細</summary>
    public List<string> Changes { get; set; } = new();
    /// <summary>所依據的 SMS 文件(編號、版次、核准人)</summary>
    public List<SmsDocumentRef> SmsDocuments { get; set; } = new();
    /// <summary>限定套用的船舶 ID(空:兩船皆可)</summary>
    public List<string> Ships { get; set; } = new();
    public List<PackageFile> Files { get; set; } = new();
    /// <summary>套用時刪除的安裝路徑(trust 類由 trust.remove 推得)</summary>
    public List<string> Remove { get; set; } = new();
    /// <summary>本包所含參數集的版次(未含參數集時 null)</summary>
    public ParameterSetRef? ParameterSet { get; set; }
    /// <summary>受影響情境清單與驗證紀錄參照(第 9.1 節)</summary>
    public List<AffectedScenario> AffectedScenarios { get; set; } = new();
    public ValidationInfo Validation { get; set; } = new();
    public TrustChange? Trust { get; set; }
    public SignerRef Signer { get; set; } = new();
    /// <summary>產生者(教官筆電主機名稱與工具版本)</summary>
    public string? CreatedBy { get; set; }
}

public sealed class SmsDocumentRef
{
    public string DocumentId { get; set; } = "";
    public string Revision { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public string? ApprovedOn { get; set; }
    public string? Title { get; set; }
}

public sealed class PackageFile
{
    /// <summary>安裝路徑(相對安裝根目錄,'/' 分隔,例如 data/parameters/company-procedures.yaml)</summary>
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class ParameterSetRef
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>引用的 SMS 程序文件與版次(sources 摘要)</summary>
    public Dictionary<string, string?> SourceRevisions { get; set; } = new();
}

public sealed class AffectedScenario
{
    public string ScenarioId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public List<string> Reasons { get; set; } = new();
    public List<string> ChangedKeys { get; set; } = new();
    public List<string> AssessmentRules { get; set; } = new();
    public int Errors { get; set; }
    public int Warnings { get; set; }
    /// <summary>驗證紀錄(未驗證時 null)</summary>
    public VerificationEntry? Verification { get; set; }
}

public static class ValidationStatuses
{
    /// <summary>所有受影響情境皆無錯誤且有相符的驗證紀錄</summary>
    public const string Verified = "verified";
    /// <summary>未含參數集或情境,不需情境驗證</summary>
    public const string NotRequired = "notRequired";
    /// <summary>以 --force 產生(未驗證或有錯誤);船上預設拒絕套用</summary>
    public const string Forced = "forced";
}

public sealed class ValidationInfo
{
    public string Status { get; set; } = ValidationStatuses.NotRequired;
    public DateTime CheckedUtc { get; set; }
    public string? BaselineParameterSetVersion { get; set; }
    public List<string> ChangedParameterKeys { get; set; } = new();
    /// <summary>驗證紀錄檔(檔名與 SHA-256;內容另存於教官筆電練習設計/測試紀錄)</summary>
    public string? VerificationRecord { get; set; }
    public string? VerificationRecordSha256 { get; set; }
    public string? ForcedReason { get; set; }
    public List<string> Problems { get; set; } = new();
}

/// <summary>信任清單變更(kind = trust;須以備援金鑰簽章)。</summary>
public sealed class TrustChange
{
    public List<TrustKeyEntry> Add { get; set; } = new();
    /// <summary>移除的金鑰 ID</summary>
    public List<string> Remove { get; set; } = new();
    public string? Reason { get; set; }
}

public sealed class TrustKeyEntry
{
    public string KeyId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string? Holder { get; set; }
    /// <summary>安裝路徑(config/trusted-keys/xxx.pem)</summary>
    public string Path { get; set; } = "";
}

public sealed class SignerRef
{
    public string KeyId { get; set; } = "";
    public string Name { get; set; } = "";
}
