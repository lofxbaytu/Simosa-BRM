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
}
