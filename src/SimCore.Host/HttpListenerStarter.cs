using System.Net;
using System.Net.Sockets;

namespace SimosaBRM.SimCore.Host;

/// <summary>
/// HttpListener 的啟動與丟棄(交接紀錄第二十四輪待辦第 1 項:埠被占用時的優雅降級)。
/// <para>
/// System.Net.HttpListener 的 <c>Start()</c> 失敗後該物件已不可再用:Linux 的受控實作在失敗時把自己標成已釋放
/// (之後讀 <c>Prefixes</c> 會拋 ObjectDisposedException),而 <c>Close()</c> 又會為每個前綴再呼叫一次 GetEPListener,
/// 對沒綁成功的前綴等於再 bind 一次 → 再拋 HttpListenerException(98)。所以每次嘗試都用**新的** HttpListener,
/// 失敗的那個只以 <see cref="Discard"/> 關閉並吞掉一切例外。
/// </para>
/// </summary>
internal static class HttpListenerStarter
{
    /// <summary>以新的 HttpListener 監聽一組前綴;成功回傳已 Start 的 listener,失敗回傳 null 並給出原因(該 listener 已丟棄)。</summary>
    public static HttpListener? TryListen(IReadOnlyList<string> prefixes, out string? error)
    {
        var listener = new HttpListener();
        try
        {
            foreach (var p in prefixes) listener.Prefixes.Add(p);
            listener.Start();
            error = null;
            return listener;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or SocketException or ArgumentException)
        {
            Discard(listener);
            error = ex is HttpListenerException hle ? $"{hle.Message}(錯誤碼 {hle.ErrorCode})" : ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 先嘗試所有介面(<c>http://*:port/path</c>);Windows 上非管理員且未設定 URL ACL 時 HttpListener 會拒絕萬用前綴,
    /// 此時以新的 HttpListener 退回只監聽 localhost + 127.0.0.1(HttpListener 以 Host 標頭比對前綴:ws://127.0.0.1 的客戶端也要能連)。
    /// 兩者皆失敗(典型為埠被占用:同機第二個核心程序或殘留程序)回傳 null,<paramref name="error"/> 為兩次失敗的原因,
    /// 呼叫端據此印「埠 N 被占用」並決定是停用該選配服務還是結束。
    /// </summary>
    /// <param name="port">埠</param>
    /// <param name="path">前綴路徑(空字串或以 / 結尾,例如 "radar/")</param>
    /// <param name="allInterfaces">false = 不試萬用前綴,只監聽 localhost + 127.0.0.1</param>
    /// <param name="log">退回 localhost 時的提示(含 netsh 指令)</param>
    /// <param name="prefix">實際監聽的代表前綴(萬用或 localhost)</param>
    /// <param name="error">失敗原因(成功為 null)</param>
    public static HttpListener? StartWithFallback(int port, string path, bool allInterfaces, Action<string>? log, out string prefix, out string? error)
    {
        var wildcard = $"http://*:{port}/{path}";
        var local = $"http://localhost:{port}/{path}";
        var loopback = $"http://127.0.0.1:{port}/{path}";
        string? wildcardError = null;
        if (allInterfaces)
        {
            if (TryListen(new[] { wildcard }, out wildcardError) is { } all)
            {
                prefix = wildcard;
                error = null;
                return all;
            }
        }
        if (TryListen(new[] { local, loopback }, out var localError) is { } loop)
        {
            prefix = local;
            error = null;
            if (allInterfaces)
                log?.Invoke($"無法監聽 {wildcard}({wildcardError});改為只監聽 {local}。遠端站別需設定 URL ACL:netsh http add urlacl url={wildcard} user=Everyone");
            return loop;
        }
        prefix = allInterfaces ? wildcard : local;
        error = allInterfaces
            ? $"{wildcard} → {wildcardError};{local} → {localError}"
            : $"{local} → {localError}";
        return null;
    }

    /// <summary>關閉 listener 並吞掉所有例外(Start 失敗後的物件、或已釋放的物件都可安全呼叫;可重複呼叫)。</summary>
    public static void Discard(HttpListener? listener)
    {
        if (listener is null) return;
        try { listener.Close(); }
        catch (Exception) { /* HttpListenerException(98)/ObjectDisposedException:Start 失敗的 listener 關閉時會再 bind 一次,見類別註解 */ }
    }

    /// <summary>
    /// 關機時立即停止監聽。<b>不要用 <c>Stop()</c></b>:Windows(http.sys)上仍有連線(尚未關閉的 WebSocket 客戶端,例如瀏覽器分頁、
    /// 教官筆電、視景)時,Stop() 會等那些連線結束才返回 — 核心卡在關機流程,被 stop-all 逾時強制結束,紀錄沒有 footer
    /// (2026-11-22 Windows CI 實測:Edge 分頁連著時 10 秒內關不掉;Linux 的受控實作沒有這個行為,所以容器內測不到)。
    /// Abort() 立即丟棄佇列中的請求並關閉;呼叫前先 Abort 各客戶端的 WebSocket。可重複呼叫,不拋例外。
    /// </summary>
    public static void AbortNow(HttpListener? listener)
    {
        if (listener is null) return;
        try { listener.Abort(); }
        catch (Exception) { /* 已釋放或 Start 失敗的 listener */ }
    }

    /// <summary>
    /// WebSocket 連線結束後釋放 HttpListener 的連線(在 <c>WebSocket.Dispose()</c> 之後呼叫;第三十輪審查 C-F1,docs/design/testing.md 第 6.6 節)。
    /// Linux / macOS 的受控 HttpListener:<c>AcceptWebSocketAsync</c> 的 WebSocket 建在不擁有 socket 的 NetworkStream 上,<c>WebSocket.Dispose()</c>
    /// 不關 socket,HttpConnection / HttpListenerContext / Socket 一直留在 listener 的連線表(每條關閉的連線 1 個檔案描述元與約 15 KB 受控記憶體,
    /// 伺服端也不關 TCP);<c>Response.Abort()</c> 才關閉 socket 並從連線表移除。Windows(http.sys)是另一套實作,是否有同樣問題、Abort() 有無副作用
    /// 都未實機驗證,所以不呼叫。不拋例外。
    /// </summary>
    public static void ReleaseWebSocketConnection(HttpListenerContext ctx)
    {
        if (OperatingSystem.IsWindows()) return;
        try { ctx.Response.Abort(); }
        catch (Exception) { /* 已關閉 */ }
    }

    /// <summary>
    /// 接受迴圈(狀態 WebSocket、VHF、錄影上傳端共用;第三十輪 2 小時搜尋 host-accept-loop-death):只有權杖取消或 listener 已關閉才結束。
    /// <para>
    /// 修正前各伺服器在迴圈本體裡 <c>await</c> 健康檢查的回應:客戶端送出 <c>GET /</c> 後立刻 RST,寫出丟例外跳出迴圈,接受迴圈結束且沒有 log,
    /// 之後所有站都連不上(Linux 實測:16 次嘗試 12 次觸發;死掉後每次重連都在受控 HttpListener 內部留下 1 條永遠沒人取走的連線)。
    /// 現在 <paramref name="dispatch"/> 必須同步返回(單一請求的處理一律放到背景,例如 <see cref="RespondTextAsync"/> 或 <c>Task.Run</c>);
    /// 取得 context 或 dispatch 時的任何其他例外記 log(有次數限制)後繼續接受,dispatch 失敗的那個請求以 Abort 收尾。
    /// </para>
    /// </summary>
    /// <param name="listener">已 Start 的 listener</param>
    /// <param name="ct">伺服器的權杖(取消 = 關機)</param>
    /// <param name="dispatch">把一個請求交給背景處理(不可 await 該請求的 I/O)</param>
    /// <param name="log">錯誤訊息</param>
    /// <param name="name">伺服器名稱(log 用)</param>
    public static async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct, Action<HttpListenerContext> dispatch, Action<string>? log, string name)
    {
        long errors = 0;
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException ex)
            {
                // 單一連線的錯誤(Windows:客戶端在請求送完前斷線等):照舊繼續接受
                LogLimited(log, ++errors, $"{name} 接受連線失敗(HttpListenerException {ex.ErrorCode}:{ex.Message}),繼續接受");
                continue;
            }
            catch (Exception ex)
            {
                // 其他非關機的例外:記 log 後繼續;稍候再接受,避免同一個錯誤空轉占滿 CPU 與 log
                LogLimited(log, ++errors, $"{name} 接受連線失敗({ex.GetType().Name}:{ex.Message}),繼續接受");
                try { await Task.Delay(50, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            try { dispatch(ctx); }
            catch (Exception ex)
            {
                LogLimited(log, ++errors, $"{name} 處理請求失敗({ex.GetType().Name}:{ex.Message}),中止該連線後繼續接受");
                AbortResponse(ctx.Response);
            }
        }
    }

    /// <summary>前 10 次與之後每 1000 次記一行(壞掉的客戶端反覆觸發時不灌爆 log)。</summary>
    private static void LogLimited(Action<string>? log, long count, string message)
    {
        if (count <= 10 || count % 1000 == 0) log?.Invoke(count > 10 ? $"{message}(累計 {count} 次)" : message);
    }

    /// <summary>
    /// 回一段純文字(健康檢查、404 等)並關閉回應;寫出失敗(客戶端已 RST、已斷線)時 Abort,**不拋例外**。
    /// 以 <c>_ = RespondTextAsync(...)</c> 在背景執行,不在接受迴圈裡 await(見 <see cref="AcceptLoopAsync"/>)。
    /// </summary>
    public static async Task RespondTextAsync(HttpListenerContext ctx, int status, string text)
    {
        var res = ctx.Response;
        try
        {
            var body = System.Text.Encoding.UTF8.GetBytes(text);
            res.StatusCode = status;
            res.ContentType = "text/plain; charset=utf-8";
            res.ContentLength64 = body.Length;
            await res.OutputStream.WriteAsync(body).ConfigureAwait(false);
            res.Close();
        }
        catch (Exception) { AbortResponse(res); }
    }

    /// <summary>
    /// 只回狀態碼(握手失敗 500、來源不允許 403)並關閉;設定狀態碼或關閉失敗(標頭已送出、連線已斷)時 Abort。不拋例外。
    /// 修正前直接 <c>StatusCode = …; Close()</c>:Close 丟例外時這條連線既沒關也沒 Abort,留在 Linux 受控 HttpListener 的連線表。
    /// </summary>
    public static void CloseResponse(HttpListenerResponse res, int status)
    {
        try
        {
            res.StatusCode = status;
            res.Close();
        }
        catch (Exception) { AbortResponse(res); }
    }

    /// <summary>中止回應並關閉連線(錯誤路徑的最後一步;已關閉或已中止時無作用)。不拋例外。</summary>
    public static void AbortResponse(HttpListenerResponse res)
    {
        try { res.Abort(); }
        catch (Exception) { /* 已關閉或已釋放 */ }
    }

    /// <summary>等背景迴圈結束,但不超過 <paramref name="timeout"/>(關機流程不可被卡住:之後還要寫紀錄 footer);逾時回傳 false。</summary>
    public static async Task<bool> WaitBoundedAsync(Task? task, TimeSpan timeout)
    {
        if (task is null) return true;
        try { await task.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// 在背景中止並關閉 listener,最多等 <paramref name="timeout"/>;逾時回傳 false(該背景工作不阻止程序結束)。
    /// 連 Abort() / Close() 本身也放在背景:關機流程的任何一步都不可無限期阻塞(之後還要寫紀錄 footer)。
    /// </summary>
    public static Task<bool> ShutdownAsync(HttpListener? listener, TimeSpan timeout)
        => listener is null
            ? Task.FromResult(true)
            : WaitBoundedAsync(Task.Run(() => { AbortNow(listener); Discard(listener); }), timeout);

    /// <summary>
    /// 關機的第一步:取消伺服器的權杖並中止各客戶端的 WebSocket — <b>全部放到執行緒集區並設等待上限</b>,呼叫端的同步段不做任何可能阻塞的事。
    /// Windows(http.sys)的 HttpListener WebSocket 上,<c>WebSocket.Abort()</c> 會取得內部鎖並同步清理送出佇列,對端不讀取或正在
    /// 握手/傳送時可能卡住;而 <c>CancellationTokenSource.Cancel()</c> 會在呼叫端執行緒上同步執行各個取消回呼 —— 傳給
    /// ReceiveAsync/SendAsync 的權杖其回呼就是 Abort()。兩者若直接在 DisposeAsync 的同步段呼叫,卡住時連外層的等待上限都還沒開始計時:
    /// 核心停在關機流程、被 stop-all 逾時強制結束、紀錄沒有 footer(2026-11-22 Windows CI:教官筆電剛開好分頁就停止時發生;
    /// 同一天 Windows 的單元測試也曾卡住不返回)。每個客戶端各自一個工作,一個卡住不影響其他;逾時回傳 false(留在背景,不阻止程序結束)。
    /// </summary>
    public static Task<bool> StopClientsAsync(CancellationTokenSource? cts, IEnumerable<System.Net.WebSockets.WebSocket> sockets, TimeSpan timeout)
    {
        var work = new List<Task>();
        if (cts is not null)
            work.Add(Task.Run(() => { try { cts.Cancel(); } catch (Exception) { /* 已釋放 */ } }));
        foreach (var socket in sockets.ToList())
            work.Add(Task.Run(() => { try { socket.Abort(); } catch (Exception) { /* 已關閉或已釋放 */ } }));
        return WaitBoundedAsync(Task.WhenAll(work), timeout);
    }
}
