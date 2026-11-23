using System.Threading.Channels;

namespace SimosaBRM.Sensors.Radar.Net;

/// <summary>
/// 傳送迴圈「等任一通道可讀」的等待者集合(<see cref="RadarWebSocketServer"/> 的 SendLoopAsync)。
/// <b>與 src/SimCore.Host/ChannelWaitSet.cs 相同的實作</b>(雷達服務不引用 SimCore.Host,故各自一份;改一份時另一份一併改):
/// 每個通道同時最多一個未完成的 <see cref="ChannelReader{T}.WaitToReadAsync"/>;沒完成的留到下一圈繼續用,已完成的(結果已過時,之後的 TryRead 才準)才換新的。
/// <para>
/// 為什麼(第三十輪 2 小時搜尋 radar-R1、R3):修正前每圈對 Frames / Json 各新建一個 WaitToReadAsync。兩個都是有界通道,等待者不會被取代而是排隊,
/// 直到該通道下一筆資料才喚醒。雷達待機或失效時 Frames 沒有影像幀,但 Json(追蹤清單每模擬秒 1 則)每則都喚醒一圈 → 每圈在 Frames 多排 1 個等待者
/// 與其在伺服器權杖 <c>_cts</c> 上的取消註冊(每則約 300 B;待機 2 小時每客戶端 +2 MiB;恢復發射後 <c>_cts</c> 的 free list 仍保留尖峰數量的節點);
/// /radar/replay 連線每則重播訊息也一樣(Frames 永遠沒有資料)。
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
