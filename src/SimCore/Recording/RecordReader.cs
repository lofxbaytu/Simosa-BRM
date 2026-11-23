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
    /// <summary>快照行(<see cref="RecordReader.Read(string, bool)"/> 以 includeSnapshots: false 讀時為空,只計數於 <see cref="SnapshotLines"/>)</summary>
    public required IReadOnlyList<RecordSnapshot> Snapshots { get; init; }
    /// <summary>快照行數(不論是否保留內容)</summary>
    public long SnapshotLines { get; init; }
    /// <summary>核心事件(交通、時間表、評估、教官、引擎;講評站用)</summary>
    public required IReadOnlyList<RecordEvent> Events { get; init; }
    /// <summary>所有 summary 行(最後一行為定稿;舊檔無則空)</summary>
    public required IReadOnlyList<RecordSummary> Summaries { get; init; }
    /// <summary>最後一行 summary(無則 null)</summary>
    public RecordSummary? Summary => Summaries.Count > 0 ? Summaries[^1] : null;
    /// <summary>所有 session 行(紀錄開啟後收到的 sessionInfo;舊檔無則空)</summary>
    public IReadOnlyList<RecordSession> Sessions { get; init; } = Array.Empty<RecordSession>();
    /// <summary>VHF 角色扮演的通話/文字/DSC 行(帶外;舊檔無則空;講評用)</summary>
    public IReadOnlyList<RecordVhf> Vhf { get; init; } = Array.Empty<RecordVhf>();
    /// <summary>參與者與休息時間確認:header 有就用 header,否則取最後一行 session;都沒有為 null</summary>
    public RecordSessionInfo? SessionInfo => Header.SessionInfo ?? (Sessions.Count > 0 ? Sessions[^1].ToInfo() : null);
    /// <summary>參與者資訊來源:header | session | null</summary>
    public string? SessionInfoSource => Header.SessionInfo is not null ? "header" : Sessions.Count > 0 ? "session" : null;
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
    /// <summary>
    /// 讀入整份紀錄並重算 SHA-256。<paramref name="includeSnapshots"/> = false 時快照行只計數與納入雜湊、不反序列化也不保留
    /// (重播與打包工具不用快照;一份 2 h 紀錄有 720 份快照,第三十輪 ENG-C3 量到讀回常駐 8.7–133 MB)。
    /// </summary>
    public static RecordFile Read(string path, bool includeSnapshots = true)
    {
        RecordHeader? header = null;
        RecordFooter? footer = null;
        var inputs = new List<RecordInput>();
        var snapshots = new List<RecordSnapshot>();
        var events = new List<RecordEvent>();
        var summaries = new List<RecordSummary>();
        var sessions = new List<RecordSession>();
        var vhf = new List<RecordVhf>();
        long stateLines = 0;
        long snapshotLines = 0;
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
                case RecordKinds.Snapshot:
                    snapshotLines++;
                    if (includeSnapshots) snapshots.Add(ContractJson.Deserialize<RecordSnapshot>(line)!);
                    break;
                case RecordKinds.Event: events.Add(ContractJson.Deserialize<RecordEvent>(line)!); break;
                case RecordKinds.Summary: summaries.Add(ContractJson.Deserialize<RecordSummary>(line)!); break;
                case RecordKinds.Session: sessions.Add(ContractJson.Deserialize<RecordSession>(line)!); break;
                case RecordKinds.Vhf: vhf.Add(ContractJson.Deserialize<RecordVhf>(line)!); break;
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
            SnapshotLines = snapshotLines,
            Events = events,
            Summaries = summaries,
            Sessions = sessions,
            Vhf = vhf,
            StateLines = stateLines,
            Footer = footer,
            ComputedSha256 = Convert.ToHexString(hash.GetCurrentHash()),
        };
    }

    /// <summary>
    /// 只讀標頭(第一個 header 行;正常紀錄即第一行),不讀其餘各行、不檢查完整性。用於只需標頭的地方(主控程序重播前選動力學模型);
    /// 完整讀取與雜湊檢查仍由 <see cref="Read(string, bool)"/> 負責。
    /// </summary>
    public static RecordHeader ReadHeader(string path)
    {
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            var kind = PeekKind(line);
            if (kind == RecordKinds.Header) return ContractJson.Deserialize<RecordHeader>(line) ?? throw new InvalidDataException($"紀錄檔 header 無法解析:{path}");
            if (kind == RecordKinds.Footer) break;
        }
        throw new InvalidDataException($"紀錄檔缺少 header:{path}");
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
