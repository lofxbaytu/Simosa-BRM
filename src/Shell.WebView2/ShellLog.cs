// 外殼 log(<安裝根目錄>/build/logs/shell-<profile>.log;與啟動腳本的 build/logs 同資料夾)。寫入失敗一律忽略,不影響顯示。
namespace SimosaBRM.Shell.App;

internal sealed class ShellLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public ShellLog(string path)
    {
        _path = path;
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); } catch { /* 不可寫時只留主控台 */ }
    }

    public string FilePath => _path;

    public void Write(string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); } catch { /* 忽略 */ }
        }
        try { Console.Error.WriteLine(line); } catch { /* 無主控台 */ }
    }
}
