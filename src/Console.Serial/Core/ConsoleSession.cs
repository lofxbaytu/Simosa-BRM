using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.ConsoleSerial.Core;

/// <summary>一次呼叫產生的輸出:送往裝置的行(已附校驗和)與送往核心的指令。</summary>
public sealed class SessionOutput
{
    public List<string> DeviceLines { get; } = new();
    public List<SimCommand> Commands { get; } = new();
    public bool IsEmpty => DeviceLines.Count == 0 && Commands.Count == 0;
}

/// <summary>解析錯誤計數(診斷;console-serial.md 第 5 節:只記 log 與計數,不回應裝置)。</summary>
public sealed class ParseStats
{
    public long Lines;
    public long Accepted;
    public long ChecksumInvalid;
    public long ChecksumMissing;
    public long UnknownCommand;
    public long BadArgument;
    public long OutOfRange;
    public long Rejected => ChecksumInvalid + ChecksumMissing + UnknownCommand + BadArgument + OutOfRange;

    public void Count(ParseError e)
    {
        switch (e)
        {
            case ParseError.ChecksumInvalid: ChecksumInvalid++; break;
            case ParseError.ChecksumMissing: ChecksumMissing++; break;
            case ParseError.UnknownCommand: UnknownCommand++; break;
            case ParseError.BadArgument: BadArgument++; break;
            case ParseError.OutOfRange: OutOfRange++; break;
        }
    }
}

/// <summary>
/// 操控台一條連線的協定狀態機(可測,無 I/O):裝置行 → <see cref="ProtocolParser"/> → 去抖(<see cref="ValueDebouncer"/>)→
/// <see cref="CommandMapper"/> → SimCommand;核心狀態幀 → <see cref="FeedbackFormatter"/> → LED/DISP/BUZZ 行;
/// HELLO 握手(回 ACK、重送全部顯示)、心跳逾時(<see cref="HeartbeatTimeoutMs"/> 內無任何行即視為斷線:log、停止回寫;再收到行即恢復)、
/// 裝置宣告 CS 後主機送出的每一行附校驗和。時間以毫秒計、由呼叫端注入;非執行緒安全,由 <see cref="Bridge.ConsoleBridge"/> 加鎖。
/// </summary>
public sealed class ConsoleSession
{
    public const double RudderMinIntervalMs = 50.0;   // 20 Hz
    public const double RudderMinDeltaDeg = 0.5;
    public const double RpmMinDelta = 1.0;
    public const double ThrusterMinDeltaPct = 1.0;

    private readonly ProtocolParser _parser = new();
    private readonly FeedbackFormatter _feedback = new();
    private readonly ValueDebouncer _rudder = new(RudderMinIntervalMs, RudderMinDeltaDeg);
    private readonly ValueDebouncer _rpm = new(RudderMinIntervalMs, RpmMinDelta);
    private readonly ValueDebouncer _thruster = new(RudderMinIntervalMs, ThrusterMinDeltaPct);
    private long _lastLineMs = long.MinValue;

    public ConsoleSession(double heartbeatTimeoutMs = 3000.0)
    {
        HeartbeatTimeoutMs = heartbeatTimeoutMs;
    }

    public double HeartbeatTimeoutMs { get; }
    public event Action<string>? Log;
    /// <summary>裝置視為連線中(收到任何行後為 true;逾時為 false)</summary>
    public bool DeviceConnected { get; private set; }
    public bool HelloReceived { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public IReadOnlyList<string> Caps { get; private set; } = Array.Empty<string>();
    public bool ChecksumRequired => _parser.RequireChecksum;
    public bool CoreConnected => _feedback.CoreConnected;
    public ParseStats Stats { get; } = new();
    public long CommandsProduced { get; private set; }
    public long HeartbeatTimeouts { get; private set; }
    public ConsoleStateView? LastState => _feedback.LastView;

    /// <summary>核心 WebSocket 連線狀態改變 → LED,LINK(裝置連線中才送)。</summary>
    public SessionOutput SetCoreConnected(bool connected, long nowMs)
    {
        var o = new SessionOutput();
        if (_feedback.CoreConnected == connected) return o;
        _feedback.CoreConnected = connected;
        Log?.Invoke(connected ? "核心已連線(LED,LINK,1)" : "核心斷線(LED,LINK,0);指令暫存、每秒重連");
        Emit(o, _feedback.Update(null, nowMs));
        return o;
    }

    /// <summary>核心狀態幀(JSON 文字)→ 回寫行;非狀態幀略過。</summary>
    public SessionOutput HandleStateFrame(string json, long nowMs)
    {
        var o = new SessionOutput();
        var view = ConsoleStateView.TryParse(json);
        if (view is null) return o;
        Emit(o, _feedback.Update(view, nowMs));
        return o;
    }

    /// <summary>裝置送來的一行(不含換行;容忍 CR)。</summary>
    public SessionOutput HandleLine(string? raw, long nowMs)
    {
        var o = new SessionOutput();
        var r = _parser.Parse(raw);
        if (r.Ignored) return o;
        Stats.Lines++;
        TouchDevice(o, nowMs, resend: r.Message?.Kind != ConsoleMessageKind.Hello);
        if (!r.Ok)
        {
            Stats.Count(r.Error);
            Log?.Invoke($"拒絕「{(raw ?? "").Trim()}」:{r.Error}({r.Detail})");
            return o;
        }
        Stats.Accepted++;
        var m = r.Message!;
        switch (m.Kind)
        {
            case ConsoleMessageKind.Hello:
                HelloReceived = true;
                FirmwareVersion = m.Text;
                Caps = m.Caps;
                Log?.Invoke($"HELLO 韌體 {m.Text},能力 {string.Join("+", m.Caps)}{(ChecksumRequired ? "(之後每行必帶校驗和)" : "")} → ACK 並重送全部顯示");
                o.DeviceLines.Add(Wrap("ACK"));
                Emit(o, _feedback.Resend(nowMs));
                return o;
            case ConsoleMessageKind.Heartbeat:
                return o;
            case ConsoleMessageKind.Rudder:
                Debounced(o, _rudder.Offer(m.Value, nowMs), v => SimCommand.Rudder(v));
                return o;
            case ConsoleMessageKind.Rpm:
                Debounced(o, _rpm.Offer(m.Value, nowMs), v => SimCommand.Rpm(v));
                return o;
            case ConsoleMessageKind.Thruster:
                Debounced(o, _thruster.Offer(m.Value, nowMs), v => CommandMapper.Map(new ConsoleMessage(ConsoleMessageKind.Thruster) { Value = v })[0]);
                return o;
            default:
                foreach (var c in CommandMapper.Map(m)) AddCommand(o, c);
                return o;
        }
    }

    /// <summary>定時呼叫(建議 ≤ 25 ms):補送去抖待送值與 DISP 待送值、判定心跳逾時。</summary>
    public SessionOutput Poll(long nowMs)
    {
        var o = new SessionOutput();
        if (DeviceConnected && _lastLineMs != long.MinValue && nowMs - _lastLineMs > HeartbeatTimeoutMs)
        {
            DeviceConnected = false;
            HeartbeatTimeouts++;
            Log?.Invoke($"操控台心跳逾時({HeartbeatTimeoutMs / 1000.0:0.#} s 無任何行):視為斷線,停止回寫(操舵模式不變)");
        }
        Debounced(o, _rudder.Poll(nowMs), v => SimCommand.Rudder(v));
        Debounced(o, _rpm.Poll(nowMs), v => SimCommand.Rpm(v));
        Debounced(o, _thruster.Poll(nowMs), v => CommandMapper.Map(new ConsoleMessage(ConsoleMessageKind.Thruster) { Value = v })[0]);
        Emit(o, _feedback.Poll(nowMs));
        return o;
    }

    /// <summary>任何行都算裝置活動;由斷線/未連線轉為連線時重送全部顯示(HELLO 自己會在 ACK 後重送,不重複)。</summary>
    private void TouchDevice(SessionOutput o, long nowMs, bool resend)
    {
        _lastLineMs = nowMs;
        if (DeviceConnected) return;
        DeviceConnected = true;
        Log?.Invoke(HeartbeatTimeouts > 0 ? "操控台恢復:重送全部燈號與顯示" : "操控台已連線(收到第一行)");
        if (resend) Emit(o, _feedback.Resend(nowMs));
    }

    private void Debounced(SessionOutput o, double? value, Func<double, SimCommand> make)
    {
        if (value is { } v) AddCommand(o, make(v));
    }

    private void AddCommand(SessionOutput o, SimCommand c)
    {
        CommandsProduced++;
        o.Commands.Add(c);
    }

    /// <summary>裝置連線中才回寫;行尾依裝置能力附校驗和。</summary>
    private void Emit(SessionOutput o, List<string> lines)
    {
        if (!DeviceConnected) return;
        foreach (var l in lines) o.DeviceLines.Add(Wrap(l));
    }

    private string Wrap(string payload) => ChecksumRequired ? LineChecksum.Append(payload) : payload;
}
