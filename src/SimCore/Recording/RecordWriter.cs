using System.Security.Cryptography;
using System.Text;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.SimCore.Recording;

/// <summary>
/// 把引擎的輸入事件(每個套用的指令)、25 Hz 狀態與快照寫成 JSON Lines(UTF-8、LF),
/// 關閉時附 footer(最終 tick、狀態雜湊、檔案 SHA-256)。純觀察者,不影響引擎的確定性。
/// </summary>
public sealed class RecordWriter : IDisposable
{
    private readonly SimulationEngine _engine;
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly bool _includeStates;
    private long _lines;
    private long _inputs;
    private bool _closed;

    public string Path { get; }

    /// <summary>build/records/&lt;yyyyMMdd-HHmmss&gt;-&lt;scenario&gt;.jsonl</summary>
    public static string DefaultPath(string recordsDirectory, string scenarioId, DateTime? utcNow = null)
    {
        var stamp = (utcNow ?? DateTime.UtcNow).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var safe = string.Concat(scenarioId.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return System.IO.Path.Combine(recordsDirectory, $"{stamp}-{safe}.jsonl");
    }

    public RecordWriter(string path, SimulationEngine engine, bool includeStates = true)
    {
        Path = path;
        _engine = engine;
        _includeStates = includeStates;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);

        WriteLine(new RecordHeader
        {
            CreatedUtc = DateTime.UtcNow,
            ShipId = engine.Ship.Id,
            ShipName = engine.Ship.Name,
            Loading = engine.Loading,
            Seed = engine.Scenario.Seed,
            Dt = engine.Dt,
            Dynamics = engine.Dynamics.ModelName,
            Scenario = engine.Scenario,
        });

        engine.CommandApplied += OnCommand;
        engine.SnapshotTaken += OnSnapshot;
        if (includeStates) engine.Broadcast += OnBroadcast;
    }

    private void OnCommand(SimulationEngine e, SimCommand cmd, long tick)
    {
        _inputs++;
        WriteLine(new RecordInput { Tick = tick, Command = cmd });
    }

    private void OnBroadcast(SimulationEngine e, OwnShipState state) => WriteLine(new RecordState { State = state });

    private void OnSnapshot(SimulationEngine e, EngineSnapshot snap) => WriteLine(new RecordSnapshot { Tick = snap.Tick, Snapshot = snap });

    private void WriteLine<T>(T line)
    {
        var bytes = ContractJson.SerializeToUtf8Bytes(line);
        _hash.AppendData(bytes);
        _hash.AppendData("\n"u8);
        _stream.Write(bytes);
        _stream.WriteByte((byte)'\n');
        _lines++;
    }

    /// <summary>寫入 footer 並關閉;回傳 footer。</summary>
    public RecordFooter Close()
    {
        if (_closed) throw new InvalidOperationException("紀錄已關閉");
        _closed = true;
        _engine.CommandApplied -= OnCommand;
        _engine.SnapshotTaken -= OnSnapshot;
        if (_includeStates) _engine.Broadcast -= OnBroadcast;

        var footer = new RecordFooter
        {
            FinalTick = _engine.Tick,
            StateHash = _engine.StateHash,
            Lines = _lines,
            Inputs = _inputs,
            Sha256 = Convert.ToHexString(_hash.GetCurrentHash()),
        };
        var bytes = ContractJson.SerializeToUtf8Bytes(footer);
        _stream.Write(bytes);
        _stream.WriteByte((byte)'\n');
        _stream.Flush(true);
        _stream.Dispose();
        _hash.Dispose();
        return footer;
    }

    public void Dispose()
    {
        if (!_closed) Close();
    }
}
