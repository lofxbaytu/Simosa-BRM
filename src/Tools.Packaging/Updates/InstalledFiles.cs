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
    /// 本清單所依據的發行清單(安裝根目錄 manifest.json 的版本、SHA-256、建置時間與內容身分;沒有 manifest.json 時 null)。
    /// 與目前的 manifest.json 不是同一個發行版(<see cref="InstalledBaseline.SameRelease"/>)= 之後又重新安裝過發行版
    /// (安裝程式先清空程式目錄,再覆寫清單所列的每個檔案),見 <see cref="Rebase"/>。
    /// </summary>
    public InstalledBaseline? Baseline { get; set; }
    /// <summary>
    /// 套用時 manifest.json 存在、卻讀不到(損毀或 IO 錯誤),所以 <see cref="Baseline"/> 是 null(與「當時沒有 manifest.json」分開記錄)。
    /// 之後讀得到時不能直接當成重新安裝過:<see cref="Reconcile"/> 改依時間判斷(發行版建置於本清單最後一次寫入之後才算)。
    /// 舊檔沒有這個欄位(false)。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool BaselineUnreadable { get; set; }
    /// <summary>
    /// 降版的證據(審查第二十九輪 P4;<see cref="DowngradeRecord"/>):重新安裝之後目前的發行版比重新安裝之前的狀態舊時由 <see cref="Reconcile"/> 記下,
    /// 之後套用非軟體更新包時原樣保留,直到套用新的軟體更新包(<see cref="Record"/>)或以夠新的發行版重新安裝(<see cref="Reconcile"/>)才清除。
    /// 降版提醒(安裝模式核對與 <c>simosa-pkg status</c>)只依這份紀錄判斷,不再依「這一次看不看得到重新安裝」推斷。舊檔沒有這個欄位(null)。
    /// </summary>
    public DowngradeRecord? Downgrade { get; set; }
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
    /// <item>發行清單換過(覆蓋清單最後一次寫入之後重新安裝了發行版;<see cref="InstalledBaseline.SameRelease"/> 為否,
    ///   覆蓋清單沒有記錄身分而目前讀得到 manifest.json 也算 — 例外:當時 manifest.json 在、只是讀不到(<see cref="BaselineUnreadable"/>),
    ///   而目前的發行版建置於清單最後一次寫入之前,視為同一個發行版,見 <see cref="ReconcileInfo.BaselineAdopted"/>):<see cref="Rebase"/>。先做,因為它只能作廢「清單最後一次寫入之前」的紀錄,
    ///   不能作廢下一項才發現的更新包。同一個發行版、只是記錄的原始 SHA-256 不同(只改編碼)時,改記目前的身分。</item>
    /// <item>沒有覆蓋清單、installed.json 卻有套用中的更新包(舊版 simosa-pkg 套用,或清單遺失/損毀):這些更新包列入 <see cref="Untracked"/>
    ///   (<see cref="UntrackedReasons.NotRecorded"/>)。</item>
    /// <item>現行更新包與 installed.json 不同(套用或回復中途中斷):兩邊對不上的更新包列入 <see cref="Untracked"/>
    ///   (<see cref="UntrackedReasons.Diverged"/>),並丟棄其前綴之下的紀錄。</item>
    /// </list>
    /// 第 2、3 項的例外:套用時間早於目前發行版建置時間(<see cref="InstalledBaseline.BuiltUtc"/>)的更新包不列入 — 發行版一定是在它之後才安裝的,
    /// 安裝已覆寫清單所列的每個檔案(<see cref="ReconcileInfo.SupersededPackages"/>)。這正是實際的舊安裝:simosa-pkg 放在 tools/updater/,
    /// 不會被軟體更新包替換,船上要有會寫覆蓋清單的版本,唯一途徑就是重新安裝發行版。
    /// rollback 在沒有覆蓋清單時也以同一個結果判斷「套用之後重新安裝過」(UpdateApplier.ReinstalledSinceLastApply,不另寫時間規則)。
    /// <para>
    /// <b>時鐘偏差</b>:appliedUtc 取自船上筆電(離線)的時鐘,builtUtc 取自岸上建置機。船上時鐘<b>落後</b>(慢)時,在發行版之後套用的更新包
    /// 可能被當成「早於發行版」:核對改以發行清單比對更新包換過的檔案(多報不符,不會漏報),rollback 被拒絕(誤擋,較安全)。
    /// 船上時鐘<b>超前</b>(快)時,在發行版之前套用的更新包可能被當成「之後套用」:核對列為無法核對(不誤報),rollback 不會因此被擋(漏擋)。
    /// 另外,發行版若在套用更新包之前就已建置、之後才安裝(例如以較舊的安裝程式重新安裝),時間規則本來就看不出來;
    /// 有覆蓋清單或新版 previous.json 時改以發行清單身分判斷,不受時鐘影響。
    /// </para>
    /// <para>
    /// 第 3 項的另一個例外 — <b>套用中斷後以原包重試</b>(<paramref name="applyingPackageId"/>):覆蓋清單的現行更新包正是現在要套用的更新包,
    /// 而 installed.json 完全沒有它的紀錄(連 rolledBack 都沒有)= 上一次套用在「覆蓋清單已寫、installed.json 未寫」之間中斷。
    /// 這時覆蓋清單是可信的:它在全部元件換完之後才寫,而且是依同一份 installed.json 對齊過的(其間若套用或回復過別的更新包,
    /// 清單的現行更新包就不會還是它)。因此不列入 <see cref="Untracked"/>、不丟棄紀錄,接著的 <see cref="Record"/> 以相同內容覆寫
    /// (<see cref="ReconcileInfo.Retried"/>)。回復時清單還原失敗的情況不同:installed.json 有該包標為 rolledBack 的紀錄,照第 3 項處理。
    /// </para>
    /// <para>
    /// <b>覆蓋清單遺失或損毀</b>(<paramref name="existing"/> 為 null)而 <paramref name="lastApply"/>(state/previous/previous.json,
    /// rollback 判斷用的同一份)記錄的發行清單身分顯示「最近一次套用之後重新安裝過」(<see cref="PreviousMeta.ReleaseChangedSince"/>):
    /// 套用中的更新包都在那次重新安裝之前,全部列入 <see cref="ReconcileInfo.SupersededPackages"/>(不依時間規則)。
    /// </para>
    /// <para>
    /// <b>降版的證據</b>(<see cref="Downgrade"/>):這一次看得到重新安裝(第 1 項,或 <see cref="ReconcileInfo.SupersededPackages"/> 非空)時,
    /// 以重新安裝之前的狀態(先前的發行版、被這次重新安裝覆蓋的 software 元件與軟體更新包最高版本;<see cref="ReconcileInfo.OverwrittenPackages"/>)
    /// 比較目前的發行版,比較舊就記下(已有紀錄時合併);沒有重新安裝時原樣保留。紀錄的各項都不比目前的發行版新(以夠新的發行版重新安裝過)時清除。
    /// 套用時寫回檔案,所以之後再套用圖資、情境、參數更新包(覆蓋清單已對齊到目前的發行版,看不到重新安裝)提醒仍在。
    /// 非軟體更新包(圖資、情境、參數)的版本不計入:安裝不會覆蓋的(放進 data/ 的新檔)與會覆蓋的(發行清單所列的參數、情境檔)都一樣,
    /// 後者由「被安裝覆蓋」的 state 提醒說明(審查第二十九輪 P5)。
    /// </para>
    /// </summary>
    /// <param name="baseline">目前 manifest.json 的身分(沒有或讀不到時 null:不做第 1 項與上述例外)</param>
    /// <param name="manifestPaths">目前發行清單所列的路徑(第 1 項用)</param>
    /// <param name="applyingPackageId">正在套用的更新包(UpdateApplier 傳入;核對時為 null)</param>
    /// <param name="lastApply">state/previous/previous.json(沒有或讀不到時 null);只在沒有覆蓋清單時使用</param>
    /// <param name="nowUtc">降版紀錄的記錄時間(UpdateApplier 傳入套用時刻;核對與 status 不寫檔,可省略)</param>
    public static InstalledFiles Reconcile(InstalledFiles? existing, InstalledState installed, InstalledBaseline? baseline,
                                           IReadOnlyCollection<string>? manifestPaths, StringComparison comparison, ReconcileInfo? info = null,
                                           string? applyingPackageId = null, PreviousMeta? lastApply = null, DateTime? nowUtc = null)
    {
        info ??= new ReconcileInfo();
        // installed.json 是否有這個更新包的任何紀錄(現行,或 history 中的任一筆,含已 rollback 者)
        bool Known(string id) => installed.PackageId == id || (installed.History ?? new()).Any(h => h is not null && h.PackageId == id);
        var applied = installed.AppliedPackages();
        var candidates = new List<TrackedPackage>();   // 內容沒有(可信的)紀錄的更新包
        HashSet<string>? rebasedOwners = null;          // Rebase 作廢的紀錄所屬的更新包
        InstalledFiles f;
        if (existing is null)
        {
            f = new InstalledFiles { PackageId = installed.PackageId, Version = installed.Version, Baseline = baseline };
            candidates.AddRange(applied.Select(a => TrackedPackage.OfKind(a.PackageId, a.Version, a.Kind, a.AppliedUtc, UntrackedReasons.NotRecorded)));
            // 覆蓋清單遺失或損毀:previous.json(屬於現行更新包者)記錄的發行清單身分與目前不同 → 最近一次套用之後重新安裝過
            if (baseline is not null && installed.PackageId is { } current && lastApply?.ReplacedByPackageId == current
                && lastApply.ReleaseChangedSince(baseline) == true)
            {
                info.ReinstalledSinceLastApply = true;
                info.LastApplyRelease = lastApply.Release;
            }
        }
        else
        {
            f = existing.Clone();
            var known = f.Packages.Concat(f.Untracked).Select(p => p.PackageId).ToHashSet(StringComparer.Ordinal);
            if (baseline is not null && manifestPaths is not null)
            {
                if (!InstalledBaseline.SameRelease(f.Baseline, baseline))
                {
                    if (f.Baseline is null && f.BaselineUnreadable && !InstalledBaseline.BuiltAfter(baseline, f.UpdatedUtc))
                    {
                        // 套用時 manifest.json 讀不到(不是不在):目前的發行版建置於那次套用之前,沒有重新安裝的跡象 —
                        // 視為同一個發行版,改記目前的身分(不 Rebase;否則更新包寫入的檔案會被當成「被安裝覆蓋」、誤報降版)
                        info.BaselineAdopted = true;
                        f.Baseline = baseline;
                    }
                    else
                    {
                        info.Rebased = true;
                        info.PreviousBaseline = f.Baseline;
                        (info.SupersededFiles, info.DroppedProgramFiles, rebasedOwners) = f.Rebase(manifestPaths, baseline, comparison);
                    }
                }
                // 同一個發行版、但紀錄的原始 SHA-256 不同(manifest.json 只改了編碼)或是沒有內容身分的舊紀錄:換成目前的身分
                // (下一次套用時寫回,之後即以內容身分比較)
                else if (!Sha256Hex.Equal(f.Baseline!.Sha256, baseline.Sha256) || f.Baseline.ContentSha256 is null) f.Baseline = baseline;
                f.BaselineUnreadable = false;
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
            if (info.ReinstalledSinceLastApply || (baseline?.BuiltUtc is { } built && c.AppliedUtc is { } at && at < built)) info.SupersededPackages.Add(c);
            else untracked.Add(c);
        }
        f.MarkUntracked(untracked, comparison);
        info.NewlyUntracked.AddRange(untracked);
        info.Missing = existing is null && untracked.Count > 0;
        var reinstalled = info.Rebased || info.SupersededPackages.Count > 0;
        if (reinstalled) CollectOverwritten(info, existing, installed, applied, rebasedOwners, baseline, manifestPaths, comparison);
        var previousRelease = info.Rebased ? info.PreviousBaseline?.Version : info.LastApplyRelease?.Version;
        f.Downgrade = DowngradeRecord.Observe(f.Downgrade, reinstalled, baseline?.Version, previousRelease, info.OverwrittenPackages, installed, nowUtc ?? DateTime.UtcNow);
        return f;
    }

    /// <summary>
    /// 這次重新安裝真的覆蓋到的更新包(<see cref="ReconcileInfo.OverwrittenPackages"/>;審查第二十九輪 P5,降版的比較基準只取其中的軟體包):
    /// <list type="bullet">
    /// <item>看得到 <see cref="Rebase"/>(有覆蓋清單):作廢的 files / removed 紀錄所屬的更新包(清單所列路徑、程式目錄之下),
    ///   加上套用中的軟體更新包與含 app/ 的 mixed 包(安裝程式清空 app/ 重裝,只刪檔的軟體包也算),與 installed.json 的 software 元件
    ///   (history 只留 50 筆,元件所指的更新包可能已不在其中)。圖資包放進 data/、發行清單沒有的檔案不會被安裝蓋掉,不算。
    ///   例外:這一次才列為無法核對的更新包(<see cref="ReconcileInfo.NewlyUntracked"/>:覆蓋清單不知道、又不早於發行版建置,
    ///   例如舊版 simosa-pkg 在重新安裝之後才套用)不知道是在重新安裝之前還是之後,不算。</item>
    /// <item>被安裝覆蓋而沒有可信紀錄的更新包(<see cref="ReconcileInfo.SupersededPackages"/>;沒有覆蓋清單時的時間規則或 previous.json):
    ///   只算軟體包、含 app/ 的 mixed 包,與前綴和發行清單所列路徑相交的種類(例如參數包,發行清單列了 data/parameters/ 的檔案);
    ///   software 元件的更新包不在 history 時,依同一個依據(previous.json 顯示重新安裝過,或元件的套用時間早於發行版建置時間)判斷。</item>
    /// </list>
    /// 「軟體包」:種類 software,或 mixed 而觸及 app/(覆蓋清單記錄的前綴,或 installed.json 的 software 元件就是它)。
    /// </summary>
    private static void CollectOverwritten(ReconcileInfo info, InstalledFiles? existing, InstalledState installed, List<AppliedPackage> applied,
                                           HashSet<string>? rebasedOwners, InstalledBaseline? baseline,
                                           IReadOnlyCollection<string>? manifestPaths, StringComparison comparison)
    {
        var component = installed.Components?.GetValueOrDefault(UpdateKinds.Software);
        // 覆蓋清單(對齊之前)記錄的各更新包實際觸及的前綴
        var trackedPrefixes = (existing?.Packages ?? new()).GroupBy(p => p.PackageId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Prefixes, StringComparer.Ordinal);
        var unknown = info.NewlyUntracked.Select(p => p.PackageId).ToHashSet(StringComparer.Ordinal);
        bool IsSoftware(string id, string? kind)
            => kind == UpdateKinds.Software
               || (kind == UpdateKinds.Mixed && ((component is not null && component.PackageId == id)
                                                 || (trackedPrefixes.TryGetValue(id, out var prefixes) && prefixes.Contains("app/"))));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id, string version, string? kind, bool software)
        {
            if (id.Length > 0 && seen.Add(id)) info.OverwrittenPackages.Add(new OverwrittenPackage(id, version, kind, software));
        }
        if (info.Rebased)
        {
            foreach (var a in applied)
            {
                var software = IsSoftware(a.PackageId, a.Kind);
                if ((software && !unknown.Contains(a.PackageId)) || rebasedOwners?.Contains(a.PackageId) == true) Add(a.PackageId, a.Version, a.Kind, software);
            }
            if (component is not null && !unknown.Contains(component.PackageId)) Add(component.PackageId, component.Version, UpdateKinds.Software, software: true);
        }
        foreach (var c in info.SupersededPackages)
        {
            // 只算 installed.json 仍在套用中(未 rollback)的更新包:Diverged 時候選可能含覆蓋清單記了、installed.json 已標為回復的包
            //(rollback 時「還原覆蓋清單」失敗的情況),已撤回的版本不能當降版的比較基準(第二十九輪第五次審查)
            if (applied.All(a => a.PackageId != c.PackageId)) continue;
            var software = IsSoftware(c.PackageId, c.Kind);
            var listed = manifestPaths is not null
                         && PrefixesOfKind(c.Kind).Any(prefix => manifestPaths.Any(p => InstalledPaths.IsAtOrUnder(p, prefix, comparison)));
            if (software || listed) Add(c.PackageId, c.Version, c.Kind, software);
        }
        if (!info.Rebased && component is not null && applied.All(a => a.PackageId != component.PackageId)
            && (info.ReinstalledSinceLastApply || (baseline?.BuiltUtc is { } built && component.AppliedUtc < built)))
            Add(component.PackageId, component.Version, UpdateKinds.Software, software: true);
    }

    /// <summary>
    /// 發行清單換過(覆蓋清單寫入之後又重新安裝了發行版;以 manifest.json 的身分改變偵測,<see cref="InstalledBaseline.SameRelease"/>):
    /// <list type="bullet">
    /// <item><b>清單所列路徑</b>改以新的發行清單為準(安裝會覆寫清單所列的每個檔案):丟棄這些路徑的紀錄與涵蓋它們的刪除紀錄。</item>
    /// <item><b>程式目錄</b>(<see cref="InstalledPaths.ProgramPrefixes"/>:app/、tools/updater/)之下、新清單沒有列的紀錄<b>一律作廢</b>:
    ///   安裝程式在安裝前清空程式目錄(tools/installer/SimosaBRM.iss 的 [InstallDelete]),重新安裝後程式目錄與發行版完全相同;
    ///   以壓縮檔或手動複製覆蓋而留下的舊檔(含先前更新包新增的檔案)則應被列為「程式目錄內多出的檔案」,由人依清單刪除 —
    ///   若仍以覆蓋清單為準,前者會誤報「檔案缺少」。</item>
    /// <item><b>其他位置</b>(data/、config/trusted-keys/;安裝程式不清空):更新包新增而清單沒有的檔案紀錄保留,仍以覆蓋清單核對。</item>
    /// <item><see cref="Untracked"/> 清空(先前無法核對的前綴已被安裝覆寫)。</item>
    /// </list>
    /// 回傳(被新清單取代的檔案紀錄數,程式目錄之下因而作廢的檔案紀錄數,作廢的 files / removed 紀錄所屬的更新包 — 這次重新安裝真的覆蓋到的更新包,
    /// 見 <see cref="ReconcileInfo.OverwrittenPackages"/>)。
    /// </summary>
    public (int Superseded, int DroppedProgramFiles, HashSet<string> Owners) Rebase(IReadOnlyCollection<string> manifestPaths, InstalledBaseline baseline, StringComparison comparison)
    {
        var listed = new HashSet<string>(manifestPaths, StringComparer.FromComparison(comparison));
        var owners = new HashSet<string>(StringComparer.Ordinal);
        bool Drop(string packageId)
        {
            if (packageId.Length > 0) owners.Add(packageId);
            return true;
        }
        var superseded = Files.RemoveAll(x => listed.Contains(x.Path) && Drop(x.PackageId));
        var dropped = Files.RemoveAll(x => InstalledPaths.IsAtOrUnderProgram(x.Path, comparison) && Drop(x.PackageId));
        Removed.RemoveAll(r => (InstalledPaths.IsAtOrUnderProgram(r.Path, comparison)
                                || manifestPaths.Any(p => InstalledPaths.IsAtOrUnder(p, r.Path, comparison))) && Drop(r.PackageId));
        Untracked.Clear();
        Baseline = baseline;
        return (superseded, dropped, owners);
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
        // 新的軟體更新包(或含 app/ 的 mixed 包):installed.json 的 software 元件改指它,降版的紀錄到此為止
        // (status 顯示的軟體版次又是更新包寫入的程式;rollback 這個包時,套用前的清單連同紀錄一起還原)
        if (UpdateApplier.ComponentKinds(manifest).Contains(UpdateKinds.Software)) Downgrade = null;
    }
}

/// <summary>
/// 降版的證據(覆蓋清單的 <see cref="InstalledFiles.Downgrade"/>;審查第二十九輪 P4):重新安裝(<see cref="InstalledFiles.Rebase"/>,
/// 或沒有覆蓋清單時 <see cref="ReconcileInfo.SupersededPackages"/> 非空)當下,重新安裝之前的狀態。
/// 降版提醒(安裝模式核對的「狀態」提醒、<c>simosa-pkg status</c> 的警告與 releaseDowngrade)一律以 <see cref="Evaluate"/> 判斷(單一出處)。
/// <para>
/// 為什麼要保存而不是每次推斷:降版之後再套用任一非軟體更新包,套用就把覆蓋清單對齊到目前的發行版(<see cref="InstalledFiles.Rebase"/>),
/// 之後的核對看不到重新安裝;但 installed.json 的 software 元件仍是重新安裝之前的軟體更新包 — status 顯示的軟體版次不是實際執行的程式。
/// </para>
/// 清除的條件:套用新的軟體更新包(<see cref="InstalledFiles.Record"/>),或以版本不低於 <see cref="BasisVersion"/> 的發行版重新安裝(<see cref="Observe"/>)。
/// </summary>
public sealed class DowngradeRecord
{
    /// <summary>記錄時(重新安裝之後)的發行版版本</summary>
    public string? ReleaseVersion { get; set; }
    /// <summary>重新安裝之前的發行版(覆蓋清單的 baseline,或 previous.json 的 release;重新安裝不只一次時取最大者)</summary>
    public string? PreviousReleaseVersion { get; set; }
    /// <summary>
    /// 當時 installed.json 的 software 元件(被重新安裝取代、status 仍顯示的軟體更新包)。只在該更新包確實被這次重新安裝覆蓋
    /// (<see cref="ReconcileInfo.OverwrittenPackages"/>)時記錄:沒有覆蓋清單時,在發行版之後才套用的軟體包仍是實際執行的程式。
    /// </summary>
    public string? SoftwarePackageId { get; set; }
    public string? SoftwareVersion { get; set; }
    /// <summary>
    /// 這次重新安裝真的覆蓋到的<b>軟體</b>更新包(software,或含 app/ 的 mixed 包;未 rollback;<see cref="ReconcileInfo.OverwrittenPackages"/>)中的最高版本
    /// (審查第二十九輪 P5)。不計:安裝不會覆蓋的更新包(圖資包放進 data/、發行清單沒有的檔案)、已 rollback 的更新包(不再以 installed.json 的
    /// highestVersion 補位)、非軟體更新包 — 參數、情境包換過的發行清單檔案被安裝蓋回時,「被安裝覆蓋」的 state 提醒已經說明;
    /// 把它們的版本算進比較基準,會把升級或回到原本的發行版誤報為降版,還要求可能根本不存在的「版本不低於某個參數包」的安裝程式。
    /// </summary>
    public string? HighestAppliedVersion { get; set; }
    public DateTime RecordedUtc { get; set; }

    /// <summary>比較基準:三者(先前的發行版、軟體元件版次、被覆蓋的軟體更新包最高版本)中的最大者 — 「版本不低於它的安裝程式」才回到降版之前。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BasisVersion => Max(Max(PreviousReleaseVersion, SoftwareVersion), HighestAppliedVersion);

    /// <summary>
    /// 以目前的發行版 <paramref name="releaseVersion"/> 與 installed.json 評估這份紀錄(降版提醒的唯一判斷):回傳比目前的發行版新的各項;
    /// 都不比它新時回傳 null。軟體元件那一項只在 installed.json 的 software 元件仍是記錄的那個更新包時才算(之後換過軟體更新包就不是「過時」)。
    /// </summary>
    public DowngradeEvaluation? Evaluate(string? releaseVersion, InstalledState installed)
    {
        if (releaseVersion is null || !PackageVersion.TryParse(releaseVersion, out _)) return null;
        bool Newer(string? v) => v is not null && PackageVersion.TryParse(v, out _) && PackageVersion.Compare(v, releaseVersion) > 0;
        var current = installed.Components?.GetValueOrDefault(UpdateKinds.Software);
        var softwareStillShown = current is not null && SoftwareVersion is not null
                                 && (SoftwarePackageId is { Length: > 0 } id ? current.PackageId == id : current.Version == SoftwareVersion);
        var previous = Newer(PreviousReleaseVersion) ? PreviousReleaseVersion : null;
        var software = softwareStillShown && Newer(SoftwareVersion) ? SoftwareVersion : null;
        var highest = Newer(HighestAppliedVersion) ? HighestAppliedVersion : null;
        if (previous is null && software is null && highest is null) return null;
        return new DowngradeEvaluation(releaseVersion, previous, software, highest, Max(Max(previous, software), highest)!);
    }

    /// <summary>
    /// <see cref="InstalledFiles.Reconcile"/> 用:這一次看得到重新安裝(<paramref name="reinstalled"/>)時,以重新安裝之前的狀態更新紀錄 —
    /// 先前的發行版、被覆蓋的 software 元件、<paramref name="overwritten"/> 之中軟體更新包的最高版本(已有紀錄時,先前的發行版與最高版本取較大者、
    /// software 元件沒有新的依據時沿用、記錄時間沿用);之後以目前的發行版評估,不再是降版就清除(回傳 null)。
    /// 只有非軟體更新包被覆蓋、先前的發行版也不比目前新時不建立紀錄(「被安裝覆蓋」的 state 提醒已說明)。
    /// 合併時不會納入降版之後才套用的更新包:之後套用的軟體更新包會清除紀錄(<see cref="InstalledFiles.Record"/>),已 rollback 的不在
    /// <paramref name="overwritten"/> 之內,非軟體更新包本來就不計 — 所以同樣的實體狀態不會因為中間套用過什麼而得到不同的結果。
    /// 讀不到發行清單(<paramref name="releaseVersion"/> 為 null)時無從判斷,原樣保留。
    /// </summary>
    public static DowngradeRecord? Observe(DowngradeRecord? kept, bool reinstalled, string? releaseVersion, string? previousReleaseVersion,
                                           IReadOnlyCollection<OverwrittenPackage> overwritten, InstalledState installed, DateTime nowUtc)
    {
        if (releaseVersion is null || !PackageVersion.TryParse(releaseVersion, out _)) return kept;
        var record = kept;
        if (reinstalled)
        {
            var software = overwritten.Where(p => p.Software).ToList();
            var component = installed.Components?.GetValueOrDefault(UpdateKinds.Software);
            var stale = component is not null && software.Any(p => p.PackageId == component.PackageId) ? component : null;
            record = new DowngradeRecord
            {
                ReleaseVersion = releaseVersion,
                PreviousReleaseVersion = Max(kept?.PreviousReleaseVersion, previousReleaseVersion),
                SoftwarePackageId = stale is not null ? stale.PackageId : kept?.SoftwarePackageId,
                SoftwareVersion = stale is not null ? stale.Version : kept?.SoftwareVersion,
                HighestAppliedVersion = Max(kept?.HighestAppliedVersion, software.Select(p => p.Version).Aggregate((string?)null, Max)),
                RecordedUtc = kept?.RecordedUtc ?? nowUtc,
            };
        }
        return record?.Evaluate(releaseVersion, installed) is null ? null : record;
    }

    /// <summary>兩個版本取較大者(其中一個為 null 或格式不合法時取另一個)。</summary>
    public static string? Max(string? a, string? b)
    {
        var okA = a is not null && PackageVersion.TryParse(a, out _);
        var okB = b is not null && PackageVersion.TryParse(b, out _);
        if (!okA) return okB ? b : null;
        if (!okB) return a;
        return PackageVersion.Compare(a!, b!) >= 0 ? a : b;
    }
}

/// <summary><see cref="DowngradeRecord.Evaluate"/> 的結果:比目前的發行版新的各項(不比它新的為 null)與比較基準(其中的最大者)。</summary>
public sealed record DowngradeEvaluation(string ReleaseVersion, string? PreviousReleaseVersion, string? SoftwareVersion, string? HighestAppliedVersion, string BasisVersion);

/// <summary>
/// 這次重新安裝真的覆蓋到的更新包(<see cref="ReconcileInfo.OverwrittenPackages"/>)。<paramref name="Software"/>:software 或含 app/ 的 mixed 包
/// (只有這類計入降版的比較基準,<see cref="DowngradeRecord.HighestAppliedVersion"/>)。
/// </summary>
public sealed record OverwrittenPackage(string PackageId, string Version, string? Kind, bool Software);

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
    /// <summary>
    /// 沒有覆蓋清單,且已被目前的發行版安裝覆蓋的更新包(不列為未記錄):在目前發行版建置之前套用的,
    /// 或 previous.json 顯示最近一次套用之後重新安裝過(<see cref="ReinstalledSinceLastApply"/>;這時是全部套用中的更新包)
    /// </summary>
    public List<TrackedPackage> SupersededPackages { get; set; } = new();
    /// <summary>沒有(或讀不到)覆蓋清單,而 state/previous/previous.json 記錄的發行清單身分顯示最近一次套用之後重新安裝過</summary>
    public bool ReinstalledSinceLastApply { get; set; }
    /// <summary><see cref="ReinstalledSinceLastApply"/> 時,previous.json 記錄的套用當時的發行清單(當時沒有或讀不到時 null)</summary>
    public InstalledBaseline? LastApplyRelease { get; set; }
    /// <summary>
    /// 看得到重新安裝時,這次重新安裝真的覆蓋到的更新包(<c>InstalledFiles.CollectOverwritten</c>:作廢的覆蓋清單紀錄所屬者、軟體更新包、
    /// 前綴與發行清單相交的種類)。降版的比較基準只取其中的軟體更新包(<see cref="DowngradeRecord.Observe"/>)。沒有重新安裝時是空的。
    /// </summary>
    public List<OverwrittenPackage> OverwrittenPackages { get; set; } = new();
    /// <summary>
    /// 覆蓋清單寫入時 manifest.json 讀不到(<see cref="InstalledFiles.BaselineUnreadable"/>),而目前的發行版建置於那次寫入之前:
    /// 視為同一個發行版,改記目前的身分(沒有 Rebase)
    /// </summary>
    public bool BaselineAdopted { get; set; }
}

/// <summary>
/// 發行清單 manifest.json 的身分(覆蓋清單的 <see cref="InstalledFiles.Baseline"/>、state/previous/previous.json 的
/// <see cref="PreviousMeta.Release"/>)。是否為「同一個發行版」一律以 <see cref="SameRelease"/> 判斷(Reconcile、安裝模式核對、
/// rollback 的「重新安裝過」共用)。
/// </summary>
public sealed class InstalledBaseline
{
    public string? Version { get; set; }
    /// <summary>manifest.json 檔案本身的 SHA-256(原始位元組)</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>發行版的建置時間(manifest.json 的 builtUtc)</summary>
    public DateTime? BuiltUtc { get; set; }
    /// <summary>
    /// 解析後內容的 SHA-256(<see cref="ContentHash"/>:version、builtUtc、gitRevision 與排序後的「path\tsha256\tsize」各行)。
    /// 只改編碼(加 BOM、LF→CRLF、縮排)不會改變它。舊版 simosa-pkg 寫的紀錄沒有這個欄位(null)。
    /// </summary>
    public string? ContentSha256 { get; set; }

    /// <summary>由已讀入的發行清單與 manifest.json 檔案的 SHA-256 組出身分。</summary>
    public static InstalledBaseline Of(Release.ReleaseManifest m, string fileSha256)
        => new() { Version = m.Version, Sha256 = fileSha256, BuiltUtc = m.BuiltUtc == default ? null : m.BuiltUtc, ContentSha256 = ContentHash(m) };

    /// <summary>
    /// 發行清單內容的身分:以 '\n' 連接的「version\t…」「builtUtc\t…(UTC,往返格式)」「gitRevision\t…」與
    /// 依路徑(ordinal)排序的「path\tsha256(小寫)\tsize」各行,取 UTF-8 的 SHA-256。null 的檔案項目略過(損毀的清單由核對另行回報)。
    /// </summary>
    public static string ContentHash(Release.ReleaseManifest m)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("version\t").Append(m.Version).Append('\n');
        sb.Append("builtUtc\t").Append(ToUtc(m.BuiltUtc).ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("gitRevision\t").Append(m.GitRevision ?? "").Append('\n');
        foreach (var f in (m.Files ?? new()).Where(f => f is not null).OrderBy(f => f.Path ?? "", StringComparer.Ordinal))
            sb.Append(f.Path ?? "").Append('\t').Append((f.Sha256 ?? "").ToLowerInvariant()).Append('\t').Append(f.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        return Sha256Hex.Of(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>
    /// <paramref name="recorded"/>(先前記錄的身分)與 <paramref name="current"/>(目前的 manifest.json)是否為同一個發行版:
    /// 原始 SHA-256 相同 → 是;否則兩邊都有內容身分 → 比 <see cref="ContentSha256"/>(只改編碼仍是同一個);
    /// 舊紀錄沒有內容身分 → 以版本 + 建置時間比較(兩者都要有且相同)。任一邊為 null → 否(呼叫端各自決定 null 的意義)。
    /// </summary>
    public static bool SameRelease(InstalledBaseline? recorded, InstalledBaseline? current)
    {
        if (recorded is null || current is null) return false;
        if (recorded.Sha256 is { Length: > 0 } && Sha256Hex.Equal(recorded.Sha256, current.Sha256)) return true;
        if (recorded.ContentSha256 is { Length: > 0 } rc && current.ContentSha256 is { Length: > 0 } cc) return Sha256Hex.Equal(rc, cc);
        return recorded.Version is not null && recorded.Version == current.Version
               && recorded.BuiltUtc is { } rb && current.BuiltUtc is { } cb && ToUtc(rb) == ToUtc(cb);
    }

    /// <summary>
    /// 發行版 <paramref name="current"/> 是否建置於 <paramref name="atUtc"/>(船上的套用時間)之後 — 是的話,它一定是在那之後才安裝的。
    /// 套用時 manifest.json 讀不到、沒有身分可比時的依據(覆蓋清單的 <see cref="InstalledFiles.BaselineUnreadable"/>、previous.json 的
    /// <see cref="PreviousMeta.ReleaseUnreadable"/>)。沒有建置時間時回答「是」(無從判斷,照重新安裝處理)。時鐘偏差的方向見 <see cref="InstalledFiles.Reconcile"/>。
    /// </summary>
    public static bool BuiltAfter(InstalledBaseline current, DateTime atUtc)
        => current.BuiltUtc is not { } built || ToUtc(built) > ToUtc(atUtc);

    private static DateTime ToUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Local => t.ToUniversalTime(),
        DateTimeKind.Utc => t,
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };
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
