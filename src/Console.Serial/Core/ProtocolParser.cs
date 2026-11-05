using System.Globalization;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.ConsoleSerial.Core;

/// <summary>裝置 → 主機的行種類(console-serial.md 第 2 節)。</summary>
public enum ConsoleMessageKind
{
    Hello,
    Rudder,
    Telegraph,
    Rpm,
    Thruster,
    Button,
    Nfu,
    Heartbeat,
}

/// <summary>解析後的一行。數值欄位已夾在協定範圍內;<see cref="Text"/> 為車鐘檔位(大寫)、NFU 方向(L/R/0)或 HELLO 的韌體版本。</summary>
public sealed record ConsoleMessage(ConsoleMessageKind Kind)
{
    public double Value { get; init; }
    public string? Text { get; init; }
    /// <summary>按鈕名稱(ACK/SILENCE/FU/NFU/EMERG/BNWAS)</summary>
    public string? Name { get; init; }
    public bool Pressed { get; init; }
    /// <summary>HELLO 宣告的能力(大寫代號)</summary>
    public IReadOnlyList<string> Caps { get; init; } = Array.Empty<string>();
}

public enum ParseError
{
    None,
    /// <summary>空行或 # 註解:略過,不計數</summary>
    Empty,
    ChecksumInvalid,
    /// <summary>HELLO 宣告 CS 後缺校驗和</summary>
    ChecksumMissing,
    UnknownCommand,
    BadArgument,
    OutOfRange,
}

public readonly record struct ParseResult(ConsoleMessage? Message, ParseError Error, string Detail)
{
    public bool Ok => Message is not null;
    public bool Ignored => Error == ParseError.Empty;
    public static ParseResult Fail(ParseError e, string detail) => new(null, e, detail);
    public static ParseResult Success(ConsoleMessage m) => new(m, ParseError.None, "");
}

/// <summary>
/// 文字行 → <see cref="ConsoleMessage"/>。容忍 CR LF 與前後空白、命令字不分大小寫;校驗和規則見 <see cref="LineChecksum"/>:
/// 行上有 <c>*XX</c> 一律驗證,HELLO 宣告 <c>CS</c> 後缺少即拒絕(<see cref="RequireChecksum"/>)。純函數式(除 RequireChecksum 狀態),可直接以字串測試。
/// </summary>
public sealed class ProtocolParser
{
    /// <summary>接受的車鐘檔位(state.schema.json telegraph 列舉:EFAS…NAVF)</summary>
    public static readonly IReadOnlyList<string> TelegraphOrders = Enum.GetNames<TelegraphOrder>();
    public static readonly IReadOnlyList<string> ButtonNames = new[] { "ACK", "SILENCE", "FU", "NFU", "EMERG", "BNWAS" };
    public const double RudderLimitDeg = 35.0;
    /// <summary>超過此值整行拒絕(電位計校正誤差容許 1°,其內夾到 ±35)</summary>
    public const double RudderRejectDeg = 36.0;
    public const double RpmRejectAbs = 1000.0;
    public const double ThrusterRejectPct = 101.0;

    /// <summary>HELLO caps 含 CS 後為 true:之後每行必帶校驗和</summary>
    public bool RequireChecksum { get; set; }

    public ParseResult Parse(string? raw)
    {
        var line = (raw ?? string.Empty).Trim();
        if (line.Length == 0 || line[0] == '#') return ParseResult.Fail(ParseError.Empty, "");
        var cs = LineChecksum.Split(line, out var payload);
        if (cs == ChecksumStatus.Invalid) return ParseResult.Fail(ParseError.ChecksumInvalid, "校驗和不符");
        if (cs == ChecksumStatus.None && RequireChecksum) return ParseResult.Fail(ParseError.ChecksumMissing, "裝置已宣告 CS,此行缺校驗和");
        var fields = payload.Split(',');
        for (var i = 0; i < fields.Length; i++) fields[i] = fields[i].Trim();
        var cmd = fields[0].ToUpperInvariant();
        string? Arg(int i) => i < fields.Length ? fields[i] : null;

        switch (cmd)
        {
            case "HELLO":
            {
                var fw = Arg(1) ?? "";
                var caps = (Arg(2) ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(c => c.ToUpperInvariant()).Distinct().ToArray();
                RequireChecksum = caps.Contains("CS");
                return ParseResult.Success(new ConsoleMessage(ConsoleMessageKind.Hello) { Text = fw, Caps = caps });
            }
            case "RUD":
            {
                if (!TryNumber(Arg(1), out var v)) return ParseResult.Fail(ParseError.BadArgument, "RUD 需要數值(度)");
                if (Math.Abs(v) > RudderRejectDeg) return ParseResult.Fail(ParseError.OutOfRange, $"舵角 {Fmt(v)}° 超出 ±{RudderRejectDeg:F0}");
                return ParseResult.Success(new ConsoleMessage(ConsoleMessageKind.Rudder) { Value = Math.Clamp(v, -RudderLimitDeg, RudderLimitDeg) });
            }
            case "TEL":
            {
                var order = (Arg(1) ?? "").ToUpperInvariant();
                if (!TelegraphOrders.Contains(order)) return ParseResult.Fail(ParseError.BadArgument, $"未知車鐘檔位「{Arg(1)}」");
                return ParseResult.Success(new ConsoleMessage(ConsoleMessageKind.Telegraph) { Text = order });
            }
            case "RPM":
            {
                if (!TryNumber(Arg(1), out var v)) return ParseResult.Fail(ParseError.BadArgument, "RPM 需要數值");
                if (Math.Abs(v) > RpmRejectAbs) return ParseResult.Fail(ParseError.OutOfRange, $"轉速 {Fmt(v)} 超出 ±{RpmRejectAbs:F0}");
                return ParseResult.Success(new ConsoleMessage(ConsoleMessageKind.Rpm) { Value = v });
            }
            case "THR":
            {
                if (!TryNumber(Arg(1), out var v)) return ParseResult.Fail(ParseError.BadArgument, "THR 需要數值(-100…100)");
                if (Math.Abs(v) > ThrusterRejectPct) return ParseResult.Fail(ParseError.OutOfRange, $"側推 {Fmt(v)} % 超出 ±100");
                return ParseResult.Success(new ConsoleMessage(ConsoleMessageKind.Thruster) { Value = Math.Clamp(v, -100.0, 100.0) });
            }
            case "BTN":
            {
                var name = (Arg(1) ?? "").ToUpperInvariant();
                if (!ButtonNames.Contains(name)) return ParseResult.Fail(ParseError.BadArgument, $"未知按鈕「{Arg(1)}」");
                var state = Arg(2);
                if (state is not ("0" or "1")) return ParseResult.Fail(ParseError.BadArgument, $"按鈕狀態須為 1|0(收到「{state}」)");
                return ParseResult.Success(new ConsoleMessage(ConsoleMessageKind.Button) { Name = name, Pressed = state == "1" });
            }
            case "NFU":
            {
                var dir = (Arg(1) ?? "").ToUpperInvariant();
                if (dir is not ("L" or "R" or "0")) return ParseResult.Fail(ParseError.BadArgument, $"NFU 方向須為 L|R|0(收到「{Arg(1)}」)");
                return ParseResult.Success(new ConsoleMessage(ConsoleMessageKind.Nfu) { Text = dir });
            }
            case "HB":
                return ParseResult.Success(new ConsoleMessage(ConsoleMessageKind.Heartbeat));
            default:
                return ParseResult.Fail(ParseError.UnknownCommand, $"未知命令「{fields[0]}」");
        }
    }

    private static bool TryNumber(string? s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
