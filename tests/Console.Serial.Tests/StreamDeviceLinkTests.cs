using System.Text;
using SimosaBRM.ConsoleSerial.Bridge;
using SimosaBRM.ConsoleSerial.Core;
using Xunit;

namespace SimosaBRM.ConsoleSerial.Tests;

/// <summary>串流連結:CR LF 容忍、串流尾端結束/輪詢、寫出以 \n 結尾;命令列參數解析。</summary>
public class StreamDeviceLinkTests
{
    [Fact]
    public async Task ReadsLines_ToleratesCrLf_EndsAtEof()
    {
        var input = new MemoryStream(Encoding.ASCII.GetBytes("HELLO,1,RUD\r\nRUD,10\n\nHB"));
        var output = new MemoryStream();
        await using var link = new StreamDeviceLink(input, output, "記憶體", pollAtEof: false);
        Assert.Equal("HELLO,1,RUD", await link.ReadLineAsync(CancellationToken.None));
        Assert.Equal("RUD,10", await link.ReadLineAsync(CancellationToken.None));
        Assert.Equal("", await link.ReadLineAsync(CancellationToken.None));
        Assert.Equal("HB", await link.ReadLineAsync(CancellationToken.None));   // 無換行的最後一行
        Assert.Null(await link.ReadLineAsync(CancellationToken.None));
        await link.WriteLineAsync("ACK*" + LineChecksum.Hex("ACK"), CancellationToken.None);
        await link.WriteLineAsync("LED,LINK,1", CancellationToken.None);
        Assert.Equal("ACK*49\nLED,LINK,1\n", Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public async Task PollAtEof_WaitsForAppendedLines()
    {
        var root = SimosaBRM.SimCore.Ship.RepositoryPaths.FindRoot();
        var dir = Path.Combine(root, "build", "test-tmp", "console-serial");
        Directory.CreateDirectory(dir);
        var inPath = Path.Combine(dir, $"in-{Guid.NewGuid():N}.txt");
        var outPath = Path.Combine(dir, $"out-{Guid.NewGuid():N}.txt");
        File.WriteAllText(inPath, "HB\n");
        await using var link = StreamDeviceLink.OpenFiles(inPath, outPath);
        Assert.Equal("HB", await link.ReadLineAsync(CancellationToken.None));
        using var cts = new CancellationTokenSource(5000);
        var pending = link.ReadLineAsync(cts.Token);
        await Task.Delay(120);
        Assert.False(pending.IsCompleted, "讀到檔尾應等待而非結束");
        File.AppendAllText(inPath, "RUD,5\n");
        Assert.Equal("RUD,5", await pending);
        await link.WriteLineAsync("ACK", CancellationToken.None);
        Assert.Equal("ACK\n", ReadShared(outPath)); // Windows:檔案仍由連結以 FileShare.ReadWrite 開著,File.ReadAllText(share Read)會被拒
        File.Delete(inPath);
        File.Delete(outPath);
    }

    [Fact]
    public void Options_ParseAndValidate()
    {
        var o = ConsoleOptions.Parse(new[] { "--port", "COM5", "--baud", "9600", "--ws", "127.0.0.1:18765", "--hb-timeout", "2.5", "--log", "-", "--duration", "3" });
        Assert.Equal("COM5", o.Port);
        Assert.Equal(9600, o.Baud);
        Assert.Equal("ws://127.0.0.1:18765", o.WsUrl);
        Assert.Equal(2.5, o.HeartbeatTimeoutS);
        Assert.Equal("-", o.LogPath);
        Assert.Equal(3.0, o.DurationS);
        var lb = ConsoleOptions.Parse(new[] { "--loopback", "build/tmp/in.txt,build/tmp/out.txt" });
        Assert.True(lb.Loopback);
        Assert.Equal("build/tmp/in.txt", lb.LoopbackIn);
        Assert.Equal("build/tmp/out.txt", lb.LoopbackOut);
        Assert.Equal("ws://127.0.0.1:8765", lb.WsUrl);
        Assert.Equal(115200, lb.Baud);
        Assert.Null(ConsoleOptions.Parse(new[] { "--loopback", "in.txt" }).LoopbackOut);
        Assert.True(ConsoleOptions.Parse(new[] { "--help" }).Help);
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse(new[] { "--port", "COM1", "--loopback", "x" }));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse(new[] { "--port", "COM1", "--baud", "0" }));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse(new[] { "--port", "COM1", "--ws", "http://x" }));
        Assert.Throws<ArgumentException>(() => ConsoleOptions.Parse(new[] { "--bogus" }));
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
