using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Updates;

/// <summary>
/// 船上筆電 <c>state/installed-files.json</c>(更新包覆蓋清單;規劃書第 5.5 節、R13;docs/design/packaging-sync.md 第 5.1 節):
/// 記錄「更新包安裝的檔案」— 每個被更新包寫入的路徑的 SHA-256、大小與來源更新包,以及被更新包刪除的路徑。
/// 安裝模式核對(<c>simosa-pkg release verify --installed</c>)的預期內容 = 發行清單 manifest.json 疊上本清單:
/// 先扣掉 <see cref="Removed"/>,再以 <see cref="Files"/> 取代/新增。重新安裝發行版之後的調整見 <see cref="Rebase"/>。
/// <para>
/// <b>逐檔覆蓋,不是整個前綴替換</b>:UpdateApplier 對每個頂層元件(app/、data/、config/)是「複製現行 → 刪除 remove → 寫入新檔」再整個目錄改名,
/// 更新包沒有列的現行檔案會保留,所以 software 包也不是把 app/ 整個換掉;本清單因此只記路徑,不記「被整個取代的前綴」。
/// </para>
/// <para>
/// <b>一致性</b>:本清單與 installed.json 在同一個提交點寫入、rollback 時一起還原,<see cref="PackageId"/>/<see cref="Version"/>
/// 必須等於 installed.json 的現行更新包;對不上(套用或回復中途中斷、檔案遺失、舊版 simosa-pkg 套用)時,
/// 對不上的更新包所屬種類的前綴列入 <see cref="Untracked"/>,核對時列為「無法核對」提醒,不會誤報錯誤也不會誤判為全部正常
/// (<see cref="Reconcile"/>;套用與核對共用同一份規則)。
/// </para>
/// 路徑一律是 <see cref="PackagePaths"/> 正規化的相對路徑('/' 分隔)。
/// </summary>
public sealed class InstalledFiles
{
    public const string FormatId = "simosa-installed-files/1";
    public const string FileName = "installed-files.json";
    /// <summary><see cref="Packages"/> 的保留筆數(與 installed.json 的 history 相同)</summary>
    public const int MaxPackages = 50;

    public string Format { get; set; } = FormatId;
    /// <summary>最後寫入本清單的更新包(= installed.json 的 packageId;尚未套用過更新包時不會有本檔)</summary>
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>
    /// 本清單所依據的發行清單(安裝根目錄 manifest.json 的版本與 SHA-256;沒有 manifest.json 時 null)。
    /// 與目前的 manifest.json 不同 = 之後又重新安裝過發行版(安裝程式先清空程式目錄,再覆寫清單所列的每個檔案),見 <see cref="Rebase"/>。
    /// </summary>
    public InstalledBaseline? Baseline { get; set; }
    /// <summary>已納入本清單的更新包(新到舊,最多 <see cref="MaxPackages"/> 筆)</summary>
    public List<TrackedPackage> Packages { get; set; } = new();
    /// <summary>已套用、但內容沒有記進本清單的更新包(本清單建立之前套用,或與 installed.json 對不上):其前綴之下沒有紀錄的路徑無法核對</summary>
    public List<TrackedPackage> Untracked { get; set; } = new();
    /// <summary>更新包寫入的檔案(依路徑排序)</summary>
    public List<InstalledFile> Files { get; set; } = new();
    /// <summary>更新包刪除的路徑(檔案或目錄;依路徑排序)。之後又被更新包寫入的檔案以 <see cref="Files"/> 為準</summary>
    public List<RemovedPath> Removed { get; set; } = new();

    /// <summary>讀取覆蓋清單;檔案不存在回傳 null。無法讀取或格式不符時回傳 null 並在 <paramref name="error"/> 說明(呼叫端視同遺失)。</summary>
    public static InstalledFiles? TryLoad(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return null;
        try
        {
            var f = PackageJson.Load<InstalledFiles>(path, FileName);
            if (f.Format != FormatId) throw new InvalidDataException($"{FileName} 格式不是 {FormatId}:{f.Format}");
            f.Sanitize();
            return f;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 讀入後整理:null 的清單與欄位補成空值、路徑重新正規化(不合法即丟 <see cref="InvalidDataException"/>,整份清單視同損毀)。
    /// 手動編輯或損毀的檔案不得讓套用或核對以未處理例外中止。
    /// </summary>
    private void Sanitize()
    {
        Packages = Clean(Packages);
        Untracked = Clean(Untracked);
        Files = (Files ?? new()).Where(x => x is not null).ToList();
        Removed = (Removed ?? new()).Where(x => x is not null).ToList();
        foreach (var x in Files)
        {
            x.Path = PackagePaths.Normalize(x.Path);
            x.Sha256 ??= "";
            x.PackageId ??= "";
            x.Version ??= "";
        }
        foreach (var x in Removed)
        {
            x.Path = PackagePaths.Normalize(x.Path);
            x.PackageId ??= "";
            x.Version ??= "";
        }
        if (Baseline is { Sha256: null }) Baseline = null;

        static List<TrackedPackage> Clean(List<TrackedPackage>? list)
        {
            var result = (list ?? new()).Where(x => x is not null).ToList();
            foreach (var x in result)
            {
                x.PackageId ??= "";
                x.Version ??= "";
                x.Prefixes = (x.Prefixes ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            }
            return result;
        }
    }

    public void Save(string path)
    {
        Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        Removed.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        PackageJson.SaveAtomic(path, this);
    }

    public InstalledFiles Clone() => PackageJson.FromBytes<InstalledFiles>(PackageJson.ToBytes(this), FileName);

    /// <summary>種類 → 該種類更新包可寫入的前綴(不認得的種類:全部更新前綴)。</summary>
    public static List<string> PrefixesOfKind(string? kind)
    {
        var p = kind is null ? Array.Empty<string>() : UpdateKinds.AllowedPrefixes(kind);
        return p.Length > 0 ? p.ToList() : InstalledPaths.UpdatePrefixes.ToList();
    }

    /// <summary>
    /// 把現有的覆蓋清單(可為 null)對齊到目前的 installed.json 與發行清單,回傳「記錄下一個更新包之前」應有的內容(不修改傳入的物件)。依序:
    /// <list type="number">
    /// <item>發行清單換過(覆蓋清單最後一次寫入之後重新安裝了發行版):<see cref="Rebase"/>。先做,因為它只能作廢「清單最後一次寫入之前」的紀錄,
    ///   不能作廢下一項才發現的更新包。</item>
    /// <item>沒有覆蓋清單、installed.json 卻有套用中的更新包(舊版 simosa-pkg 套用,或清單遺失/損毀):這些更新包列入 <see cref="Untracked"/>
    ///   (<see cref="UntrackedReasons.NotRecorded"/>)。</item>
    /// <item>現行更新包與 installed.json 不同(套用或回復中途中斷):兩邊對不上的更新包列入 <see cref="Untracked"/>
    ///   (<see cref="UntrackedReasons.Diverged"/>),並丟棄其前綴之下的紀錄。</item>
    /// </list>
    /// 第 2、3 項的例外:套用時間早於目前發行版建置時間(<see cref="InstalledBaseline.BuiltUtc"/>)的更新包不列入 — 發行版一定是在它之後才安裝的,
    /// 安裝已覆寫清單所列的每個檔案(<see cref="ReconcileInfo.SupersededPackages"/>)。這正是實際的舊安裝:simosa-pkg 放在 tools/updater/,
    /// 不會被軟體更新包替換,船上要有會寫覆蓋清單的版本,唯一途徑就是重新安裝發行版。
    /// <para>
    /// 第 3 項的另一個例外 — <b>套用中斷後以原包重試</b>(<paramref name="applyingPackageId"/>):覆蓋清單的現行更新包正是現在要套用的更新包,
    /// 而 installed.json 完全沒有它的紀錄(連 rolledBack 都沒有)= 上一次套用在「覆蓋清單已寫、installed.json 未寫」之間中斷。
    /// 這時覆蓋清單是可信的:它在全部元件換完之後才寫,而且是依同一份 installed.json 對齊過的(其間若套用或回復過別的更新包,
    /// 清單的現行更新包就不會還是它)。因此不列入 <see cref="Untracked"/>、不丟棄紀錄,接著的 <see cref="Record"/> 以相同內容覆寫
    /// (<see cref="ReconcileInfo.Retried"/>)。回復時清單還原失敗的情況不同:installed.json 有該包標為 rolledBack 的紀錄,照第 3 項處理。
    /// </para>
    /// </summary>
    /// <param name="baseline">目前 manifest.json 的身分(沒有或讀不到時 null:不做第 1 項與上述例外)</param>
    /// <param name="manifestPaths">目前發行清單所列的路徑(第 1 項用)</param>
    /// <param name="applyingPackageId">正在套用的更新包(UpdateApplier 傳入;核對時為 null)</param>
    public static InstalledFiles Reconcile(InstalledFiles? existing, InstalledState installed, InstalledBaseline? baseline,
                                           IReadOnlyCollection<string>? manifestPaths, StringComparison comparison, ReconcileInfo? info = null,
                                           string? applyingPackageId = null)
    {
        info ??= new ReconcileInfo();
        // installed.json 是否有這個更新包的任何紀錄(現行,或 history 中的任一筆,含已 rollback 者)
        bool Known(string id) => installed.PackageId == id || (installed.History ?? new()).Any(h => h is not null && h.PackageId == id);
        var applied = installed.AppliedPackages();
        var candidates = new List<TrackedPackage>();   // 內容沒有(可信的)紀錄的更新包
        InstalledFiles f;
        if (existing is null)
        {
            f = new InstalledFiles { PackageId = installed.PackageId, Version = installed.Version, Baseline = baseline };
            candidates.AddRange(applied.Select(a => TrackedPackage.OfKind(a.PackageId, a.Version, a.Kind, a.AppliedUtc, UntrackedReasons.NotRecorded)));
        }
        else
        {
            f = existing.Clone();
            var known = f.Packages.Concat(f.Untracked).Select(p => p.PackageId).ToHashSet(StringComparer.Ordinal);
            if (baseline is not null && manifestPaths is not null && !Sha256Hex.Equal(f.Baseline?.Sha256, baseline.Sha256))
            {
                info.Rebased = true;
                info.PreviousBaseline = f.Baseline;
                (info.SupersededFiles, info.DroppedProgramFiles) = f.Rebase(manifestPaths, baseline, comparison);
            }
            if (applyingPackageId is not null && f.PackageId == applyingPackageId && !Known(applyingPackageId))
            {
                // 上一次套用同一個更新包時在寫 installed.json 之前中斷:清單可信,原樣沿用(Record 以相同內容覆寫)
                info.Retried = true;
            }
            else if (f.PackageId != installed.PackageId || f.Version != installed.Version)
            {
                info.Diverged = true;
                // 清單的現行更新包在 installed.json 完全沒有紀錄:該包的套用沒有完成,可用原包重新套用(核對時據以提示)
                if (f.PackageId is { } overlayHead && !Known(overlayHead)) info.InterruptedPackage = f.Packages.FirstOrDefault(p => p.PackageId == overlayHead);
                // 覆蓋清單記了、installed.json 沒有(或已標為回復)
                foreach (var p in f.Packages.Where(p => applied.All(a => a.PackageId != p.PackageId)))
                    candidates.Add(new TrackedPackage { PackageId = p.PackageId, Version = p.Version, Kind = p.Kind, AppliedUtc = p.AppliedUtc, Prefixes = PrefixesOfKind(p.Kind), Reason = UntrackedReasons.Diverged });
                // installed.json 有、覆蓋清單不知道
                foreach (var a in applied.Where(a => !known.Contains(a.PackageId)))
                    candidates.Add(TrackedPackage.OfKind(a.PackageId, a.Version, a.Kind, a.AppliedUtc, UntrackedReasons.Diverged));
                if (candidates.Count == 0)
                {
                    // 兩邊的更新包集合相同、只有現行版本對不上:以兩邊的現行更新包為準(沒有套用時間,不適用「早於發行版建置」的例外)
                    if (f.Packages.FirstOrDefault() is { } head) candidates.Add(new TrackedPackage { PackageId = head.PackageId, Version = head.Version, Kind = head.Kind, Prefixes = PrefixesOfKind(head.Kind), Reason = UntrackedReasons.Diverged });
                    if (installed.PackageId is { } id) candidates.Add(TrackedPackage.OfKind(id, installed.Version ?? "", installed.Kind, null, UntrackedReasons.Diverged));
                }
                f.Packages.RemoveAll(p => applied.All(a => a.PackageId != p.PackageId));
                f.PackageId = installed.PackageId;
                f.Version = installed.Version;
            }
        }
        var untracked = new List<TrackedPackage>();
        foreach (var c in candidates)
        {
            if (baseline?.BuiltUtc is { } built && c.AppliedUtc is { } at && at < built) info.SupersededPackages.Add(c);
            else untracked.Add(c);
        }
        f.MarkUntracked(untracked, comparison);
        info.NewlyUntracked.AddRange(untracked);
        info.Missing = existing is null && untracked.Count > 0;
        return f;
    }

    /// <summary>
    /// 發行清單換過(覆蓋清單寫入之後又重新安裝了發行版;以 manifest.json 的身分改變偵測):
    /// <list type="bullet">
    /// <item><b>清單所列路徑</b>改以新的發行清單為準(安裝會覆寫清單所列的每個檔案):丟棄這些路徑的紀錄與涵蓋它們的刪除紀錄。</item>
    /// <item><b>程式目錄</b>(<see cref="InstalledPaths.ProgramPrefixes"/>:app/、tools/updater/)之下、新清單沒有列的紀錄<b>一律作廢</b>:
    ///   安裝程式在安裝前清空程式目錄(tools/installer/SimosaBRM.iss 的 [InstallDelete]),重新安裝後程式目錄與發行版完全相同;
    ///   以壓縮檔或手動複製覆蓋而留下的舊檔(含先前更新包新增的檔案)則應被列為「程式目錄內多出的檔案」,由人依清單刪除 —
    ///   若仍以覆蓋清單為準,前者會誤報「檔案缺少」。</item>
    /// <item><b>其他位置</b>(data/、config/trusted-keys/;安裝程式不清空):更新包新增而清單沒有的檔案紀錄保留,仍以覆蓋清單核對。</item>
    /// <item><see cref="Untracked"/> 清空(先前無法核對的前綴已被安裝覆寫)。</item>
    /// </list>
    /// 回傳(被新清單取代的檔案紀錄數,程式目錄之下因而作廢的檔案紀錄數)。
    /// </summary>
    public (int Superseded, int DroppedProgramFiles) Rebase(IReadOnlyCollection<string> manifestPaths, InstalledBaseline baseline, StringComparison comparison)
    {
        var listed = new HashSet<string>(manifestPaths, StringComparer.FromComparison(comparison));
        var superseded = Files.RemoveAll(x => listed.Contains(x.Path));
        var dropped = Files.RemoveAll(x => InstalledPaths.IsAtOrUnderProgram(x.Path, comparison));
        Removed.RemoveAll(r => InstalledPaths.IsAtOrUnderProgram(r.Path, comparison)
                               || manifestPaths.Any(p => InstalledPaths.IsAtOrUnder(p, r.Path, comparison)));
        Untracked.Clear();
        Baseline = baseline;
        return (superseded, dropped);
    }

    /// <summary>把更新包列為「內容未記錄」,並丟棄其前綴之下的紀錄(已不可信)。</summary>
    public void MarkUntracked(IEnumerable<TrackedPackage> packages, StringComparison comparison)
    {
        foreach (var p in packages)
        {
            if (Untracked.Any(u => u.PackageId == p.PackageId && u.Reason == p.Reason)) continue;
            Untracked.Add(p);
            foreach (var prefix in p.Prefixes)
            {
                Files.RemoveAll(x => InstalledPaths.IsAtOrUnder(x.Path, prefix, comparison));
                Removed.RemoveAll(x => InstalledPaths.IsAtOrUnder(x.Path, prefix, comparison));
            }
        }
    }

    /// <summary>
    /// 記錄一個更新包的套用結果(順序與 UpdateApplier 相同:先刪除 <paramref name="removePaths"/>,再寫入 <paramref name="manifest"/> 的檔案)。
    /// <paramref name="removePaths"/> 為套用時實際刪除的安裝路徑(manifest.remove 加上 trust.remove 推得的金鑰檔)。
    /// </summary>
    public void Record(UpdateManifest manifest, IEnumerable<string> removePaths, DateTime appliedUtc, StringComparison comparison)
    {
        var touched = new List<string>();
        foreach (var raw in removePaths)
        {
            var r = PackagePaths.Normalize(raw);
            touched.Add(r);
            Files.RemoveAll(x => InstalledPaths.IsAtOrUnder(x.Path, r, comparison));
            Removed.RemoveAll(x => InstalledPaths.IsAtOrUnder(x.Path, r, comparison));
            Removed.Add(new RemovedPath { Path = r, PackageId = manifest.PackageId, Version = manifest.Version });
        }
        foreach (var file in manifest.Files)
        {
            var p = PackagePaths.Normalize(file.Path);
            touched.Add(p);
            Files.RemoveAll(x => x.Path.Equals(p, comparison));
            Removed.RemoveAll(x => x.Path.Equals(p, comparison));
            Files.Add(new InstalledFile { Path = p, Sha256 = file.Sha256.ToLowerInvariant(), Size = file.Size, PackageId = manifest.PackageId, Version = manifest.Version });
        }
        var allowed = PrefixesOfKind(manifest.Kind);
        var prefixes = allowed.Where(a => touched.Any(t => InstalledPaths.IsAtOrUnder(t, a, StringComparison.Ordinal))).ToList();
        Packages.RemoveAll(p => p.PackageId == manifest.PackageId);
        Packages.Insert(0, new TrackedPackage { PackageId = manifest.PackageId, Version = manifest.Version, Kind = manifest.Kind, AppliedUtc = appliedUtc, Prefixes = prefixes.Count > 0 ? prefixes : allowed });
        if (Packages.Count > MaxPackages) Packages.RemoveRange(MaxPackages, Packages.Count - MaxPackages);
        PackageId = manifest.PackageId;
        Version = manifest.Version;
        UpdatedUtc = appliedUtc;
    }
}

/// <summary><see cref="InstalledFiles.Reconcile"/> 做了哪些調整(核對時據以顯示提醒)。</summary>
public sealed class ReconcileInfo
{
    /// <summary>沒有覆蓋清單,但 installed.json 有套用中的更新包</summary>
    public bool Missing { get; set; }
    /// <summary>覆蓋清單的現行更新包與 installed.json 不同</summary>
    public bool Diverged { get; set; }
    /// <summary>發行清單換過(重新安裝)</summary>
    public bool Rebased { get; set; }
    public InstalledBaseline? PreviousBaseline { get; set; }
    /// <summary>重新安裝後被發行清單取代的檔案紀錄數</summary>
    public int SupersededFiles { get; set; }
    /// <summary>重新安裝後作廢的「程式目錄之下、新清單沒有列」的檔案紀錄數(更新包在 app/ 新增的檔案:安裝程式已清除,手動覆蓋而留下者列為多出)</summary>
    public int DroppedProgramFiles { get; set; }
    /// <summary>套用中斷後以原包重試:覆蓋清單原樣沿用(只在套用時)</summary>
    public bool Retried { get; set; }
    /// <summary>
    /// 不一致,且覆蓋清單的現行更新包在 installed.json 完全沒有紀錄:該包的套用在寫 installed.json 之前中斷,
    /// 以同一個更新包重新套用即可恢復一致(核對時據以提示)。
    /// </summary>
    public TrackedPackage? InterruptedPackage { get; set; }
    /// <summary>這一次對齊才列入 <see cref="InstalledFiles.Untracked"/> 的更新包(與檔案內原有的紀錄區分:原有的是「先前曾經」對不上)</summary>
    public List<TrackedPackage> NewlyUntracked { get; set; } = new();
    /// <summary>沒有覆蓋清單,且在目前發行版建置之前套用的更新包(已被這次安裝覆蓋,不列為未記錄)</summary>
    public List<TrackedPackage> SupersededPackages { get; set; } = new();
}

/// <summary>發行清單 manifest.json 的身分。</summary>
public sealed class InstalledBaseline
{
    public string? Version { get; set; }
    /// <summary>manifest.json 檔案本身的 SHA-256</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>發行版的建置時間(manifest.json 的 builtUtc)</summary>
    public DateTime? BuiltUtc { get; set; }
}

public static class UntrackedReasons
{
    /// <summary>套用時尚未記錄覆蓋清單(舊版 simosa-pkg),或清單遺失/損毀後重新建立</summary>
    public const string NotRecorded = "notRecorded";
    /// <summary>覆蓋清單與 installed.json 對不上(套用或回復中途中斷)</summary>
    public const string Diverged = "diverged";
    /// <summary>state/installed.json 無法讀取(只在核對結果中出現,不會寫進檔案)</summary>
    public const string StateUnreadable = "stateUnreadable";
}

public sealed class TrackedPackage
{
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Kind { get; set; }
    public DateTime? AppliedUtc { get; set; }
    /// <summary>涉及的前綴(app/、data/scenarios/…;已記錄者依實際路徑,未記錄者依種類)</summary>
    public List<string> Prefixes { get; set; } = new();
    /// <summary><see cref="UntrackedReasons"/>(只用於 untracked)</summary>
    public string? Reason { get; set; }

    public static TrackedPackage OfKind(string packageId, string version, string? kind, DateTime? appliedUtc, string reason)
        => new() { PackageId = packageId, Version = version, Kind = kind, AppliedUtc = appliedUtc, Prefixes = InstalledFiles.PrefixesOfKind(kind), Reason = reason };
}

public sealed class InstalledFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    /// <summary>寫入這個檔案的更新包</summary>
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class RemovedPath
{
    /// <summary>被刪除的安裝路徑(檔案或目錄)</summary>
    public string Path { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
}
