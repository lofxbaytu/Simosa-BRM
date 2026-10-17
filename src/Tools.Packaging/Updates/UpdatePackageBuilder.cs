using System.IO.Compression;
using System.Reflection;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Impact;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.Packaging.Updates;

/// <summary>更新包內容來源:安裝路徑 ← 本機檔案。</summary>
public sealed record PackageSource(string InstallPath, string SourcePath);

public sealed class UpdateBuildRequest
{
    public string? PackageId { get; init; }
    public required string Version { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public List<string> Changes { get; init; } = new();
    public List<SmsDocumentRef> SmsDocuments { get; init; } = new();
    public List<string> Ships { get; init; } = new();
    public List<PackageSource> Files { get; init; } = new();
    /// <summary>trust 類:要移除的信任金鑰 ID</summary>
    public List<string> RemoveKeyIds { get; init; } = new();
    public string? TrustReason { get; init; }
    /// <summary>驗證用的現行資料根目錄(含 data/scenarios、data/parameters、data/ships;預設專案根目錄)</summary>
    public string? ValidationRoot { get; init; }
    /// <summary>比對用的舊參數集(船上現行版次);未提供時以驗證根目錄的同名檔比對,同一檔案則全部鍵視為變更</summary>
    public string? BaselineParameters { get; init; }
    public string? VerificationRecordPath { get; init; }
    public bool Force { get; init; }
    public string? ForceReason { get; init; }
    public AuditLog? Audit { get; init; }

    /// <summary>把目錄下所有檔案加入(安裝路徑前綴 + 相對路徑)。</summary>
    public UpdateBuildRequest AddDirectory(string installPrefix, string sourceDir)
    {
        foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            Files.Add(new PackageSource(installPrefix.TrimEnd('/') + "/" + PackagePaths.Relative(sourceDir, f), f));
        return this;
    }
}

public sealed class UpdateBuildResult
{
    public required UpdateManifest Manifest { get; init; }
    public ImpactReport? Impact { get; init; }
    public required string OutputPath { get; init; }
    public required string PackageSha256 { get; init; }
    public List<string> Warnings { get; init; } = new();
}

/// <summary>產生更新包失敗(含影響分析報告供顯示)。</summary>
public sealed class PackageBuildException : Exception
{
    public PackageBuildException(string message, ImpactReport? impact = null, IReadOnlyList<string>? problems = null) : base(message)
    {
        Impact = impact;
        Problems = problems ?? Array.Empty<string>();
    }

    public ImpactReport? Impact { get; }
    public IReadOnlyList<string> Problems { get; }
}

/// <summary>
/// 教官筆電產生簽章更新包(規劃書第 5.5、9.1 節):檢查內容 → 含參數集或情境時做影響分析與驗證把關 → 寫 manifest → 簽章 → zip。
/// 未驗證或有錯誤時拒絕產生;--force 可強制產生但 manifest 標示 validation.status = forced 並記入稽核,船上預設拒絕套用。
/// </summary>
public static class UpdatePackageBuilder
{
    public const string DefaultParameterSetInstallPath = "data/parameters/company-procedures.yaml";

    public static string ToolVersion => typeof(UpdatePackageBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public static UpdateBuildResult Build(UpdateBuildRequest req, IKeyProvider key, string outputPath, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var now = clock.GetUtcNow().UtcDateTime;
        var warnings = new List<string>();

        PackageVersion.Parse(req.Version);
        if (!UpdateKinds.All.Contains(req.Kind)) throw new PackageBuildException($"kind 須為 {string.Join("|", UpdateKinds.All)}:{req.Kind}");
        if (string.IsNullOrWhiteSpace(req.Summary)) throw new PackageBuildException("需提供變更摘要(--summary)");
        if (req.PackageId is { } pid && !UpdateManifest.IsValidPackageId(pid))
            throw new PackageBuildException($"--package-id 格式不合法(須為英數字開頭,只含英數字 . _ -,最多 128 字元):{pid}");
        foreach (var d in req.SmsDocuments)
            if (string.IsNullOrWhiteSpace(d.DocumentId) || string.IsNullOrWhiteSpace(d.Revision) || string.IsNullOrWhiteSpace(d.ApprovedBy))
                throw new PackageBuildException("SMS 文件須含編號、版次與核准人(--sms 編號:版次:核准人[:核准日])");
        if (req.SmsDocuments.Count == 0)
        {
            if (req.Kind is UpdateKinds.Software or UpdateKinds.Charts) warnings.Add("未標示所依據的 SMS 文件");
            else throw new PackageBuildException("參數、情境、信任與混合更新包須標示所依據的 SMS 文件編號、版次與核准人(--sms)");
        }

        // 檔案:正規化、種類一致、來源存在、不重複
        var files = new List<(string Path, string Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = UpdateKinds.AllowedPrefixes(req.Kind);
        foreach (var f in req.Files)
        {
            var p = PackagePaths.NormalizeInstallPath(f.InstallPath);
            if (!prefixes.Any(pre => p.StartsWith(pre, StringComparison.Ordinal)))
                throw new PackageBuildException($"{req.Kind} 更新包不可包含 {p}(允許:{string.Join("、", prefixes)})");
            if (UpdatePackageReader.ProtectedPathError(p, req.Kind) is { } pe) throw new PackageBuildException(pe);
            if (!seen.Add(p)) throw new PackageBuildException($"重複的安裝路徑:{p}");
            if (!File.Exists(f.SourcePath)) throw new PackageBuildException($"找不到檔案:{f.SourcePath}");
            files.Add((p, Path.GetFullPath(f.SourcePath)));
        }
        if (files.Count == 0 && req.RemoveKeyIds.Count == 0) throw new PackageBuildException("更新包沒有內容");

        // 信任清單變更
        TrustChange? trust = null;
        if (req.Kind == UpdateKinds.Trust)
        {
            trust = new TrustChange { Reason = req.TrustReason, Remove = req.RemoveKeyIds.Select(k => k.ToLowerInvariant()).ToList() };
            foreach (var (p, src) in files)
            {
                if (!p.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || p.Count(c => c == '/') != 2) throw new PackageBuildException($"信任清單只接受 config/trusted-keys/ 下的 .pem:{p}");
                using var tk = TrustedKey.Parse(File.ReadAllText(src), src, KeyRoles.Primary);
                if (!KeyRoles.IsSigner(tk.Role)) throw new PackageBuildException($"{p} 的 role 須為 primary 或 backup");
                trust.Add.Add(new TrustKeyEntry { KeyId = tk.KeyId, Name = tk.Name, Role = tk.Role, Holder = tk.Holder, Path = p });
            }
            if (string.IsNullOrWhiteSpace(req.TrustReason)) throw new PackageBuildException("信任更新包須說明原因(--reason,例如:主金鑰遺失、訓練負責人交接)");
        }
        else if (req.RemoveKeyIds.Count > 0) throw new PackageBuildException("--remove-key 只能用於 trust 更新包");

        // 參數集與情境:影響分析與驗證把關(第 9.1 節)
        var paramFiles = files.Where(f => f.Path.StartsWith("data/parameters/", StringComparison.Ordinal) && IsYaml(f.Path)).ToList();
        var scenarioFiles = files.Where(f => IsScenarioYaml(f.Path)).ToList();
        if (paramFiles.Count > 1) throw new PackageBuildException("一個更新包只能含一個參數集");
        ImpactReport? impact = null;
        var validation = new ValidationInfo { CheckedUtc = now, Status = ValidationStatuses.NotRequired };
        ParameterSetRef? psRef = null;
        var affectedOut = new List<AffectedScenario>();
        if (paramFiles.Count > 0 || scenarioFiles.Count > 0)
        {
            var root = req.ValidationRoot ?? SimCore.Ship.RepositoryPaths.TryFindRoot()
                       ?? throw new PackageBuildException("找不到驗證用的資料根目錄(--data-root)");
            if (!Directory.Exists(Path.Combine(root, "data"))) throw new PackageBuildException($"驗證根目錄沒有 data/:{root}");
            ParameterSet? newSet;
            ParameterSet? baseline;
            try
            {
                var installPath = paramFiles.Count > 0 ? paramFiles[0].Path : DefaultParameterSetInstallPath;
                var deployed = PackagePaths.Combine(root, installPath);
                if (paramFiles.Count > 0)
                {
                    newSet = ParameterSetLoader.Load(paramFiles[0].Source);
                    if (req.BaselineParameters is { } bp) baseline = ParameterSetLoader.Load(bp);
                    else if (File.Exists(deployed) && !SamePath(deployed, paramFiles[0].Source)) baseline = ParameterSetLoader.Load(deployed);
                    else
                    {
                        baseline = null;
                        warnings.Add("未提供舊版參數集(--baseline),全部參數鍵視為變更,所有引用參數的情境都列為受影響");
                    }
                }
                else
                {
                    newSet = File.Exists(deployed) ? ParameterSetLoader.Load(deployed) : null;
                    baseline = newSet;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException)
            {
                throw new PackageBuildException($"參數集檢查失敗:{ex.Message}");
            }

            var overridden = scenarioFiles.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scenarioDir = Path.Combine(root, "data", "scenarios");
            var all = new List<string>();
            var display = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(scenarioDir))
            {
                foreach (var f in Directory.EnumerateFiles(scenarioDir, "*.yaml"))
                {
                    var rel = "data/scenarios/" + Path.GetFileName(f);
                    if (overridden.Contains(rel)) continue;
                    all.Add(Path.GetFullPath(f));
                    display[Path.GetFullPath(f)] = rel;
                }
            }
            foreach (var (p, src) in scenarioFiles)
            {
                all.Add(src);
                display[src] = p;
            }
            impact = ImpactAnalyzer.Analyze(new ImpactRequest
            {
                NewSet = newSet, Baseline = baseline, ScenarioFiles = all,
                ChangedScenarioFiles = scenarioFiles.Select(s => s.Source).ToHashSet(StringComparer.Ordinal),
                DataRoot = root, DisplayRoot = root, DisplayPaths = display,
            });

            var record = req.VerificationRecordPath is { } vr ? VerificationRecord.LoadOrNew(vr) : null;
            if (req.VerificationRecordPath is { } vrp && !File.Exists(vrp)) throw new PackageBuildException($"找不到驗證紀錄:{vrp}");
            var problems = new List<string>();
            foreach (var a in impact.Affected)
            {
                var entry = record?.Find(a.ScenarioId, a.Sha256, newSet?.Sha256);
                var errors = (a.LoadError is null ? 0 : 1) + a.Issues.Count(i => i.Severity == "error");
                if (errors > 0) problems.Add($"情境 {a.ScenarioId} 有 {errors} 個錯誤" + (a.LoadError is { } le ? $"({le})" : ""));
                if (entry is null) problems.Add($"情境 {a.ScenarioId} 未以本參數集/情境內容完成驗證(simosa-pkg verification add)");
                affectedOut.Add(new AffectedScenario
                {
                    ScenarioId = a.ScenarioId, Path = a.Path, Sha256 = a.Sha256, Reasons = a.Reasons, ChangedKeys = a.ChangedKeys,
                    AssessmentRules = a.AssessmentRules, Errors = errors, Warnings = a.Issues.Count(i => i.Severity == "warning"), Verification = entry,
                });
            }
            if (paramFiles.Count > 0 && newSet is not null && baseline is not null && impact.Changes.Count > 0
                && (!PackageVersion.TryParse(newSet.Version, out var nv) || !PackageVersion.TryParse(baseline.Version, out var bv) || nv.CompareTo(bv) <= 0))
                problems.Add($"參數集內容已變更,但 version {newSet.Version} 未大於舊版次 {baseline.Version}(講評報告以版次區分新舊參數)");
            if (newSet is not null)
            {
                foreach (var d in req.SmsDocuments)
                    if (newSet.Sources.TryGetValue(d.DocumentId, out var src) && src.Revision != d.Revision)
                        warnings.Add($"SMS 文件 {d.DocumentId} 標示第 {d.Revision} 版,但參數集 sources.{d.DocumentId}.revision 為 {src.Revision}");
            }
            validation.BaselineParameterSetVersion = baseline?.Version;
            validation.ChangedParameterKeys = impact.Changes.Select(c => c.Key).ToList();
            if (req.VerificationRecordPath is { } path)
            {
                validation.VerificationRecord = Path.GetFileName(path);
                validation.VerificationRecordSha256 = Sha256Hex.OfFile(path);
            }
            validation.Problems = problems;
            if (problems.Count > 0)
            {
                if (!req.Force) throw new PackageBuildException("受影響情境未通過檢查或未驗證,拒絕產生更新包(第 9.1 節)", impact, problems);
                if (string.IsNullOrWhiteSpace(req.ForceReason)) throw new PackageBuildException("--force 須同時以 --force-reason 說明原因", impact, problems);
                validation.Status = ValidationStatuses.Forced;
                validation.ForcedReason = req.ForceReason;
                warnings.Add("以 --force 產生:船上預設拒絕套用此更新包");
            }
            else validation.Status = ValidationStatuses.Verified;

            if (paramFiles.Count > 0 && newSet is not null)
            {
                psRef = new ParameterSetRef
                {
                    Id = newSet.Id, Version = newSet.Version, Sha256 = Sha256Hex.OfFile(paramFiles[0].Source), Path = paramFiles[0].Path,
                    SourceRevisions = newSet.Sources.ToDictionary(s => s.Key, s => s.Value.Revision),
                };
            }
        }

        var manifest = new UpdateManifest
        {
            PackageId = req.PackageId ?? $"upd-{now:yyyyMMdd'T'HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            Version = req.Version,
            CreatedUtc = now,
            Kind = req.Kind,
            Summary = req.Summary,
            Changes = req.Changes,
            SmsDocuments = req.SmsDocuments,
            Ships = req.Ships,
            Files = files.Select(f => new PackageFile { Path = f.Path, Sha256 = Sha256Hex.OfFile(f.Source), Size = new FileInfo(f.Source).Length }).ToList(),
            ParameterSet = psRef,
            AffectedScenarios = affectedOut,
            Validation = validation,
            Trust = trust,
            Signer = new SignerRef { KeyId = key.KeyId, Name = key.DisplayName },
            CreatedBy = $"{System.Environment.MachineName} simosa-pkg {ToolVersion}",
        };
        var manifestBytes = PackageJson.ToBytes(manifest);
        var sig = SignatureEnvelope.Create(key, manifestBytes, now);

        var outFull = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
        var tmp = outFull + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Write(zip, UpdateManifest.ManifestEntry, manifestBytes);
            Write(zip, UpdateManifest.SignatureEntry, PackageJson.ToBytes(sig));
            foreach (var (p, src) in files)
                zip.CreateEntryFromFile(src, UpdateManifest.PayloadPrefix + p, CompressionLevel.Optimal);
        }
        File.Move(tmp, outFull, overwrite: true);
        var pkgSha = Sha256Hex.OfFile(outFull);

        req.Audit?.Write(AuditEvents.PackageBuilt, new
        {
            manifest.PackageId, manifest.Version, manifest.Kind, manifest.Summary, signer = manifest.Signer, packageSha256 = pkgSha,
            validation = manifest.Validation.Status, affected = manifest.AffectedScenarios.Select(a => a.ScenarioId).ToList(),
        });
        if (validation.Status == ValidationStatuses.Forced)
            req.Audit?.Write(AuditEvents.PackageForced, new { manifest.PackageId, manifest.Version, reason = req.ForceReason, problems = validation.Problems });

        return new UpdateBuildResult { Manifest = manifest, Impact = impact, OutputPath = outFull, PackageSha256 = pkgSha, Warnings = warnings };
    }

    internal static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(bytes);
    }

    public static bool IsYaml(string p) => p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    /// <summary>情境 YAML:data/scenarios/ 下第一層的 *.yaml(子目錄為情境附件)。</summary>
    public static bool IsScenarioYaml(string p)
        => p.StartsWith("data/scenarios/", StringComparison.Ordinal) && p.Count(c => c == '/') == 2 && IsYaml(p);

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
