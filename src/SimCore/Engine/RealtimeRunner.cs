using System.Diagnostics;

namespace SimosaBRM.SimCore.Engine;

/// <summary>
/// 以牆鐘驅動引擎(規劃書第 5.3 節):每個週期(預設 20 ms)依時間倍率推進應到的步數;
/// 凍結時只處理指令。落後太多(例如 ×10 時物理太慢)時丟棄多餘步數並計數,而不是無限追趕。
/// </summary>
public sealed class RealtimeRunner
{
    private readonly SimulationEngine _engine;

    public TimeSpan Period { get; }
    /// <summary>單一週期最多推進的步數</summary>
    public int MaxStepsPerPeriod { get; init; } = 100;
    /// <summary>因落後而丟棄的步數</summary>
    public long DroppedSteps { get; private set; }

    public RealtimeRunner(SimulationEngine engine, TimeSpan? period = null)
    {
        _engine = engine;
        Period = period ?? TimeSpan.FromMilliseconds(20);
    }

    /// <summary>阻塞執行直到取消。</summary>
    public void Run(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        var next = sw.Elapsed + Period;
        double accumulator = 0.0;

        while (!ct.IsCancellationRequested)
        {
            var now = sw.Elapsed;
            var wall = (now - last).TotalSeconds;
            last = now;

            if (_engine.Frozen)
            {
                accumulator = 0.0;
                _engine.ProcessPendingCommands();
            }
            else
            {
                accumulator += wall * _engine.TimeScale;
                var steps = (int)Math.Floor(accumulator / _engine.Dt);
                if (steps > MaxStepsPerPeriod)
                {
                    DroppedSteps += steps - MaxStepsPerPeriod;
                    steps = MaxStepsPerPeriod;
                    accumulator = 0.0;
                }
                else accumulator -= steps * _engine.Dt;

                if (steps == 0) _engine.ProcessPendingCommands();
                for (var i = 0; i < steps && !ct.IsCancellationRequested; i++)
                {
                    _engine.Step();
                    if (_engine.Frozen) { accumulator = 0.0; break; }
                }
            }

            // 等到下一個週期(先 Sleep 再短暫自旋,兼顧精度與 CPU)
            next += Period;
            var remaining = next - sw.Elapsed;
            if (remaining < TimeSpan.Zero) { next = sw.Elapsed; continue; }
            if (remaining > TimeSpan.FromMilliseconds(2)) Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));
            while (sw.Elapsed < next && !ct.IsCancellationRequested) Thread.SpinWait(50);
        }
    }
}
