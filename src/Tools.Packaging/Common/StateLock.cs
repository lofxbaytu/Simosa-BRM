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

    /// <summary>
    /// 開不了鎖定檔、而原因不是「被別人獨占開啟」的 IO 錯誤(建立不了檔:磁碟或 inode 用完、開啟的檔案太多、裝置錯誤…)連續多久之後丟出原本的例外。
    /// 這類錯誤先短暫重試(舊版的 simosa-pkg 放開並刪除鎖定檔的瞬間也會出現一次),但不當成被佔用一路等到 <see cref="Timeout"/> —
    /// 否則操作人員等了一兩分鐘之後看到的是「另一個 simosa-pkg 正在使用…」,真正的原因(例如 No space left on device)看不到。
    /// </summary>
    public TimeSpan OtherErrorGrace { get; init; } = TimeSpan.FromSeconds(2);

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
///   <see cref="IsBusy"/> 可以分辨它與其他 IO 錯誤。只有「開得到、鎖不到」(<see cref="IsSharingViolation"/>)才算被佔用:鎖定檔建立不了
///   (磁碟或 inode 用完…)這類錯誤短暫重試(<see cref="StateLockSpec.OtherErrorGrace"/>)後丟出原本的例外,不等到逾時、也不說成「另一個 simosa-pkg」。</item>
/// <item><b>同一個行程</b>:獨占開啟在同一個行程內也互斥,所以同一把鎖在行程內只開一次檔 — 同一條執行緒重複取得(例如備份紀錄庫的過程中標記已備份)
///   只增加計數,不會把自己卡死;別的執行緒要等(與跨行程相同的等待上限與訊息)。取得與放開必須在同一條執行緒(以 using 包住一段同步的程式;不可跨 await)。</item>
/// <item><b>兩把以上的鎖</b>:一律依固定的順序取得 — 紀錄庫:帳冊 <c>store-ledger.json.lock</c> → 兩船版次清單 <c>fleet-versions.json.lock</c> →
///   稽核紀錄 <c>audit.jsonl.lock</c>;船上:狀態鎖 <c>state/.lock</c> → 稽核紀錄 <c>state/audit.jsonl.lock</c>。稽核紀錄的鎖永遠最後取得
///   (<see cref="AuditLog"/>;拿著它的時候不再取別的鎖)。同一個行程不會同時持有船上的鎖與紀錄庫的鎖。</item>
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

        // last:最後一次開不了鎖定檔的例外(放進 InnerException)。它不是「被獨占開啟」時,在訊息尾附上它的說明 — 那才是真正的原因
        IOException Busy(Exception? last)
        {
            var message = spec.BusyMessage(spec.Timeout);
            if (last is not null && !IsSharingViolation(last)) message += $"(開啟鎖定檔時最後一次的錯誤:{last.Message})";
            var ex = new IOException(message, last);
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
                if (rest <= TimeSpan.Zero || !Monitor.TryEnter(gate, rest)) throw Busy(null);
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

    /// <summary>
    /// 這個例外是不是「檔案被別人獨占開啟」(共用違規)。Windows:ERROR_SHARING_VIOLATION(32)或 ERROR_LOCK_VIOLATION(33);
    /// Linux / macOS:.NET 對 flock 回 EWOULDBLOCK 時丟的 <see cref="IOException"/>,HResult 是原始的 errno(Linux 11、macOS / BSD 35)。
    /// 其他的 IO 錯誤(No space left on device、Too many open files…)回傳 false。
    /// </summary>
    internal static bool IsSharingViolation(Exception ex)
    {
        if (ex.GetType() != typeof(IOException)) return false;
        if (OperatingSystem.IsWindows())
        {
            var code = unchecked((uint)ex.HResult);   // 0x8007xxxx:Win32 錯誤碼
            return code >> 16 == 0x8007 && (code & 0xFFFF) is 32 or 33;
        }
        return ex.HResult is 11 or 35;
    }

    private static FileStream OpenExclusive(StateLockSpec spec, string full, Stopwatch clock, Action notifyOnce, Func<Exception?, IOException> busy)
    {
        // 與 PackageJson.SaveAtomic 相同:資料夾被移走時重建,不因為多了鎖定檔而改成失敗
        var dir = Path.GetDirectoryName(full);
        if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
        Stopwatch? deniedSince = null;   // 連續「存取被拒」的起點(中間成功開到檔或遇到共用違規就重算)
        Stopwatch? otherSince = null;    // 連續「不是被佔用的 IO 錯誤」的起點(中間成功開到檔或遇到共用違規就重算)
        while (true)
        {
            try
            {
                var stream = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                if (OperatingSystem.IsWindows() || IsTheFileOnThePath(stream, full)) return stream;
                stream.Dispose();        // 鎖到的是已經不在路徑上的檔:放開重來(不算等待失敗)
                deniedSince = null;
                otherSince = null;
            }
            catch (UnauthorizedAccessException ex)
            {
                deniedSince ??= Stopwatch.StartNew();
                if (deniedSince.Elapsed < spec.AccessDeniedGrace && clock.Elapsed < spec.Timeout)
                {
                    Thread.Sleep(20 + Random.Shared.Next(40));
                    continue;
                }
                if (spec.AccessDeniedMessage is null) throw;
                throw new UnauthorizedAccessException(spec.AccessDeniedMessage(ex.Message), ex);
            }
            // 被佔用:重試。鎖定檔不存在時的 IOException 是別的原因(例如資料夾不在),不重試 — 例外:Linux 上舊版的 simosa-pkg 以「關閉時刪除」
            // 持有同一個檔時,我們開到它、鎖不到,而它在這一瞬間放開並把檔案從路徑上移除(這時檔案不在、資料夾在):也是被佔用,重試(下一次開檔會重建)
            catch (IOException ex) when (File.Exists(full) || (!OperatingSystem.IsWindows() && ex.GetType() == typeof(IOException) && Directory.Exists(dir)))
            {
                deniedSince = null;
                if (IsSharingViolation(ex)) otherSince = null;
                else
                {
                    // 不是「開得到、鎖不到」:鎖定檔建立不了(磁碟或 inode 用完)、開啟的檔案太多、裝置錯誤… 短暫重試後丟出原本的例外 —
                    // 一路等到逾時再說「另一個 simosa-pkg 正在使用…」的話,真正的原因看不到(原本沒有鎖定檔時是立刻報 No space left on device)
                    otherSince ??= Stopwatch.StartNew();
                    if (otherSince.Elapsed >= spec.OtherErrorGrace) throw;
                }
                if (clock.Elapsed >= spec.Timeout) throw busy(ex);
                if (clock.Elapsed >= spec.NoticeAfter && otherSince is null) notifyOnce();
                Thread.Sleep(20 + Random.Shared.Next(40));
            }
        }
    }

    /// <summary>
    /// Linux / macOS:剛鎖到的檔是不是路徑上的那一個。舊版的 simosa-pkg 以「關閉時刪除」持有同一個鎖定檔(state/.lock)時,它放開的瞬間檔案已從路徑上移除 —
    /// 我們若在那之前開了檔、在那之後才鎖到,鎖到的是一個已經不在路徑上的檔,下一個行程會另建新檔、同時取得(.NET 對「關閉時刪除」的開啟自己有這項檢查,
    /// 所以舊版之間沒有這個問題)。作法:把我們這個檔的修改時間設成一個隨機的時間,再看路徑上的檔是不是同一個時間 — 路徑上若是別的檔(舊版新建的、
    /// 或另一個新版行程正在做同樣檢查的),時間不會相同。確認後把修改時間設回現在。Windows 上被開啟中的檔不會從路徑上消失,不必檢查。
    /// 檔案系統不能設定時間等情形下無法確認,照舊當成是(與沒有舊版同時執行時相同)。
    /// </summary>
    private static bool IsTheFileOnThePath(FileStream stream, string full)
    {
        try
        {
            // 2 秒的倍數:FAT 類檔案系統的修改時間只有 2 秒的解析度
            var token = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(2L * Random.Shared.NextInt64(1, 500_000_000));
            File.SetLastWriteTimeUtc(stream.SafeFileHandle, token);
            var mine = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            var onPath = File.GetLastWriteTimeUtc(full);            // 以路徑查詢,不開檔;檔案不在時是 1601 年
            File.SetLastWriteTimeUtc(stream.SafeFileHandle, DateTime.UtcNow);
            return onPath == mine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return File.Exists(full);
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
