namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 安裝根目錄內「不屬發行內容」的位置。<paramref name="Path"/> 以 '/' 分隔:結尾為 '/' 表示整個目錄,否則是單一檔案。
/// </summary>
public sealed record InstalledPathRule(string Path, string Description)
{
    public bool IsDirectory => Path.EndsWith('/');
}

/// <summary>
/// 安裝模式核對(<c>simosa-pkg release verify --installed</c>;規劃書第 5.5 節、R13)對安裝根目錄內路徑的分類,集中定義於此:
/// <list type="bullet">
/// <item><see cref="RuntimeData"/>:執行期資料與本機設定的位置 — 這些位置之下「清單沒有列的檔案」不算多出、也不逐一列出
///   (清單有列的檔案即使位於這些位置仍照常核對)。須與 <see cref="InstallLayout"/>、安裝程式 tools/installer/SimosaBRM.iss 的 [Dirs]
///   與 docs/ops/本機資料位置.md 第 2 節一致(tests/Tools.Packaging.Tests/InstalledVerifyTests 對照前兩者)。</item>
/// <item><see cref="ProgramPrefixes"/>:程式目錄 — 其下多出清單沒有的檔案是錯誤(舊版殘留的組件、被放進去的檔案會被載入或被網頁伺服送出)。</item>
/// <item><see cref="UserConfigPrefix"/>:使用者依本機修改的設定檔 — 清單內 config/ 之下的檔案與清單不符或缺少只列為提醒。</item>
/// </list>
/// 其餘位置(docs/、data/、packages/、安裝根目錄本身…)多出的檔案列為提醒。
/// </summary>
public static class InstalledPaths
{
    /// <summary>
    /// 執行期資料與本機設定(相對安裝根目錄)。
    /// <code>
    /// build/                 輸出與執行期資料:logs(各程序 log、PID)、records(練習紀錄、錄影)、browser/webview2(瀏覽器端資料)、
    ///                        downloads、tmp、visual(視景品質設定)、run(啟動腳本存的發行版狀態 release-status.json:
    ///                        simosa-pkg status --json 的輸出,simosa-web 據此提供教官站頁首的發行版本與降版提醒)
    ///                        — CLAUDE.md「本機資料守則」:build\ 之下全部是本機產生的資料
    /// .cache/                快取(Godot 使用者資料 .cache\godot\appdata 等)
    /// state/                 更新包狀態、稽核紀錄、待回傳紀錄包、上一版元件(InstallLayout.StateDir)
    /// store/                 教官筆電紀錄庫(PackagingConfig.Store 預設值)
    /// config/device/         裝置金鑰(InstallLayout.DeviceDir)
    /// config/trusted-keys/   信任公鑰(InstallLayout.TrustedKeysDir;安裝時 trust add 放入者不在任何清單內;
    ///                        trust 更新包寫入者記在覆蓋清單 state/installed-files.json,照常核對)
    /// config/packaging.json  本機封裝設定(InstallLayout.ConfigPath)
    /// data/records/          練習紀錄的另一個位置(安裝程式 [Dirs] 保留;發行版不含)
    /// tools/webview2/、tools/dotnet/  岸上放入的離線執行階段安裝檔(docs/ops/安裝與啟動.md 第 2.8、4.5 節;README.txt 在清單內照常核對)
    /// </code>
    /// 解除安裝檔 tools/uninstall/unins000.* 由 <c>ReleaseManifestBuilder</c> 依檔名略過,不在此列。
    /// </summary>
    public static readonly IReadOnlyList<InstalledPathRule> RuntimeData = new InstalledPathRule[]
    {
        new("build/", "輸出與執行期資料(log、練習紀錄與錄影、瀏覽器端資料、下載、暫存、視景設定、發行版狀態)"),
        new(".cache/", "快取(Godot 使用者資料等)"),
        new("state/", "更新包狀態、稽核紀錄、待回傳紀錄包"),
        new("store/", "教官筆電紀錄庫"),
        new("config/device/", "裝置金鑰"),
        new("config/trusted-keys/", "信任公鑰"),
        new("config/packaging.json", "本機封裝設定"),
        new("data/records/", "練習紀錄"),
        new("tools/webview2/", "WebView2 Runtime 離線安裝檔"),
        new("tools/dotnet/", ".NET 執行階段離線安裝檔"),
    };

    /// <summary>
    /// 程式目錄:app/(軟體更新包的元件)與 tools/updater/(simosa-pkg 本身)。
    /// 安裝程式 tools/installer/SimosaBRM.iss 的 [InstallDelete] 在安裝前清空的正是這兩個資料夾(重新安裝後與發行版完全一致;
    /// tools/installer/check-iss.py 與 tests/Tools.Packaging.Tests/InstalledVerifyTests 對照),要改須三邊一起改。
    /// </summary>
    public static readonly IReadOnlyList<string> ProgramPrefixes = new[] { "app/", "tools/updater/" };

    /// <summary>使用者依本機修改的設定檔所在(config/ship-laptop.json、config/instructor-laptop.json…;安裝程式對既有檔不覆寫)。</summary>
    public const string UserConfigPrefix = "config/";

    /// <summary>更新包可寫入的全部前綴(無法得知套用過哪些更新包時,整個列為無法核對)。</summary>
    public static readonly IReadOnlyList<string> UpdatePrefixes = new[] { "app/", "data/", PackagePaths.TrustedKeysPrefix };

    /// <summary>Windows 檔名不分大小寫(與 UpdateApplier 的檔名比較一致)。</summary>
    public static StringComparison DefaultComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary><paramref name="path"/> 是否等於 <paramref name="prefix"/> 或在其下(<paramref name="prefix"/> 可帶或不帶結尾的 '/')。</summary>
    public static bool IsAtOrUnder(string path, string prefix, StringComparison comparison)
    {
        var p = prefix.TrimEnd('/');
        if (p.Length == 0) return true;
        return path.Length == p.Length ? path.Equals(p, comparison)
            : path.Length > p.Length && path[p.Length] == '/' && path.StartsWith(p, comparison);
    }

    /// <summary>相對路徑(檔案,或結尾帶 '/' 的目錄)是否位於執行期資料位置;<paramref name="extra"/> 為 packaging.json 自訂的紀錄/紀錄庫目錄。</summary>
    public static InstalledPathRule? FindRuntimeData(string relative, StringComparison comparison, IEnumerable<InstalledPathRule>? extra = null)
    {
        foreach (var rule in extra is null ? RuntimeData : RuntimeData.Concat(extra))
        {
            if (rule.IsDirectory ? relative.StartsWith(rule.Path, comparison) : relative.Equals(rule.Path, comparison)) return rule;
        }
        return null;
    }

    public static bool IsProgram(string relative, StringComparison comparison)
        => ProgramPrefixes.Any(p => relative.StartsWith(p, comparison));

    /// <summary>路徑(檔案或目錄;不必帶結尾的 '/')是否為程式目錄本身或在其下(覆蓋清單的紀錄用:被刪除的路徑可以是整個目錄)。</summary>
    public static bool IsAtOrUnderProgram(string path, StringComparison comparison)
        => ProgramPrefixes.Any(p => IsAtOrUnder(path, p, comparison));

    /// <summary>
    /// 目錄(結尾帶 '/')是否位於程式目錄內、是程式目錄本身,或是程式目錄的上層(例如 tools/ 之於 tools/updater/)—
    /// 這樣的目錄若無法走訪(例如是目錄連結),程式目錄內多出的檔案就查不到。
    /// </summary>
    public static bool IsProgramOrAbove(string relativeDir, StringComparison comparison)
        => ProgramPrefixes.Any(p => relativeDir.StartsWith(p, comparison) || p.StartsWith(relativeDir, comparison));

    public static bool IsUserConfig(string relative, StringComparison comparison)
        => relative.StartsWith(UserConfigPrefix, comparison);
}
