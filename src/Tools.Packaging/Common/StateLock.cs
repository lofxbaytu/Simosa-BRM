using System.Collections.Concurrent;
using System.Diagnostics;

namespace SimosaBRM.Packaging.Common;

/// <summary>一把鎖定檔的設定(<see cref="StateLock.Acquire"/>)。</summary>
public sealed class StateLockSpec
{
    /// <summary>鎖定檔的路徑(放在它保護的狀態檔旁邊;0 位元組,用完不刪除)</summary>
    public required string Path { get; init; }

    /// <summary>等另一個持有者放開的時間上限;超過就丟「等不到」的 <see cref="IOException"/></summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>等不到時的錯誤訊息(參數:設定的等待上限)。寫給操作人員看:誰可能佔著、這次有沒有執行、接下來怎麼做。</summary>
    public required Func<TimeSpan, string> BusyMessage { get; init; }

    /// <summary>
    /// 鎖定檔打不開(資料夾唯讀、沒有寫入權限、鎖定檔本身唯讀)時的訊息(參數:原本的例外訊息);null = 丟原本的例外。
    /// 例外的型別不變(<see cref="UnauthorizedAccessException"/>,原例外放在 InnerException)。
    /// </summary>
    public Func<string, string>? AccessDeniedMessage { get; init; }

    /// <summary>
    /// 「存取被拒」先重試多久才當成真的打不開(預設不重試)。船上的 <c>state/.lock</c> 用:舊版的 simosa-pkg 以「關閉時刪除」開啟同一個檔,
    /// Windows 上刪除尚未完成的短暫期間開檔會得到存取被拒,不是權限問題。
    /// </summary>
    public TimeSpan AccessDeniedGrace { get; init; } = TimeSpan.Zero;

    /// <summary>等了 <see cref="NoticeAfter"/> 還沒有拿到時,呼叫一次 <see cref="Notice"/>(參數是這句話)— 讓操作人員知道指令為什麼沒有動靜;null = 不通知</summary>
    public string? WaitingMessage { get; init; }

    public Action<string>? Notice { get; init; }

    public TimeSpan NoticeAfter { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// 狀態檔「讀 → 改 → 寫」的跨行程鎖(docs/design/packaging-sync.md 第 5.2 節):獨占開啟一個鎖定檔(<see cref="FileShare.None"/>)。
/// <list type="bullet">
/// <item><b>跨行程</b>:Windows 上第二個開啟者得到共用違規、Linux 上是 flock;兩者都由作業系統在行程結束(含被強制結束、當機、斷電)時自動放開,
///   不會留下過期的鎖 — 鎖是「有沒有人獨占開啟」,不是檔案在不在。鎖定檔用完不刪除(關閉時刪除會讓等待中的行程鎖到已經不在路徑上的檔)。</item>
/// <item><b>被佔用</b>:每隔幾十毫秒重試,到 <see cref="StateLockSpec.Timeout"/> 為止;防毒或索引程式短暫開著鎖定檔時也是同一條路(重試,不直接失敗)。
///   等不到時丟 <see cref="IOException"/>(型別就是 IOException:呼叫端與指令列原有的處理、結束碼都不變),訊息由 <see cref="StateLockSpec.BusyMessage"/> 產生;
///   <see cref="IsBusy"/> 可以分辨它與其他 IO 錯誤。</item>
/// <item><b>同一個行程</b>:獨占開啟在同一個行程內也互斥,所以同一把鎖在行程內只開一次檔 — 同一條執行緒重複取得(例如備份紀錄庫的過程中標記已備份)
///   只增加計數,不會把自己卡死;別的執行緒要等(與跨行程相同的等待上限與訊息)。取得與放開必須在同一條執行緒(以 using 包住一段同步的程式;不可跨 await)。</item>
/// <item><b>兩把以上的鎖</b>:一律依固定的順序取得(紀錄庫:先帳冊 <c>store-ledger.json.lock</c>、後兩船版次清單 <c>fleet-versions.json.lock</c>;
///   船上只有一把 <c>state/.lock</c>;船上與紀錄庫的鎖不會同時持有)。</item>
/// </list>
/// 只讀的指令(status、verify-install、store list、records list…)不取鎖。
/// </summary>
public sealed class StateLock : IDisposable
{
    private const string BusyKey = "SimosaBRM.StateLock.Busy";

    /// <summary>行程內每個鎖定檔一份:Monitor 管執行緒之間的互斥與重入,<see cref="Stream"/> 是對其他行程的鎖(Depth 由 0 變 1 時開、回到 0 時關)</summary>
    private sealed class Gate
    {
        public FileStream? Stream;
        public int Depth;
    }

    private static readonly ConcurrentDictionary<string, Gate> Gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private Gate? _gate;

    private StateLock(Gate gate) => _gate = gate;

    /// <summary>這個例外是不是「等不到鎖」(另一個 simosa-pkg 佔著);其他 IO 錯誤(磁碟滿、檔案不在…)回傳 false。</summary>
    public static bool IsBusy(Exception? ex) => ex is IOException && ex.Data.Contains(BusyKey);

    /// <summary>取得鎖;放開用 <see cref="Dispose"/>(同一條執行緒)。等不到丟 <see cref="IOException"/>(<see cref="IsBusy"/> 為 true)。</summary>
    public static StateLock Acquire(StateLockSpec spec)
    {
        var full = Path.GetFullPath(spec.Path);
        var gate = Gates.GetOrAdd(full, _ => new Gate());
        var clock = Stopwatch.StartNew();
        var noticed = false;

        void NotifyOnce()
        {
            if (noticed || spec.Notice is null || spec.WaitingMessage is null) return;
            noticed = true;
            try { spec.Notice(spec.WaitingMessage); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }   // 通知寫不出去不影響取鎖
        }

        IOException Busy()
        {
            var ex = new IOException(spec.BusyMessage(spec.Timeout));
            ex.Data[BusyKey] = true;
            return ex;
        }

        // 1. 行程內:別的執行緒持有時等待(同一條執行緒直接重入)
        if (!Monitor.TryEnter(gate))
        {
            var first = spec.NoticeAfter < spec.Timeout ? spec.NoticeAfter : spec.Timeout;
            if (!Monitor.TryEnter(gate, first))
            {
                NotifyOnce();
                var rest = spec.Timeout - clock.Elapsed;
                if (rest <= TimeSpan.Zero || !Monitor.TryEnter(gate, rest)) throw Busy();
            }
        }
        try
        {
            // 2. 跨行程:這個行程第一次取得時才開檔
            if (gate.Depth == 0) gate.Stream = OpenExclusive(spec, full, clock, NotifyOnce, Busy);
            gate.Depth++;
        }
        catch
        {
            Monitor.Exit(gate);
            throw;
        }
        return new StateLock(gate);
    }

    private static FileStream OpenExclusive(StateLockSpec spec, string full, Stopwatch clock, Action notifyOnce, Func<IOException> busy)
    {
        // 與 PackageJson.SaveAtomic 相同:資料夾被移走時重建,不因為多了鎖定檔而改成失敗
        if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        while (true)
        {
            try
            {
                var stream = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                // Linux:舊版的 simosa-pkg 以「關閉時刪除」持有同一個鎖定檔時,它放開的瞬間檔案已從路徑上移除,這裡鎖到的可能是那個已經不在路徑上的檔
                // (下一個行程會另建新檔、同時取得);檔案不在路徑上就放開重來。Windows 上被開啟中的檔不會這樣
                if (!OperatingSystem.IsWindows() && !File.Exists(full))
                {
                    stream.Dispose();
                    continue;
                }
                return stream;
            }
            catch (UnauthorizedAccessException ex)
            {
                if (clock.Elapsed < spec.AccessDeniedGrace && clock.Elapsed < spec.Timeout)
                {
                    Thread.Sleep(20 + Random.Shared.Next(40));
                    continue;
                }
                if (spec.AccessDeniedMessage is null) throw;
                throw new UnauthorizedAccessException(spec.AccessDeniedMessage(ex.Message), ex);
            }
            catch (IOException) when (File.Exists(full))   // 被佔用(鎖定檔不存在時的 IOException 是別的原因,例如資料夾不在,不重試)
            {
                if (clock.Elapsed >= spec.Timeout) throw busy();
                if (clock.Elapsed >= spec.NoticeAfter) notifyOnce();
                Thread.Sleep(20 + Random.Shared.Next(40));
            }
        }
    }

    /// <summary>放開(重入的那幾次只減計數;最外層放開時才關閉鎖定檔,讓其他行程取得)。鎖定檔留著不刪。</summary>
    public void Dispose()
    {
        var gate = Interlocked.Exchange(ref _gate, null);
        if (gate is null) return;
        try
        {
            if (--gate.Depth == 0)
            {
                gate.Stream?.Dispose();
                gate.Stream = null;
            }
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }
}
