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
    }

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

/// <summary>收集句型(測試用)。</summary>
public sealed class CollectingNmeaSink : INmeaSink
{
    public List<string> Sentences { get; } = new();
    public void Send(IReadOnlyList<string> sentences) => Sentences.AddRange(sentences);
    public void Dispose() { }
}
