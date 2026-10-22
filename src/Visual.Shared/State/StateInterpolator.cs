using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Scenario;

namespace SimosaBRM.Visual.Shared.State;

/// <summary>插值後的一個物體姿態(ENU 公尺、航向度)。</summary>
public readonly record struct Pose(double East, double North, double HeadingDeg, double SogKn, double CogDeg);

/// <summary>某一顯示幀要畫的全部內容:自船姿態、目標船姿態(含原始 TargetState)、最後一幀的其餘資料。</summary>
public sealed class VisualSample
{
    public required double SimTime { get; init; }
    public required Pose Own { get; init; }
    public required IReadOnlyList<(TargetState Target, Pose Pose)> Targets { get; init; }
    /// <summary>最新原始幀(拖船、錨、泊位、警報、環境等直接取用)</summary>
    public required OwnShipState Latest { get; init; }
    /// <summary>最後一次關鍵幀帶出的雷達環境(雨區;關鍵幀之間保留)</summary>
    public RadarEnvironmentState? RadarEnvironment { get; init; }
    /// <summary>true = 顯示時間超出最新幀且已達外推上限(連線停滯或凍結)</summary>
    public required bool Stalled { get; init; }
    public required bool Extrapolating { get; init; }
}

/// <summary>
/// 把 25 Hz 的狀態幀平滑到顯示幀率(規劃書第 5.3 節)。
/// 作法:保留最近 N 幀(模擬時間 t 與到達牆鐘),估計模擬時間對牆鐘的速率(timescale、凍結時為 0),
/// 顯示時間 = 最新幀 t + rate·(now − 到達) − 緩衝(rate·DelaySeconds);落在兩幀之間時線性插值(航向走最短弧),
/// 超過最新幀時以 COG/SOG/ROT 外推,最多 MaxExtrapolationSeconds,之後凍結(Stalled)。
/// 模擬時間倒退(重設/倒帶/重播)或跳躍超過 JumpThresholdSeconds 時清空歷史重新開始。
/// 速率估計(審查發現:幀成批到達時最舊/最新兩幀的 Δt/Δ牆鐘會膨脹 2–3 倍,顯示時間倒退並卡在最舊幀):
/// 同一牆鐘到達的幀(間隔 ≤ DegenerateIntervalSeconds:啟動第一幀、網路停頓後 TCP 一次送達、GC 卡頓)不含速率資訊,
/// 有成批幀時以「有效到達間隔的中位數 × 間隔數」作 Δ牆鐘下限(偏向低估:低估只造成幀間微小停頓,高估才會倒退),
/// 有效間隔不到一半時沿用前一次估計;沒有成批幀時直接用最舊/最新兩幀(抖動在緩衝內平均掉)。顯示時間另外保證單調不減(估計暫時偏高時暫停而非倒退;Reset 時清除)。
/// 目標船以 id 配對插值;只在單側出現者取最新幀。雷達環境只在關鍵幀帶出,故另外保留並於時間倒退時清除。
/// 無每幀配置(除回傳的 VisualSample 與目標清單;清單容量重用)。
/// </summary>
public sealed class StateInterpolator
{
    private sealed record Frame(OwnShipState State, double Wall);

    private readonly Frame?[] _ring;
    private readonly double[] _intervals; // 有效到達間隔(排序用;容量 capacity − 1,無每幀配置)
    private int _count;
    private int _head; // 下一個寫入位置
    private double _lastSimT = double.NegativeInfinity; // 上次 Sample 回傳的顯示時間(單調保證)
    private RadarEnvironmentState? _radar;
    private readonly List<(TargetState, Pose)> _targetBuffer = new();

    public double DelaySeconds { get; init; } = 0.08;
    public double MaxExtrapolationSeconds { get; init; } = 0.25;
    public double JumpThresholdSeconds { get; init; } = 5.0;
    /// <summary>到達間隔 ≤ 此值視為成批到達(同一牆鐘),不用於速率估計</summary>
    public double DegenerateIntervalSeconds { get; init; } = 0.002;
    public int Capacity => _ring.Length;
    public int Count => _count;
    public OwnShipState? Latest => _count == 0 ? null : _ring[(_head - 1 + _ring.Length) % _ring.Length]!.State;
    /// <summary>目前估計的模擬時間對牆鐘速率(1 = 即時、0 = 凍結、10 = timescale 10)。</summary>
    public double Rate { get; private set; } = 1.0;
    public int Resets { get; private set; }

    public StateInterpolator(int capacity = 8)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new Frame?[capacity];
        _intervals = new double[capacity - 1];
    }

    /// <summary>收到一幀(wall 為到達時的牆鐘秒,任意基準)。</summary>
    public void Push(OwnShipState state, double wall)
    {
        var latest = Latest;
        if (latest is not null)
        {
            var dt = state.T - latest.T;
            if (dt < -1e-6 || dt > JumpThresholdSeconds || !string.Equals(state.ShipId, latest.ShipId, StringComparison.Ordinal))
            {
                Reset();
                if (dt < -1e-6) _radar = null; // 時間倒退:雷達環境待下一個關鍵幀
            }
        }
        if (state.RadarEnvironment is not null) _radar = state.RadarEnvironment;
        _ring[_head] = new Frame(state, wall);
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
        UpdateRate();
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _count = 0;
        _head = 0;
        Rate = 1.0;
        _lastSimT = double.NegativeInfinity;
        Resets++;
    }

    private Frame FrameAt(int i) => _ring[(_head - _count + i + _ring.Length) % _ring.Length]!; // i = 0 最舊

    private void UpdateRate()
    {
        if (_count < 2) { Rate = 1.0; return; }
        var oldest = FrameAt(0);
        var newest = FrameAt(_count - 1);
        var dT = newest.State.T - oldest.State.T;
        if (dT <= 1e-9) { Rate = 0.0; return; } // 凍結:模擬時間不動,與牆鐘無關
        var n = _count - 1;
        var valid = 0;
        for (var i = 0; i < n; i++)
        {
            var d = FrameAt(i + 1).Wall - FrameAt(i).Wall;
            if (d > DegenerateIntervalSeconds) _intervals[valid++] = d;
        }
        double dWall;
        if (valid == n)
        {
            dWall = newest.Wall - oldest.Wall; // 無成批幀:到達抖動在整個緩衝內平均掉
        }
        else
        {
            if (valid * 2 < n) return; // 多數幀成批到達:牆鐘無資訊,沿用前值
            Array.Sort(_intervals, 0, valid);
            dWall = Math.Max(newest.Wall - oldest.Wall, n * _intervals[valid / 2]);
        }
        if (dWall <= 1e-6) return;
        Rate = Math.Clamp(dT / dWall, 0.0, 100.0);
    }

    /// <summary>取某牆鐘時刻要顯示的內容;尚無幀時回傳 null。</summary>
    public VisualSample? Sample(double wall)
    {
        if (_count == 0) return null;
        var newest = FrameAt(_count - 1);
        var simT = newest.State.T + Rate * (wall - newest.Wall - DelaySeconds);
        if (simT < _lastSimT) simT = _lastSimT; // 顯示時間單調:成批到達/速率暫估偏高時暫停而非倒退
        _targetBuffer.Clear();

        // 介於兩幀之間:找包住 simT 的幀對
        for (var i = _count - 2; i >= 0; i--)
        {
            var a = FrameAt(i);
            var b = FrameAt(i + 1);
            if (simT >= a.State.T && simT <= b.State.T)
            {
                var span = b.State.T - a.State.T;
                var f = span <= 1e-9 ? 1.0 : (simT - a.State.T) / span;
                return Build(simT, Lerp(a.State, b.State, f), LerpTargets(a.State, b.State, f), newest.State, false, false);
            }
        }

        if (simT <= FrameAt(0).State.T)
        {
            var oldest = FrameAt(0).State;
            return Build(oldest.T, PoseOf(oldest), TargetsOf(oldest), newest.State, false, false);
        }

        // 超出最新幀:外推(上限 MaxExtrapolationSeconds)
        var ahead = simT - newest.State.T;
        var stalled = ahead > MaxExtrapolationSeconds;
        var dt = Math.Min(ahead, MaxExtrapolationSeconds);
        if (Rate <= 1e-9) { dt = 0; stalled = false; } // 凍結:停在最新幀
        var own = Extrapolate(PoseOf(newest.State), newest.State.Rot, dt);
        foreach (var t in newest.State.Targets ?? Array.Empty<TargetState>())
            _targetBuffer.Add((t, Extrapolate(PoseOf(t), t.Rot, dt)));
        return Build(newest.State.T + dt, own, _targetBuffer, newest.State, dt > 0, stalled);
    }

    private VisualSample Build(double simT, Pose own, IReadOnlyList<(TargetState, Pose)> targets, OwnShipState latest, bool extrapolating, bool stalled)
    {
        _lastSimT = simT;
        return new()
        {
            SimTime = simT,
            Own = own,
            Targets = targets,
            Latest = latest,
            RadarEnvironment = _radar,
            Extrapolating = extrapolating,
            Stalled = stalled,
        };
    }

    private IReadOnlyList<(TargetState, Pose)> TargetsOf(OwnShipState s)
    {
        foreach (var t in s.Targets ?? Array.Empty<TargetState>()) _targetBuffer.Add((t, PoseOf(t)));
        return _targetBuffer;
    }

    private IReadOnlyList<(TargetState, Pose)> LerpTargets(OwnShipState a, OwnShipState b, double f)
    {
        var bt = b.Targets ?? Array.Empty<TargetState>();
        var at = a.Targets ?? Array.Empty<TargetState>();
        foreach (var tb in bt)
        {
            TargetState? ta = null;
            foreach (var x in at) if (string.Equals(x.Id, tb.Id, StringComparison.Ordinal)) { ta = x; break; }
            _targetBuffer.Add((tb, ta is null ? PoseOf(tb) : Lerp(PoseOf(ta), PoseOf(tb), f)));
        }
        return _targetBuffer;
    }

    public static Pose PoseOf(OwnShipState s) => new(s.Pos.X, s.Pos.Y, s.Heading, s.Sog, s.Cog);
    public static Pose PoseOf(TargetState t) => new(t.Pos.X, t.Pos.Y, t.Heading, t.Sog, t.Cog);

    public static Pose Lerp(OwnShipState a, OwnShipState b, double f) => Lerp(PoseOf(a), PoseOf(b), f);

    public static Pose Lerp(Pose a, Pose b, double f)
    {
        f = Math.Clamp(f, 0.0, 1.0);
        return new Pose(
            a.East + (b.East - a.East) * f,
            a.North + (b.North - a.North) * f,
            LerpHeading(a.HeadingDeg, b.HeadingDeg, f),
            a.SogKn + (b.SogKn - a.SogKn) * f,
            LerpHeading(a.CogDeg, b.CogDeg, f));
    }

    /// <summary>航向插值走最短弧(359 → 1 不會轉一整圈)。</summary>
    public static double LerpHeading(double a, double b, double f)
        => Units.NormalizeHeadingDeg(a + Units.WrapDeg180(b - a) * f);

    /// <summary>以 COG/SOG 與 ROT(度/分)外推 dt 秒。</summary>
    public static Pose Extrapolate(Pose p, double rotDegPerMin, double dt)
    {
        if (dt <= 0) return p;
        var v = Units.KnToMps(p.SogKn);
        var c = Units.DegToRad(p.CogDeg);
        return p with
        {
            East = p.East + v * Math.Sin(c) * dt,
            North = p.North + v * Math.Cos(c) * dt,
            HeadingDeg = Units.NormalizeHeadingDeg(p.HeadingDeg + rotDegPerMin / 60.0 * dt),
        };
    }
}
