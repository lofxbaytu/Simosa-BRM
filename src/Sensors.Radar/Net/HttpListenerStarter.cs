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
}
