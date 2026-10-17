using System.Security.Cryptography;
using System.Text;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;

namespace SimosaBRM.SimCore.Recording;

/// <summary>
/// 把引擎的輸入事件(每個套用的指令)、25 Hz 狀態與快照寫成 JSON Lines(UTF-8、LF),
/// 關閉時附 footer(最終 tick、狀態雜湊、檔案 SHA-256)。純觀察者,不影響引擎的確定性。
/// 每份紀錄只有一個 header 情境:引擎即將切換情境(<see cref="SimulationEngine.ScenarioLoading"/>)時自動關閉(footer 為舊情境的最終 tick 與雜湊),
/// 由主控程序為新情境另開新檔;<c>loadScenario</c> 指令本身不寫 input 行(它不是引擎可重播的輸入)。
/// </summary>
public sealed class RecordWriter : IDisposable
{
    private readonly SimulationEngine _engine;
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly bool _includeStates;
    private long _lines;
    private long _inputs;
    private long _summaries;
    private bool _closed;

    public string Path { get; }
    /// <summary>已關閉(Close() 或情境切換時自動關閉)</summary>
    public bool Closed => _closed;
    /// <summary>關閉時的 footer(未關閉為 null)</summary>
    public RecordFooter? Footer { get; private set; }

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
            ParameterSet = engine.ParameterSet,
        });

        engine.CommandApplied += OnCommand;
        engine.SnapshotTaken += OnSnapshot;
        engine.EventRaised += OnEvent;
        engine.ScenarioLoading += OnScenarioLoading;
        if (includeStates) engine.Broadcast += OnBroadcast;
    }

    /// <summary>情境即將被換掉:在引擎狀態重設前關閉本紀錄(footer 仍是舊情境的最終 tick/雜湊)。</summary>
    private void OnScenarioLoading(SimulationEngine e, Scenario.Scenario next)
    {
        if (!_closed) Close();
    }

    /// <summary>所有事件(含交通)寫 event 行;exerciseEnded 時隨即寫一行 summary(定稿)。</summary>
    private void OnEvent(SimulationEngine e, SimEvent ev)
    {
        WriteLine(new RecordEvent { Tick = ev.Tick, Event = ev });
        if (ev.Kind == SimEventKinds.ExerciseEnded) WriteSummaryNow();
    }

    /// <summary>立即寫入目前的評估摘要(endExercise 時;Close() 前亦會寫 final 版)。</summary>
    public void WriteSummaryNow()
    {
        if (_closed) return;
        var summary = _engine.LatestSummary;
        if (summary is null) return;
        WriteLine(new RecordSummary { Tick = _engine.Tick, Summary = summary });
        _summaries++;
    }

    private void OnCommand(SimulationEngine e, SimCommand cmd, long tick)
    {
        if (cmd.Type == SimCommandType.LoadScenario) return; // 情境切換不是可重播的輸入:舊檔已在切換前關閉,新檔由主控程序另開
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
        _engine.EventRaised -= OnEvent;
        _engine.ScenarioLoading -= OnScenarioLoading;
        if (_includeStates) _engine.Broadcast -= OnBroadcast;

        // 定稿摘要(final = true;endExercise 已寫過時內容相同)
        if (_engine.Assessment is { } assessment)
        {
            var summary = _engine.Ended ? assessment.LatestSummary ?? assessment.BuildSummary(_engine.Tick, final: true) : assessment.BuildSummary(_engine.Tick, final: true);
            WriteLine(new RecordSummary { Tick = _engine.Tick, Summary = summary });
            _summaries++;
        }

        var footer = new RecordFooter
        {
            FinalTick = _engine.Tick,
            StateHash = _engine.StateHash,
            Lines = _lines,
            Inputs = _inputs,
            Sha256 = Convert.ToHexString(_hash.GetCurrentHash()),
            SummaryLines = _summaries,
        };
        var bytes = ContractJson.SerializeToUtf8Bytes(footer);
        _stream.Write(bytes);
        _stream.WriteByte((byte)'\n');
        _stream.Flush(true);
        _stream.Dispose();
        _hash.Dispose();
        Footer = footer;
        return footer;
    }

    public void Dispose()
    {
        if (!_closed) Close();
    }
}
