using System.Net;
using System.Net.Sockets;

namespace SimosaBRM.Sensors.Radar.Net;

/// <summary>
/// HttpListener 的啟動與丟棄(與 src/SimCore.Host/HttpListenerStarter.cs 相同作法;雷達服務不引用 SimCore.Host,故各自一份)。
/// System.Net.HttpListener 的 <c>Start()</c> 失敗後該物件已不可再用:Linux 的受控實作在失敗時把自己標成已釋放
/// (之後讀 <c>Prefixes</c> 會拋 ObjectDisposedException),而 <c>Close()</c> 又會為沒綁成功的前綴再 bind 一次 →
/// 再拋 HttpListenerException(98)。所以每次嘗試都用新的 HttpListener,失敗的那個只以 <see cref="Discard"/> 關閉並吞掉例外。
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
    /// 先嘗試所有介面(<c>http://*:port/path</c>),Windows 無 URL ACL 時以新的 HttpListener 退回 localhost + 127.0.0.1;
    /// <paramref name="allInterfaces"/> 為 false 時只試 localhost + 127.0.0.1。兩者皆失敗(典型為埠被占用)回 null 並給出原因。
    /// </summary>
    public static HttpListener? StartWithFallback(int port, string path, bool allInterfaces, Action<string>? log, out string prefix, out string? error)
    {
        var wildcard = $"http://*:{port}/{path}";
        var local = $"http://localhost:{port}/{path}";
        var loopback = $"http://127.0.0.1:{port}/{path}"; // HttpListener 以 Host 標頭比對前綴:ws://127.0.0.1 的客戶端需要這一條
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
        catch (Exception) { /* HttpListenerException(98)/ObjectDisposedException:見類別註解 */ }
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
