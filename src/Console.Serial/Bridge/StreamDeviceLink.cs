using System.Text;

namespace SimosaBRM.ConsoleSerial.Bridge;

/// <summary>
/// 串流版裝置連結:自 <paramref name="input"/> 讀行(容忍 CR LF、ASCII/UTF-8)、寫到 <paramref name="output"/>(可為 null = 不回寫)。
/// <see cref="PollAtEof"/> 為 true 時讀到串流尾端不結束、每 <see cref="PollIntervalMs"/> 再讀(loopback 檔案:外部程序持續追加行);
/// false 時尾端即結束(記憶體串流測試)。
/// </summary>
public sealed class StreamDeviceLink : IDeviceLink
{
    private readonly Stream _input;
    private readonly Stream? _output;
    private readonly StringBuilder _line = new();
    private readonly byte[] _buf = new byte[4096];
    private int _bufLen;
    private int _bufPos;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public StreamDeviceLink(Stream input, Stream? output, string description, bool pollAtEof = false)
    {
        _input = input;
        _output = output;
        Description = description;
        PollAtEof = pollAtEof;
    }

    public string Description { get; }
    public bool PollAtEof { get; }
    public int PollIntervalMs { get; init; } = 50;

    /// <summary>開啟 loopback 檔案/具名管道對:讀取端以共享讀寫開啟(別的程序追加),寫入端追加開啟(不存在時建立)。</summary>
    public static StreamDeviceLink OpenFiles(string inPath, string? outPath)
    {
        var input = new FileStream(inPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
        FileStream? output = null;
        if (outPath is not null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            output = new FileStream(outPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
        }
        return new StreamDeviceLink(input, output, $"loopback {inPath} → {outPath ?? "(不回寫)"}", pollAtEof: true);
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            // 先消化上次讀入但尚未處理的位元組(一次 Read 可能含多行)
            while (_bufPos < _bufLen)
            {
                var c = (char)_buf[_bufPos++];
                if (c == '\n') return Flush();
                if (c != '\r') _line.Append(c);
                if (_line.Length > 512) _line.Clear(); // 沒有換行的垃圾:丟棄,避免無限成長
            }
            int n;
            try { n = await _input.ReadAsync(_buf, ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) { return null; }
            if (n == 0)
            {
                if (!PollAtEof) return _line.Length > 0 ? Flush() : null;
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                continue;
            }
            _bufLen = n;
            _bufPos = 0;
        }
    }

    private string Flush()
    {
        var s = _line.ToString();
        _line.Clear();
        return s;
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (_output is null) return;
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _input.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        if (_output is not null) { try { await _output.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
        _writeGate.Dispose();
    }
}
