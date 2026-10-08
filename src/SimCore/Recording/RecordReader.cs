using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimosaBRM.SimCore.Contracts;

namespace SimosaBRM.SimCore.Recording;

/// <summary>讀入的紀錄檔(狀態行只計數,不保留,以免佔記憶體;需要時用 <see cref="RecordReader.ReadStates"/>)。</summary>
public sealed class RecordFile
{
    public required RecordHeader Header { get; init; }
    public required IReadOnlyList<RecordInput> Inputs { get; init; }
    public required IReadOnlyList<RecordSnapshot> Snapshots { get; init; }
    public required long StateLines { get; init; }
    public RecordFooter? Footer { get; init; }
    /// <summary>重新計算的 SHA-256(hex)</summary>
    public required string ComputedSha256 { get; init; }
    /// <summary>footer 存在且雜湊一致</summary>
    public bool IntegrityOk => Footer is not null && string.Equals(Footer.Sha256, ComputedSha256, StringComparison.OrdinalIgnoreCase);
}

/// <summary>JSON Lines 紀錄讀取與完整性檢查。</summary>
public static class RecordReader
{
    public static RecordFile Read(string path)
    {
        RecordHeader? header = null;
        RecordFooter? footer = null;
        var inputs = new List<RecordInput>();
        var snapshots = new List<RecordSnapshot>();
        long stateLines = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            var kind = PeekKind(line);
            if (kind == RecordKinds.Footer)
            {
                footer = ContractJson.Deserialize<RecordFooter>(line);
                break;
            }
            hash.AppendData(Encoding.UTF8.GetBytes(line));
            hash.AppendData("\n"u8);
            switch (kind)
            {
                case RecordKinds.Header: header = ContractJson.Deserialize<RecordHeader>(line); break;
                case RecordKinds.Input: inputs.Add(ContractJson.Deserialize<RecordInput>(line)!); break;
                case RecordKinds.Snapshot: snapshots.Add(ContractJson.Deserialize<RecordSnapshot>(line)!); break;
                case RecordKinds.State: stateLines++; break;
                default: break; // 未知行型別:保留在雜湊內但忽略
            }
        }

        if (header is null) throw new InvalidDataException($"紀錄檔缺少 header:{path}");
        return new RecordFile
        {
            Header = header,
            Inputs = inputs,
            Snapshots = snapshots,
            StateLines = stateLines,
            Footer = footer,
            ComputedSha256 = Convert.ToHexString(hash.GetCurrentHash()),
        };
    }

    /// <summary>逐行讀出 25 Hz 狀態(講評站航跡圖用)。</summary>
    public static IEnumerable<OwnShipState> ReadStates(string path)
    {
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0 || PeekKind(line) != RecordKinds.State) continue;
            var rs = ContractJson.Deserialize<RecordState>(line);
            if (rs is not null) yield return rs.State;
        }
    }

    private static string? PeekKind(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null;
    }
}
