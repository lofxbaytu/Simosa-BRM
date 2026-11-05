namespace SimosaBRM.ConsoleSerial.Core;

/// <summary>
/// 連續量(舵令/轉速/側推)的去抖與速率限制(console-serial.md 第 5 節):
/// 與上次<b>送出</b>值差小於 <see cref="MinDelta"/> → 丟棄(並清除待送);距上次送出未滿 <see cref="MinIntervalMs"/> → 記為待送,
/// <see cref="Poll"/> 時間隔到即補送(最後位置不遺失);首筆一律送。時間以毫秒計,由呼叫端注入(可測)。
/// </summary>
public sealed class ValueDebouncer
{
    private long _lastSentMs = long.MinValue;
    private double? _pending;

    public ValueDebouncer(double minIntervalMs, double minDelta)
    {
        MinIntervalMs = minIntervalMs;
        MinDelta = minDelta;
    }

    public double MinIntervalMs { get; }
    public double MinDelta { get; }
    public double? LastSent { get; private set; }
    public bool HasPending => _pending is not null;

    /// <summary>收到新值;回傳應立即送出的值,或 null(丟棄/待送)。</summary>
    public double? Offer(double value, long nowMs)
    {
        if (LastSent is { } last && Math.Abs(value - last) < MinDelta)
        {
            _pending = null;   // 回到已送出的位置:待送作廢
            return null;
        }
        if (LastSent is null || nowMs - _lastSentMs >= MinIntervalMs) return Send(value, nowMs);
        _pending = value;
        return null;
    }

    /// <summary>定時呼叫;待送值且間隔已到時回傳它。</summary>
    public double? Poll(long nowMs)
    {
        if (_pending is not { } p || nowMs - _lastSentMs < MinIntervalMs) return null;
        return Send(p, nowMs);
    }

    private double Send(double value, long nowMs)
    {
        LastSent = value;
        _lastSentMs = nowMs;
        _pending = null;
        return value;
    }
}
