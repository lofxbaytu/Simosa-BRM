namespace SimosaBRM.Packaging.Records;

/// <summary>
/// 已備份回報(.sbbak = SignedDocument;以教官金鑰(信任清單中的主/備援金鑰)簽章)。船上收到並驗證後,
/// 才刪除所列紀錄包與其原始紀錄檔(以紀錄包雜湊比對;規劃書第 5.5 節)。
/// </summary>
public sealed class BackupReport
{
    public const string FormatId = "simosa-backup-report/1";

    public string Format { get; set; } = FormatId;
    public string ReportId { get; set; } = "";
    public string ShipId { get; set; } = "";
    public DateTime IssuedUtc { get; set; }
    /// <summary>教官筆電紀錄庫識別(主機名稱)</summary>
    public string? Store { get; set; }
    public List<BackedUpPackage> Packages { get; set; } = new();
    public string? SignerKeyId { get; set; }
    public string? SignerName { get; set; }
}

public sealed class BackedUpPackage
{
    public string PackageId { get; set; } = "";
    /// <summary>.sbrec 檔案的 SHA-256</summary>
    public string Sha256 { get; set; } = "";
    public DateTime? ImportedUtc { get; set; }
    public DateTime? BackedUpUtc { get; set; }
    /// <summary>備份目的地(加密外接碟/公司檔案共享的標示)</summary>
    public string? BackupTarget { get; set; }
}

/// <summary>船上處理回報的結果(同步訊息 cleanupResult)。</summary>
public sealed class CleanupResult
{
    public bool Ok { get; set; }
    public string? ReportId { get; set; }
    public string? Signer { get; set; }
    /// <summary>已刪除副本的紀錄包</summary>
    public List<string> Deleted { get; set; } = new();
    /// <summary>已刪除的原始紀錄檔(相對紀錄目錄)</summary>
    public List<string> DeletedFiles { get; set; } = new();
    /// <summary>未列入回報、繼續保留的紀錄包</summary>
    public List<string> Retained { get; set; } = new();
    /// <summary>雜湊不符(不刪除)</summary>
    public List<string> Mismatched { get; set; } = new();
    /// <summary>船上沒有的紀錄包 ID</summary>
    public List<string> Unknown { get; set; } = new();
    /// <summary>無法刪除的檔案(「路徑:原因」;該紀錄包保留,下次回報時重試)</summary>
    public List<string> Failed { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 船上清理通知(隨身碟途徑;.sbcln = SignedDocument,以船上裝置金鑰簽章):船上處理已備份回報後寫到
/// from-ship/&lt;ship&gt;/,教官端匯入時以紀錄庫登錄的裝置公鑰驗證,標記紀錄包「船上已刪除」(直連同步則由 cleanupResult 回報)。
/// </summary>
public sealed class CleanupNotice
{
    public const string FormatId = "simosa-cleanup/1";
    public const string Extension = ".sbcln";

    public string Format { get; set; } = FormatId;
    public string ShipId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    /// <summary>所處理的已備份回報 ID</summary>
    public string ReportId { get; set; } = "";
    /// <summary>船上已刪除副本的紀錄包</summary>
    public List<string> Deleted { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
}
