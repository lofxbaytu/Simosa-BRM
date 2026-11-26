using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 安裝目錄配置(船上筆電;預設為程式所在的安裝根目錄,例如 D:\Simosa BRM\ 或 G:\Simosa BRM\;CLAUDE.md:所有輸出留在專案資料夾內、不寫 C:\):
/// <code>
/// &lt;root&gt;/app/                     軟體(更新包元件)
/// &lt;root&gt;/data/                    船模、情境、參數集、圖資(更新包元件)
/// &lt;root&gt;/config/trusted-keys/*.pem 信任公鑰(主金鑰 + 備援金鑰;更新包元件,僅 trust 類可改)
/// &lt;root&gt;/config/device/           裝置金鑰(安裝時產生;Windows 以 DPAPI 保護)
/// &lt;root&gt;/config/packaging.json     角色、船舶 ID、直連 IP/埠、保留上限
/// &lt;root&gt;/build/records/            練習紀錄 *.jsonl(SimCore RecordWriter 輸出)
/// &lt;root&gt;/state/installed.json      現行各元件版次、曾套用最高版本
/// &lt;root&gt;/state/installed-files.json 更新包覆蓋清單:更新包寫入/刪除的路徑與雜湊(安裝模式核對 release verify --installed 用;與 installed.json 同時寫入、rollback 一起還原)
/// &lt;root&gt;/state/audit.jsonl         稽核紀錄
/// &lt;root&gt;/state/previous/           上一版元件(rollback 用,只保留一層)
/// &lt;root&gt;/state/outbox/*.sbrec      已簽章、待回傳的紀錄包
/// &lt;root&gt;/state/records-ledger.json 紀錄包帳冊(打包/取回/備份/刪除)
/// &lt;root&gt;/state/.lock               船上狀態的跨行程鎖定檔(0 位元組,留著不刪;<see cref="AcquireLock"/>)
/// </code>
/// 哪些位置是執行期資料(安裝模式核對時不算「清單沒有的檔案」)、哪些是程式目錄,集中定義在 <see cref="InstalledPaths"/>。
/// </summary>
public sealed class InstallLayout
{
    public InstallLayout(string root, PackagingConfig? config = null)
    {
        Root = Path.GetFullPath(root);
        Config = config ?? PackagingConfig.LoadOrDefault(Path.Combine(Root, "config", "packaging.json"));
    }

    public string Root { get; }
    public PackagingConfig Config { get; }

    public string ConfigDir => Path.Combine(Root, "config");
    public string TrustedKeysDir => Path.Combine(ConfigDir, "trusted-keys");
    public string DeviceDir => Path.Combine(ConfigDir, "device");
    public string ConfigPath => Path.Combine(ConfigDir, "packaging.json");
    public string StateDir => Path.Combine(Root, "state");
    public string InstalledStatePath => Path.Combine(StateDir, "installed.json");
    /// <summary>更新包覆蓋清單(Updates.InstalledFiles;檔名同 InstalledFiles.FileName)</summary>
    public string InstalledFilesPath => Path.Combine(StateDir, "installed-files.json");
    public string AuditPath => Path.Combine(StateDir, "audit.jsonl");
    public string StagingDir => Path.Combine(StateDir, "staging");
    public string PreviousDir => Path.Combine(StateDir, "previous");
    public string IncomingDir => Path.Combine(StateDir, "incoming");
    public string OutboxDir => Path.Combine(StateDir, "outbox");
    public string LedgerPath => Path.Combine(StateDir, "records-ledger.json");
    public string LockPath => Path.Combine(StateDir, ".lock");
    public string RecordsDir => Config.RecordsDir is { Length: > 0 } r
        ? (Path.IsPathRooted(r) ? r : Path.Combine(Root, r))
        : Path.Combine(Root, "build", "records");

    public AuditLog Audit(TimeProvider? clock = null) => new(AuditPath, clock);

    /// <summary>Windows 上依序嘗試的預設安裝位置(守則:本機資料放 D: 或 G:,不放系統磁碟 C:)。</summary>
    public static readonly IReadOnlyList<string> WindowsDefaultRoots = new[] { @"D:\Simosa BRM", @"G:\Simosa BRM" };

    /// <summary>
    /// 預設安裝根目錄:
    /// 1. 環境變數 SIMOSA_INSTALL_ROOT;
    /// 2. 執行檔所在位置往上找到的安裝根目錄(含 data/ 與 app/;發行版 &lt;root&gt;/app/…)或專案根目錄(Directory.Build.props + data/,
    ///    或環境變數 SIMOSA_BRM_ROOT)— 程式放在哪個磁碟就用哪個磁碟(D:、G: 皆可),不寫死磁碟代號;兩者都找到時離執行檔較近者優先
    ///   (專案的某個上層剛好同時有 data/ 與 app/ 時,不把那個上層當成安裝根目錄);
    /// 3. Windows:<see cref="WindowsDefaultRoots"/> 中第一個已存在的資料夾,其次第一個磁碟存在者;兩個磁碟都沒有時仍回傳清單的第一個
    ///   (磁碟不存在,後續建立目錄會明確失敗)— **不會自動落到系統磁碟**(清單中與系統磁碟同代號者略過,例如 Windows 本身裝在 D: 的機器;
    ///    要放系統磁碟須以 --root 或 SIMOSA_INSTALL_ROOT 明確指定,即經同意);
    /// 4. 其他平台:目前目錄。
    /// </summary>
    public static string DefaultRoot()
        => DefaultRoot(System.Environment.GetEnvironmentVariable("SIMOSA_INSTALL_ROOT"), AppContext.BaseDirectory,
            Directory.Exists, () => RepositoryPaths.TryFindRoot(), OperatingSystem.IsWindows(), Directory.GetCurrentDirectory(),
            System.Environment.GetEnvironmentVariable("SystemDrive"));

    /// <summary>可測版本:所有對環境的查詢由參數注入。<paramref name="systemDrive"/>:Windows 的系統磁碟(例如 "C:";null = 不排除)。</summary>
    public static string DefaultRoot(string? envRoot, string baseDirectory, Func<string, bool> directoryExists,
        Func<string?> findRepositoryRoot, bool isWindows, string currentDirectory, string? systemDrive = null)
    {
        if (!string.IsNullOrEmpty(envRoot)) return envRoot;
        var installed = FindInstalledRoot(baseDirectory, directoryExists);
        var repo = findRepositoryRoot() is { Length: > 0 } found ? found : null;
        if (installed is not null && repo is not null && IsUnder(repo, installed, strictly: true) && IsUnder(baseDirectory, repo, strictly: false))
            return repo;   // 專案根目錄比「含 data/ 與 app/ 的上層」更接近執行檔
        if (installed is not null) return installed;
        if (repo is not null) return repo;
        if (isWindows)
        {
            var sys = string.IsNullOrEmpty(systemDrive) ? null : systemDrive.TrimEnd('\\', '/') + "\\";
            var candidates = WindowsDefaultRoots.Where(r => sys is null || !r.StartsWith(sys, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) candidates = WindowsDefaultRoots.ToList();
            foreach (var r in candidates)
                if (directoryExists(r)) return r;
            foreach (var r in candidates)
                if (directoryExists(r[..3])) return r;   // 磁碟根目錄(D:\、G:\)存在
            return candidates[0];
        }
        return currentDirectory;
    }

    /// <summary><paramref name="path"/> 是否在 <paramref name="parent"/> 之下(strictly = false 時相等也算);不分大小寫(Windows 路徑)。</summary>
    private static bool IsUnder(string path, string parent, bool strictly)
    {
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var a = Norm(path); var b = Norm(parent);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return !strictly;
        return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>自 <paramref name="start"/> 往上找發行版的安裝根目錄(同時含 data/ 與 app/ 的目錄);找不到回傳 null。</summary>
    public static string? FindInstalledRoot(string start, Func<string, bool> directoryExists)
        => RepositoryPaths.FindInstalledRoot(start, directoryExists);   // 與 Console.Serial 等共用同一份(SimCore)

    /// <summary>等另一個行程放開船上狀態鎖的時間上限(見 <see cref="AcquireLock"/>)</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>等鎖超過幾秒還沒有拿到時的通知(指令列印一行,讓操作人員知道為什麼沒有動靜;同步服務寫進自己的 log);null = 不通知</summary>
    public Action<string>? LockNotice { get; set; }

    /// <summary>
    /// 取得船上的狀態鎖 <see cref="LockPath"/>(state/.lock;共用的 <see cref="StateLock"/>);放開用 Dispose。
    /// <list type="bullet">
    /// <item>範圍:同一時間只有一個行程(同步服務 serve,或另一個視窗的 apply / rollback / import / export / records pack / records accept-report / device init)
    ///   在改 state/ 之下的狀態(installed.json、installed-files.json、records-ledger.json、previous/、outbox/、staging/)、替換 app/、data/、config/ 元件、
    ///   依已備份回報刪除紀錄,或產生裝置金鑰。每一個步驟(套用一個更新包、打包一次、處理一份回報、記一次訪船)各自取得、做完就放開 —
    ///   直連同步的一整段不會一直佔著,另一個視窗的指令可以排在兩個步驟之間。</item>
    /// <item>等多久:持有的時間通常不到一秒,但套用大的更新包(複製整個元件)、打包錄影(每 1 GB 約半分鐘)可以到幾分鐘。
    ///   最多等 <see cref="LockTimeout"/>(預設 120 秒;原本是 30 秒,而且等不到時只有一句英文的「The process cannot access the file …」);
    ///   等不到丟 <see cref="IOException"/>(<see cref="StateLock.IsBusy"/>):指令列照原本的 IO 錯誤處理(「錯誤:…」、結束碼 1),
    ///   同步服務把它回給教官端(<see cref="Sync.SyncServer"/>)。</item>
    /// <item>檔名沿用原本的 state/.lock:舊版的 simosa-pkg 與新版仍然互相排除。舊版以「關閉時刪除」開啟,新版不刪(行程結束時作業系統自動放開,
    ///   不會留下過期的鎖);Windows 上舊版放開的瞬間開檔可能得到「存取被拒」,先重試 2 秒才當成真的沒有權限。</item>
    /// </list>
    /// </summary>
    public IDisposable AcquireLock() => StateLock.Acquire(new StateLockSpec
    {
        Path = LockPath, Timeout = LockTimeout, AccessDeniedGrace = TimeSpan.FromSeconds(2),
        BusyMessage = t => $"另一個 simosa-pkg 正在使用這台筆電的更新包與紀錄狀態(同步服務正在處理教官筆電的同步,或另一個視窗正在套用、回復更新包、打包紀錄或匯入隨身碟),"
                           + $"等了 {t.TotalSeconds:0} 秒還沒有結束,這一步沒有執行。請等它結束(教官筆電顯示「同步完成」,或另一個視窗的指令結束)後再執行一次(鎖定檔 {LockPath})",
        WaitingMessage = "另一個 simosa-pkg 正在使用這台筆電的更新包與紀錄狀態(同步服務正在處理教官筆電的同步,或另一個視窗的指令還沒有結束),"
                         + $"等它結束(最多 {LockTimeout.TotalSeconds:0} 秒)…",
        Notice = LockNotice,
    });
}

/// <summary>
/// config/packaging.json(船上與教官筆電共用格式;未提供時用預設值)。
/// <code>
/// { "role": "ship", "shipId": "FSB1", "deviceId": "FSB1-LT01",
///   "listen": "192.168.77.11", "port": 7300, "ships": { "FSB1": "192.168.77.11", "FSB2": "192.168.77.12" },
///   "maxVisitIntervals": 2, "visitMergeHours": 12, "recordsDir": "build/records", "store": "store", "mediaMaxMb": 2048 }
/// </code>
/// </summary>
public sealed class PackagingConfig
{
    public const int DefaultPort = 7300;

    /// <summary>ship(船上筆電)或 instructor(教官筆電)</summary>
    public string Role { get; set; } = "ship";
    public string? ShipId { get; set; }
    public string? DeviceId { get; set; }
    /// <summary>船端 serve 綁定的直連介面固定私有 IP(未設時綁 0.0.0.0 並警告)</summary>
    public string? Listen { get; set; }
    public int Port { get; set; } = DefaultPort;
    /// <summary>教官端:船舶 ID → 船上筆電固定私有 IP</summary>
    public Dictionary<string, string> Ships { get; set; } = new();
    /// <summary>未取得備份回報時船上紀錄的累積上限(訪船間隔數);超過時於 status 與同步回報警告,但不自動刪除</summary>
    public int MaxVisitIntervals { get; set; } = 2;
    /// <summary>距上一個連線(直連 session 或隨身碟匯入)不到此時數的連線視為同一次訪船(重連、補推更新包);0 表示每個連線各算一次</summary>
    public double VisitMergeHours { get; set; } = 12;
    public string? RecordsDir { get; set; }
    /// <summary>教官端紀錄庫目錄(相對安裝根目錄;預設 store)</summary>
    public string? Store { get; set; }
    /// <summary>紀錄包納入單段錄影(build/records/media/*.webm)的大小上限(MB;預設 2048);超過者只放旁檔並標 omitted,錄影留在船上</summary>
    public long MediaMaxMb { get; set; } = 2048;

    public static PackagingConfig LoadOrDefault(string path)
        => File.Exists(path) ? PackageJson.Load<PackagingConfig>(path, "packaging.json") : new PackagingConfig();

    public void Save(string path) => PackageJson.SaveAtomic(path, this);
}
