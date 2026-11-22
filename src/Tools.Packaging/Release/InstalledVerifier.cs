using SimosaBRM.Packaging.Common;
using SimosaBRM.Packaging.Crypto;
using SimosaBRM.Packaging.Updates;

namespace SimosaBRM.Packaging.Release;

/// <summary>安裝模式核對的項目分類(<see cref="InstallFinding.Category"/>)。</summary>
public static class InstallFindingCategories
{
    // ── 錯誤(結束碼 1)──
    /// <summary>發行清單讀不到、格式錯、路徑不合法</summary>
    public const string Manifest = "manifest";
    /// <summary>清單(或覆蓋清單)列出的程式/資料檔不在</summary>
    public const string Missing = "missing";
    /// <summary>大小或 SHA-256 不符</summary>
    public const string Mismatch = "mismatch";
    /// <summary>讀取失敗(磁碟錯誤、權限)</summary>
    public const string Unreadable = "unreadable";
    /// <summary>程式目錄(app/、tools/updater/)內多出清單與覆蓋清單都沒有的檔案,或有目錄連結(其下內容無法核對;路徑以 '/' 結尾)</summary>
    public const string Extra = "extra";

    // ── 提醒(結束碼 0)──
    /// <summary>清單內 config/ 之下的檔案與清單不同(使用者依本機修改)</summary>
    public const string ConfigModified = "configModified";
    /// <summary>清單內 config/ 之下的檔案不在</summary>
    public const string ConfigMissing = "configMissing";
    /// <summary>執行期資料位置以外、也不在程式目錄內的未列檔案,或目錄連結(不跟進;路徑以 '/' 結尾)</summary>
    public const string Unlisted = "unlisted";
    /// <summary>無法核對的前綴(舊安裝沒有覆蓋清單、覆蓋清單與 installed.json 不一致)</summary>
    public const string Unverifiable = "unverifiable";
    /// <summary>狀態檔讀不到、重新安裝過發行版等說明</summary>
    public const string State = "state";

    public static readonly string[] ErrorOrder = { Manifest, Missing, Mismatch, Unreadable, Extra };
    public static readonly string[] NoticeOrder = { State, Unverifiable, ConfigModified, ConfigMissing, Unlisted };

    public static string Label(string category) => category switch
    {
        Manifest => "發行清單有問題",
        Missing => "檔案缺少",
        Mismatch => "內容不符(大小或 SHA-256)",
        Unreadable => "無法讀取",
        Extra => "程式目錄內多出的檔案(發行清單與更新包都沒有)或目錄連結",
        ConfigModified => "設定檔已依本機修改(正常)",
        ConfigMissing => "設定檔不在(本機用不到可忽略;需要時自發行版的 config\\ 複製,安裝程式不會覆寫既有設定)",
        Unlisted => "清單沒有列的檔案或目錄連結(不在程式目錄內)",
        Unverifiable => "無法核對(不算錯誤)",
        State => "狀態",
        _ => category,
    };
}

public sealed class InstallFinding
{
    public string Category { get; set; } = "";
    /// <summary>相對安裝根目錄的路徑('/' 分隔;無法核對的項目為前綴)</summary>
    public string Path { get; set; } = "";
    public string? Detail { get; set; }
    /// <summary>預期內容的來源:release(發行清單)或 package:&lt;版本&gt;(更新包覆蓋清單)</summary>
    public string? Source { get; set; }
    /// <summary>無法核對:該前綴之下未核對的檔案數</summary>
    public int? Count { get; set; }
    /// <summary>
    /// 只能以什麼修復(目前只有 <see cref="RepairByPackage"/>):更新包新增、發行清單沒有、又不在程式目錄的檔案(例如更新包放進 data\ 的圖資)
    /// 缺少、不符或讀不出來 — 安裝程式沒有這些檔案、也不清除 data\,重新安裝修不好,要含這些檔案的更新包。其他錯誤為 null。
    /// </summary>
    public string? RepairableBy { get; set; }

    public const string RepairByPackage = "package";
}

/// <summary>
/// 降版(安裝模式核對 --json 的 <c>downgrade</c>、<c>simosa-pkg status --json</c> 的 <c>releaseDowngrade</c>):套用更新包之後重新安裝過,
/// 而目前的發行版比先前安裝的發行版、曾套用的軟體更新包或仍在套用狀態的更新包最高版本舊(<c>InstalledVerifier.DetectDowngrade</c>,
/// 依覆蓋清單保存的證據 <see cref="DowngradeRecord"/>;之後套用非軟體更新包也保留,直到套用新的軟體更新包或以夠新的安裝程式重新安裝)。
/// 不是錯誤:以較舊版本的安裝程式回到舊版是 rollback 拒絕訊息建議的正規作法;只是 status 的版次與實際執行的程式不符,一定要明說。
/// </summary>
public sealed class ReleaseDowngrade
{
    /// <summary>目前 manifest.json 的版本</summary>
    public string ReleaseVersion { get; set; } = "";
    /// <summary>比較基準:下列三者中比目前發行版新者的最大者(「版本不低於它的安裝程式」才回到降版之前)</summary>
    public string BasisVersion { get; set; } = "";
    /// <summary>重新安裝之前的發行版(覆蓋清單的 baseline 或 previous.json 的 release;沒有時 null)</summary>
    public string? PreviousReleaseVersion { get; set; }
    /// <summary>重新安裝當時 installed.json 的 software 元件版次(status 顯示的軟體版次;沒有時 null)</summary>
    public string? SoftwareVersion { get; set; }
    /// <summary>
    /// 重新安裝之前仍在套用狀態(未 rollback)的更新包中的最高版本(<see cref="DowngradeRecord.HighestAppliedVersion"/>;
    /// 沒有套用中的更新包時才以 installed.json 的 highestVersion 補位)
    /// </summary>
    public string? HighestVersion { get; set; }
    /// <summary>software 元件版次比目前的發行版新(而 installed.json 的 software 元件仍是那個更新包):status 顯示的軟體版次不是實際執行的程式</summary>
    public bool SoftwareStale { get; set; }
    /// <summary>給人看的說明(核對的「狀態」提醒與 status 的警告用同一段文字)</summary>
    public string Message { get; set; } = "";
}

public sealed class InstallCheckRelease
{
    public string Version { get; set; } = "";
    public string? GitRevision { get; set; }
    public string? Runtime { get; set; }
    public DateTime BuiltUtc { get; set; }
    public int Files { get; set; }
}

public sealed class InstallCheckInstalled
{
    /// <summary>現行更新包版本(null:初始安裝,尚未套用更新包)</summary>
    public string? Version { get; set; }
    public string? PackageId { get; set; }
    public string? Kind { get; set; }
}

public sealed class InstallCheckOverlay
{
    /// <summary>state/installed-files.json 是否存在且可讀</summary>
    public bool Present { get; set; }
    /// <summary>與 state/installed.json 一致(現行更新包相同;沒套用過更新包且沒有覆蓋清單也算一致)</summary>
    public bool Consistent { get; set; }
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    public int Files { get; set; }
    public int Removed { get; set; }
    /// <summary>覆蓋清單寫入之後又重新安裝過發行版(manifest.json 換過)</summary>
    public bool Reinstalled { get; set; }
    /// <summary>
    /// 套用沒有完成的更新包(覆蓋清單已寫、installed.json 完全沒有它的紀錄:在最後一步之前中斷);
    /// 以同一個更新包重新套用即可恢復一致。沒有這種情況時 null。
    /// </summary>
    public string? InterruptedPackageId { get; set; }
    public string? InterruptedVersion { get; set; }
}

/// <summary>安裝模式核對結果(<c>simosa-pkg release verify --installed --json</c> 的輸出;format simosa-install-check/1)。</summary>
public sealed class InstallCheckReport
{
    public const string FormatId = "simosa-install-check/1";

    public string Format { get; set; } = FormatId;
    public string Mode { get; set; } = "installed";
    public bool Ok => Errors.Count == 0;
    public string Root { get; set; } = "";
    public InstallCheckRelease? Release { get; set; }
    public InstallCheckInstalled? Installed { get; set; }
    public InstallCheckOverlay? InstalledFiles { get; set; }
    /// <summary>降版(套用更新包之後以較舊的安裝程式重新安裝過;不算錯誤,另有「狀態」提醒);沒有時 null</summary>
    public ReleaseDowngrade? Downgrade { get; set; }
    /// <summary>已比對大小與 SHA-256 的檔案數</summary>
    public int Checked { get; set; }
    /// <summary>實際存在、未列入核對的執行期資料位置</summary>
    public List<string> SkippedLocations { get; set; } = new();
    public List<InstallFinding> Errors { get; set; } = new();
    public List<InstallFinding> Notices { get; set; } = new();
    /// <summary>分類 → 筆數</summary>
    public Dictionary<string, int> Counts { get; set; } = new();
    /// <summary>有錯誤時給船上人員的下一步</summary>
    public List<string> NextSteps { get; set; } = new();
}

public sealed class InstalledVerifyOptions
{
    /// <summary>檔名是否不分大小寫(null:依作業系統,Windows 不分)</summary>
    public bool? IgnoreCase { get; init; }
}

/// <summary>
/// 安裝模式核對(規劃書第 5.5 節、R13;docs/design/packaging-sync.md 第 5.1 節):在「使用中的安裝根目錄」核對程式與資料檔的完整性。
/// 船上筆電離線、程式可能放在隨身碟,檔案損毀是實際風險;<see cref="ReleaseManifestBuilder.Verify"/> 把執行期資料、本機設定與更新包換過的檔案
/// 都列為錯誤,只適用剛複製好的發行版。本類別的預期內容 = 發行清單 manifest.json 疊上更新包覆蓋清單 state/installed-files.json
/// (<see cref="InstalledFiles"/>),結果分三級:
/// <list type="bullet">
/// <item><b>錯誤</b>:清單所列的程式/資料檔缺少、大小或 SHA-256 不符、讀不出來;程式目錄(<see cref="InstalledPaths.ProgramPrefixes"/>)內多出檔案
///   或有目錄連結(其下內容無法核對);清單讀不到。</item>
/// <item><b>提醒</b>:config/ 之下的設定檔已修改或不在;其他位置的未列檔案與目錄連結;無法核對的前綴;狀態說明
///   (重新安裝過、降版 — 以較舊的安裝程式回到舊版是允許的作法,所以降版不是錯誤,但一定列出)。</item>
/// <item><b>略過</b>:執行期資料位置(<see cref="InstalledPaths.RuntimeData"/>)之下清單沒有列的檔案(不列出、不進入這些目錄)。</item>
/// </list>
/// 設定檔「不在」列為提醒而不是錯誤:config/ 是使用者的檔案(安裝程式對既有設定不覆寫、解除安裝不刪),船上筆電用不到 instructor-laptop.json、
/// 教官筆電用不到 ship-laptop.json,本工具無從判斷哪一個是必要的;真的缺了必要設定時啟動腳本會明確報錯,補救也不是重新安裝程式檔。
/// 信任公鑰(config/trusted-keys/)同屬 config/:trust 更新包寫入的公鑰若與覆蓋清單不同只列提醒(可由管理者以 trust add/remove 手動變更,
/// 現況以 simosa-pkg status / trust list 為準)。
/// </summary>
public static class InstalledVerifier
{
    /// <summary>安裝模式另外略過的檔名(Windows 檔案總管在資料夾內建立的設定檔;一般模式的略過清單不變)。</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini" };

    /// <summary>目前安裝根目錄的發行清單身分(<see cref="InstalledBaseline.Of"/>)與所列路徑;沒有清單或讀不到時回傳 null。</summary>
    public static InstalledBaseline? ReadBaseline(string root, out List<string>? manifestPaths) => ReadBaseline(root, out manifestPaths, out _);

    /// <summary>同上;<paramref name="unreadable"/> 區分「manifest.json 不在」(false)與「在、但讀不到或格式錯」(true)。</summary>
    public static InstalledBaseline? ReadBaseline(string root, out List<string>? manifestPaths, out bool unreadable)
    {
        manifestPaths = null;
        unreadable = false;
        var path = Path.Combine(root, ReleaseManifest.FileName);
        try
        {
            if (!File.Exists(path)) return null;
            var m = ReleaseManifestBuilder.Load(root);
            var paths = new List<string>();
            foreach (var f in m.Files ?? new())
            {
                try { paths.Add(PackagePaths.Normalize(f?.Path ?? "")); }
                catch (InvalidDataException) { }
            }
            var baseline = InstalledBaseline.Of(m, Sha256Hex.OfFile(path));
            manifestPaths = paths;
            return baseline;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            unreadable = true;
            return null;
        }
    }

    /// <summary>
    /// 目錄看起來是否為使用中的安裝根目錄(有 state\、build\records、build\logs 或 config\packaging.json);回傳看到的跡象,否則 null。
    /// 一般模式的 release verify 據以提示改用 --installed(不改結束碼)。
    /// </summary>
    public static string? LooksInstalled(string dir)
    {
        if (Directory.Exists(Path.Combine(dir, "state"))) return "state\\";
        if (Directory.Exists(Path.Combine(dir, "build", "records"))) return "build\\records";
        if (Directory.Exists(Path.Combine(dir, "build", "logs"))) return "build\\logs";
        if (File.Exists(Path.Combine(dir, "config", "packaging.json"))) return "config\\packaging.json";
        return null;
    }

    private sealed record Expected(string Path, string Sha256, long Size, string? PackageVersion, ReleaseFile? Baseline);

    private sealed class Gap
    {
        public string Prefix = "";
        public readonly List<TrackedPackage> Packages = new();
        public int Count;
    }

    public static InstallCheckReport Verify(string root, InstalledVerifyOptions? options = null)
    {
        root = Path.GetFullPath(root);
        var cmp = (options?.IgnoreCase ?? OperatingSystem.IsWindows()) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = StringComparer.FromComparison(cmp);
        var rep = new InstallCheckReport { Root = root };
        void Error(string category, string path, string? detail = null, string? source = null, string? repairableBy = null)
            => rep.Errors.Add(new InstallFinding { Category = category, Path = path, Detail = detail, Source = source, RepairableBy = repairableBy });
        void Notice(string category, string path, string? detail = null, int? count = null)
            => rep.Notices.Add(new InstallFinding { Category = category, Path = path, Detail = detail, Count = count });

        // 1. 發行清單
        ReleaseManifest m;
        string manifestSha;
        try
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到目錄:{root}");
            m = ReleaseManifestBuilder.Load(root);
            manifestSha = Sha256Hex.OfFile(Path.Combine(root, ReleaseManifest.FileName));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Error(InstallFindingCategories.Manifest, ReleaseManifest.FileName, ex.Message);
            Finish(rep, null);
            return rep;
        }
        m.Files ??= new();
        rep.Release = new InstallCheckRelease { Version = m.Version, GitRevision = m.GitRevision, Runtime = m.Runtime, BuiltUtc = m.BuiltUtc, Files = m.Files.Count };
        if (!PackageVersion.TryParse(m.Version, out _)) Error(InstallFindingCategories.Manifest, ReleaseManifest.FileName, $"版本格式不合法:{m.Version}");
        var expected = new Dictionary<string, Expected>(comparer);
        var baselineByPath = new Dictionary<string, ReleaseFile>(comparer);
        foreach (var f in m.Files)
        {
            string norm;
            try { norm = PackagePaths.Normalize(f?.Path ?? ""); }
            catch (InvalidDataException ex)
            {
                Error(InstallFindingCategories.Manifest, f?.Path ?? "(空)", $"清單路徑不合法:{ex.Message}");
                continue;
            }
            if (!expected.TryAdd(norm, new Expected(norm, f!.Sha256, f.Size, null, f))) Error(InstallFindingCategories.Manifest, norm, "清單路徑重複");
            else baselineByPath[norm] = f;
        }
        var manifestPaths = expected.Keys.ToList();

        // 2. 更新包狀態與覆蓋清單(state/installed.json、state/installed-files.json)
        InstalledState? installed = null;
        try
        {
            installed = InstalledState.LoadOrNew(Path.Combine(root, "state", "installed.json"));
            installed.History = (installed.History ?? new()).Where(h => h is not null).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Notice(InstallFindingCategories.State, "state/installed.json", $"無法讀取,無法確認套用過哪些更新包:{ex.Message}");
        }
        var existing = InstalledFiles.TryLoad(Path.Combine(root, "state", InstalledFiles.FileName), out var overlayError);
        if (overlayError is not null) Notice(InstallFindingCategories.State, $"state/{InstalledFiles.FileName}", $"覆蓋清單無法讀取,視同遺失:{overlayError}");
        var info = new ReconcileInfo();
        InstalledFiles overlay;
        if (installed is null)
        {
            // 不知道套用過什麼:更新包能寫入的前綴全部無法核對(覆蓋清單即使存在也無從比對一致性)
            overlay = new InstalledFiles();
            overlay.Untracked.Add(new TrackedPackage { Prefixes = InstalledPaths.UpdatePrefixes.ToList(), Reason = UntrackedReasons.StateUnreadable });
        }
        else
        {
            var baseline = InstalledBaseline.Of(m, manifestSha);
            // 覆蓋清單不在或讀不到時,previous.json(rollback 判斷用的同一份)的發行清單身分也是「之後重新安裝過」的依據
            var lastApply = PreviousMeta.TryLoad(Path.Combine(root, "state", "previous", "previous.json"));
            overlay = InstalledFiles.Reconcile(existing, installed, baseline, manifestPaths, cmp, info, lastApply: lastApply);
            if (overlayError is not null && installed.AppliedPackages().Count == 0)
                overlay.Untracked.Add(new TrackedPackage { Prefixes = InstalledPaths.UpdatePrefixes.ToList(), Reason = UntrackedReasons.NotRecorded });
        }
        rep.Installed = installed is null ? null : new InstallCheckInstalled { Version = installed.Version, PackageId = installed.PackageId, Kind = installed.Kind };
        rep.InstalledFiles = new InstallCheckOverlay
        {
            Present = existing is not null, PackageId = existing?.PackageId, Version = existing?.Version,
            Files = existing?.Files.Count ?? 0, Removed = existing?.Removed.Count ?? 0, Reinstalled = info.Rebased,
            Consistent = installed is not null && overlayError is null && !info.Diverged && !info.Missing,
            InterruptedPackageId = info.InterruptedPackage?.PackageId, InterruptedVersion = info.InterruptedPackage?.Version,
        };
        // 降版(以較舊的安裝程式重新安裝):不是錯誤(rollback 拒絕訊息建議的正規作法就是以舊版安裝程式回到舊版),但一定要明說。
        // 依覆蓋清單保存的證據判斷(Reconcile 在看得到重新安裝時記下,之後套用非軟體更新包也保留),與 status 同一個判斷
        if (installed is not null && DetectDowngrade(m.Version, overlay, installed) is { } downgrade)
        {
            rep.Downgrade = downgrade;
            Notice(InstallFindingCategories.State, ReleaseManifest.FileName, downgrade.Message);
        }
        // state\previous 還在、而重新安裝過:rollback 會不會被拒絕,以 UpdateApplier 的實際判斷為準(單一出處)
        var rollbackNote = info.Rebased || info.SupersededPackages.Count > 0 ? RollbackNote(root) : "";
        if (info.SupersededPackages.Count > 0)
        {
            var packages = string.Join("、", info.SupersededPackages.Select(p => $"{p.Version}({p.Kind ?? "?"})").Distinct());
            const string superseded = "清單所列檔案以發行清單核對(這些更新包的變更已被安裝覆蓋,simosa-pkg status 顯示的版次或參數集可能與實際檔案不符,請訪船船長確認)";
            Notice(InstallFindingCategories.State, "state/installed.json",
                info.ReinstalledSinceLastApply
                    // 覆蓋清單不在或讀不到,但 previous.json 記錄的套用當時發行清單與目前不同:套用之後重新安裝過
                    ? $"更新包 {packages} 套用之後重新安裝過發行版(state/previous/previous.json 記錄套用時的發行版 {info.LastApplyRelease?.Version ?? "(當時沒有或讀不到發行清單)"},"
                      + $"目前 {m.Version};覆蓋清單 state/{InstalledFiles.FileName} 不在或讀不到):{superseded}{rollbackNote}"
                    // 舊版 simosa-pkg 套用過更新包、之後才安裝了目前的發行版(會寫覆蓋清單的 simosa-pkg 隨這次安裝而來):不是「無法核對」
                    : $"更新包 {packages} 是在目前的發行版(建置於 {m.BuiltUtc:yyyy-MM-dd HH:mm}Z)之前套用的,之後才安裝了這個發行版:{superseded}"
                      + (info.Rebased ? "" : rollbackNote));
        }
        if (info.BaselineAdopted)
        {
            Notice(InstallFindingCategories.State, ReleaseManifest.FileName,
                $"覆蓋清單最後一次寫入(套用更新包 {overlay.Version ?? "?"})時發行清單 manifest.json 讀不到,沒有記錄當時的發行版;目前的發行版 {m.Version} 建置於那次套用之前,"
                + "視為同一個發行版(沒有重新安裝),更新包換過的檔案照覆蓋清單核對。下一次套用更新包時會記下目前的發行清單");
        }
        if (info.Rebased)
        {
            // 重新安裝後:清單所列路徑與整個程式目錄只認新的發行清單(安裝程式的 [InstallDelete] 先清空 app\ 與 tools\updater\;
            // 以壓縮檔或手動複製覆蓋而留下的舊檔會在走訪時列為「多出的檔案」);data\ 等其他位置由更新包新增的檔案仍以覆蓋清單核對
            var undone = new List<string>();
            if (info.SupersededFiles > 0) undone.Add($"更新包對 {info.SupersededFiles} 個檔案的變更已被安裝覆蓋");
            if (info.DroppedProgramFiles > 0) undone.Add($"更新包在程式目錄新增的 {info.DroppedProgramFiles} 個檔案不再列入預期(安裝程式已清除;手動覆蓋而留下者列為多出的檔案)");
            Notice(InstallFindingCategories.State, ReleaseManifest.FileName,
                (info.PreviousBaseline is { } prev
                    ? $"發行版在最後一次套用更新包之後重新安裝過(覆蓋清單依據的發行清單版本 {prev.Version ?? "?"},目前 {m.Version})"
                    : $"覆蓋清單寫入時安裝根目錄沒有可讀的發行清單,之後才安裝了發行版 {m.Version}")
                + ":清單所列檔案與程式目錄(app\\、tools\\updater\\)改以目前的發行清單核對"
                + (undone.Count > 0 ? $";{string.Join(";", undone)}。simosa-pkg status 顯示的版次或參數集可能與實際檔案不符,請訪船船長確認(必要時重新派送更新包)" : "")
                + rollbackNote);
        }

        // 預期內容 = 發行清單 − 更新包刪除的路徑 + 更新包寫入的檔案
        foreach (var r in overlay.Removed)
            foreach (var key in expected.Keys.Where(k => InstalledPaths.IsAtOrUnder(k, r.Path, cmp)).ToList()) expected.Remove(key);
        foreach (var f in overlay.Files)
        {
            expected[f.Path] = new Expected(f.Path, f.Sha256, f.Size, f.Version, baselineByPath.GetValueOrDefault(f.Path));
        }

        // 無法核對的前綴:其下只核對覆蓋清單有紀錄的檔案,其餘(清單所列與未列的)都不比對、只計數
        var gaps = new List<Gap>();
        foreach (var u in overlay.Untracked)
        {
            foreach (var prefix in u.Prefixes)
            {
                var g = gaps.FirstOrDefault(x => x.Prefix.Equals(prefix, cmp));
                if (g is null) gaps.Add(g = new Gap { Prefix = prefix });
                g.Packages.Add(u);
            }
        }
        Gap? GapOf(string rel) => gaps.FirstOrDefault(g => InstalledPaths.IsAtOrUnder(rel, g.Prefix, cmp));

        // packaging.json 自訂的紀錄/紀錄庫目錄(位於安裝根目錄內時)也是執行期資料
        var extraRuntime = new List<InstalledPathRule>();
        try
        {
            var cfg = PackagingConfig.LoadOrDefault(Path.Combine(root, "config", "packaging.json"));
            AddCustomRuntime(extraRuntime, root, cfg.RecordsDir, "練習紀錄(packaging.json recordsDir)", cmp);
            AddCustomRuntime(extraRuntime, root, cfg.Store, "紀錄庫(packaging.json store)", cmp);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Notice(InstallFindingCategories.State, "config/packaging.json", $"無法讀取:{ex.Message}");
        }

        // 3. 走訪安裝根目錄:找出清單沒有列的檔案(執行期資料位置整個不進入)
        var present = new Dictionary<string, string>(comparer);
        var skipped = new List<string>();
        void Skip(InstalledPathRule rule)
        {
            if (!skipped.Contains(rule.Path)) skipped.Add(rule.Path);
        }
        void Walk(string dir, string relDir)
        {
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var matters = InstalledPaths.IsProgram(relDir, cmp) || expected.Keys.Any(k => k.StartsWith(relDir, cmp));
                if (matters) Error(InstallFindingCategories.Unreadable, relDir, $"無法讀取目錄:{ex.Message}");
                else Notice(InstallFindingCategories.State, relDir, $"無法讀取目錄:{ex.Message}");
                return;
            }
            Array.Sort(files, StringComparer.Ordinal);
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (ReleaseManifestBuilder.IsSkipped(file) || Noise.Contains(name)) continue;
                var rel = relDir + name;
                if (expected.ContainsKey(rel))
                {
                    present[rel] = file;
                    continue;
                }
                if (InstalledPaths.FindRuntimeData(rel, cmp, extraRuntime) is { } rule) Skip(rule);
                else if (GapOf(rel) is { } gap) gap.Count++;
                else if (InstalledPaths.IsProgram(rel, cmp)) Error(InstallFindingCategories.Extra, rel);
                else Notice(InstallFindingCategories.Unlisted, rel);
            }
            foreach (var sub in dirs)
            {
                var relSub = relDir + Path.GetFileName(sub) + "/";
                if (InstalledPaths.FindRuntimeData(relSub, cmp, extraRuntime) is { } rule)
                {
                    Skip(rule);
                    continue;
                }
                var isLink = false;
                try { isLink = (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (isLink)
                {
                    // 目錄連結(junction/symlink)不跟進(避免走出安裝根目錄或形成迴圈),但一定列出:其下多出的檔案查不到。
                    // 程式目錄內(或其上層,例如 tools\ 本身)→ 錯誤:連結指到的內容會被載入或被網頁伺服送出;其他位置 → 提醒。
                    // 清單所列的檔案之後仍依路徑直接核對(會跟著連結走);執行期資料位置(build\ 等)在上面就略過,不算
                    if (InstalledPaths.IsProgramOrAbove(relSub, cmp))
                        Error(InstallFindingCategories.Extra, relSub, "目錄連結(junction/symlink),其下內容無法核對:程式目錄內不應有連結,請移除連結本身(不是刪除它指向的內容)");
                    else
                        Notice(InstallFindingCategories.Unlisted, relSub, "目錄連結(junction/symlink),不跟進:清單所列的檔案仍依路徑核對,其下未列的檔案沒有檢查");
                    continue;
                }
                Walk(sub, relSub);
            }
        }
        Walk(root, "");
        rep.SkippedLocations = skipped;

        // 4. 逐一比對預期的檔案
        foreach (var (key, e) in expected.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var source = e.PackageVersion is null ? "release" : $"package:{e.PackageVersion}";
            if (e.PackageVersion is null && GapOf(e.Path) is { } gap)
            {
                gap.Count++;
                continue;
            }
            string? actual;
            try
            {
                if (!present.TryGetValue(key, out actual))
                {
                    var full = PackagePaths.Combine(root, e.Path);
                    actual = File.Exists(full) ? full : null;
                }
            }
            catch (InvalidDataException ex)
            {
                Error(InstallFindingCategories.Manifest, e.Path, ex.Message);
                continue;
            }
            var isConfig = InstalledPaths.IsUserConfig(e.Path, cmp);
            var isTrustKey = InstalledPaths.IsAtOrUnder(e.Path, PackagePaths.TrustedKeysPrefix, cmp);
            // 更新包新增、發行清單沒有、又不在程式目錄(安裝程式不清除)的檔案:安裝程式沒有它,重新安裝修不好,只能以含該檔的更新包修復
            var repairableBy = e.PackageVersion is not null && e.Baseline is null && !InstalledPaths.IsProgram(e.Path, cmp) ? InstallFinding.RepairByPackage : null;
            if (actual is null)
            {
                if (isConfig) Notice(InstallFindingCategories.ConfigMissing, e.Path, isTrustKey ? "信任公鑰不在(曾以 trust remove 手動移除?以 simosa-pkg trust list 核對)" : null);
                else Error(InstallFindingCategories.Missing, e.Path, e.PackageVersion is null && ReleaseManifestBuilder.IsExcludedByInstaller(e.Path) ? ExcludedByInstallerDetail : null, source, repairableBy);
                continue;
            }
            try
            {
                var size = new FileInfo(actual).Length;
                string? sha = null;
                string? detail = null;
                if (size != e.Size) detail = $"大小不符(應為 {e.Size},實際 {size})";
                else if (!Sha256Hex.Equal(sha = Sha256Hex.OfFile(actual), e.Sha256)) detail = "SHA-256 不符";
                rep.Checked++;
                if (detail is null) continue;
                if (isConfig)
                {
                    Notice(InstallFindingCategories.ConfigModified, e.Path, isTrustKey ? "信任公鑰與更新包安裝的不同(曾以 trust add/remove 手動變更?以 simosa-pkg trust list 核對)" : null);
                    continue;
                }
                // 更新包寫入的檔案現在卻是發行清單的內容:不是損毀,而是更新包的變更被還原(以較舊的安裝程式覆蓋安裝)
                if (e.PackageVersion is not null && e.Baseline is { } bl && size == bl.Size && Sha256Hex.Equal(sha ?? Sha256Hex.OfFile(actual), bl.Sha256))
                    detail += $";內容與發行清單 {m.Version} 相同 — 更新包 {e.PackageVersion} 的變更被還原(套用更新包之後又以原安裝程式覆蓋安裝?)";
                Error(InstallFindingCategories.Mismatch, e.Path, detail, source, repairableBy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error(InstallFindingCategories.Unreadable, e.Path, ex.Message, source, repairableBy);
            }
        }

        // 5. 無法核對的前綴。覆蓋清單與 installed.json 一致時,前綴之下已沒有未核對的檔案就不再提醒
        //   (之後的更新包已涵蓋全部檔案);清單不在、不一致或狀態讀不到時一律列出
        var alwaysReport = existing is null || info.Diverged || installed is null;
        foreach (var g in gaps)
        {
            if (g.Count == 0 && !alwaysReport) continue;
            Notice(InstallFindingCategories.Unverifiable, g.Prefix, DescribeGap(g, info), g.Count);
        }
        // 目前的發行版之上是否還有套用中的更新包(重新安裝過、或更新包都早於這個發行版的安裝時不算):決定「下一步」建議的安裝程式版本
        var applied = installed?.AppliedPackages().Count ?? 0;
        Finish(rep, installed, cmp, packagesOnRelease: applied > 0 && !info.Rebased && applied > info.SupersededPackages.Count);
        return rep;
    }

    /// <summary>
    /// 降版判斷(核對與 <c>simosa-pkg status</c> 共用;唯一的判斷在 <see cref="DowngradeRecord.Evaluate"/>):讀
    /// <see cref="InstalledFiles.Reconcile"/> 對齊後的覆蓋清單所帶的降版證據 <see cref="InstalledFiles.Downgrade"/> —
    /// 重新安裝當下記下(先前安裝的發行版、installed.json 的 software 元件、仍在套用狀態的更新包最高版本),之後套用非軟體更新包也保留,
    /// 直到套用新的軟體更新包或以版本不低於比較基準的安裝程式重新安裝為止。目前的發行版 <paramref name="releaseVersion"/> 比其中任一項舊即為降版。
    /// 沒有重新安裝的跡象時一律不算(在 2026.11.1 上套用 2026.11.2 的軟體更新包是正常狀態)。
    /// </summary>
    public static ReleaseDowngrade? DetectDowngrade(string? releaseVersion, InstalledFiles reconciled, InstalledState installed)
    {
        if (reconciled.Downgrade is not { } record || record.Evaluate(releaseVersion, installed) is not { } d) return null;
        var parts = new List<string>();
        if (d.PreviousReleaseVersion is { } previous) parts.Add($"先前安裝的發行版 {previous}");
        if (d.SoftwareVersion is { } software) parts.Add($"曾套用的軟體更新包 {software}");
        if (d.HighestAppliedVersion is { } highest && highest != d.SoftwareVersion && highest != d.PreviousReleaseVersion) parts.Add($"曾套用過的更新包最高版本 {highest}");
        var stale = d.SoftwareVersion is not null;
        return new ReleaseDowngrade
        {
            ReleaseVersion = d.ReleaseVersion, BasisVersion = d.BasisVersion, PreviousReleaseVersion = record.PreviousReleaseVersion,
            SoftwareVersion = record.SoftwareVersion, HighestVersion = record.HighestAppliedVersion, SoftwareStale = stale,
            Message = $"降版:目前的發行版 {d.ReleaseVersion} 比{string.Join("、", parts)} 舊 — 套用更新包之後以較舊的安裝程式(或較舊的發行版)重新安裝過。"
                      + (stale ? $"simosa-pkg status 顯示的軟體版次 {d.SoftwareVersion} 不是實際執行的程式(實際執行的是發行版 {d.ReleaseVersion} 的程式)。" : "")
                      + $"這不算錯誤(以較舊版本的安裝程式回到舊版是允許的作法),請訪船船長確認;若不是刻意回到舊版,請以版本不低於 {d.BasisVersion} 的安裝程式重新安裝,"
                      + "或請岸上派送版本更高的軟體更新包。這則提醒會一直保留(之後套用圖資、情境或參數更新包也一樣),"
                      + $"直到套用新的軟體更新包、或以版本不低於 {d.BasisVersion} 的安裝程式重新安裝為止",
        };
    }

    /// <summary>
    /// <c>simosa-pkg status</c> 用:讀取安裝根目錄的 manifest.json、state/installed.json、覆蓋清單與 state/previous/previous.json,
    /// 以與核對相同的規則(<see cref="InstalledFiles.Reconcile"/> → <see cref="DetectDowngrade(string?, InstalledFiles, InstalledState)"/>)
    /// 判斷降版(只讀;不做逐檔雜湊)。<paramref name="releaseVersion"/> 為目前 manifest.json 的版本(沒有或讀不到時 null);讀不到狀態時回傳 null。
    /// </summary>
    public static ReleaseDowngrade? DetectDowngrade(string root, out string? releaseVersion)
    {
        releaseVersion = null;
        try
        {
            var current = ReadBaseline(root, out var paths);
            if (current is null) return null;
            releaseVersion = current.Version;
            var installed = InstalledState.LoadOrNew(Path.Combine(root, "state", "installed.json"));
            installed.History = (installed.History ?? new()).Where(h => h is not null).ToList();
            var existing = InstalledFiles.TryLoad(Path.Combine(root, "state", InstalledFiles.FileName), out _);
            var lastApply = PreviousMeta.TryLoad(Path.Combine(root, "state", "previous", "previous.json"));
            var reconciled = InstalledFiles.Reconcile(existing, installed, current, paths, InstalledPaths.DefaultComparison, lastApply: lastApply);
            return DetectDowngrade(current.Version, reconciled, installed);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 重新安裝過、而 state\previous 還在時附在「狀態」提醒後面的 rollback 說明。是否會被拒絕以 <see cref="UpdateApplier.CheckRollback"/> 的實際結果為準
    /// (單一出處;不另寫一套判斷):因為重新安裝過而會被拒絕時回傳說明,否則空字串(沒有 state\previous、或 rollback 的判斷不是因為重新安裝)。
    /// </summary>
    private static string RollbackNote(string root)
    {
        if (!File.Exists(Path.Combine(root, "state", "previous", "previous.json"))) return "";
        try
        {
            // 不讀 config/packaging.json(rollback 的判斷用不到;核對另行回報它讀不到)
            var check = new UpdateApplier(new InstallLayout(root, new PackagingConfig())).CheckRollback();
            return check.ReinstalledSinceApply
                ? ";重新安裝之前留下的上一版(state\\previous)已不適用,simosa-pkg rollback 會拒絕(請不要執行 rollback;下一次套用更新包之後才又有可回復的上一版)"
                : "";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>發行清單列了安裝程式不安裝的檔案(<see cref="ReleaseManifestBuilder.InstallerExcludedPatterns"/>;舊版 simosa-pkg 產生的清單)而檔案不在時的說明。</summary>
    private const string ExcludedByInstallerDetail = "安裝程式不安裝這類檔案(偵錯符號等),發行清單不該列入:這是發行版建置的問題,不是檔案損毀,不影響執行";

    private static void AddCustomRuntime(List<InstalledPathRule> rules, string root, string? configured, string description, StringComparison cmp)
    {
        if (string.IsNullOrWhiteSpace(configured)) return;
        var full = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(root, configured));
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (rel is "." or "" || rel.StartsWith("../", StringComparison.Ordinal) || rel == ".." || Path.IsPathRooted(rel)) return;   // 根目錄本身或在根目錄之外
        var prefix = rel.TrimEnd('/') + "/";
        // 不讓設定把程式目錄變成「執行期資料」
        if (InstalledPaths.ProgramPrefixes.Any(p => p.StartsWith(prefix, cmp) || prefix.StartsWith(p, cmp))) return;
        rules.Add(new InstalledPathRule(prefix, description));
    }

    private static string DescribeGap(Gap g, ReconcileInfo info)
    {
        static string List(IEnumerable<TrackedPackage> packages)
            => string.Join("、", packages.Where(p => p.Version.Length > 0).Select(p => $"{p.Version}({p.Kind ?? "?"})").Distinct());
        var parts = new List<string>();
        if (g.Packages.Any(p => p.Reason == UntrackedReasons.StateUnreadable))
            parts.Add("state/installed.json 無法讀取,無法確認套用過哪些更新包");
        // 不一致分兩種:這一次核對才發現的(現在式:兩個狀態檔此刻對不上),與先前某次套用時發現、已寫進覆蓋清單的(過去式:現在已一致,
        // 只是當時作廢的紀錄補不回來)。後者不再指示以 status 確認 — 沒有待處理的事,--json 的 installedFiles.consistent 也是 true
        var diverged = g.Packages.Where(p => p.Reason == UntrackedReasons.Diverged).ToList();
        var live = diverged.Where(p => info.NewlyUntracked.Any(n => n.PackageId == p.PackageId && n.Reason == p.Reason)).ToList();
        var past = diverged.Except(live).ToList();
        if (live.Count > 0)
        {
            var text = $"覆蓋清單與 state/installed.json 不一致(更新包 {List(live)};套用或回復可能中途中斷,先以 simosa-pkg status 確認現行版本)";
            if (info.InterruptedPackage is { } ip && live.Any(p => p.PackageId == ip.PackageId))
                text += $";更新包 {ip.Version} 的套用在最後一步中斷、沒有記入 installed.json:以同一個更新包檔重新套用(simosa-pkg apply)即可恢復一致";
            parts.Add(text);
        }
        if (past.Count > 0)
        {
            parts.Add($"先前套用或回復更新包 {List(past)} 時,覆蓋清單曾與 state/installed.json 不一致(當時中途中斷過),其下在那之前的紀錄已作廢"
                      + (info.Diverged ? "" : ";現在兩者已一致"));
        }
        if (g.Packages.Any(p => p.Reason == UntrackedReasons.NotRecorded))
        {
            var list = List(g.Packages.Where(p => p.Reason == UntrackedReasons.NotRecorded));
            parts.Add(list.Length > 0
                ? $"更新包 {list} 套用時沒有留下覆蓋清單(由舊版 simosa-pkg 套用,或 state/{InstalledFiles.FileName} 遺失)"
                : $"state/{InstalledFiles.FileName} 無法讀取");
        }
        var count = g.Count > 0 ? $"其下 {g.Count} 個檔案未核對" : "其下沒有可核對的檔案";
        return $"{string.Join(";", parts)}:{count}";
    }

    /// <summary>兩個版本取較大者(其中一個為 null 或格式不合法時取另一個)。</summary>
    private static string? MaxVersion(string? a, string? b)
    {
        var okA = a is not null && PackageVersion.TryParse(a, out _);
        var okB = b is not null && PackageVersion.TryParse(b, out _);
        if (!okA) return okB ? b : a ?? b;
        if (!okB) return a;
        return PackageVersion.Compare(a!, b!) >= 0 ? a : b;
    }

    /// <summary>
    /// 統計各分類筆數,有錯誤時產生「下一步」。重新安裝的版本基準取<b>發行清單版本與現行更新包版本的較大者</b>:
    /// 套用更新包之後又以更新的安裝程式重新安裝過時,現行更新包版本比發行版舊,照它去找安裝程式會把整個發行版降版。
    /// 例外(<paramref name="packagesOnRelease"/>):目前的發行版之上還有套用中的更新包、而發行版版本不低於現行更新包時,
    /// 以同一個發行版重新安裝會把更新包換過的檔案還原(核對報「被還原」),所以要求版本<b>高於</b>發行版的安裝程式。
    /// 更新包只能寫 app/、data/(與信任清單),而且同一版本不能重複套用、版本須高於曾套用過的最高版本:
    /// 「以更新包修復」那一句要說明須包含受損的檔案,位於其他位置(tools/、docs/…)的檔案只能以安裝程式修復。
    /// 更新包新增、發行版沒有、又不在程式目錄的檔案(<see cref="InstallFinding.RepairableBy"/> = package)另寫一項:安裝程式修不好
    /// (重新安裝後仍會列出,要求的安裝程式版本只會一直往上推),只能以含這些檔案的更新包修復;「重新安裝後仍有錯誤 → 換磁碟」不套用在這類檔案。
    /// 順序:重新安裝那一項、緊接著更新包新增檔案那一項、多出的檔案,「換磁碟」那一句放在最後;互相參照寫明「見下一步(N)」。
    /// </summary>
    private static void Finish(InstallCheckReport rep, InstalledState? installed, StringComparison cmp = StringComparison.Ordinal, bool packagesOnRelease = false)
    {
        foreach (var f in rep.Errors.Concat(rep.Notices))
            rep.Counts[f.Category] = rep.Counts.GetValueOrDefault(f.Category) + 1;
        if (rep.Errors.Count == 0) return;
        bool Has(params string[] categories) => rep.Errors.Any(e => categories.Contains(e.Category));
        if (Has(InstallFindingCategories.Manifest))
        {
            rep.NextSteps.Add(rep.Release is null
                ? "確認 --dir(或 --root)指到安裝根目錄(含 app\\、data\\ 與 manifest.json 的資料夾,例如 D:\\Simosa BRM);發行清單遺失或損毀時,以安裝程式重新安裝到同一位置。"
                : "發行清單 manifest.json 本身有問題:以安裝程式重新安裝到同一位置。");
        }

        const string installerClears = "安裝程式會先清除舊的程式檔(app\\ 與 tools\\updater\\)再安裝;以壓縮檔或手動複製覆蓋發行版時舊檔不會清除,殘留者會列為「程式目錄內多出的檔案」,依清單刪除";
        const string kept = "設定檔、練習紀錄與 state\\ 都會保留。";
        var release = rep.Release?.Version is { } rv && PackageVersion.TryParse(rv, out _) ? rv : null;
        var current = installed?.Version;   // 現行更新包版本(null:沒套用過更新包,或 installed.json 讀不到)
        var basis = MaxVersion(release, current);
        // 「…的安裝程式」:沒套用過更新包 → 原安裝程式;否則依版本基準
        var installer = current is null || basis is null ? null
            : packagesOnRelease && release is not null && basis == release ? $"版本高於 {release} 的安裝程式"
            : $"版本不低於 {basis} 的安裝程式";

        var broken = rep.Errors.Where(e => e.Category is InstallFindingCategories.Missing or InstallFindingCategories.Mismatch or InstallFindingCategories.Unreadable).ToList();
        var damagedAll = broken.Where(e => e.Detail != ExcludedByInstallerDetail).ToList();
        // 更新包新增、發行版沒有的檔案(data\ 等程式目錄以外):安裝程式修不好,另寫一段下一步,也不套用「重新安裝後仍有錯誤 → 換磁碟」
        var packageOnly = damagedAll.Where(e => e.RepairableBy == InstallFinding.RepairByPackage).ToList();
        var damaged = damagedAll.Except(packageOnly).ToList();
        // 順序:重新安裝那一項 → 緊接著更新包新增檔案那一項 → …(多出的檔案)→ 最後才是「仍有錯誤 → 換磁碟」;互相參照一律寫明「見下一步(N)」
        // (文字輸出有兩項以上時才編號,這裡一定有三項以上)
        var packageOnlyNo = rep.NextSteps.Count + (damaged.Count > 0 ? 2 : 1);
        var seePackageOnly = packageOnly.Count > 0 ? $"(標示為更新包新增的檔案見下一步({packageOnlyNo}))" : "";
        string? diskStep = null;
        if (damaged.Count > 0)
        {
            if (installer is null)
            {
                rep.NextSteps.Add("程式或資料檔損壞或不完整,請先不要用於訓練。以原安裝程式(SimosaBRM-Setup-<版本>.exe)或專用隨身碟上的發行版重新安裝到同一位置,再執行本核對"
                                  + $"({installerClears}){seePackageOnly}。{kept}");
            }
            else
            {
                // 更新包能不能修:只能寫 app/、data/;版本須高於曾套用過的最高版本(rollback 後仍不下降),也不該比現行發行版舊
                bool Under(InstallFinding e, string prefix) => InstalledPaths.IsAtOrUnder(e.Path, prefix, cmp);
                var inApp = damaged.Any(e => Under(e, "app/"));
                var inData = damaged.Any(e => Under(e, "data/"));
                var outside = damaged.Count(e => !Under(e, "app/") && !Under(e, "data/"));
                var packageBasis = MaxVersion(basis, installed?.HighestVersion);
                string byPackage;
                if (!inApp && !inData) byPackage = ";上列檔案都不在更新包可寫入的位置(app\\、data\\),只能以安裝程式修復";
                else
                {
                    var example = inApp && inData ? "app\\ 的程式檔要完整的軟體更新包,data\\ 的檔案要含該檔的情境、參數或圖資更新包"
                        : inApp ? "例如完整的軟體更新包會涵蓋 app\\ 的全部程式檔;只含情境或參數的更新包修不好程式檔"
                        : "例如含該檔的情境、參數或圖資更新包;軟體更新包不含 data\\ 的檔案";
                    byPackage = $",或取得「版本高於 {packageBasis}、且包含上列受損檔案的更新包」套用({example}"
                                + (outside > 0 ? $";上列有 {outside} 個檔案不在更新包可寫入的位置(app\\、data\\ 以外),那些只能以安裝程式修復" : "") + ")";
                }
                rep.NextSteps.Add("程式或資料檔損壞或不完整,請先不要用於訓練,並通知訪船船長。"
                                  + $"這台筆電的發行版是 {rep.Release?.Version ?? "?"}、現行更新包是 {current}:請取得「{installer}」重新安裝到同一位置({installerClears})"
                                  + byPackage
                                  + seePackageOnly
                                  // 以較舊的安裝程式重新安裝 = 整個發行版降版:核對只列「降版」的狀態提醒,不是錯誤(以舊版安裝程式回到舊版是允許的作法),所以不能指望核對擋下
                                  + "。已套用過的更新包不能重複套用(版本須遞增);不要用較舊的安裝程式:那會把整個發行版降版"
                                  + $"(核對只以「狀態」提醒指出「降版」,不列為錯誤;提醒保留到套用新的軟體更新包或以夠新的安裝程式重新安裝為止)。{kept}");
            }
            diskStep = "重新安裝(或套用更新包)後仍有錯誤時,磁碟(隨身碟)可能故障,請更換後再安裝"
                       + (packageOnly.Count > 0 ? $"(標示為更新包新增的檔案除外:重新安裝修不好它們,見下一步({packageOnlyNo}))。" : "。");
        }
        else if (broken.Count > packageOnly.Count)
        {
            // 只有「發行清單列了安裝程式不安裝的檔案」:不是損毀,重新安裝也不會好
            rep.NextSteps.Add("上列缺少的檔案是安裝程式不安裝的檔案類型(偵錯符號 *.pdb 等),不影響執行、不需要重新安裝:發行清單多列了這些檔案(發行版建置的問題),請回報開發者取得修正後的發行版。");
        }
        if (packageOnly.Count > 0)
        {
            // 安裝程式沒有這些檔案、也不清除 data\;覆蓋清單在重新安裝後仍保留它們的紀錄(InstalledFiles.Rebase)— 重新安裝之後還是同樣的錯誤,
            // 照「重新安裝」的下一步做只會讓要求的安裝程式版本一直往上推。只能以含這些檔案的更新包修復(版本須高於曾套用過的最高版本)
            var versions = packageOnly.Select(e => e.Source is { } s && s.StartsWith("package:", StringComparison.Ordinal) ? s["package:".Length..] : "?").Distinct().ToList();
            var highest = installed?.HighestVersion ?? current;
            var examples = string.Join("、", packageOnly.Take(3).Select(e => e.Path)) + (packageOnly.Count > 3 ? $" …等 {packageOnly.Count} 個" : "");
            rep.NextSteps.Add((damaged.Count == 0 ? "資料檔損壞或不完整,請先不要用於相關的訓練,並通知訪船船長。" : "")
                              + $"上列標示為更新包 {string.Join("、", versions)} 的 {packageOnly.Count} 個檔案({examples})是更新包新增、發行版沒有的檔案:"
                              + "安裝程式不會修復(發行版沒有這些檔案,也不清除 data\\),重新安裝之後仍會列出(不能據以判斷磁碟故障)。"
                              + $"請取得「包含這些檔案、版本高於 {highest ?? "曾套用過的最高版本(simosa-pkg status 的 highestVersion)"} 的更新包」套用"
                              + "(例如含該檔的情境、參數或圖資更新包;軟體更新包不含 data\\ 的檔案;已套用過的更新包不能重複套用)。");
        }
        if (Has(InstallFindingCategories.Extra))
        {
            rep.NextSteps.Add("程式目錄(app\\、tools\\updater\\)內多出的檔案(舊版殘留、或被放進去的檔案)與目錄連結:確認不是要保留的資料後依上列清單刪除"
                              + "(或移到程式目錄之外;目錄連結只移除連結本身,不要刪它指向的內容),再執行本核對。以安裝程式重新安裝也會清除多出的檔案(安裝前先清空這兩個資料夾"
                              + (installer is null ? "" : $";須用{installer},較舊的會把整個發行版降版")
                              + ";有目錄連結時請先手動移除連結再安裝);以壓縮檔或手動複製覆蓋發行版不會清除舊檔。");
        }
        if (diskStep is not null) rep.NextSteps.Add(diskStep);
    }

    /// <summary>
    /// 文字輸出(船上人員看的):標頭與提醒寫到 <paramref name="stdout"/>,錯誤與下一步寫到 <paramref name="stderr"/>;
    /// 每一類最多列 <paramref name="maxItems"/> 筆(0 = 全部)再給總數,完整清單用 --json。
    /// </summary>
    public static void Write(InstallCheckReport rep, TextWriter stdout, TextWriter stderr, int maxItems = 10)
    {
        if (rep.Release is { } r)
            stdout.WriteLine($"發行清單:版本 {r.Version}{(r.GitRevision is { } g ? $",git {KeyFiles.Short(g)}" : "")},{r.Runtime ?? "(未標示執行階段)"},建置於 {r.BuiltUtc:yyyy-MM-dd HH:mm}Z,{r.Files} 個檔案");
        stdout.WriteLine($"安裝模式核對:{rep.Root}");
        if (rep.Release is not null)
        {
            var o = rep.InstalledFiles;
            var overlayText = o is { Present: true } ? $";覆蓋清單 state/{InstalledFiles.FileName}:更新包寫入 {o.Files} 個檔案、刪除 {o.Removed} 個路徑" : "";
            stdout.WriteLine(rep.Installed?.Version is { } v
                ? $"現行更新包:{v}({rep.Installed.Kind},{rep.Installed.PackageId}){overlayText}"
                : $"現行更新包:{(rep.Installed is null ? "(state/installed.json 無法讀取)" : "(初始安裝,尚未套用更新包)")}{overlayText}");
            stdout.WriteLine($"已核對 {rep.Checked} 個檔案(大小與 SHA-256)"
                + (rep.SkippedLocations.Count > 0 ? $";未列入核對的執行期資料位置:{string.Join("、", rep.SkippedLocations)}" : ""));
        }
        WriteGroup(stdout, $"提醒 {rep.Notices.Count} 項(不影響核對結果):", rep.Notices, InstallFindingCategories.NoticeOrder, maxItems);
        if (rep.Notices.Any(n => n.Category == InstallFindingCategories.Unverifiable))
        {
            stdout.WriteLine("  「無法核對」不是錯誤:之後每套用一個更新包,該包寫入的檔案就會納入核對(例如下一次完整的軟體更新包會涵蓋 app\\ 的程式檔);"
                + "已套用過的更新包不能重複套用(版本須遞增),不需要、也無法為了建立清單而重套。"
                + (rep.InstalledFiles?.InterruptedVersion is { } iv ? $"例外:更新包 {iv} 的套用沒有完成(見上),以同一個更新包檔重新套用即可。" : ""));
        }
        WriteGroup(stderr, $"錯誤 {rep.Errors.Count} 個:", rep.Errors, InstallFindingCategories.ErrorOrder, maxItems);
        var notices = rep.Notices.Count > 0 ? $"{rep.Notices.Count} 項提醒" : "沒有提醒";
        if (rep.Ok) stdout.WriteLine($"核對通過({rep.Checked} 個檔案;{notices})");
        else
        {
            stderr.WriteLine($"核對失敗({rep.Errors.Count} 個錯誤;{notices})");
            for (var i = 0; i < rep.NextSteps.Count; i++) stderr.WriteLine($"下一步{(rep.NextSteps.Count > 1 ? $"({i + 1})" : "")}:{rep.NextSteps[i]}");
        }
    }

    private static void WriteGroup(TextWriter w, string heading, List<InstallFinding> findings, string[] order, int maxItems)
    {
        if (findings.Count == 0) return;
        w.WriteLine(heading);
        foreach (var category in order.Concat(findings.Select(f => f.Category).Where(c => !order.Contains(c)).Distinct()))
        {
            var items = findings.Where(f => f.Category == category).ToList();
            if (items.Count == 0) continue;
            w.WriteLine($"  {InstallFindingCategories.Label(category)}:{items.Count} 筆");
            var shown = maxItems > 0 ? items.Take(maxItems).ToList() : items;
            foreach (var f in shown)
            {
                var source = f.Source is null ? "" : f.Source == "release" ? "(發行清單)" : $"(更新包 {f.Source["package:".Length..]})";
                w.WriteLine($"    - {f.Path}{source}{(f.Detail is { } d ? ":" + d : "")}");
            }
            if (shown.Count < items.Count) w.WriteLine($"    …另有 {items.Count - shown.Count} 筆(共 {items.Count} 筆;--json 列出全部,--max-items 0 全部顯示)");
        }
    }
}
