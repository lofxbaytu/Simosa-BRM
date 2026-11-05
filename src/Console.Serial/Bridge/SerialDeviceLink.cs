using System.IO.Ports;
using System.Text;

namespace SimosaBRM.ConsoleSerial.Bridge;

/// <summary>
/// 序列埠裝置連結(System.IO.Ports;只在 --port 時建立):115200 8N1、無流量控制、ASCII;以 BaseStream 非同步讀,行處理同 <see cref="StreamDeviceLink"/>。
/// 讀寫例外(拔線)由 <see cref="ConsoleBridge"/> 記 log 後重新開啟(每秒重試)。
/// </summary>
public sealed class SerialDeviceLink : IDeviceLink
{
    private readonly SerialPort _port;
    private readonly StreamDeviceLink _inner;

    private SerialDeviceLink(SerialPort port)
    {
        _port = port;
        _inner = new StreamDeviceLink(port.BaseStream, port.BaseStream, $"序列埠 {port.PortName} {port.BaudRate} 8N1", pollAtEof: true);
    }

    public string Description => _inner.Description;

    public static SerialDeviceLink Open(string portName, int baud)
    {
        var p = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            Encoding = Encoding.ASCII,
            NewLine = "\n",
            DtrEnable = true,   // Teensy USB CDC:DTR 為真時韌體 Serial 才視為已連線
            RtsEnable = true,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 1000,
        };
        p.Open();
        try { p.DiscardInBuffer(); } catch (Exception) { }
        return new SerialDeviceLink(p);
    }

    /// <summary>目前系統的序列埠名稱(診斷/--help 用)。</summary>
    public static string[] PortNames()
    {
        try { return SerialPort.GetPortNames(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>讀一行;Windows 的 SerialStream 不以 CancellationToken 中止進行中的讀取,故取消時關閉埠讓讀取以例外返回(視為連結結束)。</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { if (_port.IsOpen) _port.Close(); } catch (Exception) { } });
        try { return await _inner.ReadLineAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { return null; }
    }
    public Task WriteLineAsync(string line, CancellationToken ct) => _inner.WriteLineAsync(line, ct);

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        try { if (_port.IsOpen) _port.Close(); } catch (Exception) { }
        _port.Dispose();
    }
}
