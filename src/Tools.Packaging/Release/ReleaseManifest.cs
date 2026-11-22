using System.Text.RegularExpressions;
using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Release;

/// <summary>
/// 發行版清單 <c>manifest.json</c>(規劃書第 5.4–5.5 節、第 12.1 節 M4 交付「安裝程式」;tools/build-release.* 產生):
/// 記錄版本、git 版次、建置平台、各元件版本與發行目錄內每一個檔案的 SHA-256 與大小,供安裝前核對
/// (<c>simosa-pkg release verify</c>)、安裝後與使用中的完整性核對(<c>release verify --installed</c>,疊上更新包覆蓋清單
/// state/installed-files.json)與日後比對船上版次。清單本身不簽章;正式派送時以 <c>simosa-pkg pack</c>
/// 把 app/ 包成簽章更新包(.sbupd),簽章涵蓋每檔雜湊(docs/design/packaging-sync.md 第 3.1 節)。
/// </summary>
public sealed class ReleaseManifest
{
    public const string FormatId = "simosa-release/1";
    public const string FileName = "manifest.json";

    public string Format { get; set; } = FormatId;
    /// <summary>發行版本(1–4 段數字,與更新包版本規則相同,可直接作為 pack --version)</summary>
    public string Version { get; set; } = "";
    /// <summary>git 版次(由協調者或 --git 注入;無法取得時 null)</summary>
    public string? GitRevision { get; set; }
    public string? GitBranch { get; set; }
    public DateTime BuiltUtc { get; set; }
    /// <summary>建置機主機名稱與工具版本</summary>
    public string? BuiltBy { get; set; }
    /// <summary>.NET 執行階段識別(win-x64、linux-x64);framework-dependent,需安裝 .NET 8 執行階段</summary>
    public string? Runtime { get; set; }
    /// <summary>各元件(simcore、radar、simosa-pkg、web、visual、data、contracts、docs…)的版本與相對路徑</summary>
    public Dictionary<string, ReleaseComponent> Components { get; set; } = new(StringComparer.Ordinal);
    /// <summary>建置註記(例如「Godot 視景未匯出:缺匯出範本」)</summary>
    public List<string> Notes { get; set; } = new();
    /// <summary>發行目錄內全部檔案(相對路徑、以 '/' 分隔、排序),不含本清單</summary>
    public List<ReleaseFile> Files { get; set; } = new();
    public int FileCount => Files?.Count ?? 0;
    public long TotalSize => Files?.Sum(f => f?.Size ?? 0) ?? 0;
}

public sealed class ReleaseComponent
{
    public string Version { get; set; } = "";
    /// <summary>發行目錄內的相對路徑(例如 app/simcore)</summary>
    public string? Path { get; set; }
    /// <summary>該元件的狀態說明(例如「已匯出」「略過:缺匯出範本」)</summary>
    public string? Status { get; set; }
}

public sealed class ReleaseFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>清單核對結果。</summary>
public sealed class ReleaseVerification
{
    public bool Ok => Errors.Count == 0;
    public ReleaseManifest? Manifest { get; init; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public int Checked { get; set; }
}

/// <summary>
/// 產生與核對發行清單:掃描目錄(不含 manifest.json 與暫存檔)、每檔 SHA-256;核對時檔案缺少、雜湊/大小不符、多出的檔案都列為錯誤。
/// 這是「剛複製好的發行版」的核對(安裝前);使用中的安裝根目錄有執行期資料、本機設定與更新包換過的檔案,
/// 改用 <see cref="InstalledVerifier"/>(<c>simosa-pkg release verify --installed</c>)。
/// 發行清單所列的檔案必須是安裝程式會安裝的檔案:目錄內有安裝程式排除的檔名(<see cref="InstallerExcludedPatterns"/>)時 <see cref="Build"/> 失敗。
/// </summary>
public static class ReleaseManifestBuilder
{
    /// <summary>掃描時略過的檔名(清單本身與作業系統縮圖快取)。</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { ReleaseManifest.FileName, "Thumbs.db", ".DS_Store" };
    /// <summary>
    /// Inno Setup 安裝程式(tools/installer/SimosaBRM.iss)安裝後寫入安裝根目錄(tools/uninstall/)的解除安裝檔 unins000.exe/.dat/.msg:
    /// 不屬發行內容,掃描與核對時一併略過,安裝後 <c>release verify</c> 才不會把它們列為「清單中沒有的檔案」。
    /// </summary>
    private static readonly Regex UninstallerFile = new(@"^unins\d{3}\.(exe|dat|msg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 安裝程式不會安裝的檔名樣式 — 與 tools/installer/SimosaBRM.iss [Files] 主項目的 Excludes(扣掉另列安裝的 \config\*)必須完全相同,
    /// <b>兩邊要一起改</b>:tools/installer/check-iss.py 以正規表示式讀下面這一行(請維持「new[] { "…", "…" }」寫在同一行)與 .iss 對照,
    /// tests/Tools.Packaging.Tests/ReleaseTests 再對照一次。樣式只比對檔名(不含路徑),'*' 與 '?' 為萬用字元,不分大小寫。
    /// 這些檔案一旦列進發行清單,以安裝程式安裝的筆電上就沒有清單所列的檔案,<c>release verify --installed</c> 會報「檔案缺少」
    /// (審查第二十九輪:Godot .NET 匯出把 .pdb 放進 app/visual/data_*/,而安裝程式排除 *.pdb)。所以掃描時不是依檔名略過
    /// (<see cref="Skip"/> 的 Thumbs.db),就是讓 <see cref="Build"/> 失敗(*.pdb:偵錯符號不進發行版,也不進軟體更新包)。
    /// </summary>
    public static readonly IReadOnlyList<string> InstallerExcludedPatterns = new[] { "*.pdb", "Thumbs.db" };

    internal static bool IsSkipped(string file)
    {
        var name = Path.GetFileName(file);
        return Skip.Contains(name) || UninstallerFile.IsMatch(name);
    }

    /// <summary>檔名是否符合安裝程式的排除樣式(<see cref="InstallerExcludedPatterns"/>;只看檔名,不分大小寫)。</summary>
    public static bool IsExcludedByInstaller(string file)
    {
        var name = Path.GetFileName(file.Replace('\\', '/'));
        return InstallerExcludedPatterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
    }

    /// <summary>目錄內會被列進發行清單、安裝程式卻不會安裝的檔案(相對路徑,排序)。</summary>
    public static List<string> FindExcludedByInstaller(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !IsSkipped(f) && IsExcludedByInstaller(f))
            .Select(f => PackagePaths.Relative(dir, f)).OrderBy(p => p, StringComparer.Ordinal).ToList();

    public static ReleaseManifest Build(string dir, string version, string? gitRevision = null, string? gitBranch = null, string? runtime = null,
                                        IReadOnlyDictionary<string, ReleaseComponent>? components = null, IEnumerable<string>? notes = null, TimeProvider? clock = null)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"找不到發行目錄:{dir}");
        PackageVersion.Parse(version);
        // 安裝程式會排除、清單卻會列出的檔案:在產生端擋下(tools/build-release.* 與手動組裝的發行版都經過這裡),不等到安裝後才報「檔案缺少」
        var excluded = FindExcludedByInstaller(dir);
        if (excluded.Count > 0)
        {
            throw new InvalidDataException(
                $"發行目錄內有 {excluded.Count} 個安裝程式不會安裝的檔案(檔名樣式 {string.Join("、", InstallerExcludedPatterns)};tools/installer/SimosaBRM.iss [Files] 的 Excludes):"
                + string.Join("、", excluded.Take(5)) + (excluded.Count > 5 ? $" …等 {excluded.Count} 個" : "")
                + "。這些檔案若列進發行清單,以安裝程式安裝後 release verify --installed 會報「檔案缺少」;請先刪除再產生清單"
                + "(偵錯符號 *.pdb 不進發行版:tools/build-release.* 在 Godot 視景匯出之後、簽章更新包與清單之前刪除 app/ 與 tools/ 之下的 *.pdb)");
        }
        clock ??= TimeProvider.System;
        var m = new ReleaseManifest
        {
            Version = version, GitRevision = gitRevision, GitBranch = gitBranch, Runtime = runtime,
            BuiltUtc = clock.GetUtcNow().UtcDateTime,
            BuiltBy = $"{System.Environment.MachineName} simosa-pkg {Updates.UpdatePackageBuilder.ToolVersion}",
            Files = Scan(dir),
        };
        if (components is not null) foreach (var (k, v) in components) m.Components[k] = v;
        if (notes is not null) m.Notes.AddRange(notes);
        return m;
    }

    /// <summary>寫入 <paramref name="dir"/>/manifest.json(先寫暫存檔再取代)。</summary>
    public static string Save(ReleaseManifest manifest, string dir)
    {
        var path = Path.Combine(dir, ReleaseManifest.FileName);
        PackageJson.SaveAtomic(path, manifest);
        return path;
    }

    public static ReleaseManifest Load(string dir)
    {
        var path = Path.Combine(dir, ReleaseManifest.FileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"發行目錄沒有 {ReleaseManifest.FileName}:{dir}");
        var m = PackageJson.Load<ReleaseManifest>(path, "發行清單");
        if (m.Format != ReleaseManifest.FormatId) throw new InvalidDataException($"發行清單格式不是 {ReleaseManifest.FormatId}:{m.Format}");
        // "files": null(損毀或手動編輯):不是「沒有檔案的發行版」,當成清單損毀回報,不讓呼叫端以未處理例外中止、也不讓空目錄因此「核對通過」
        if (m.Files is null) throw new InvalidDataException("發行清單的 files 不是清單(null):清單損毀");
        return m;
    }

    /// <summary>核對目錄內容與清單是否一致(缺少、雜湊/大小不符、多出的檔案均為錯誤)。</summary>
    public static ReleaseVerification Verify(string dir)
    {
        ReleaseManifest m;
        try { m = Load(dir); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            var failed = new ReleaseVerification();
            failed.Errors.Add(ex.Message);
            return failed;
        }
        var v = new ReleaseVerification { Manifest = m };
        if (!PackageVersion.TryParse(m.Version, out _)) v.Errors.Add($"版本格式不合法:{m.Version}");
        var listed = new Dictionary<string, ReleaseFile>(StringComparer.Ordinal);
        var excludedByInstaller = new List<string>();
        foreach (var f in m.Files)
        {
            // 清單項目為 null 或沒有 path(損毀或手動編輯):列為「清單路徑不合法」,與安裝模式(InstalledVerifier)一致,不以未處理例外中止
            string norm;
            try { norm = PackagePaths.Normalize(f?.Path ?? ""); }
            catch (InvalidDataException ex)
            {
                v.Errors.Add($"清單路徑不合法:{ex.Message}");
                continue;
            }
            if (!listed.TryAdd(norm, f!)) v.Errors.Add($"清單路徑重複:{norm}");
            else if (IsExcludedByInstaller(norm)) excludedByInstaller.Add(norm);
        }
        // 舊版 simosa-pkg 產生的清單可能列有安裝程式不安裝的檔案(*.pdb):這個目錄本身核對得過,但以安裝程式安裝後會報「檔案缺少」
        if (excludedByInstaller.Count > 0)
        {
            v.Warnings.Add($"清單列有 {excludedByInstaller.Count} 個安裝程式不會安裝的檔案(檔名樣式 {string.Join("、", InstallerExcludedPatterns)};例如 {excludedByInstaller[0]}):"
                           + "以安裝程式安裝後 release verify --installed 會把它們報為「檔案缺少」;請刪除這些檔案後以目前的 simosa-pkg 重新產生清單(release manifest)");
        }
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (IsSkipped(file)) continue;
            var rel = PackagePaths.Relative(dir, file);
            present.Add(rel);
            if (!listed.TryGetValue(rel, out var f))
            {
                v.Errors.Add($"清單中沒有的檔案:{rel}");
                continue;
            }
            var size = new FileInfo(file).Length;
            if (size != f.Size) v.Errors.Add($"大小不符:{rel}(清單 {f.Size},實際 {size})");
            else if (!Sha256Hex.Equal(Sha256Hex.OfFile(file), f.Sha256)) v.Errors.Add($"SHA-256 不符:{rel}");
            v.Checked++;
        }
        foreach (var (rel, _) in listed)
            if (!present.Contains(rel)) v.Errors.Add($"檔案缺少:{rel}");
        return v;
    }

    /// <summary>以排序後的相對路徑列出目錄內全部檔案(不含 manifest.json)。</summary>
    public static List<ReleaseFile> Scan(string dir)
    {
        var list = new List<ReleaseFile>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => PackagePaths.Relative(dir, f), StringComparer.Ordinal))
        {
            if (IsSkipped(file)) continue;
            list.Add(new ReleaseFile { Path = PackagePaths.Relative(dir, file), Sha256 = Sha256Hex.OfFile(file), Size = new FileInfo(file).Length });
        }
        return list;
    }
}
