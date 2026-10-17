using SimosaBRM.SimCore.Ship;

namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 安裝目錄配置(船上筆電;預設 D:\Simosa BRM\,CLAUDE.md:所有輸出留在專案資料夾內):
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

    /// <summary>
    /// 預設安裝根目錄:環境變數 SIMOSA_INSTALL_ROOT → Windows 上 D:\Simosa BRM → 專案根目錄 → 目前目錄。
    /// </summary>
    public static string DefaultRoot()
    {
        var env = System.Environment.GetEnvironmentVariable("SIMOSA_INSTALL_ROOT");
        if (!string.IsNullOrEmpty(env)) return env;
        if (OperatingSystem.IsWindows()) return @"D:\Simosa BRM";
        return RepositoryPaths.TryFindRoot() ?? Directory.GetCurrentDirectory();
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
///   "maxVisitIntervals": 2, "recordsDir": "build/records", "store": "store" }
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
    public string? RecordsDir { get; set; }
    /// <summary>教官端紀錄庫目錄(相對安裝根目錄;預設 store)</summary>
    public string? Store { get; set; }

    public static PackagingConfig LoadOrDefault(string path)
        => File.Exists(path) ? PackageJson.Load<PackagingConfig>(path, "packaging.json") : new PackagingConfig();

    public void Save(string path) => PackageJson.SaveAtomic(path, this);
}
