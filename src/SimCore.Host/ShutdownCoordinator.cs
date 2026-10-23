using System.Runtime.InteropServices;

namespace SimosaBRM.SimCore.Host;

/// <summary>
/// 優雅停止的單一入口(交接紀錄待辦第 1 項;規劃書第 5.3 節紀錄 footer):Ctrl+C、POSIX 訊號(SIGINT/SIGTERM/SIGQUIT/SIGHUP;
/// Windows 上對應 Ctrl+C/關機登出/Ctrl+Break/主控台視窗關閉,不支援者略過)、AppDomain.ProcessExit、停止檔、WebSocket <c>shutdown</c>
/// 指令與 <c>--duration</c> 都呼叫 <see cref="Request"/>,觸發同一個 <see cref="Token"/>;主控程序關閉紀錄(寫完 footer)後呼叫
/// <see cref="MarkFinished"/>,ProcessExit 處理常式最多等 <see cref="ProcessExitGrace"/> 讓 footer 寫完再讓程序結束。
/// 第一個原因保留(<see cref="Reason"/>),之後的請求只是冪等的重複。
/// </summary>
public sealed class ShutdownCoordinator : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly List<PosixSignalRegistration> _registrations = new();
    private readonly object _sync = new();

    /// <summary>ProcessExit 時等待 footer 寫完的上限(Windows 主控台關閉約給 5 s)</summary>
    public TimeSpan ProcessExitGrace { get; init; } = TimeSpan.FromSeconds(4);

    public CancellationToken Token => _cts.Token;
    public bool IsRequested => _cts.IsCancellationRequested;
    /// <summary>第一次請求的原因(Ctrl+C、SIGTERM、stopFile、shutdown、duration、processExit…)</summary>
    public string? Reason { get; private set; }
    /// <summary>停止前要先結束練習(定稿 summary、發 exerciseEnded);shutdown 指令的語意</summary>
    public bool EndExerciseRequested { get; private set; }
    /// <summary>footer 已寫完(或無紀錄)</summary>
    public bool Finished => _finished.IsSet;
    public event Action<string>? Requested;

    /// <summary>請求停止;第一次有效,之後冪等。</summary>
    public void Request(string reason, bool endExercise = false)
    {
        lock (_sync)
        {
            if (endExercise) EndExerciseRequested = true;
            if (_cts.IsCancellationRequested) return;
            Reason = reason;
        }
        Requested?.Invoke(reason);
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void MarkFinished() => _finished.Set();

    /// <summary>等 footer 寫完;協調器已 Dispose(主程式正常返回後 ProcessExit 才觸發)時視為已完成。</summary>
    public bool WaitFinished(TimeSpan timeout)
    {
        try { return _finished.Wait(timeout); }
        catch (ObjectDisposedException) { return true; }
    }

    /// <summary>
    /// 註冊程序層級的停止來源:Console.CancelKeyPress、PosixSignalRegistration(各訊號個別 try/catch:Windows 不支援的訊號丟
    /// PlatformNotSupportedException,略過)與 AppDomain.ProcessExit(請求停止並等 footer 寫完)。
    /// </summary>
    public void RegisterProcessSignals()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Request("Ctrl+C"); };
        foreach (var (signal, name) in new[] { (PosixSignal.SIGINT, "SIGINT"), (PosixSignal.SIGTERM, "SIGTERM"), (PosixSignal.SIGQUIT, "SIGQUIT"), (PosixSignal.SIGHUP, "SIGHUP") })
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    ctx.Cancel = true; // 由主控程序自行結束(寫 footer),不讓執行階段直接終止
                    Request(name);
                }));
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // 主程式正常返回後也會觸發:已完成(或已 Dispose)就直接放行;否則(例如 Environment.Exit)請求停止並等 footer 寫完
            if (_disposed || _finished.IsSet) return;
            Request("processExit");
            WaitFinished(ProcessExitGrace);
        };
    }

    private volatile bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        foreach (var r in _registrations) r.Dispose();
        _registrations.Clear();
        _cts.Dispose();
        // _finished 不 Dispose:ProcessExit 處理常式可能在 Dispose 之後才執行(ManualResetEventSlim 未建立 WaitHandle 時無非受控資源)
    }
}
