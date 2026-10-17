namespace SimosaBRM.Packaging.Common;

/// <summary>
/// 包內相對路徑的正規化與安全檢查:一律以 '/' 分隔、不得為絕對路徑、不得含磁碟代號或 ".."(防止解壓時寫出安裝目錄,zip slip)。
/// 更新包只允許三個頂層元件:app/(軟體)、data/(船模、情境、參數集、圖資)、config/(信任清單等)。
/// </summary>
public static class PackagePaths
{
    public static readonly string[] UpdateComponents = { "app", "data", "config" };
    public const string TrustedKeysPrefix = "config/trusted-keys/";

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("路徑為空");
        var p = path.Replace('\\', '/').Trim();
        if (p.StartsWith('/') || p.Contains(':') || Path.IsPathRooted(p)) throw new InvalidDataException($"路徑不得為絕對路徑:{path}");
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new InvalidDataException($"路徑為空:{path}");
        foreach (var part in parts)
        {
            if (part is "." or "..") throw new InvalidDataException($"路徑不得含 . 或 ..:{path}");
            if (part.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c != '/' && c != '\\').ToArray()) >= 0 || part.Any(char.IsControl))
                throw new InvalidDataException($"路徑含不合法字元:{path}");
        }
        return string.Join('/', parts);
    }

    /// <summary>更新包內的安裝路徑:正規化並檢查頂層元件。</summary>
    public static string NormalizeInstallPath(string path)
    {
        var p = Normalize(path);
        var top = TopLevel(p);
        if (!UpdateComponents.Contains(top) || p == top)
            throw new InvalidDataException($"安裝路徑須位於 app/、data/ 或 config/ 之下:{path}");
        return p;
    }

    public static string TopLevel(string normalized) => normalized.Split('/')[0];

    /// <summary>相對路徑 → 本機完整路徑,並確認結果仍在 <paramref name="root"/> 之內。</summary>
    public static string Combine(string root, string normalized)
    {
        var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"路徑超出目錄範圍:{normalized}");
        return full;
    }

    /// <summary>目錄內相對路徑(以 '/' 分隔)。</summary>
    public static string Relative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}

/// <summary>
/// 更新包版本:1 至 4 段非負整數(例如 2026.10.3),逐段比較。船上只接受大於「曾套用過的最高版本」的更新包(拒絕降版與重播)。
/// </summary>
public readonly record struct PackageVersion(int[] Parts) : IComparable<PackageVersion>
{
    public static PackageVersion Parse(string text)
    {
        if (!TryParse(text, out var v)) throw new InvalidDataException($"版本格式須為 1 至 4 段數字(例如 2026.10.3):{text}");
        return v;
    }

    public static bool TryParse(string? text, out PackageVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var segs = text.Trim().Split('.');
        if (segs.Length is < 1 or > 4) return false;
        var parts = new int[segs.Length];
        for (var i = 0; i < segs.Length; i++)
        {
            if (segs[i].Length == 0 || !segs[i].All(char.IsAsciiDigit) || !int.TryParse(segs[i], out parts[i])) return false;
        }
        version = new PackageVersion(parts);
        return true;
    }

    public int CompareTo(PackageVersion other)
    {
        var n = Math.Max(Parts?.Length ?? 0, other.Parts?.Length ?? 0);
        for (var i = 0; i < n; i++)
        {
            var a = Parts is not null && i < Parts.Length ? Parts[i] : 0;
            var b = other.Parts is not null && i < other.Parts.Length ? other.Parts[i] : 0;
            if (a != b) return a.CompareTo(b);
        }
        return 0;
    }

    public static int Compare(string a, string b) => Parse(a).CompareTo(Parse(b));

    public override string ToString() => Parts is null ? "" : string.Join('.', Parts);
}
