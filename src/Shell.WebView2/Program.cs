// WebView2 操船台外殼進入點:解析參數(Shell.Core)→ 全螢幕視窗(ShellForm)。結束碼:0 正常、2 參數錯誤。
using SimosaBRM.Shell.Core;

namespace SimosaBRM.Shell.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
