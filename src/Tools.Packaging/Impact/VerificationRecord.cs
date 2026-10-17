using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Impact;

/// <summary>
/// 練習設計/測試紀錄中的「受影響情境重新驗證」紀錄(規劃書第 9.1 節:訓練負責人就受影響的情境至少執行 1 次或以既有練習紀錄
/// 重播評分,確認結果並記入練習設計/測試紀錄(R12)後,方可產生簽章更新包)。由 <c>simosa-pkg verification add</c> 維護。
/// <code>
/// { "format": "simosa-verification/1",
///   "entries": [ { "scenarioId": "E01_baseline", "scenarioSha256": "…", "parameterSetId": "simosa-company-procedures",
///                  "parameterSetVersion": "2026.10.2", "parameterSetSha256": "…", "method": "run",
///                  "recordRef": "build/records/20261017-…-E01_baseline.jsonl", "recordSha256": "…",
///                  "result": "pass", "verifiedBy": "訪船船長 王○○", "verifiedUtc": "2026-10-17T02:00:00Z", "notes": "…" } ] }
/// </code>
/// </summary>
public sealed class VerificationRecord
{
    public const string FormatId = "simosa-verification/1";
    public static readonly string[] Methods = { "run", "replay" };

    public string Format { get; set; } = FormatId;
    public List<VerificationEntry> Entries { get; set; } = new();

    public static VerificationRecord LoadOrNew(string path)
        => File.Exists(path) ? PackageJson.Load<VerificationRecord>(path, "驗證紀錄") : new VerificationRecord();

    public void Save(string path) => PackageJson.SaveAtomic(path, this);

    /// <summary>找出與情境內容、參數集內容都相符的最新驗證(情境或參數集任何一方再變更都須重新驗證)。</summary>
    public VerificationEntry? Find(string scenarioId, string scenarioSha256, string? parameterSetSha256)
        => Entries.Where(e => e.ScenarioId == scenarioId && Sha256Hex.Equal(e.ScenarioSha256, scenarioSha256)
                              && (parameterSetSha256 is null || Sha256Hex.Equal(e.ParameterSetSha256, parameterSetSha256))
                              && e.Result is "pass" or "acceptable")
                  .OrderByDescending(e => e.VerifiedUtc).FirstOrDefault();
}

public sealed class VerificationEntry
{
    public string ScenarioId { get; set; } = "";
    public string ScenarioSha256 { get; set; } = "";
    public string? ParameterSetId { get; set; }
    public string? ParameterSetVersion { get; set; }
    public string? ParameterSetSha256 { get; set; }
    /// <summary>run(以新參數集實際執行)或 replay(以既有練習紀錄重播評分)</summary>
    public string Method { get; set; } = "run";
    public string? RecordRef { get; set; }
    public string? RecordSha256 { get; set; }
    /// <summary>pass/acceptable 才算通過;fail 表示驗證未通過(保留紀錄但不放行)</summary>
    public string Result { get; set; } = "pass";
    public string VerifiedBy { get; set; } = "";
    public DateTime VerifiedUtc { get; set; }
    public string? Notes { get; set; }
}
