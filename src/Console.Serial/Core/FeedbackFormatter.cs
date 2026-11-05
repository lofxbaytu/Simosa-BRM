using System.Globalization;

namespace SimosaBRM.ConsoleSerial.Core;

/// <summary>
/// 核心狀態 → 主機→裝置的 <c>LED</c> / <c>DISP</c> / <c>BUZZ</c> 行(console-serial.md 第 3、6 節;不含校驗和,由 <see cref="ConsoleSession"/> 附加):
/// LED 只在值改變時送;DISP 每欄位最多 5 Hz、只在顯示字串改變時送(被速率限制擋下的值記為待送,<see cref="Poll"/> 時補送);
/// 新出現的未確認可聽警報送一次 <c>BUZZ,500</c>;<see cref="Reset"/> 清掉「已送出」記憶讓下一次 <see cref="Update"/> 重送全部(不重送蜂鳴)。
/// 純邏輯、時間由呼叫端注入(毫秒)。
/// </summary>
public sealed class FeedbackFormatter
{
    public const double DisplayMinIntervalMs = 200.0;   // 5 Hz
    public const int BuzzMs = 500;
    public static readonly string[] SteeringLeds = { "FU", "NFU", "AUTO", "EMERG" };

    private readonly Dictionary<string, string> _ledSent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DisplayField> _disp = new(StringComparer.Ordinal)
    {
        ["RUD"] = new(), ["RPM"] = new(), ["HDG"] = new(), ["TEL"] = new(),
    };
    private readonly HashSet<string> _buzzed = new(StringComparer.Ordinal);

    private sealed class DisplayField
    {
        public string? Sent;
        public long SentMs = long.MinValue;
        public string? Pending;
    }

    /// <summary>主機服務與核心 WebSocket 的連線狀態(LED,LINK)</summary>
    public bool CoreConnected { get; set; }
    public ConsoleStateView? LastView { get; private set; }

    /// <summary>清掉已送出記憶:下一次 <see cref="Update"/>(或 <see cref="Resend"/>)重送全部 LED 與 DISP;已蜂鳴過的警報不再蜂鳴。</summary>
    public void Reset()
    {
        _ledSent.Clear();
        foreach (var f in _disp.Values) { f.Sent = null; f.SentMs = long.MinValue; f.Pending = null; }
    }

    /// <summary>重送全部燈號與顯示(裝置重新連線/握手後;沒有狀態時只送 LINK 與預設操舵燈)。</summary>
    public List<string> Resend(long nowMs)
    {
        Reset();
        return Update(LastView, nowMs);
    }

    /// <summary>套用新狀態(null = 只更新 LINK),回傳應送往裝置的行。</summary>
    public List<string> Update(ConsoleStateView? view, long nowMs)
    {
        var lines = new List<string>();
        Led(lines, "LINK", CoreConnected ? "1" : "0");
        if (view is not null) LastView = view;
        var v = LastView;
        if (v is null) return lines;

        // 操舵模式燈(互斥):先熄舊燈再亮新燈,重送時四顆都送
        var active = v.SteeringMode switch { "nfu" => "NFU", "autopilot" => "AUTO", "emergency" => "EMERG", _ => "FU" };
        foreach (var name in SteeringLeds) if (name != active) Led(lines, name, "0");
        Led(lines, active, "1");
        Led(lines, "ALARM", v.AlertUnacknowledged ? "blink" : v.AlertActiveAcknowledged ? "1" : "0");
        Led(lines, "BNWAS", !v.BnwasActive ? "0" : v.BnwasStage >= 1 ? "blink" : "1");

        // 四捨五入(遠離零)後再格式化;-0.04 顯示 0.0 而非 -0.0
        var rud = Math.Round(v.Rudder, 1, MidpointRounding.AwayFromZero);
        if (rud == 0) rud = 0.0;
        Disp(lines, "RUD", rud.ToString("0.0", CultureInfo.InvariantCulture), nowMs);
        var rpm = Math.Round(v.Rpm, MidpointRounding.AwayFromZero);
        if (rpm == 0) rpm = 0.0;
        Disp(lines, "RPM", rpm.ToString("0", CultureInfo.InvariantCulture), nowMs);
        var hdg = ((int)Math.Round(v.Heading, MidpointRounding.AwayFromZero) % 360 + 360) % 360;
        Disp(lines, "HDG", hdg.ToString("000", CultureInfo.InvariantCulture), nowMs);
        if (v.Telegraph.Length > 0) Disp(lines, "TEL", v.Telegraph, nowMs);

        // 新警報(未確認且可聽、尚未蜂鳴過的鍵)→ BUZZ 一次
        var fresh = false;
        foreach (var k in v.AudibleAlertKeys) if (_buzzed.Add(k)) fresh = true;
        if (fresh) lines.Add($"BUZZ,{BuzzMs}");
        return lines;
    }

    /// <summary>定時呼叫:補送被 5 Hz 限制擋下的 DISP 值。</summary>
    public List<string> Poll(long nowMs)
    {
        var lines = new List<string>();
        foreach (var (name, f) in _disp)
        {
            if (f.Pending is { } p && nowMs - f.SentMs >= DisplayMinIntervalMs) Send(lines, name, f, p, nowMs);
        }
        return lines;
    }

    private void Led(List<string> lines, string name, string value)
    {
        if (_ledSent.TryGetValue(name, out var prev) && prev == value) return;
        _ledSent[name] = value;
        lines.Add($"LED,{name},{value}");
    }

    private void Disp(List<string> lines, string name, string value, long nowMs)
    {
        var f = _disp[name];
        if (f.Sent == value) { f.Pending = null; return; }
        if (f.Sent is null || nowMs - f.SentMs >= DisplayMinIntervalMs) Send(lines, name, f, value, nowMs);
        else f.Pending = value;
    }

    private static void Send(List<string> lines, string name, DisplayField f, string value, long nowMs)
    {
        f.Sent = value;
        f.SentMs = nowMs;
        f.Pending = null;
        lines.Add($"DISP,{name},{value}");
    }
}
