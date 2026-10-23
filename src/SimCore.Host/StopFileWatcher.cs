namespace SimosaBRM.SimCore.Host;

/// <summary>
/// 停止檔(<c>--stop-file</c>;預設 build/run/simcore.stop):主控程序每秒檢查該檔是否存在,存在即刪除並如同 Ctrl+C 正常結束(寫完紀錄 footer)。
/// 供 tools/launch/stop-all.{ps1,sh} 使用 — Windows 的 Stop-Process 不送 Ctrl+C,主控台程序收不到訊號,停止檔是跨平台的優雅停止途徑。
/// 路徑必須在專案(安裝根目錄)內(CLAUDE.md:輸出不離開專案資料夾);啟動時殘留的舊停止檔先刪除,不視為停止請求。
/// </summary>
public sealed class StopFileWatcher
{
    public const string DefaultRelativePath = "build/run/simcore.stop";

    public string Path { get; }
    /// <summary>啟動時刪掉的殘留停止檔(上次 stop-all 寫了但主控程序沒有消化)</summary>
    public bool RemovedStale { get; }

    private StopFileWatcher(string path, bool removedStale)
    {
        Path = path;
        RemovedStale = removedStale;
    }

    /// <summary>
    /// 解析 <paramref name="option"/>(null = 預設;相對路徑以 <paramref name="root"/> 為準),驗證在根目錄內並建立父資料夾;殘留檔即刪除。
    /// </summary>
    /// <exception cref="ArgumentException">路徑不在專案資料夾內</exception>
    public static StopFileWatcher Create(string root, string? option = null)
    {
        var path = Resolve(root, option);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var stale = false;
        if (File.Exists(path))
        {
            try { File.Delete(path); stale = true; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return new StopFileWatcher(path, stale);
    }

    /// <summary>完整路徑;不在 <paramref name="root"/> 內時丟 ArgumentException。</summary>
    public static string Resolve(string root, string? option = null)
    {
        var rel = string.IsNullOrWhiteSpace(option) ? DefaultRelativePath : option.Trim();
        var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(rel) ? rel : System.IO.Path.Combine(root, rel));
        var rootFull = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull, comparison))
            throw new ArgumentException($"--stop-file 必須在專案資料夾內({rootFull}):{full}");
        return full;
    }

    /// <summary>檢查一次:停止檔存在即刪除並回傳 true。</summary>
    public bool Check()
    {
        if (!File.Exists(Path)) return false;
        try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>每 <paramref name="period"/> 輪詢直到偵測到停止檔(呼叫 <paramref name="onStop"/> 後結束)或取消。</summary>
    public async Task RunAsync(TimeSpan period, Action onStop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Check()) { onStop(); return; }
            try { await Task.Delay(period, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
