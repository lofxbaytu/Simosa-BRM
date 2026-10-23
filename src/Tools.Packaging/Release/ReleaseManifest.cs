using SimosaBRM.Packaging.Common;

namespace SimosaBRM.Packaging.Release;

/// <summary>
/// 發行版清單 <c>manifest.json</c>(規劃書第 5.4–5.5 節、第 12.1 節 M4 交付「安裝程式」;tools/build-release.* 產生):
/// 記錄版本、git 版次、建置平台、各元件版本與發行目錄內每一個檔案的 SHA-256 與大小,供安裝前核對
/// (<c>simosa-pkg release verify</c>)與日後比對船上版次。清單本身不簽章;正式派送時以 <c>simosa-pkg pack</c>
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
    public int FileCount => Files.Count;
    public long TotalSize => Files.Sum(f => f.Size);
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
/// </summary>
public static class ReleaseManifestBuilder
{
    /// <summary>掃描時略過的檔名(清單本身與作業系統縮圖快取)。</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { ReleaseManifest.FileName, "Thumbs.db", ".DS_Store" };

    public static ReleaseManifest Build(string dir, string version, string? gitRevision = null, string? gitBranch = null, string? runtime = null,
                                        IReadOnlyDictionary<string, ReleaseComponent>? components = null, IEnumerable<string>? notes = null, TimeProvider? clock = null)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"找不到發行目錄:{dir}");
        PackageVersion.Parse(version);
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
        foreach (var f in m.Files)
        {
            string norm;
            try { norm = PackagePaths.Normalize(f.Path); }
            catch (InvalidDataException ex)
            {
                v.Errors.Add($"清單路徑不合法:{ex.Message}");
                continue;
            }
            if (!listed.TryAdd(norm, f)) v.Errors.Add($"清單路徑重複:{norm}");
        }
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (Skip.Contains(Path.GetFileName(file))) continue;
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
            if (Skip.Contains(Path.GetFileName(file))) continue;
            list.Add(new ReleaseFile { Path = PackagePaths.Relative(dir, file), Sha256 = Sha256Hex.OfFile(file), Size = new FileInfo(file).Length });
        }
        return list;
    }
}
