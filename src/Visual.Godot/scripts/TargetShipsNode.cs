using Godot;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.Visual.Shared.Quality;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.State;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 目標船集合:以 id 配對節點(重用,不每幀配置);AIS 關閉者仍畫(視景是「眼睛看到的」);依 LOA/B 用同一程序化模型;
/// 依品質等級的繪製距離與 LOD 距離切換;燈號類別改變時重建燈。
/// </summary>
public partial class TargetShipsNode : Node3D
{
    private readonly Dictionary<string, ShipNode> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _toRemove = new();

    public int DrawnCount { get; private set; }
    public int NodeCount => _nodes.Count;

    public void Update(VisualSample sample, QualitySettings quality, bool lightsOn)
    {
        _seen.Clear();
        DrawnCount = 0;
        var own = sample.Own;
        foreach (var (t, pose) in sample.Targets)
        {
            _seen.Add(t.Id);
            if (!_nodes.TryGetValue(t.Id, out var node))
            {
                node = new ShipNode { Name = "target_" + t.Id };
                node.Build(HullParameters.ForTarget(t.Id, t.Name, t.ShipType, t.Loa, t.Beam, t.Draft), ColorFor(t.ShipType), t.Lights, withLowPoly: true);
                AddChild(node);
                _nodes[t.Id] = node;
            }
            node.SetLightCategory(t.Lights);
            node.SetPose(pose.East, pose.North, pose.HeadingDeg);
            var dx = pose.East - own.East;
            var dy = pose.North - own.North;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (node.ApplyLod(dist, quality.TargetLodDistanceM, quality.DrawDistanceM, lightsOn)) DrawnCount++;
        }
        // 情境重設後消失的目標:移除節點
        _toRemove.Clear();
        foreach (var id in _nodes.Keys) if (!_seen.Contains(id)) _toRemove.Add(id);
        foreach (var id in _toRemove)
        {
            _nodes[id].QueueFree();
            _nodes.Remove(id);
        }
    }

    private static Color ColorFor(string shipType) => shipType.ToLowerInvariant() switch
    {
        "tanker" => new Color(0.55f, 0.1f, 0.1f),
        "cargo" or "container" => new Color(0.1f, 0.2f, 0.45f),
        "fishing" => new Color(0.1f, 0.4f, 0.45f),
        "pilot" => new Color(0.9f, 0.5f, 0.1f),
        "tug" => new Color(0.2f, 0.2f, 0.2f),
        _ => new Color(0.3f, 0.3f, 0.35f),
    };
}
