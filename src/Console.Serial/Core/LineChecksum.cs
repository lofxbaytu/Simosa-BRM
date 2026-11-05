using System.Globalization;

namespace SimosaBRM.ConsoleSerial.Core;

/// <summary>行尾 <c>*XX</c> 校驗和的狀態(console-serial.md 第 1 節)。</summary>
public enum ChecksumStatus
{
    /// <summary>行上沒有校驗和</summary>
    None,
    Valid,
    /// <summary>格式不對或值不符</summary>
    Invalid,
}

/// <summary>NMEA 式校驗和:<c>*</c> 之前全部字元逐位元 XOR,兩位大寫十六進位。</summary>
public static class LineChecksum
{
    public static byte Compute(ReadOnlySpan<char> payload)
    {
        byte x = 0;
        foreach (var c in payload) x ^= unchecked((byte)c);
        return x;
    }

    public static string Hex(ReadOnlySpan<char> payload) => Compute(payload).ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>附上校驗和:<c>payload*XX</c>。</summary>
    public static string Append(string payload) => string.Concat(payload, "*", Hex(payload));

    /// <summary>拆出 <paramref name="payload"/>(不含 <c>*XX</c>)並驗證;沒有 <c>*</c> 時原樣回傳 <see cref="ChecksumStatus.None"/>。</summary>
    public static ChecksumStatus Split(string line, out string payload)
    {
        var i = line.LastIndexOf('*');
        if (i < 0)
        {
            payload = line;
            return ChecksumStatus.None;
        }
        payload = line[..i];
        var hex = line.AsSpan(i + 1).Trim();
        if (hex.Length != 2 || !byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var given)) return ChecksumStatus.Invalid;
        return given == Compute(payload) ? ChecksumStatus.Valid : ChecksumStatus.Invalid;
    }
}
