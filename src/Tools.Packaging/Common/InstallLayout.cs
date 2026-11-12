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
/// &lt;root&gt;/state/audit.jsonl         稽核紀錄
/// &lt;root&gt;/state/previous/           上一版元件(rollback 用,只保留一層)
/// &lt;root&gt;/state/outbox/*.sbrec      已簽章、待回傳的紀錄包
/// &lt;root&gt;/state/records-ledger.json 紀錄包帳冊(打包/取回/備份/刪除)
/// </code>
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
    ///    或環境變數 SIMOSA_BRM_ROOT)— 程式放在哪個磁碟就用哪個磁碟(D:、G: 皆可),不寫死磁碟代號;
    /// 3. Windows:<see cref="WindowsDefaultRoots"/> 中第一個已存在的資料夾,其次第一個磁碟存在者;兩個磁碟都沒有時仍回傳 D:\Simosa BRM
    ///   (磁碟不存在,後續建立目錄會明確失敗)— **不會自動落到系統磁碟 C:**(要放 C: 須以 --root 或 SIMOSA_INSTALL_ROOT 明確指定,即經同意);
    /// 4. 其他平台:目前目錄。
    /// </summary>
    public static string DefaultRoot()
        => DefaultRoot(System.Environment.GetEnvironmentVariable("SIMOSA_INSTALL_ROOT"), AppContext.BaseDirectory,
            Directory.Exists, () => RepositoryPaths.TryFindRoot(), OperatingSystem.IsWindows(), Directory.GetCurrentDirectory());

    /// <summary>可測版本:所有對環境的查詢由參數注入。</summary>
    public static string DefaultRoot(string? envRoot, string baseDirectory, Func<string, bool> directoryExists,
        Func<string?> findRepositoryRoot, bool isWindows, string currentDirectory)
    {
        if (!string.IsNullOrEmpty(envRoot)) return envRoot;
        if (FindInstalledRoot(baseDirectory, directoryExists) is { } installed) return installed;
        if (findRepositoryRoot() is { Length: > 0 } repo) return repo;
        if (isWindows)
        {
            foreach (var r in WindowsDefaultRoots)
                if (directoryExists(r)) return r;
            foreach (var r in WindowsDefaultRoots)
                if (directoryExists(r[..3])) return r;   // 磁碟根目錄(D:\、G:\)存在
            return WindowsDefaultRoots[0];
        }
        return currentDirectory;
    }

    /// <summary>自 <paramref name="start"/> 往上找發行版的安裝根目錄(同時含 data/ 與 app/ 的目錄);找不到回傳 null。</summary>
    public static string? FindInstalledRoot(string start, Func<string, bool> directoryExists)
    {
        var dir = Path.GetFullPath(start).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(dir))
        {
            if (directoryExists(Path.Combine(dir, "data")) && directoryExists(Path.Combine(dir, "app"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    /// <summary>取得排他鎖(同時只允許一個套用/打包/清理程序);釋放即解鎖。</summary>
    public IDisposable AcquireLock()
    {
        Directory.CreateDirectory(StateDir);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }
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
