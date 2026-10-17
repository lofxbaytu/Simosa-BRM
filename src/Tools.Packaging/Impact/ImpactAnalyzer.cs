using System.Text.Json;
using System.Text.RegularExpressions;
using SimosaBRM.Packaging.Common;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Packaging.Impact;

/// <summary>參數變更種類。</summary>
public static class ParameterChangeKinds
{
    public const string Added = "added";
    public const string Removed = "removed";
    public const string Changed = "changed";
    /// <summary>值未變但來源程序文件版次變更(講評報告顯示的來源版次會不同)</summary>
    public const string SourceRevision = "sourceRevision";
    /// <summary>無舊版參數集可比對,視為全部變更</summary>
    public const string NoBaseline = "noBaseline";
}

public sealed record ParameterChange
{
    public required string Key { get; init; }
    public required string Change { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string? Unit { get; init; }
    public string? Source { get; init; }
    public string? OldRevision { get; init; }
    public string? NewRevision { get; init; }
}

public sealed record IssueInfo(string Severity, string Path, string Message);

/// <summary>一個受影響情境的分析結果。</summary>
public sealed record ScenarioImpact
{
    public required string ScenarioId { get; init; }
    /// <summary>相對驗證根目錄的路徑(例如 data/scenarios/E01_baseline.yaml)</summary>
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    /// <summary>受影響原因(引用變更參數、情境檔本身變更、無法載入)</summary>
    public List<string> Reasons { get; init; } = new();
    /// <summary>引用到的變更參數鍵</summary>
    public List<string> ChangedKeys { get; init; } = new();
    /// <summary>引用變更參數的欄位(情境欄位路徑 → 鍵)</summary>
    public List<string> Fields { get; init; } = new();
    /// <summary>引用變更參數的評估規則(assessment.* 欄位;含規則 id)</summary>
    public List<string> AssessmentRules { get; init; } = new();
    public List<IssueInfo> Issues { get; init; } = new();
    public string? LoadError { get; init; }
    public bool HasErrors => LoadError is not null || Issues.Any(i => i.Severity == "error");
}

public sealed record ParameterSetSummary(string Id, string Version, string Sha256, string? Path);

/// <summary>影響分析報告(<c>simosa-pkg impact</c> 輸出;更新包 manifest 的 affectedScenarios 來源)。</summary>
public sealed record ImpactReport
{
    public required ParameterSetSummary? NewParameterSet { get; init; }
    public ParameterSetSummary? BaselineParameterSet { get; init; }
    public List<ParameterChange> Changes { get; init; } = new();
    public List<ScenarioImpact> Affected { get; init; } = new();
    public List<string> Unaffected { get; init; } = new();
    public bool HasErrors => Affected.Any(a => a.HasErrors);
}

public sealed class ImpactRequest
{
    /// <summary>新參數集(null:沿用現行參數集,僅檢查變更的情境檔)</summary>
    public ParameterSet? NewSet { get; init; }
    /// <summary>比對用的舊參數集(null:無法比對,參數集中所有鍵視為變更)</summary>
    public ParameterSet? Baseline { get; init; }
    /// <summary>要檢查的情境檔(完整路徑)</summary>
    public required IReadOnlyList<string> ScenarioFiles { get; init; }
    /// <summary>本次變更的情境檔(完整路徑;一律列為受影響)</summary>
    public IReadOnlySet<string> ChangedScenarioFiles { get; init; } = new HashSet<string>();
    /// <summary>含 data/ships 的根目錄(船舶資料,供 UKC 換算)與 data/scenarios/&lt;id&gt;/ 附件</summary>
    public string? DataRoot { get; init; }
    /// <summary>相對路徑的基準(顯示用)</summary>
    public string? DisplayRoot { get; init; }
    /// <summary>完整路徑 → 顯示路徑(更新包內檔案以安裝路徑顯示)</summary>
    public IReadOnlyDictionary<string, string>? DisplayPaths { get; init; }
}

/// <summary>
/// 規劃書第 9.1 節「參數集修改後,系統在教官筆電列出引用該參數的情境與評估規則,並自動檢查」:
/// 比對新舊參數集 → 以新參數集載入每個情境(ScenarioLoader,參數引用可解析)→ 列出引用變更鍵的欄位與評估規則 →
/// ScenarioValidator 靜態檢查(初始即觸發/永不觸發等)。
/// </summary>
public static class ImpactAnalyzer
{
    private static readonly Regex RulePath = new(@"^assessment\.(hardFail|bands|required)\[(\d+)\]", RegexOptions.Compiled);

    public static List<ParameterChange> Diff(ParameterSet? baseline, ParameterSet current)
    {
        var changes = new List<ParameterChange>();
        if (baseline is null)
        {
            foreach (var (k, p) in current.Parameters.OrderBy(k => k.Key, StringComparer.Ordinal))
                changes.Add(new ParameterChange { Key = k, Change = ParameterChangeKinds.NoBaseline, NewValue = p.Value.GetRawText(), Unit = p.Unit, Source = p.Source, NewRevision = Rev(current, p.Source) });
            return changes;
        }
        foreach (var key in baseline.Parameters.Keys.Union(current.Parameters.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var had = baseline.Parameters.TryGetValue(key, out var o);
            var has = current.Parameters.TryGetValue(key, out var n);
            if (had && !has)
                changes.Add(new ParameterChange { Key = key, Change = ParameterChangeKinds.Removed, OldValue = o!.Value.GetRawText(), Unit = o.Unit, Source = o.Source, OldRevision = Rev(baseline, o.Source) });
            else if (!had && has)
                changes.Add(new ParameterChange { Key = key, Change = ParameterChangeKinds.Added, NewValue = n!.Value.GetRawText(), Unit = n.Unit, Source = n.Source, NewRevision = Rev(current, n.Source) });
            else if (had && has)
            {
                var valueChanged = !JsonValueEquals(o!.Value, n!.Value) || o.Unit != n.Unit || o.Source != n.Source || o.Clause != n.Clause || o.Min != n.Min || o.Max != n.Max;
                var oldRev = Rev(baseline, o.Source);
                var newRev = Rev(current, n.Source);
                if (valueChanged)
                    changes.Add(new ParameterChange { Key = key, Change = ParameterChangeKinds.Changed, OldValue = o.Value.GetRawText(), NewValue = n.Value.GetRawText(), Unit = n.Unit, Source = n.Source, OldRevision = oldRev, NewRevision = newRev });
                else if (oldRev != newRev)
                    changes.Add(new ParameterChange { Key = key, Change = ParameterChangeKinds.SourceRevision, OldValue = o.Value.GetRawText(), NewValue = n.Value.GetRawText(), Unit = n.Unit, Source = n.Source, OldRevision = oldRev, NewRevision = newRev });
            }
        }
        return changes;
    }

    private static string? Rev(ParameterSet set, string source) => set.Sources.TryGetValue(source, out var s) ? s.Revision : null;

    private static bool JsonValueEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number) return a.GetDouble() == b.GetDouble();
        return a.ValueKind == b.ValueKind && a.GetRawText() == b.GetRawText();
    }

    public static ImpactReport Analyze(ImpactRequest req)
    {
        var changes = req.NewSet is null ? new List<ParameterChange>() : Diff(req.Baseline, req.NewSet);
        var changedKeys = changes.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var removedKeys = changes.Where(c => c.Change == ParameterChangeKinds.Removed).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var affected = new List<ScenarioImpact>();
        var unaffected = new List<string>();
        var displayRoot = req.DisplayRoot ?? req.DataRoot;

        foreach (var file in req.ScenarioFiles.Distinct().OrderBy(f => f, StringComparer.Ordinal))
        {
            var full = System.IO.Path.GetFullPath(file);
            var display = req.DisplayPaths is { } dp && (dp.TryGetValue(full, out var mapped) || dp.TryGetValue(file, out mapped)) ? mapped
                : displayRoot is null ? full : PackagePaths.Relative(displayRoot, full);
            var sha = Sha256Hex.OfFile(full);
            var isChangedFile = req.ChangedScenarioFiles.Contains(full) || req.ChangedScenarioFiles.Contains(file);
            Scenario scenario;
            try
            {
                scenario = ScenarioLoader.Load(full, req.NewSet);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or YamlDotNet.Core.YamlException or KeyNotFoundException)
            {
                // 無法載入:可能引用了被移除的鍵,或情境檔本身有誤。只要引用了變更鍵或檔案在本次變更內就列為受影響
                var text = File.ReadAllText(full);
                var mentioned = changedKeys.Where(k => text.Contains("${params:" + k + "}", StringComparison.Ordinal)).ToList();
                if (isChangedFile || mentioned.Count > 0 || removedKeys.Any(k => text.Contains(k, StringComparison.Ordinal)))
                {
                    affected.Add(new ScenarioImpact
                    {
                        ScenarioId = System.IO.Path.GetFileNameWithoutExtension(full), Path = display, Sha256 = sha,
                        Reasons = Reasons(isChangedFile, mentioned.Count > 0, true), ChangedKeys = mentioned, LoadError = ex.Message,
                    });
                }
                else unaffected.Add(System.IO.Path.GetFileNameWithoutExtension(full));
                continue;
            }

            var refs = scenario.ParameterSetInfo?.Resolved ?? Array.Empty<ParameterRef>();
            var hits = refs.Where(r => changedKeys.Contains(r.Key)).ToList();
            if (!isChangedFile && hits.Count == 0)
            {
                unaffected.Add(scenario.Id);
                continue;
            }
            var impact = new ScenarioImpact
            {
                ScenarioId = scenario.Id, Path = display, Sha256 = sha,
                Reasons = Reasons(isChangedFile, hits.Count > 0, false),
                ChangedKeys = hits.Select(h => h.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList(),
                Fields = hits.Select(h => $"{h.Path} ← {h.Key}").ToList(),
                AssessmentRules = hits.Where(h => h.Path.StartsWith("assessment.", StringComparison.Ordinal)).Select(h => DescribeRule(scenario, h)).Distinct().ToList(),
            };
            ShipParticulars? ship = null;
            if (req.DataRoot is { } dr && File.Exists(System.IO.Path.Combine(dr, "data", "ships", scenario.Ship.Id, "particulars.json")))
            {
                try { ship = ShipParticulars.LoadFromDataRoot(dr, scenario.Ship.Id); }
                catch (InvalidDataException ex) { impact.Issues.Add(new IssueInfo("warning", "ship", $"船舶資料無法讀取:{ex.Message}")); }
            }
            else impact.Issues.Add(new IssueInfo("warning", "ship", $"找不到船舶資料 {scenario.Ship.Id},UKC 門檻未檢查"));
            var scenarioDir = req.DataRoot is { } root ? System.IO.Path.Combine(root, "data", "scenarios", scenario.Id) : null;
            foreach (var issue in ScenarioValidator.Check(scenario, ship, scenarioDir))
                impact.Issues.Add(new IssueInfo(issue.Severity == ScenarioIssueSeverity.Error ? "error" : "warning", issue.Path, issue.Message));
            affected.Add(impact);
        }

        return new ImpactReport
        {
            NewParameterSet = req.NewSet is null ? null : Summary(req.NewSet),
            BaselineParameterSet = req.Baseline is null ? null : Summary(req.Baseline),
            Changes = changes, Affected = affected, Unaffected = unaffected,
        };
    }

    public static ParameterSetSummary Summary(ParameterSet s) => new(s.Id, s.Version, s.Sha256.ToLowerInvariant(), s.Path);

    private static List<string> Reasons(bool changedFile, bool referencesChanged, bool loadFailed)
    {
        var r = new List<string>();
        if (changedFile) r.Add("情境檔本次變更");
        if (referencesChanged) r.Add("引用變更的參數");
        if (loadFailed) r.Add("以新參數集無法載入");
        return r;
    }

    private static string DescribeRule(Scenario sc, ParameterRef r)
    {
        var m = RulePath.Match(r.Path);
        if (!m.Success) return r.Path;
        var idx = int.Parse(m.Groups[2].Value);
        string? id = m.Groups[1].Value switch
        {
            "hardFail" when idx < sc.Assessment.HardFail.Count => sc.Assessment.HardFail[idx].Id,
            "bands" when idx < sc.Assessment.Bands.Count => sc.Assessment.Bands[idx].Id,
            "required" when idx < sc.Assessment.Required.Count => sc.Assessment.Required[idx].Id,
            _ => null,
        };
        return string.IsNullOrEmpty(id) ? m.Value : $"{m.Value}({id})";
    }

    /// <summary>人可讀的報告(CLI 輸出)。</summary>
    public static string Format(ImpactReport rep)
    {
        var sb = new System.Text.StringBuilder();
        if (rep.NewParameterSet is { } n)
            sb.AppendLine($"參數集 {n.Id} 版次 {n.Version}(sha256 {n.Sha256[..16]}…)" + (rep.BaselineParameterSet is { } b ? $",比對舊版次 {b.Version}" : ",無舊版可比對(全部鍵視為變更)"));
        if (rep.Changes.Count > 0)
        {
            sb.AppendLine($"變更的參數({rep.Changes.Count}):");
            foreach (var c in rep.Changes.Where(c => c.Change != ParameterChangeKinds.NoBaseline))
                sb.AppendLine($"  [{c.Change}] {c.Key}:{c.OldValue ?? "—"} → {c.NewValue ?? "—"} {c.Unit}(來源 {c.Source} 版次 {c.OldRevision ?? "—"} → {c.NewRevision ?? "—"})");
        }
        sb.AppendLine($"受影響情境({rep.Affected.Count}):");
        foreach (var a in rep.Affected)
        {
            sb.AppendLine($"  {a.ScenarioId}({a.Path};{string.Join("、", a.Reasons)})");
            if (a.ChangedKeys.Count > 0) sb.AppendLine($"    引用變更鍵:{string.Join(", ", a.ChangedKeys)}");
            foreach (var r in a.AssessmentRules) sb.AppendLine($"    評估規則:{r}");
            if (a.LoadError is not null) sb.AppendLine($"    [error] 無法載入:{a.LoadError}");
            foreach (var i in a.Issues) sb.AppendLine($"    [{i.Severity}] {i.Path}:{i.Message}");
        }
        if (rep.Unaffected.Count > 0) sb.AppendLine($"未受影響:{string.Join(", ", rep.Unaffected)}");
        return sb.ToString();
    }
}
