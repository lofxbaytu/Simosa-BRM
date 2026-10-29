// WebView2 全螢幕視窗(規劃書第 5.2 節「WebView2 殼」;需求 R1 操船台、R13 船上部署):
//   - 無邊框全螢幕(--kiosk 另加最上層);F11 切換全螢幕/視窗;Esc 不做任何事(不退出);Alt+F4/關閉鈕需確認;視窗關閉即結束程式。
//   - WebView2 使用者資料夾放在安裝根目錄 build/webview2/<profile>(或 --profile),不寫 C:\ 使用者目錄。
//   - Runtime 未安裝/建立環境失敗:以中文說明(Shell.Core RuntimeCheck)取代網頁,附離線安裝檔路徑與「重試」。
//   - 頁面載入失敗(網頁伺服尚未啟動、斷線):指數退避自動重試(ReloadBackoff),狀態列顯示次數;成功即隱藏。
//   - 右鍵選單、DevTools、瀏覽器快捷鍵、縮放、狀態列預設關閉(--allow-devtools 開啟);新視窗請求改在本視窗導向。
//   - 視窗標題「Simosa BRM 操船台 — 駕駛台 bridge」等(ShellOptions.WindowTitle),網頁標題附在後面。
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SimosaBRM.Shell.Core;

namespace SimosaBRM.Shell.App;

internal sealed class ShellForm : Form
{
    private readonly ShellOptions _options;
    private readonly string _installRoot;
    private readonly ShellLog _log;
    private readonly string _url;
    private readonly string _profileDir;
    private readonly ReloadBackoff _backoff = new();
    private readonly System.Windows.Forms.Timer _retryTimer = new();

    private readonly WebView2 _web = new();
    private readonly Label _status = new();          // 載入中/重試中的狀態列(成功後隱藏)
    private readonly Panel _fallback = new();        // Runtime 未安裝時的說明畫面
    private readonly TextBox _fallbackText = new();
    private readonly Button _retryButton = new();
    private readonly Button _exitButton = new();

    private bool _fullscreen;
    private bool _closeConfirmed;
    private string _documentTitle = "";
    private Rectangle _windowedBounds;

    public ShellForm(ShellOptions options, string installRoot, ShellLog log)
    {
        _options = options;
        _installRoot = installRoot;
        _log = log;
        _url = options.BuildUrl();
        _profileDir = ShellPaths.ResolveProfileDirectory(options.Profile, installRoot, options.ProfileName);

        Text = options.WindowTitle;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        var screen = PickScreen(options.Monitor);
        _windowedBounds = Rectangle.Inflate(screen.WorkingArea, -screen.WorkingArea.Width / 16, -screen.WorkingArea.Height / 16);
        Bounds = _windowedBounds;

        // 狀態列(載入/重試訊息)
        _status.Dock = DockStyle.Top;
        _status.Height = 28;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(8, 0, 8, 0);
        _status.BackColor = Color.FromArgb(40, 40, 40);
        _status.ForeColor = Color.Gainsboro;
        _status.Font = new Font(FontFamily.GenericSansSerif, 10f);
        _status.Visible = false;

        // WebView2
        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.Black;
        _web.CoreWebView2InitializationCompleted += OnCoreInitialized;
        _web.NavigationCompleted += OnNavigationCompleted;
        _web.KeyDown += OnWebKeyDown;

        // Runtime 未安裝的說明畫面
        BuildFallbackPanel();

        Controls.Add(_web);
        Controls.Add(_status);
        Controls.Add(_fallback);
        _fallback.Visible = false;

        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); NavigateToPage(); };

        Load += async (_, _) =>
        {
            if (options.Kiosk) EnterFullscreen(); else WindowState = FormWindowState.Maximized;
            await InitializeWebViewAsync();
        };
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => { _retryTimer.Dispose(); };
    }

    // ---------------------------------------------------------------- 初始化與 Runtime 偵測

    private async Task InitializeWebViewAsync()
    {
        _fallback.Visible = false;
        _web.Visible = true;
        ShowStatus("載入中…");
        string? version = null;
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var check = RuntimeCheck.Evaluate(version, null, _installRoot);
            if (!check.Available) { ShowFallback(check); return; }
            _log.Write($"{check.Message};使用者資料夾 {_profileDir}");
            Directory.CreateDirectory(_profileDir);
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                Language = "zh-TW",
                // 警報聲(BAM 可聽警報、BNWAS)不需使用者先點擊;船上無網際網路,不需 SmartScreen
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --disable-features=msSmartScreenProtection",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, _profileDir, envOptions);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            _log.Write($"WebView2 初始化失敗:{ex}");
            ShowFallback(RuntimeCheck.Evaluate(version, ex, _installRoot));
        }
    }

    private void OnCoreInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            _log.Write($"CoreWebView2 初始化失敗:{e.InitializationException}");
            ShowFallback(RuntimeCheck.Evaluate(null, e.InitializationException, _installRoot));
            return;
        }
        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = _options.AllowDevTools;
        s.AreDevToolsEnabled = _options.AllowDevTools;
        s.AreBrowserAcceleratorKeysEnabled = _options.AllowDevTools;   // Ctrl+N/T/F 等瀏覽器快捷鍵;F5/F11 由本視窗處理
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;   // 失敗時由狀態列顯示並自動重試
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };   // target=_blank 也留在本視窗
        core.DocumentTitleChanged += (_, _) => { _documentTitle = core.DocumentTitle ?? ""; UpdateTitle(); };
        core.ProcessFailed += (_, a) =>
        {
            _log.Write($"WebView2 程序失敗:{a.ProcessFailedKind} {a.Reason}");
            if (a.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                ScheduleRetry("程序失敗");
        };
        NavigateToPage();
    }

    private void NavigateToPage()
    {
        if (_web.CoreWebView2 is null) return;
        ShowStatus(_backoff.Attempt == 0 ? "載入中…" : $"重試第 {_backoff.Attempt} 次…");
        try { _web.CoreWebView2.Navigate(_url); }
        catch (Exception ex) { _log.Write($"Navigate 失敗:{ex.Message}"); ScheduleRetry(ex.Message); }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            if (_backoff.Attempt > 0) _log.Write($"載入成功(重試 {_backoff.Attempt} 次後)");
            _backoff.Reset();
            HideStatus();
            return;
        }
        ScheduleRetry(e.WebErrorStatus.ToString());
    }

    private void ScheduleRetry(string reason)
    {
        var delay = _backoff.NextDelay();
        var text = ReloadBackoff.Describe(_backoff.Attempt, delay, reason);
        _log.Write($"{text};{_url}");
        ShowStatus(text + "(網頁伺服尚未啟動?start-ship-laptop.ps1 會先啟動 simosa-web)");
        _retryTimer.Interval = Math.Max(100, (int)delay.TotalMilliseconds);
        _retryTimer.Start();
    }

    // ---------------------------------------------------------------- 鍵盤、全螢幕、關閉

    private void OnWebKeyDown(object? sender, KeyEventArgs e) => HandleShellKey(e);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        HandleShellKey(e);
        if (!e.Handled) base.OnKeyDown(e);
    }

    /// <summary>
    /// F11 切換全螢幕、F5 重新載入、Alt+F4 關閉(經確認);Esc 不做任何事(交給網頁)。
    /// WebView2 的快捷鍵事件在瀏覽器程序被阻塞的情況下觸發(此時呼叫 CoreWebView2 API 可能丟例外、開對話框會卡住瀏覽器),
    /// 所以只標記 Handled,實際動作以 BeginInvoke 排到訊息迴圈再做。
    /// </summary>
    private void HandleShellKey(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11 && !e.Alt && !e.Control)
        {
            e.Handled = true; e.SuppressKeyPress = true;
            BeginInvoke(() => { if (_fullscreen) LeaveFullscreen(); else EnterFullscreen(); });
        }
        else if (e.KeyCode == Keys.F5 && !e.Alt)
        {
            e.Handled = true; e.SuppressKeyPress = true;
            BeginInvoke(() => { _retryTimer.Stop(); _backoff.Reset(); NavigateToPage(); });
        }
        else if (e.KeyCode == Keys.F4 && e.Alt)
        {
            e.Handled = true; e.SuppressKeyPress = true;
            BeginInvoke(Close);
        }
    }

    private void EnterFullscreen()
    {
        if (!_fullscreen && WindowState != FormWindowState.Minimized) _windowedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _fullscreen = true;
        var screen = Screen.FromControl(this);
        if (_options.Monitor is int m && m >= 0 && m < Screen.AllScreens.Length) screen = Screen.AllScreens[m];
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        Bounds = screen.Bounds;
        TopMost = _options.Kiosk;
        UpdateTitle();
    }

    private void LeaveFullscreen()
    {
        _fullscreen = false;
        TopMost = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        Bounds = _windowedBounds;
        WindowState = FormWindowState.Maximized;
        UpdateTitle();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Windows 關機/登出與程式自身結束不詢問;使用者關閉(Alt+F4、關閉鈕)需確認,避免練習中誤關
        if (_closeConfirmed || _options.NoCloseConfirm || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.ApplicationExitCall or CloseReason.TaskManagerClosing) return;
        var wasTopMost = TopMost;
        TopMost = false;
        var r = MessageBox.Show(this, $"確定要關閉「{_options.WindowTitle}」?\n練習進行中請先在教官站結束練習。", "Simosa BRM 操船台外殼",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) { e.Cancel = true; TopMost = wasTopMost; return; }
        _closeConfirmed = true;
        _log.Write("使用者確認關閉");
    }

    // ---------------------------------------------------------------- 顯示

    private void UpdateTitle()
    {
        var t = _options.WindowTitle;
        if (!string.IsNullOrWhiteSpace(_documentTitle) && !_documentTitle.StartsWith("Simosa BRM", StringComparison.Ordinal)) t += " — " + _documentTitle;
        if (_backoff.Attempt > 0) t += $"(重試 {_backoff.Attempt})";
        Text = t;
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.Visible = true;
        UpdateTitle();
    }

    private void HideStatus()
    {
        _status.Visible = false;
        UpdateTitle();
    }

    private void ShowFallback(RuntimeCheckResult check)
    {
        _log.Write($"{check.Status}:{check.Message.Replace('\n', ' ')}");
        _retryTimer.Stop();
        _web.Visible = false;
        _status.Visible = false;
        _fallbackText.Text = check.Message + Environment.NewLine + Environment.NewLine +
            $"網址:{_url}" + Environment.NewLine + $"使用者資料夾:{_profileDir}" + Environment.NewLine + $"log:{_log.FilePath}";
        _fallback.Visible = true;
        _fallback.BringToFront();
        _retryButton.Focus();
    }

    private void BuildFallbackPanel()
    {
        _fallback.Dock = DockStyle.Fill;
        _fallback.BackColor = Color.FromArgb(24, 24, 24);
        _fallback.Padding = new Padding(40);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = "Simosa BRM 操船台外殼 — 無法顯示網頁",
            ForeColor = Color.Orange,
            Font = new Font(FontFamily.GenericSansSerif, 18f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 16),
        };
        _fallbackText.Multiline = true;
        _fallbackText.ReadOnly = true;
        _fallbackText.ScrollBars = ScrollBars.Vertical;
        _fallbackText.Dock = DockStyle.Fill;
        _fallbackText.BackColor = Color.FromArgb(32, 32, 32);
        _fallbackText.ForeColor = Color.Gainsboro;
        _fallbackText.Font = new Font(FontFamily.GenericSansSerif, 12f);
        _fallbackText.BorderStyle = BorderStyle.None;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 16, 0, 0) };
        _retryButton.Text = "重試(已安裝 WebView2 Runtime 後)";
        _retryButton.AutoSize = true;
        _retryButton.Padding = new Padding(12, 6, 12, 6);
        _retryButton.Click += async (_, _) => await InitializeWebViewAsync();
        _exitButton.Text = "離開";
        _exitButton.AutoSize = true;
        _exitButton.Padding = new Padding(12, 6, 12, 6);
        _exitButton.Click += (_, _) => { _closeConfirmed = true; Close(); };
        buttons.Controls.Add(_retryButton);
        buttons.Controls.Add(_exitButton);

        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(_fallbackText, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        _fallback.Controls.Add(layout);
    }

    private static Screen PickScreen(int? monitor)
    {
        var screens = Screen.AllScreens;
        if (monitor is int m && m >= 0 && m < screens.Length) return screens[m];
        return Screen.PrimaryScreen ?? screens[0];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _retryTimer.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}
