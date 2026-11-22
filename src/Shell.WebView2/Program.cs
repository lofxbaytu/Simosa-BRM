// WebView2 操船台外殼進入點:解析參數(Shell.Core)→ 全螢幕視窗(ShellForm)。
// 結束碼:0 正常、2 參數錯誤或使用者資料夾(--profile)位置被本機資料守則拒絕(安裝根目錄外的系統磁碟;docs/ops/本機資料位置.md 第 4、5 節)。
using SimosaBRM.Shell.Core;

namespace SimosaBRM.Shell.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 重導向的標準輸出 / 錯誤(啟動腳本收成 shell-*.log / .err.log)以 UTF-8 寫出,不用系統的 OEM 字碼頁
        // (WinExe 沒有主控台:設定 Console.OutputEncoding 會失敗而維持 OEM 字碼頁,所以對「已重導向」的串流直接換成 UTF-8 寫入器)
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        }
        catch (Exception) { /* 沒有可用的標準串流時維持預設 */ }
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

        var foundRoot = ShellPaths.TryFindInstallRoot(AppContext.BaseDirectory);
        var installRoot = foundRoot ?? Path.GetFullPath(AppContext.BaseDirectory);   // 同 ShellPaths.FindInstallRoot 找不到時的回傳值
        var log = new ShellLog(ShellPaths.LogFile(installRoot, options.ProfileName));
        Application.ThreadException += (_, e) => log.Write($"未處理的例外:{e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write($"未處理的例外(非 UI 執行緒):{e.ExceptionObject}");

        log.Write($"啟動 {string.Join(' ', args)} → {options.BuildUrl()};安裝根目錄 {installRoot}{(foundRoot is null ? "(找不到含 data\\ 的上層,以程式所在資料夾代替;--profile 的相對路徑以目前目錄為準)" : "")}");
        // 使用者資料夾(--profile 的相對路徑以安裝根目錄為準):在安裝根目錄外而且位於系統磁碟時拒絕(本機資料守則;經同意設 SIMOSA_ALLOW_SYSTEM_DRIVE=1)
        var profile = ShellPaths.ProfileDirectory(options.Profile, foundRoot, installRoot, options.ProfileName, SimosaBRM.SimCore.Ship.LocalDataEnvironment.FromProcess());
        if (!profile.Allowed)
        {
            var message = SimosaBRM.SimCore.Ship.LocalDataGuard.RejectionMessage("--profile(WebView2 使用者資料夾)", profile);
            log.Write($"拒絕啟動:{message}");
            MessageBox.Show(message, "Simosa BRM 操船台外殼", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        if (profile.Verdict == SimosaBRM.SimCore.Ship.LocalDataVerdict.AllowedBySwitch)
            log.Write(SimosaBRM.SimCore.Ship.LocalDataGuard.ConsentNotice("--profile(WebView2 使用者資料夾)", profile));
        using var form = new ShellForm(options, installRoot, profile.Path, log);
        Application.Run(form);
        log.Write("結束");
        return 0;
    }
}
