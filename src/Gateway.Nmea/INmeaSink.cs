using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SimosaBRM.Gateway.Nmea;

/// <summary>NMEA 句型輸出端。</summary>
public interface INmeaSink : IDisposable
{
    void Send(IReadOnlyList<string> sentences);
}

/// <summary>
/// UDP 輸出:每句加 CR LF,同一批句型合成一個資料包送到所有端點
/// (單播 127.0.0.1:10110 給 OpenCPN;多播 239.192.0.1:60001 為 IEC 61162-450 預設傳輸群組,
/// 目前為純文字句型,尚未加 61162-450 的 UdPbC TAG block)。
/// <paramref name="bindAddress"/> 指定時 socket 綁定該位址,且多播由該介面送出(船上筆電直連介面 192.168.77.11;
/// 規劃書第 5.4 節「多播收發一律綁定直連介面的 IP」);未指定時依作業系統路由表。
/// </summary>
public sealed class UdpNmeaSender : INmeaSink
{
    private readonly UdpClient _client;
    private readonly IReadOnlyList<IPEndPoint> _endpoints;

    public UdpNmeaSender(IEnumerable<IPEndPoint> endpoints, IPAddress? bindAddress = null)
    {
        _endpoints = endpoints.ToList();
        _client = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, 0));
        try { _client.Ttl = 1; _client.MulticastLoopback = true; } catch (SocketException) { /* 平台不支援時忽略 */ }
        if (bindAddress is not null && bindAddress.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(bindAddress))
        {
            // 多播送出介面:Windows 依路由表選介面,直連介面沒有閘道時可能選到 Wi-Fi;明確指定與綁定位址相同的介面
            try { _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, bindAddress.GetAddressBytes()); }
            catch (SocketException) { /* 不支援時沿用路由表 */ }
        }
    }

    /// <summary>送出的端點(單播與多播)。</summary>
    public IReadOnlyList<IPEndPoint> Endpoints => _endpoints;

    public void Send(IReadOnlyList<string> sentences)
    {
        if (sentences.Count == 0) return;
        var sb = new StringBuilder(sentences.Count * 80);
        foreach (var s in sentences) sb.Append(s).Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        foreach (var ep in _endpoints)
        {
            try { _client.Send(bytes, bytes.Length, ep); }
            catch (SocketException) { /* 無接收者或網路不可用時忽略 */ }
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// 寫檔輸出(Host <c>--nmea-file</c>;規劃書第 5.2 節 Recorder「另輸出時間戳 NMEA 文字檔」):與 UDP 送出完全相同的句型,
/// 每句一行、CR LF 結尾、ASCII、不加前綴(OpenCPN 的 VDR 外掛與 <c>simosa_brm.nmea_check --file</c> 可直接重播/檢驗;
/// 時間戳由句型本身的 UTC 欄位(GGA/RMC/TTM/ALR 每秒)提供)。每批寫完即 flush(程序異常結束最多遺失一批)。
/// 只在引擎執行緒上呼叫 <see cref="Send"/>;<see cref="Dispose"/> 於引擎執行緒結束後由主執行緒呼叫。
/// </summary>
public sealed class FileNmeaSink : INmeaSink
{
    private readonly StreamWriter _writer;
    private bool _disposed;

    /// <summary>完整路徑</summary>
    public string Path { get; }
    public long SentencesWritten { get; private set; }

    /// <summary>開啟(覆寫)<paramref name="path"/>;父資料夾不存在時建立。</summary>
    public FileNmeaSink(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new ASCIIEncoding()) { NewLine = "\r\n", AutoFlush = false };
    }

    /// <summary>
    /// 解析 <c>--nmea-file</c>(相對路徑以 <paramref name="root"/> 為準),必須在專案資料夾內(CLAUDE.md:所有輸出留在專案內)。
    /// </summary>
    /// <exception cref="ArgumentException">路徑不在專案資料夾內</exception>
    public static string Resolve(string root, string option)
    {
        if (string.IsNullOrWhiteSpace(option)) throw new ArgumentException("--nmea-file 需要路徑");
        var rel = option.Trim();
        var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(rel) ? rel : System.IO.Path.Combine(root, rel));
        var rootFull = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootFull, comparison))
            throw new ArgumentException($"--nmea-file 必須在專案資料夾內({rootFull}):{full}");
        return full;
    }

    public void Send(IReadOnlyList<string> sentences)
    {
        if (_disposed || sentences.Count == 0) return;
        foreach (var s in sentences) _writer.WriteLine(s);
        SentencesWritten += sentences.Count;
        try { _writer.Flush(); } catch (IOException) { /* 磁碟滿等:不影響模擬 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _writer.Dispose(); } catch (IOException) { }
    }
}

/// <summary>把同一批句型送到多個輸出端(UDP + 檔案);任一端的例外不影響其他端。</summary>
public sealed class CompositeNmeaSink : INmeaSink
{
    private readonly IReadOnlyList<INmeaSink> _sinks;
    public CompositeNmeaSink(IEnumerable<INmeaSink> sinks) => _sinks = sinks.ToList();
    public IReadOnlyList<INmeaSink> Sinks => _sinks;

    public void Send(IReadOnlyList<string> sentences)
    {
        foreach (var s in _sinks)
        {
            try { s.Send(sentences); }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        foreach (var s in _sinks) s.Dispose();
    }
}

/// <summary>收集句型(測試用)。</summary>
public sealed class CollectingNmeaSink : INmeaSink
{
    public List<string> Sentences { get; } = new();
    public void Send(IReadOnlyList<string> sentences) => Sentences.AddRange(sentences);
    public void Dispose() { }
}
