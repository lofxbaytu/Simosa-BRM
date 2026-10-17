namespace SimosaBRM.SimCore.Ship;

/// <summary>
/// 找到專案根目錄(含 Directory.Build.props 與 data/ 的資料夾),讓建置輸出位於 build/bin/... 的程式與測試
/// 都能以相對路徑讀 data/ships、data/scenarios,並把紀錄寫到 build/records(CLAUDE.md:所有輸出留在專案內)。
/// </summary>
public static class RepositoryPaths
{
    /// <summary>自 start(預設為執行檔所在目錄)往上找;找不到時回傳 null。</summary>
    public static string? TryFindRoot(string? start = null)
    {
        var env = System.Environment.GetEnvironmentVariable("SIMOSA_BRM_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "data"))) return Path.GetFullPath(env);

        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(dir.FullName, "data")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static string FindRoot(string? start = null)
        => TryFindRoot(start) ?? throw new DirectoryNotFoundException("找不到專案根目錄(需含 Directory.Build.props 與 data/);可設定環境變數 SIMOSA_BRM_ROOT");

    public static string RecordsDirectory(string? root = null) => Path.Combine(root ?? FindRoot(), "build", "records");

    /// <summary>
    /// 專案內的檔案改為相對專案根目錄的路徑(以 '/' 分隔,例如 data/parameters/company-procedures.yaml),
    /// 供紀錄標頭等對外輸出(不洩漏機器路徑、紀錄可跨機器比對);不在專案內或找不到根目錄時原樣回傳。
    /// </summary>
    public static string ToProjectRelative(string path, string? root = null)
    {
        root ??= TryFindRoot();
        if (root is null) return path;
        var full = Path.GetFullPath(path);
        var rel = Path.GetRelativePath(root, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return path;
        return rel.Replace('\\', '/');
    }
}
