// WebView2 操船台外殼進入點:解析參數(Shell.Core)→ 全螢幕視窗(ShellForm)。結束碼:0 正常、2 參數錯誤。
using SimosaBRM.Shell.Core;

namespace SimosaBRM.Shell.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 重導向的標準輸出 / 錯誤(啟動腳本收成 shell-*.log / .err.log)以 UTF-8 寫出,不用系統的 OEM 字碼頁
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch (Exception) { /* 沒有主控台(一般啟動方式)時維持預設 */ }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var parsed = ShellOptions.Parse(args);
        if (!parsed.Ok)
        {
            MessageBox.Show(parsed.Error + "\n\n" + ShellOptions.Usage, "Simosa BRM 操船台外殼", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        var options = parsed.Options!;
        if (options.ShowHelp)
        {
            MessageBox.Show(ShellOptions.Usage, "Simosa BRM 操船台外殼", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        var installRoot = ShellPaths.FindInstallRoot(AppContext.BaseDirectory);
        var log = new ShellLog(ShellPaths.LogFile(installRoot, options.ProfileName));
        Application.ThreadException += (_, e) => log.Write($"未處理的例外:{e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write($"未處理的例外(非 UI 執行緒):{e.ExceptionObject}");

        log.Write($"啟動 {string.Join(' ', args)} → {options.BuildUrl()};安裝根目錄 {installRoot}");
        using var form = new ShellForm(options, installRoot, log);
        Application.Run(form);
        log.Write("結束");
        return 0;
    }
}
