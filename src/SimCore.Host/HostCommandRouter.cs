using System.Net;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Engine;
using SimosaBRM.SimCore.Recording;

namespace SimosaBRM.SimCore.Host;

/// <summary>主控程序層指令的回覆:<see cref="Reply"/> 送回該客戶端(ack 框);<see cref="AfterReply"/> 在回覆送出後執行(shutdown 用)。</summary>
public sealed record HostCommandResult(object Reply, Action? AfterReply = null);

/// <summary>
/// 主控程序層指令(不進引擎佇列、不寫 input 行、不進雜湊;與 getAssessment 同一層):
/// <list type="bullet">
/// <item><c>sessionInfo</c>:參與者與休息時間確認(RecordSessionInfo.TryParse 驗證)。寫進目前紀錄 — 紀錄已開啟時追加 session 行;
/// 未開啟(或已關閉等待情境切換另開新檔)時保留,由 <see cref="SwapRecorder"/> 在開新紀錄時寫進 header。回 ack {ok, detail, tick}。</item>
/// <item><c>shutdown</c>:僅接受本機或直連私有網段(教官筆電)的客戶端;語意 = 結束練習 → 寫 footer → 程序結束,回覆後 ≤ 1 s 內結束。</item>
/// </list>
/// 其他指令回傳 null(交給引擎)。
/// <para>
/// 紀錄器由本類別擁有(<see cref="Recorder"/>、<see cref="SwapRecorder"/>):sessionInfo 的「寫 session 行或留給下一份 header」
/// 與情境切換時「以目前的 Session 開新檔並換上」在同一把鎖內完成,所以不論 sessionInfo 落在切換前後,資訊都恰好進入一份紀錄
/// (舊檔 session 行 / 新檔 header / 新檔 session 行),不會兩邊都沒有(第十六輪審查已知限制 1)。
/// </para>
/// </summary>
public sealed class HostCommandRouter
{
    private readonly SimulationEngine _engine;
    private readonly ShutdownCoordinator _shutdown;
    // 保護 Session 與 _recorder 的一致性:sessionInfo(WebSocket 執行緒)與 SwapRecorder(引擎執行緒的情境切換)互斥
    private readonly object _sync = new();
    private RecordWriter? _recorder;

    public HostCommandRouter(SimulationEngine engine, ShutdownCoordinator shutdown)
    {
        _engine = engine;
        _shutdown = shutdown;
    }

    /// <summary>目前的紀錄器(可為 null = 未開啟紀錄;只由 <see cref="SwapRecorder"/> 換上)</summary>
    public RecordWriter? Recorder
    {
        get { lock (_sync) return _recorder; }
    }
    /// <summary>最近一次接受的參與者資訊(開新紀錄時寫進 header)</summary>
    public RecordSessionInfo? Session { get; private set; }

    /// <summary>
    /// 開第一份紀錄或情境切換另開新檔:在 <c>_sync</c> 內以目前的 <see cref="Session"/> 呼叫 <paramref name="factory"/> 建立新紀錄器
    /// (通常 <c>new RecordWriter(path, engine, session: session)</c>)並換上,回傳新紀錄器。舊紀錄器不由此關閉
    /// (情境切換時 RecordWriter 已在 <see cref="SimulationEngine.ScenarioLoading"/> 自動關閉;程序結束時由主控程序關閉)。
    /// factory 擲例外時紀錄器維持原樣。
    /// </summary>
    public RecordWriter SwapRecorder(Func<RecordSessionInfo?, RecordWriter> factory)
    {
        lock (_sync)
        {
            var writer = factory(Session);
            _recorder = writer;
            return writer;
        }
    }
    public event Action<RecordSessionInfo, string>? SessionInfoAccepted;
    public event Action<string>? Log;

    /// <summary>非 null = 已處理(回覆 ack);null = 不是主控程序層指令。</summary>
    public HostCommandResult? Handle(SimCommand cmd, IPEndPoint? remote, bool readOnly = false)
    {
        switch (cmd.Type)
        {
            case SimCommandType.SessionInfo:
            {
                if (readOnly) return Ack(cmd, false, "重播模式:不接受 sessionInfo");
                var info = RecordSessionInfo.TryParse(cmd.Args, out var error);
                if (info is null) return Ack(cmd, false, $"sessionInfo 被拒:{error}");
                string where;
                lock (_sync)
                {
                    // 與 SwapRecorder 同一把鎖:此處看到的紀錄器若已關閉(情境切換中),Session 必定被接下來的 SwapRecorder 寫進新檔 header
                    Session = info;
                    where = _recorder is not null && _recorder.WriteSession(info) ? "session 行" : "下一份紀錄的 header";
                }
                SessionInfoAccepted?.Invoke(info, where);
                Log?.Invoke($"sessionInfo 已寫入{where}:{info.Describe()}");
                return Ack(cmd, true, $"已寫入{where}:{info.Describe()}");
            }
            case SimCommandType.Shutdown:
            {
                if (readOnly) return Ack(cmd, false, "重播模式:不接受 shutdown");
                if (!IsAllowedShutdownClient(remote)) return Ack(cmd, false, $"shutdown 只接受本機或直連網段的教官站(來源 {remote?.Address})");
                var reason = cmd.ArgAsString("reason");
                Log?.Invoke($"收到 shutdown 指令(來源 {remote?.ToString() ?? "本機"}{(reason is null ? "" : $",原因 {reason}")}):結束練習並寫入紀錄 footer 後結束");
                return new HostCommandResult(
                    Ack(cmd, true, $"{(_engine.Ended ? "" : "結束練習、")}寫入紀錄 footer 後結束(≤ 1 s)").Reply,
                    AfterReply: () => _shutdown.Request("shutdown", endExercise: true));
            }
            default:
                return null;
        }
    }

    /// <summary>本機或私有/鏈路本地位址(船上筆電與教官筆電以網路線直連,規劃書第 5.4 節);未知來源(null)視為本機(例如測試)。</summary>
    public static bool IsAllowedShutdownClient(IPEndPoint? remote)
    {
        if (remote is null) return true;
        var ip = remote.Address;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }

    private HostCommandResult Ack(SimCommand cmd, bool ok, string detail)
        => new(new { type = "ack", command = ContractJson.EnumName(cmd.Type), ok, detail, tick = _engine.Tick });
}
