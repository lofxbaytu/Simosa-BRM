using System.Diagnostics;
using SimosaBRM.ConsoleSerial.Core;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.ConsoleSerial.Bridge;

/// <summary>
/// 橋接:裝置連結(<see cref="IDeviceLink"/>)的行 → <see cref="ConsoleSession"/> → 核心(<see cref="ICoreLink"/>)指令;
/// 核心狀態幀 → 會話 → 裝置回寫行。三個來源(裝置讀取、核心接收、25 ms 定時 Poll)都在同一把鎖內呼叫會話;
/// 送往裝置的行經單一寫出佇列,避免交錯;佇列有界並合併同類行(<see cref="DeviceLineQueue"/>:裝置跟不上時只留各類最新值,不累積)。
/// 裝置讀取結束(序列埠拔線/loopback 串流關閉)時 <see cref="RunAsync"/> 回傳。
/// </summary>
public sealed class ConsoleBridge : IAsyncDisposable
{
    private readonly IDeviceLink _device;
    private readonly ICoreLink _core;
    private readonly ConsoleSession _session;
    private readonly Func<long> _clock;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly DeviceLineQueue _toDevice = new();

    public ConsoleBridge(IDeviceLink device, ICoreLink core, ConsoleSession session, Action<string> log, Func<long>? clock = null)
    {
        _device = device;
        _core = core;
        _session = session;
        _log = log;
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.ElapsedMilliseconds);
        // 訂閱存成欄位以便 DisposeAsync 解除:序列埠重開會建立新的 bridge,舊 bridge 若仍掛在共用的 session/core 上,
        // 狀態幀會先被舊 bridge 消化(回寫行寫進無人讀取的通道),新裝置就收不到 LED/DISP(審查第二十三輪 major)
        _onSessionLog = m => _log($"[會話] {m}");
        _onConnection = c => Dispatch(() => _session.SetCoreConnected(c, _clock()));
        _onFrame = f => Dispatch(() => _session.HandleStateFrame(f, _clock()));
        _session.Log += _onSessionLog;
        _core.ConnectionChanged += _onConnection;
        _core.FrameReceived += _onFrame;
    }

    private readonly Action<string> _onSessionLog;
    private readonly Action<bool> _onConnection;
    private readonly Action<string> _onFrame;

    public int PollIntervalMs { get; init; } = 25;
    public long LinesIn { get; private set; }
    public long LinesOut { get; private set; }
    public long CommandsOut { get; private set; }
    /// <summary>等待寫出的裝置行數(診斷;有上限,見 <see cref="DeviceLineQueue"/>)</summary>
    public int DeviceQueueLength => _toDevice.Count;
    /// <summary>被同類新行取代(裝置跟不上)而沒有送出的行數(診斷)</summary>
    public long DeviceLinesCoalesced => _toDevice.Coalesced;
    /// <summary>每一行送往裝置前通知(測試/除錯)</summary>
    public event Action<string>? DeviceLineSent;
    /// <summary>每一筆指令送往核心前通知(測試/除錯)</summary>
    public event Action<SimCommand>? CommandSent;

    /// <summary>執行到取消或裝置連結結束。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _core.Start();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteLoopAsync(linked.Token);
        var poll = PollLoopAsync(linked.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var line = await _device.ReadLineAsync(linked.Token).ConfigureAwait(false);
                if (line is null) { _log("裝置連結結束(讀到串流尾端)"); break; }
                LinesIn++;
                Dispatch(() => _session.HandleLine(line, _clock()));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(writer, poll).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private void Dispatch(Func<SessionOutput> action)
    {
        SessionOutput o;
        lock (_gate) o = action();
        if (o.IsEmpty) return;
        foreach (var c in o.Commands)
        {
            CommandsOut++;
            CommandSent?.Invoke(c);
            _log($"→ 核心 {c.ToJson()}");
            _core.Send(c);
        }
        foreach (var l in o.DeviceLines) _toDevice.TryWrite(l);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _toDevice.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_toDevice.TryRead(out var line))
                {
                    try
                    {
                        await _device.WriteLineAsync(line, ct).ConfigureAwait(false);
                        LinesOut++;
                        DeviceLineSent?.Invoke(line);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { _log($"寫入裝置失敗:{ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                Dispatch(() => _session.Poll(_clock()));
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>解除共用 session/core 的訂閱並釋放裝置;core 由呼叫端(Program)擁有,這裡不關閉(重開序列埠時沿用同一條核心連線)。</summary>
    public async ValueTask DisposeAsync()
    {
        _toDevice.Complete();
        _session.Log -= _onSessionLog;
        _core.ConnectionChanged -= _onConnection;
        _core.FrameReceived -= _onFrame;
        await _device.DisposeAsync().ConfigureAwait(false);
    }
}
