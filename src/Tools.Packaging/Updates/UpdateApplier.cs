using System.IO.Compression;
using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.SimCore.Parameters;
using SimosaBRM.SimCore.Scenario;
using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Packaging.Updates;

public sealed class UpdateApplyOptions
{
    /// <summary>允許套用以 --force 產生的更新包(僅限船上管理者於本機 CLI;直連同步一律不允許)</summary>
    public bool AllowUnverified { get; init; }
    /// <summary>操作者(稽核用)</summary>
    public string? Operator { get; init; }
    /// <summary>經由(cli/tcp/usb;稽核用)</summary>
    public string Via { get; init; } = "cli";
    /// <summary>測試掛勾:每個元件替換完成後呼叫(丟出例外以模擬中途失敗)</summary>
    public Action<string>? AfterComponentSwap { get; init; }
    /// <summary>測試掛勾:提交步驟(上一版移入 state/previous)各階段前呼叫(move-previous、delete-stale;丟出例外以模擬檔案鎖定)</summary>
    public Action<string>? CommitStep { get; init; }
}

/// <summary>套用結果(同步訊息 updateResult 與 CLI 輸出)。</summary>
public sealed class UpdateApplyResult
{
    public bool Ok { get; set; }
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    public string? Kind { get; set; }
    public string? Summary { get; set; }
    public string? Signer { get; set; }
    public string? SignerRole { get; set; }
    public List<string> Components { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>中途失敗且已回復原狀</summary>
    public bool RolledBack { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>state/previous/previous.json:上一版元件的中繼資料(rollback 用)。</summary>
public sealed class PreviousMeta
{
    public string? ReplacedByPackageId { get; set; }
    public string? ReplacedByVersion { get; set; }
    /// <summary>取代者的種類(trust 類不可 rollback)</summary>
    public string? ReplacedByKind { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>元件 → 套用前是否存在</summary>
    public Dictionary<string, bool> Components { get; set; } = new();
}

/// <summary>
/// 船上套用更新包(規劃書第 5.5 節):驗證(簽章、信任清單、雜湊、版本)→ 解壓到 state/staging 並重算雜湊 → 套用前檢查
/// (參數集可載入、情境以新參數集可載入且無錯誤、信任清單變更後仍有備援金鑰)→ 每個受影響的頂層元件(app/、data/、config/)
/// 以「複製現行 + 覆蓋新檔」建立新目錄,再以目錄改名原子替換;舊目錄移到 state/previous/ 供 rollback(保留一層)
/// → 更新 installed.json → 稽核紀錄。任何一步失敗都完整回復原狀。
/// </summary>
public sealed class UpdateApplier
{
    private readonly InstallLayout _layout;
    private readonly TimeProvider _clock;

    public UpdateApplier(InstallLayout layout, TimeProvider? clock = null)
    {
        _layout = layout;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public UpdateVerification Verify(string packagePath, bool allowUnverified = false)
    {
        using var trust = TrustStore.LoadDirectory(_layout.TrustedKeysDir);
        return UpdatePackageReader.Verify(packagePath, trust, InstalledState.LoadOrNew(_layout.InstalledStatePath), _layout.Config.ShipId, allowUnverified);
    }

    public UpdateApplyResult Apply(string packagePath, UpdateApplyOptions? options = null)
    {
        options ??= new UpdateApplyOptions();
        using var _ = _layout.AcquireLock();
        var audit = _layout.Audit(_clock);
        var installed = InstalledState.LoadOrNew(_layout.InstalledStatePath);
        using var trust = TrustStore.LoadDirectory(_layout.TrustedKeysDir);
        var v = UpdatePackageReader.Verify(packagePath, trust, installed, _layout.Config.ShipId, options.AllowUnverified);
        var result = new UpdateApplyResult
        {
            PackageId = v.Manifest?.PackageId, Version = v.Manifest?.Version, Kind = v.Manifest?.Kind, Summary = v.Manifest?.Summary,
            Signer = v.SignerDescription, SignerRole = v.SignerRole, Warnings = v.Warnings.ToList(),
        };
        if (!v.Ok)
        {
            result.Errors.AddRange(v.Errors);
            result.Message = "更新包驗證失敗,未套用";
            audit.Write(AuditEvents.UpdateRejected, new
            {
                file = Path.GetFileName(packagePath), packageSha256 = v.PackageSha256, result.PackageId, result.Version,
                claimedSigner = v.Signature?.Signer, keyId = v.Signature?.KeyId, signer = v.SignerDescription, errors = v.Errors, via = options.Via, options.Operator,
            });
            return result;
        }
        var m = v.Manifest!;
        string staging;
        try
        {
            // packageId 已由 Verify 以白名單檢查;此處再確認暫存目錄仍在 state/staging 之內(縱深防禦)
            staging = PackagePaths.Combine(_layout.StagingDir, SafeName(m.PackageId));
        }
        catch (InvalidDataException ex)
        {
            result.Errors.Add(ex.Message);
            result.Message = "更新包驗證失敗,未套用";
            audit.Write(AuditEvents.UpdateRejected, new { m.PackageId, m.Version, signer = v.SignerDescription, errors = result.Errors, via = options.Via, options.Operator });
            return result;
        }
        var nextRoot = Path.Combine(_layout.StateDir, "next");
        var pendingPrev = Path.Combine(_layout.StateDir, "previous.pending");
        var committed = false;
        var preservePending = false;
        try
        {
            PackagePaths.DeleteDirectory(staging);
            PackagePaths.DeleteDirectory(nextRoot);
            PackagePaths.DeleteDirectory(pendingPrev);
            CleanupStalePrevious();

            // 1. 解壓到暫存並重算雜湊(以解出的檔案為準,避免驗證後被替換)
            Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(packagePath))
            {
                if (UpdatePackageReader.FindDuplicateEntry(zip) is { } dup) throw new InvalidDataException($"更新包內有重複的項目:{dup}");
                foreach (var f in m.Files)
                {
                    var entry = zip.GetEntry(UpdateManifest.PayloadPrefix + f.Path) ?? throw new InvalidDataException($"更新包缺少檔案:{f.Path}");
                    var dest = PackagePaths.Combine(staging, f.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: false);
                    if (!Sha256Hex.Equal(Sha256Hex.OfFile(dest), f.Sha256)) throw new InvalidDataException($"解壓後檔案雜湊不符:{f.Path}");
                }
            }

            // 2. 套用前檢查
            var removePaths = m.Remove.Select(PackagePaths.NormalizeInstallPath).ToList();
            PreCheck(m, staging, trust, removePaths, result);
            if (result.Errors.Count > 0)
            {
                result.Message = "套用前檢查失敗,未套用";
                audit.Write(AuditEvents.UpdateRejected, new { m.PackageId, m.Version, signer = v.SignerDescription, errors = result.Errors, via = options.Via, options.Operator });
                return result;
            }

            // 3. 建立新元件目錄:複製現行 → 先刪除 remove(含 trust.remove 推得的金鑰檔)→ 再寫入新檔
            //    (先刪後寫:新檔與被移除的檔案同名時,新檔不會被當成移除項刪掉)
            var components = m.Files.Select(f => PackagePaths.TopLevel(f.Path)).Concat(removePaths.Select(PackagePaths.TopLevel))
                .Distinct().OrderBy(c => Array.IndexOf(PackagePaths.UpdateComponents, c)).ToList();
            result.Components = components;
            var existed = new Dictionary<string, bool>();
            foreach (var comp in components)
            {
                var live = Path.Combine(_layout.Root, comp);
                var next = Path.Combine(nextRoot, comp);
                existed[comp] = Directory.Exists(live);
                if (existed[comp]) PackagePaths.CopyDirectory(live, next);
                else Directory.CreateDirectory(next);
            }
            foreach (var r in removePaths)
            {
                var target = PackagePaths.Combine(nextRoot, r);
                if (File.Exists(target)) File.Delete(target);
                else if (Directory.Exists(target)) PackagePaths.DeleteDirectory(target);
            }
            foreach (var f in m.Files)
            {
                var dest = PackagePaths.Combine(nextRoot, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(PackagePaths.Combine(staging, f.Path), dest, overwrite: true);
            }
            // trust 類:以實際組出的信任清單目錄重新載入並核對(不只依清單推算)
            if (m.Kind == UpdateKinds.Trust && m.Trust is { } tc)
            {
                CheckResultingTrust(Path.Combine(nextRoot, "config", "trusted-keys"), tc, result);
                if (result.Errors.Count > 0)
                {
                    result.Message = "套用前檢查失敗,未套用";
                    audit.Write(AuditEvents.UpdateRejected, new { m.PackageId, m.Version, signer = v.SignerDescription, errors = result.Errors, via = options.Via, options.Operator });
                    return result;
                }
            }

            // 4. 原子替換(目錄改名);失敗時逐一回復
            Directory.CreateDirectory(pendingPrev);
            var attempted = new List<string>();
            try
            {
                foreach (var comp in components)
                {
                    attempted.Add(comp);
                    var live = Path.Combine(_layout.Root, comp);
                    if (existed[comp]) Directory.Move(live, Path.Combine(pendingPrev, comp));
                    Directory.Move(Path.Combine(nextRoot, comp), live);
                    options.AfterComponentSwap?.Invoke(comp);
                }
                PackageJson.SaveAtomic(Path.Combine(pendingPrev, "previous.json"), new PreviousMeta
                {
                    ReplacedByPackageId = m.PackageId, ReplacedByVersion = m.Version, ReplacedByKind = m.Kind, CreatedUtc = Now, Components = existed,
                });
                if (File.Exists(_layout.InstalledStatePath)) File.Copy(_layout.InstalledStatePath, Path.Combine(pendingPrev, "installed.json"), overwrite: true);
                UpdateInstalled(installed, m, v);
                installed.Save(_layout.InstalledStatePath);
            }
            catch (Exception ex)
            {
                var restoreErrors = Restore(attempted, existed, pendingPrev);
                result.RolledBack = true;
                result.Errors.Add($"套用中途失敗,已回復原狀:{ex.Message}");
                result.Errors.AddRange(restoreErrors);
                result.Message = restoreErrors.Count == 0 ? "套用失敗,已完整回復上一版" : "套用失敗,回復時發生錯誤(請以 simosa-pkg status 檢查)";
                audit.Write(AuditEvents.UpdateFailed, new { m.PackageId, m.Version, signer = v.SignerDescription, error = ex.Message, restoreErrors, components, via = options.Via, options.Operator });
                return result;
            }

            // 5. 提交:新版已上線、installed.json 已寫入。之後的任何失敗都只記警告,不得回報「未變更」
            committed = true;
            result.Ok = true;
            result.Message = $"已套用 {m.Kind} 更新包 {m.Version}(簽章者 {v.SignerDescription})";
            preservePending = !CommitPrevious(pendingPrev, m, options, result);
            try
            {
                audit.Write(AuditEvents.UpdateApplied, new
                {
                    m.PackageId, m.Version, m.Kind, m.Summary, signer = new { keyId = v.SignerKeyId, name = v.SignerName, role = v.SignerRole },
                    claimedSigner = m.Signer.Name, packageSha256 = v.PackageSha256, smsDocuments = m.SmsDocuments, files = m.Files.Count, components,
                    parameterSet = m.ParameterSet is { } ps ? new { ps.Id, ps.Version, ps.Sha256 } : null, validation = m.Validation.Status,
                    affected = m.AffectedScenarios.Select(a => a.ScenarioId).ToList(), warnings = result.Warnings, via = options.Via, options.Operator,
                });
                if (m.Kind == UpdateKinds.Trust && m.Trust is { } t)
                    audit.Write(AuditEvents.TrustRotated, new { m.PackageId, m.Version, signer = v.SignerDescription, added = t.Add, removed = t.Remove, reason = t.Reason });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"已套用,但稽核紀錄寫入失敗:{ex.Message}");
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            if (committed)
            {
                // 不應發生(提交後的步驟各自捕捉例外);保守起見仍回報已套用
                result.Warnings.Add($"已套用,但提交後的步驟失敗:{ex.Message}");
                return result;
            }
            result.Errors.Add(ex.Message);
            result.Message = "套用失敗,未變更安裝目錄";
            audit.Write(AuditEvents.UpdateFailed, new { m.PackageId, m.Version, error = ex.Message, via = options.Via, options.Operator });
            return result;
        }
        finally
        {
            TryDelete(staging);
            TryDelete(nextRoot);
            if (!preservePending) TryDelete(pendingPrev);
        }
    }

    /// <summary>
    /// 提交上一版:舊的 state/previous 先改名移開(state/previous.stale-*)再把 previous.pending 改名為 state/previous,
    /// 最後盡力刪除舊的;刪不掉只記警告(下次套用時再清理)。上一版無法放入 state/previous 時另存為
    /// state/previous.unsaved-*(不刪除,供手動回復),並使殘留的舊 state/previous 失效,避免 rollback 回到錯誤的版本。
    /// 回傳 false 表示 previous.pending 仍保有上一版資料(呼叫端不得刪除)。
    /// </summary>
    private bool CommitPrevious(string pendingPrev, UpdateManifest m, UpdateApplyOptions options, UpdateApplyResult result)
    {
        string? stale = null;
        try
        {
            options.CommitStep?.Invoke("move-previous");
            if (Directory.Exists(_layout.PreviousDir))
            {
                stale = Path.Combine(_layout.StateDir, $"previous.stale-{Guid.NewGuid().ToString("N")[..8]}");
                Directory.Move(_layout.PreviousDir, stale);
            }
            Directory.Move(pendingPrev, _layout.PreviousDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var kept = pendingPrev;
            var movedAway = false;
            try
            {
                var unsaved = Path.Combine(_layout.StateDir, $"previous.unsaved-{SafeName(m.PackageId)}-{Now:yyyyMMdd'T'HHmmss}");
                Directory.Move(pendingPrev, unsaved);
                kept = unsaved;
                movedAway = true;
            }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
            // 殘留的 state/previous 是更早一版的上一版,已不能用來 rollback 現行版本
            try
            {
                var meta = Path.Combine(_layout.PreviousDir, "previous.json");
                if (File.Exists(meta)) File.Delete(meta);
            }
            catch (Exception ex3) when (ex3 is IOException or UnauthorizedAccessException) { }
            result.Warnings.Add($"新版已套用,但上一版無法存入 state/previous({ex.Message});rollback 不可用,被替換的元件保存在 {kept}(可手動回復)");
            return movedAway;
        }
        if (stale is not null)
        {
            try
            {
                options.CommitStep?.Invoke("delete-stale");
                PackagePaths.DeleteDirectory(stale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Warnings.Add($"已套用;舊的上一版暫存 {stale} 無法刪除({ex.Message}),下次套用時再清理");
            }
        }
        return true;
    }

    /// <summary>盡力刪除先前提交時刪不掉的 state/previous.stale-*。</summary>
    private void CleanupStalePrevious()
    {
        if (!Directory.Exists(_layout.StateDir)) return;
        foreach (var d in Directory.EnumerateDirectories(_layout.StateDir, "previous.stale-*")) TryDelete(d);
    }

    private List<string> Restore(List<string> attempted, Dictionary<string, bool> existed, string pendingPrev)
    {
        var errors = new List<string>();
        foreach (var comp in Enumerable.Reverse(attempted))
        {
            try
            {
                var live = Path.Combine(_layout.Root, comp);
                var saved = Path.Combine(pendingPrev, comp);
                if (Directory.Exists(saved))
                {
                    if (Directory.Exists(live)) PackagePaths.DeleteDirectory(live);
                    Directory.Move(saved, live);
                }
                else if (!existed[comp] && Directory.Exists(live)) PackagePaths.DeleteDirectory(live);
            }
            catch (Exception ex)
            {
                errors.Add($"回復 {comp} 失敗:{ex.Message}");
            }
        }
        return errors;
    }

    private void PreCheck(UpdateManifest m, string staging, TrustStore trust, List<string> removePaths, UpdateApplyResult result)
    {
        ParameterSet? set = null;
        var paramFile = m.Files.FirstOrDefault(f => f.Path.StartsWith("data/parameters/", StringComparison.Ordinal) && UpdatePackageBuilder.IsYaml(f.Path));
        try
        {
            if (paramFile is not null) set = ParameterSetLoader.FromYaml(File.ReadAllText(PackagePaths.Combine(staging, paramFile.Path)));
            else
            {
                var live = PackagePaths.Combine(_layout.Root, UpdatePackageBuilder.DefaultParameterSetInstallPath);
                if (File.Exists(live)) set = ParameterSetLoader.FromYaml(File.ReadAllText(live));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException)
        {
            result.Errors.Add($"參數集無法載入:{ex.Message}");
            return;
        }
        foreach (var f in m.Files.Where(f => UpdatePackageBuilder.IsScenarioYaml(f.Path)))
        {
            try
            {
                var sc = ScenarioLoader.Load(PackagePaths.Combine(staging, f.Path), set);
                ShipParticulars? ship = null;
                var shipFile = Path.Combine(_layout.Root, "data", "ships", sc.Ship.Id, "particulars.json");
                if (File.Exists(shipFile)) ship = ShipParticulars.Load(shipFile);
                foreach (var issue in ScenarioValidator.Check(sc, ship, Path.Combine(_layout.Root, "data", "scenarios", sc.Id)).Where(i => i.Severity == ScenarioIssueSeverity.Error))
                    result.Errors.Add($"情境 {f.Path}:{issue}");
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException)
            {
                result.Errors.Add($"情境 {f.Path} 無法載入:{ex.Message}");
            }
        }
        if (m.Kind == UpdateKinds.Trust && m.Trust is { } t)
        {
            // 依「實際套用後的檔案集合」計算:現有檔案 − trust.remove 的金鑰檔 + trust.add(新增或覆寫)
            var resulting = new Dictionary<string, string>(PathComparer); // 檔名 → 角色
            foreach (var k in trust.Keys.Where(k => k.FilePath is not null)) resulting[Path.GetFileName(k.FilePath!)] = k.Role;
            var addNames = t.Add.Select(a => Path.GetFileName(a.Path)).ToHashSet(PathComparer);
            foreach (var r in t.Remove)
            {
                var files = trust.Keys.Where(k => k.FilePath is not null && string.Equals(k.KeyId, r, StringComparison.OrdinalIgnoreCase)).ToList();
                if (files.Count == 0)
                {
                    result.Warnings.Add($"要移除的金鑰 {KeyFiles.Short(r)} 不在信任清單");
                    continue;
                }
                foreach (var k in files)
                {
                    var name = Path.GetFileName(k.FilePath!);
                    resulting.Remove(name);
                    // 同名檔會被 trust.add 的新公鑰取代,不列入移除(否則新公鑰會被刪掉)
                    if (!addNames.Contains(name)) removePaths.Add(PackagePaths.TrustedKeysPrefix + name);
                }
            }
            foreach (var a in t.Add)
            {
                var name = Path.GetFileName(a.Path);
                if (resulting.ContainsKey(name))
                {
                    var existing = trust.Keys.FirstOrDefault(k => k.FilePath is not null && PathComparer.Equals(Path.GetFileName(k.FilePath), name));
                    result.Errors.Add($"trust.add 的 {a.Path} 會覆寫信任清單中現有的金鑰檔(金鑰 {existing?.Describe() ?? name});要更換該金鑰請在 trust.remove 列出其金鑰 ID,或改用其他檔名");
                    continue;
                }
                try
                {
                    using var parsed = TrustedKey.Parse(File.ReadAllText(PackagePaths.Combine(staging, a.Path)), a.Path, KeyRoles.Primary);
                    if (!string.Equals(parsed.KeyId, a.KeyId, StringComparison.OrdinalIgnoreCase) || parsed.Role != a.Role)
                        result.Errors.Add($"{a.Path} 的公鑰或角色與 trust.add 不符");
                    else if (!KeyRoles.IsSigner(parsed.Role))
                        result.Errors.Add($"{a.Path} 的 role 須為 primary 或 backup");
                }
                catch (System.Security.Cryptography.CryptographicException ex)
                {
                    result.Errors.Add($"{a.Path} 不是有效的公鑰:{ex.Message}");
                }
                resulting[name] = a.Role;
            }
            if (!resulting.ContainsValue(KeyRoles.Backup)) result.Errors.Add("變更後信任清單沒有備援金鑰,拒絕套用(避免無法再輪替)");
            if (!resulting.ContainsValue(KeyRoles.Primary)) result.Warnings.Add("變更後信任清單沒有主金鑰,之後的更新包須以備援金鑰簽章");
        }
    }

    /// <summary>船上檔名比較(Windows 不分大小寫)。</summary>
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>以組好的新信任清單目錄重新載入核對:須有備援金鑰、新增的金鑰都在、移除的金鑰都不在。</summary>
    private static void CheckResultingTrust(string dir, TrustChange t, UpdateApplyResult result)
    {
        try
        {
            using var after = TrustStore.LoadDirectory(dir);
            if (!after.Keys.Any(k => k.Role == KeyRoles.Backup)) result.Errors.Add("變更後信任清單沒有備援金鑰,拒絕套用(避免無法再輪替)");
            foreach (var a in t.Add)
                if (after.Find(a.KeyId) is null) result.Errors.Add($"新增的金鑰 {KeyFiles.Short(a.KeyId)}({a.Name})未出現在變更後的信任清單,拒絕套用");
            foreach (var r in t.Remove)
                if (after.Find(r) is not null) result.Errors.Add($"要移除的金鑰 {KeyFiles.Short(r)} 仍在變更後的信任清單,拒絕套用");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            result.Errors.Add($"變更後的信任清單無法載入:{ex.Message}");
        }
    }

    private void UpdateInstalled(InstalledState s, UpdateManifest m, UpdateVerification v)
    {
        var now = Now;
        s.Version = m.Version;
        s.PackageId = m.PackageId;
        s.Kind = m.Kind;
        s.AppliedUtc = now;
        s.Signer = new InstalledSigner { KeyId = v.SignerKeyId ?? "", Name = v.SignerName ?? "", Role = v.SignerRole ?? "" };
        if (s.HighestVersion is null || PackageVersion.Compare(m.Version, s.HighestVersion) > 0) s.HighestVersion = m.Version;
        foreach (var kind in ComponentKinds(m))
            s.Components[kind] = new ComponentVersion { Version = m.Version, PackageId = m.PackageId, AppliedUtc = now, Summary = m.Summary };
        if (m.ParameterSet is not null) s.ParameterSet = m.ParameterSet;
        if (m.SmsDocuments.Count > 0) s.SmsDocuments = m.SmsDocuments;
        s.History.Insert(0, new AppliedPackage { PackageId = m.PackageId, Version = m.Version, Kind = m.Kind, AppliedUtc = now, Signer = v.SignerDescription ?? "", Summary = m.Summary });
        if (s.History.Count > 50) s.History.RemoveRange(50, s.History.Count - 50);
    }

    /// <summary>更新包內容對應的元件種類(船上版次顯示)。</summary>
    public static IEnumerable<string> ComponentKinds(UpdateManifest m)
    {
        if (m.Kind != UpdateKinds.Mixed) return new[] { m.Kind };
        return m.Files.Select(f => f.Path switch
        {
            var p when p.StartsWith("app/", StringComparison.Ordinal) => UpdateKinds.Software,
            var p when p.StartsWith("data/scenarios/", StringComparison.Ordinal) => UpdateKinds.Scenarios,
            var p when p.StartsWith("data/parameters/", StringComparison.Ordinal) => UpdateKinds.Parameters,
            var p when p.StartsWith("data/charts/", StringComparison.Ordinal) => UpdateKinds.Charts,
            _ => "data",
        }).Distinct();
    }

    /// <summary>
    /// 是否可 rollback:state/previous 須存在、且正是現行更新包取代掉的上一版;上一個套用的是信任清單變更(trust)時不可 rollback
    /// (否則被撤銷的金鑰會重新受信任;撤回信任變更須以備援金鑰簽章新的 trust 更新包)。
    /// </summary>
    public bool CanRollback(out string? reason) => CanRollback(out reason, out _, out _);

    private bool CanRollback(out string? reason, out PreviousMeta? meta, out InstalledState current)
    {
        meta = null;
        current = InstalledState.LoadOrNew(_layout.InstalledStatePath);
        var metaPath = Path.Combine(_layout.PreviousDir, "previous.json");
        if (!File.Exists(metaPath))
        {
            reason = "沒有可回復的上一版(state/previous 不存在)";
            return false;
        }
        try { meta = PackageJson.Load<PreviousMeta>(metaPath, "previous.json"); }
        catch (InvalidDataException ex)
        {
            reason = $"上一版資料無法讀取:{ex.Message}";
            return false;
        }
        if (meta.ReplacedByPackageId is null || meta.ReplacedByPackageId != current.PackageId)
        {
            reason = $"state/previous 不是現行更新包 {current.PackageId ?? "(無)"} 的上一版(屬於 {meta.ReplacedByPackageId ?? "?"}),拒絕回復";
            return false;
        }
        var replacedId = meta.ReplacedByPackageId;
        var kind = meta.ReplacedByKind ?? current.History.FirstOrDefault(h => h.PackageId == replacedId)?.Kind;
        if (kind == UpdateKinds.Trust || meta.Components.ContainsKey("config"))
        {
            reason = "上一個套用的是信任清單變更(trust),不可 rollback(會讓被撤銷的金鑰重新受信任);要撤回請由備援金鑰簽章新的 trust 更新包";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>回復到上一版(state/previous;只保留一層)。曾套用最高版本不下降,因此回復後不能重新套用同一版或更舊的更新包。trust 類不可回復。</summary>
    public UpdateApplyResult Rollback(string? operatorName = null)
    {
        using var _ = _layout.AcquireLock();
        var audit = _layout.Audit(_clock);
        var result = new UpdateApplyResult();
        if (!CanRollback(out var reason, out var metaOrNull, out var current))
        {
            result.Errors.Add(reason!);
            result.Message = "無法回復";
            if (metaOrNull is not null) audit.Write(AuditEvents.UpdateFailed, new { action = "rollback", error = reason, operatorName });
            return result;
        }
        var meta = metaOrNull!;
        var discard = Path.Combine(_layout.StateDir, "discard");
        PackagePaths.DeleteDirectory(discard);
        Directory.CreateDirectory(discard);
        var done = new List<string>();
        try
        {
            foreach (var (comp, existedBefore) in meta.Components)
            {
                var live = Path.Combine(_layout.Root, comp);
                if (Directory.Exists(live)) Directory.Move(live, Path.Combine(discard, comp));
                done.Add(comp);
                var saved = Path.Combine(_layout.PreviousDir, comp);
                if (existedBefore && Directory.Exists(saved)) Directory.Move(saved, live);
            }
        }
        catch (Exception ex)
        {
            // 盡力恢復成回復前的狀態
            foreach (var comp in Enumerable.Reverse(done))
            {
                var live = Path.Combine(_layout.Root, comp);
                var saved = Path.Combine(_layout.PreviousDir, comp);
                if (Directory.Exists(live) && !Directory.Exists(saved)) Directory.Move(live, saved);
                if (Directory.Exists(Path.Combine(discard, comp))) Directory.Move(Path.Combine(discard, comp), live);
            }
            result.Errors.Add($"回復失敗:{ex.Message}");
            result.Message = "回復失敗,已保持現況";
            audit.Write(AuditEvents.UpdateFailed, new { action = "rollback", error = ex.Message, operatorName });
            return result;
        }
        var prevStatePath = Path.Combine(_layout.PreviousDir, "installed.json");
        var restored = File.Exists(prevStatePath) ? PackageJson.Load<InstalledState>(prevStatePath, "installed.json") : new InstalledState();
        restored.HighestVersion = MaxVersion(current.HighestVersion, restored.HighestVersion);
        restored.History = current.History;
        var undone = restored.History.FirstOrDefault(h => h.PackageId == meta.ReplacedByPackageId);
        if (undone is not null) undone.RolledBack = true;
        restored.Save(_layout.InstalledStatePath);
        PackagePaths.DeleteDirectory(discard);
        PackagePaths.DeleteDirectory(_layout.PreviousDir);
        result.Ok = true;
        result.PackageId = restored.PackageId;
        result.Version = restored.Version;
        result.Components = meta.Components.Keys.ToList();
        result.Message = $"已回復到 {restored.Version ?? "(初始安裝)"};撤回 {meta.ReplacedByVersion}";
        audit.Write(AuditEvents.UpdateRolledBack, new { undonePackageId = meta.ReplacedByPackageId, undoneVersion = meta.ReplacedByVersion, restoredVersion = restored.Version, components = result.Components, operatorName });
        return result;
    }

    private static string? MaxVersion(string? a, string? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return PackageVersion.Compare(a, b) >= 0 ? a : b;
    }

    /// <summary>目錄名:只留英數字與 - _ .,且不得為空或全是點(同 RecordStore)。</summary>
    private static string SafeName(string id)
    {
        var cleaned = new string(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        if (cleaned.Length == 0 || cleaned.All(c => c == '.')) throw new InvalidDataException($"不合法的識別字:{id}");
        return cleaned;
    }

    private static void TryDelete(string dir)
    {
        try { PackagePaths.DeleteDirectory(dir); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
