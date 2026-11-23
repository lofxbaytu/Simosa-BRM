using System.Threading.Channels;

namespace SimosaBRM.ConsoleSerial.Bridge;

/// <summary>
/// 送往裝置的行的佇列(<see cref="ConsoleBridge"/> 的寫出迴圈),**有界並合併**(第三十輪 2 小時搜尋 serial-todevice-unbounded):
/// <list type="bullet">
/// <item>同類行只保留最新一行:<c>LED,&lt;名稱&gt;</c>、<c>DISP,&lt;欄位&gt;</c> 各一類、<c>BUZZ</c> 一類。新行取代佇列中同類的舊行,
///       位置不變(先排隊的類別先送,不會因某一欄持續更新而讓其他類別一直等)。LED/DISP 都是狀態值,裝置最後一定收到每一類的最新值;
///       等待中的多個 BUZZ 合成一個。</item>
/// <item>其他行(<c>ACK</c>)不合併;整個佇列超過 <see cref="Capacity"/> 行時丟最舊的行。</item>
/// </list>
/// 修正前是無界通道:裝置仍送心跳但不讀(韌體卡住、序列埠寫出每行 1 s 逾時)時,LED/DISP 行以產生速度(實測 3.9 行/s、最多約 20 行/s)無上限累積,
/// 送往裝置的顯示延遲也無上限。合併後佇列最多約 12 類狀態行加少數 ACK。
/// 行尾的校驗和(<c>*XX</c>)在第二個逗號之後,不影響分類。執行緒安全(多個來源寫入、單一寫出迴圈讀取)。
/// </summary>
public sealed class DeviceLineQueue
{
    /// <summary>預設上限(行):12 類狀態行(LED 7 顆、DISP 4 欄、BUZZ)遠低於此,只有不合併的行會碰到</summary>
    public const int DefaultCapacity = 64;

    private readonly object _sync = new();
    private readonly LinkedList<(string? Key, string Line)> _items = new();
    private readonly Dictionary<string, LinkedListNode<(string? Key, string Line)>> _byKey = new(StringComparer.Ordinal);
    // 「有資料」的訊號(容量 1、滿時不再寫):只有單一讀者依序 WaitToReadAsync,不會累積等待者
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private bool _completed;

    public DeviceLineQueue(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int Capacity { get; }
    /// <summary>目前排隊的行數</summary>
    public int Count { get { lock (_sync) return _items.Count; } }
    /// <summary>被同類新行取代的行數(診斷)</summary>
    public long Coalesced { get; private set; }
    /// <summary>因超過上限而丟掉的行數(診斷)</summary>
    public long Dropped { get; private set; }

    /// <summary>行的類別:<c>LED,名稱</c>、<c>DISP,欄位</c>、<c>BUZZ</c>;其他(ACK 等)為 null(不合併)。</summary>
    public static string? KeyOf(string line)
    {
        if (line.StartsWith("BUZZ", StringComparison.Ordinal) && (line.Length == 4 || line[4] is ',' or '*')) return "BUZZ";
        if (!line.StartsWith("LED,", StringComparison.Ordinal) && !line.StartsWith("DISP,", StringComparison.Ordinal)) return null;
        var first = line.IndexOf(',');
        var second = line.IndexOf(',', first + 1);
        return second < 0 ? null : line[..second];
    }

    /// <summary>加入一行(已完成時回傳 false)。</summary>
    public bool TryWrite(string line)
    {
        lock (_sync)
        {
            if (_completed) return false;
            var key = KeyOf(line);
            if (key is not null && _byKey.TryGetValue(key, out var node))
            {
                node.Value = (key, line);   // 同類:取代舊行,位置不變
                Coalesced++;
            }
            else
            {
                var added = _items.AddLast((key, line));
                if (key is not null) _byKey[key] = added;
                while (_items.Count > Capacity)
                {
                    var oldest = _items.First!;
                    _items.RemoveFirst();
                    if (oldest.Value.Key is { } k) _byKey.Remove(k);
                    Dropped++;
                }
            }
        }
        _signal.Writer.TryWrite(true);
        return true;
    }

    /// <summary>取出最舊的一行。</summary>
    public bool TryRead(out string line)
    {
        lock (_sync)
        {
            var first = _items.First;
            if (first is null)
            {
                line = "";
                return false;
            }
            _items.RemoveFirst();
            if (first.Value.Key is { } k) _byKey.Remove(k);
            line = first.Value.Line;
            return true;
        }
    }

    /// <summary>等到有行可讀(true)或已完成且已讀完(false);單一讀者。</summary>
    public async ValueTask<bool> WaitToReadAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_items.Count > 0) return true;
            }
            if (!await _signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                lock (_sync) return _items.Count > 0;
            }
            _signal.Reader.TryRead(out _);
        }
    }

    /// <summary>不再接受新行(已排隊的行仍可讀完)。</summary>
    public void Complete()
    {
        lock (_sync) _completed = true;
        _signal.Writer.TryComplete();
    }
}
