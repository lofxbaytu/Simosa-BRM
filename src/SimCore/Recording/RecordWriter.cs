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
    // 引擎執行緒寫 input/state/event/snapshot,主控程序的 WebSocket 執行緒寫 session 行、主執行緒關檔:以同一把鎖序列化檔案與雜湊的寫入
    private readonly object _sync = new();
    private long _lines;
    private long _inputs;
    private long _summaries;
    private long _sessions;
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

    /// <param name="session">開檔前已收到的參與者與休息時間確認(sessionInfo 指令;寫進 header 的 participants / restHoursConfirmation / course)</param>
    public RecordWriter(string path, SimulationEngine engine, bool includeStates = true, RecordSessionInfo? session = null)
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
            ManoeuvreTargets = HeaderTargets(engine),
            Participants = session?.Participants,
            RestHoursConfirmation = session?.RestHoursConfirmation,
            Course = session?.Course,
        });

        engine.CommandApplied += OnCommand;
        engine.SnapshotTaken += OnSnapshot;
        engine.EventRaised += OnEvent;
        engine.ScenarioLoading += OnScenarioLoading;
        if (includeStates) engine.Broadcast += OnBroadcast;
    }

    /// <summary>評估模組使用操縱量測且有目標值時,標頭內嵌目標檔內容(重播不受檔案日後更新影響)。</summary>
    public static RecordManoeuvreTargets? HeaderTargets(SimulationEngine engine)
    {
        var tracker = engine.Assessment?.Manoeuvres;
        if (tracker is not { Enabled: true, Targets: { } t }) return null;
        return new RecordManoeuvreTargets
        {
            ShipId = engine.Ship.Id, Loading = engine.Loading, Path = t.RelativePath ?? Assessment.ManoeuvreTargets.RelativePathFor(engine.Ship.Id),
            Sha256 = t.Sha256, Content = t.Content.Clone(),
        };
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
        if (cmd.Type is SimCommandType.SessionInfo or SimCommandType.Shutdown) return; // 主控程序層指令(不應進引擎佇列;保險起見不寫 input 行)
        _inputs++;
        WriteLine(new RecordInput { Tick = tick, Command = cmd });
    }

    /// <summary>
    /// 紀錄已開啟後收到 sessionInfo:追加一行 session(tick 與當前模擬時間;不進狀態雜湊鏈、只計入 footer 的檔案 SHA-256)。
    /// 可由任何執行緒呼叫;已關閉時不寫並回傳 false(主控程序改把資訊帶進下一份紀錄的 header)。
    /// </summary>
    public bool WriteSession(RecordSessionInfo info)
    {
        lock (_sync)
        {
            if (_closed) return false;
            WriteLine(new RecordSession
            {
                Tick = _engine.Tick, T = _engine.Time,
                Participants = info.Participants, RestHoursConfirmation = info.RestHoursConfirmation, Course = info.Course,
            });
            _sessions++;
            return true;
        }
    }

    /// <summary>已寫入的 session 行數</summary>
    public long SessionLines => _sessions;

    private void OnBroadcast(SimulationEngine e, OwnShipState state) => WriteLine(new RecordState { State = state });

    private void OnSnapshot(SimulationEngine e, EngineSnapshot snap) => WriteLine(new RecordSnapshot { Tick = snap.Tick, Snapshot = snap });

    private void WriteLine<T>(T line)
    {
        var bytes = ContractJson.SerializeToUtf8Bytes(line);
        lock (_sync)
        {
            if (_closed) return; // 關檔與引擎執行緒的最後幾行競爭時丟棄(footer 已定稿)
            _hash.AppendData(bytes);
            _hash.AppendData("\n"u8);
            _stream.Write(bytes);
            _stream.WriteByte((byte)'\n');
            _lines++;
        }
    }

    /// <summary>寫入 footer(含關檔牆鐘 closedUtc)並關閉;回傳 footer。</summary>
    public RecordFooter Close()
    {
        lock (_sync)
        {
            if (_closed) throw new InvalidOperationException("紀錄已關閉");
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
            _closed = true;

            var footer = new RecordFooter
            {
                FinalTick = _engine.Tick,
                StateHash = _engine.StateHash,
                Lines = _lines,
                Inputs = _inputs,
                Sha256 = Convert.ToHexString(_hash.GetCurrentHash()),
                SummaryLines = _summaries,
                ClosedUtc = DateTime.UtcNow,
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
    }

    public void Dispose()
    {
        if (!_closed) Close();
    }
}
