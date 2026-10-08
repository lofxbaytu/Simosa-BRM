using System.Net;
using System.Net.Sockets;

namespace SimosaBRM.SimCore.Host;

/// <summary>
/// UDP 多播狀態匯流排(規劃書第 5.2 節 State Bus:25 Hz;目前為 JSON,日後可換 MessagePack):
/// 每幀一個資料包送到 239.255.70.1:7001。視景、雷達等訂閱者各自加入群組。
/// </summary>
public sealed class UdpStateBus : IDisposable
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _endpoint;
    public long FramesSent { get; private set; }

    public UdpStateBus(string group, int port, IPAddress? bindAddress = null)
    {
        _endpoint = new IPEndPoint(IPAddress.Parse(group), port);
        _client = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, 0));
        try { _client.Ttl = 1; _client.MulticastLoopback = true; } catch (SocketException) { }
    }

    public void Publish(byte[] frame)
    {
        try
        {
            _client.Send(frame, frame.Length, _endpoint);
            FramesSent++;
        }
        catch (SocketException) { /* 無網路介面時忽略 */ }
    }

    public void Dispose() => _client.Dispose();
}
