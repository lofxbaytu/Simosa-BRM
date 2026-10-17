using System.Buffers.Binary;

namespace SimosaBRM.Sensors.Radar;

/// <summary>
/// 二進位影像幀(WebSocket binary 訊息;radar.schema.json <c>$defs/spokeFrame</c>、docs/design/radar-arpa.md 第 4 節):
/// 40 位元組標頭(little-endian)+ spokeCount × cells 位元組強度(0–255)。方位線 j 的方位索引 = (firstSpoke + j) mod spokesPerRev,
/// 方位為「相對艏向、順時針」(天線座標;船首向上);北向上時真方位 = 方位 + heading_deg。
/// </summary>
public sealed class SpokeFrame
{
    public const uint Magic = 0x44415253; // "SRAD"(位元組 53 52 41 44)
    public const byte Version = 1;
    public const int HeaderBytes = 40;

    [Flags]
    public enum FrameFlags : byte
    {
        None = 0,
        Transmitting = 1,
        HeadingValid = 2,
        Degraded = 4,
    }

    public char RadarId { get; init; }
    public FrameFlags Flags { get; init; }
    public uint Scan { get; init; }
    public int FirstSpoke { get; init; }
    public int SpokeCount { get; init; }
    public int SpokesPerRev { get; init; }
    public int Cells { get; init; }
    public float RangeM { get; init; }
    public float HeadingDeg { get; init; }
    public float CogDeg { get; init; }
    public double T { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <summary>方位線 j 的強度資料。</summary>
    public ReadOnlySpan<byte> Spoke(int j) => Data.AsSpan(j * Cells, Cells);

    /// <summary>方位線 j 的相對方位(度,0 = 船首)。</summary>
    public double RelativeAzimuthDeg(int j) => (FirstSpoke + j) % SpokesPerRev * 360.0 / SpokesPerRev;

    /// <summary>寫入標頭到 buffer 開頭(buffer 長度須 ≥ HeaderBytes + spokeCount × cells)。</summary>
    public static void WriteHeader(Span<byte> b, char radarId, FrameFlags flags, uint scan, int firstSpoke, int spokeCount, int spokesPerRev, int cells,
        float rangeM, float headingDeg, float cogDeg, double t)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        b[4] = Version;
        b[5] = (byte)radarId;
        b[6] = (byte)flags;
        b[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], scan);
        BinaryPrimitives.WriteUInt16LittleEndian(b[12..], (ushort)firstSpoke);
        BinaryPrimitives.WriteUInt16LittleEndian(b[14..], (ushort)spokeCount);
        BinaryPrimitives.WriteUInt16LittleEndian(b[16..], (ushort)spokesPerRev);
        BinaryPrimitives.WriteUInt16LittleEndian(b[18..], (ushort)cells);
        BinaryPrimitives.WriteSingleLittleEndian(b[20..], rangeM);
        BinaryPrimitives.WriteSingleLittleEndian(b[24..], headingDeg);
        BinaryPrimitives.WriteSingleLittleEndian(b[28..], cogDeg);
        BinaryPrimitives.WriteDoubleLittleEndian(b[32..], t);
    }

    public static SpokeFrame Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) throw new InvalidDataException("不是雷達影像幀(magic 不符)");
        if (b[4] != Version) throw new InvalidDataException($"不支援的幀版本 {b[4]}");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(b[14..]);
        var cells = BinaryPrimitives.ReadUInt16LittleEndian(b[18..]);
        if (b.Length < HeaderBytes + count * cells) throw new InvalidDataException("幀長度不足");
        return new SpokeFrame
        {
            RadarId = (char)b[5],
            Flags = (FrameFlags)b[6],
            Scan = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            FirstSpoke = BinaryPrimitives.ReadUInt16LittleEndian(b[12..]),
            SpokeCount = count,
            SpokesPerRev = BinaryPrimitives.ReadUInt16LittleEndian(b[16..]),
            Cells = cells,
            RangeM = BinaryPrimitives.ReadSingleLittleEndian(b[20..]),
            HeadingDeg = BinaryPrimitives.ReadSingleLittleEndian(b[24..]),
            CogDeg = BinaryPrimitives.ReadSingleLittleEndian(b[28..]),
            T = BinaryPrimitives.ReadDoubleLittleEndian(b[32..]),
            Data = b.Slice(HeaderBytes, count * cells).ToArray(),
        };
    }
}
