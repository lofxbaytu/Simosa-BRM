using System.Threading.Channels;

namespace SimosaBRM.SimCore.Host;

/// <summary>
/// 傳送迴圈「等任一通道可讀」的等待者集合(<see cref="WebSocketStateServer"/> 的 SendLoopAsync;docs/design/testing.md 第 6.6 節):
/// 每個通道同時最多一個未完成的 <see cref="ChannelReader{T}.WaitToReadAsync"/>;沒完成的留到下一圈繼續用,
/// 已完成的(結果已過時,之後的 TryRead 才準)才換新的。
/// <para>
/// 為什麼不每圈重新 WaitToReadAsync:.NET 8(8.0.33 實測)的 SingleReader 無界通道以新等待者取代舊的並把舊的取消,
/// 被取代的等待者在長壽權杖(伺服器整個生命週期的 <c>_cts.Token</c>)上的取消註冊不會釋放,連同 OperationCanceledException
/// 與其堆疊一直存活到權杖釋放(每個約 1 KB;狀態幀 25 Hz → 每條連線約 1.3 MB/min,不會持平);
/// 有界通道的等待者則排隊到下一筆資料才喚醒。
/// </para>
/// 非執行緒安全:只由單一傳送迴圈依序呼叫 <see cref="WaitAnyAsync"/>(前一次完成後才呼叫下一次)。
/// </summary>
internal sealed class ChannelWaitSet<T>
{
    private readonly ChannelReader<T>[] _readers;
    private readonly Task<bool>[] _pending;
    private readonly CancellationToken _token;
    private long _waitsStarted;

    /// <param name="token">所有等待共用的權杖(保留的等待沿用建立時的權杖,所以固定在建構時給)</param>
    /// <param name="readers">要等的通道讀取端</param>
    public ChannelWaitSet(CancellationToken token, params ChannelReader<T>[] readers)
    {
        if (readers.Length == 0) throw new ArgumentException("至少要一個通道", nameof(readers));
        _readers = readers;
        _pending = new Task<bool>[readers.Length];
        _token = token;
    }

    /// <summary>已建立的 WaitToReadAsync 數(診斷/測試用):通道閒置時不隨圈數增加,每次喚醒只換掉已完成的那幾個</summary>
    public long WaitsStarted => Interlocked.Read(ref _waitsStarted);

    /// <summary>第 <paramref name="index"/> 個通道目前保留的等待(測試用;null = 尚未建立)</summary>
    internal Task<bool>? Pending(int index) => _pending[index];

    /// <summary>
    /// 等任一通道可讀或已完成:回傳先完成的那個等待的結果(false = 該通道已完成且沒有資料);權杖取消時拋 OperationCanceledException。
    /// 呼叫前應已用 TryRead 確認各通道皆空(有資料時立即返回)。
    /// </summary>
    public async Task<bool> WaitAnyAsync()
    {
        for (var i = 0; i < _readers.Length; i++)
        {
            if (_pending[i] is { IsCompleted: false }) continue;   // 未完成:保留,不另建等待者
            _pending[i] = _readers[i].WaitToReadAsync(_token).AsTask();
            Interlocked.Increment(ref _waitsStarted);
        }
        var done = await Task.WhenAny(_pending).ConfigureAwait(false);
        return await done.ConfigureAwait(false);
    }
}
