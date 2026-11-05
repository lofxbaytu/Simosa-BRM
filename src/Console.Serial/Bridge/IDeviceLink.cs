namespace SimosaBRM.ConsoleSerial.Bridge;

/// <summary>裝置端連結(序列埠、檔案/管道 loopback、測試用記憶體串流):逐行讀、逐行寫。</summary>
public interface IDeviceLink : IAsyncDisposable
{
    /// <summary>讀一行(不含換行);連結結束時回傳 null。</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
    /// <summary>寫一行(自動加 <c>\n</c>)並清空緩衝。</summary>
    Task WriteLineAsync(string line, CancellationToken ct);
    string Description { get; }
}
