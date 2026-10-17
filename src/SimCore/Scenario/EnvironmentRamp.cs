using SimosaBRM.SimCore.Contracts;
using SimosaBRM.SimCore.Environment;

namespace SimosaBRM.SimCore.Scenario;

/// <summary>環境漸變的通道(每個通道最多一個進行中的漸變;情境引擎設計第 3.5 節)。</summary>
public enum RampChannel
{
    WindSpeed,
    WindDir,
    Gustiness,
    CurrentSet,
    CurrentDrift,
    WaterDepth,
    Visibility,
}

/// <summary>單一通道的線性漸變(角度通道走最短弧);From/To 為內部單位(m/s、弧度、m、nm)。</summary>
public sealed record EnvironmentRamp(RampChannel Channel, long StartTick, long EndTick, double From, double To)
{
    public bool IsAngle => Channel is RampChannel.WindDir or RampChannel.CurrentSet;

    /// <summary>在 tick 的插值(超過 EndTick 為 To)。</summary>
    public double ValueAt(long tick)
    {
        var span = EndTick - StartTick;
        var f = span <= 0 ? 1.0 : Units.Clamp((double)(tick - StartTick) / span, 0.0, 1.0);
        if (!IsAngle) return From + (To - From) * f;
        var d = Units.WrapRadPi(To - From);
        return Units.NormalizeHeadingRad(From + d * f);
    }
}

/// <summary>進行中的漸變集合;每步套用到環境,完成者移除並回報。</summary>
public sealed class RampSet
{
    private readonly List<EnvironmentRamp> _ramps = new();

    public IReadOnlyList<EnvironmentRamp> Ramps => _ramps;
    public int Count => _ramps.Count;

    public void Clear() => _ramps.Clear();

    /// <summary>新增/覆蓋通道的漸變(From 為目前值)。</summary>
    public void Set(RampChannel channel, long startTick, long endTick, double from, double to)
    {
        _ramps.RemoveAll(r => r.Channel == channel);
        _ramps.Add(new EnvironmentRamp(channel, startTick, endTick, from, to));
        _ramps.Sort((a, b) => a.Channel.CompareTo(b.Channel));
    }

    public void Remove(RampChannel channel) => _ramps.RemoveAll(r => r.Channel == channel);

    public EnvironmentRamp? Find(RampChannel channel) => _ramps.FirstOrDefault(r => r.Channel == channel);

    public void Restore(IEnumerable<EnvironmentRamp> ramps)
    {
        _ramps.Clear();
        _ramps.AddRange(ramps);
        _ramps.Sort((a, b) => a.Channel.CompareTo(b.Channel));
    }

    /// <summary>套用目前 tick 的插值;回傳本步完成的漸變(已移除)。</summary>
    public List<EnvironmentRamp>? Apply(long tick, EnvironmentConditions env)
    {
        if (_ramps.Count == 0) return null;
        List<EnvironmentRamp>? done = null;
        for (var i = _ramps.Count - 1; i >= 0; i--)
        {
            var r = _ramps[i];
            var v = r.ValueAt(tick);
            Write(env, r.Channel, v);
            if (tick >= r.EndTick)
            {
                _ramps.RemoveAt(i);
                (done ??= new()).Add(r);
            }
        }
        done?.Reverse();
        return done;
    }

    public static double Read(EnvironmentConditions env, RampChannel ch) => ch switch
    {
        RampChannel.WindSpeed => env.WindTrueSpeedMps,
        RampChannel.WindDir => env.WindTrueDirFromRad,
        RampChannel.Gustiness => env.Gustiness,
        RampChannel.CurrentSet => env.CurrentSetRad,
        RampChannel.CurrentDrift => env.CurrentDriftMps,
        RampChannel.WaterDepth => env.Depth is ZoneDepth zd ? zd.Base.Depth : env.Depth is ConstantDepth cd ? cd.Depth : env.Depth.DepthAt(0, 0),
        RampChannel.Visibility => env.VisibilityNm ?? 0.0,
        _ => 0.0,
    };

    public static void Write(EnvironmentConditions env, RampChannel ch, double v)
    {
        switch (ch)
        {
            case RampChannel.WindSpeed: env.WindTrueSpeedMps = v; break;
            case RampChannel.WindDir: env.WindTrueDirFromRad = v; break;
            case RampChannel.Gustiness: env.Gustiness = v; break;
            case RampChannel.CurrentSet: env.CurrentSetRad = v; break;
            case RampChannel.CurrentDrift: env.CurrentDriftMps = v; break;
            case RampChannel.WaterDepth:
                if (env.Depth is ZoneDepth zd) zd.Base.Depth = v;
                else if (env.Depth is ConstantDepth cd) cd.Depth = v;
                else env.Depth = new ConstantDepth(v);
                break;
            case RampChannel.Visibility: env.VisibilityNm = v; break;
        }
    }

    /// <summary>通道的對外單位值(kn、度、m、nm;事件 data 用)。</summary>
    public static double External(RampChannel ch, double v) => ch switch
    {
        RampChannel.WindSpeed or RampChannel.CurrentDrift => Units.MpsToKn(v),
        RampChannel.WindDir or RampChannel.CurrentSet => Units.NormalizeHeadingDeg(Units.RadToDeg(v)),
        _ => v,
    };
}
