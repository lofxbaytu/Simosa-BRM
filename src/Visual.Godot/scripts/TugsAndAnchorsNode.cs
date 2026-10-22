using Godot;
using SimosaBRM.SimCore.Contracts;
using SimosaBRM.Visual.Shared.Scene;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.State;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 拖船(state.tugs[].pos 為示意位置)與纜線、錨鏈(state.anchors[].position)示意。
/// 拖船以小型程序化船體表示(28 × 9 m),艏向:push 朝向自船(force 方向)、pull 背向;纜線以 ImmediateMesh 畫自拖船艏至自船側。
/// 錨鏈:自錨鏈孔(particulars 無則取 0.44 LOA 前、±0.15 B)到錨位(海底,深度取 waterDepth)。每幀重建兩條線(頂點數極少)。
/// </summary>
public partial class TugsAndAnchorsNode : Node3D
{
    private readonly Dictionary<string, ShipNode> _tugs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _remove = new();
    private ImmediateMesh? _lines;
    private MeshInstance3D? _lineInstance;
    private readonly Vector3[] _anchorMarkPositions = new Vector3[2];
    private readonly MeshInstance3D[] _anchorMarks = new MeshInstance3D[2];

    public int TugCount => _tugs.Count;

    public override void _Ready()
    {
        _lines = new ImmediateMesh();
        _lineInstance = new MeshInstance3D { Mesh = _lines, Name = "lines", MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.85f, 0.6f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
        AddChild(_lineInstance);
        for (var i = 0; i < 2; i++)
        {
            _anchorMarks[i] = MeshFactory.Sphere(1.0f, MeshFactory.Flat(new Color(0.2f, 0.2f, 0.2f)), "anchor" + i);
            _anchorMarks[i].Visible = false;
            AddChild(_anchorMarks[i]);
        }
    }

    public void Update(VisualSample sample, HullParameters ownHull, bool lightsOn, double drawDistance)
    {
        var latest = sample.Latest;
        var own = sample.Own;
        _lines!.ClearSurfaces();
        var any = false;
        _lines.SurfaceBegin(Mesh.PrimitiveType.Lines);

        _seen.Clear();
        if (latest.Tugs is not null)
        {
            foreach (var tug in latest.Tugs)
            {
                if (tug.Pos is null || tug.Status is "unavailable") continue;
                _seen.Add(tug.Id);
                if (!_tugs.TryGetValue(tug.Id, out var node))
                {
                    node = new ShipNode { Name = "tug_" + tug.Id };
                    node.Build(HullParameters.ForTarget(tug.Id, tug.Name ?? tug.Id, "tug", 28, 9, 3.5), new Color(0.15f, 0.15f, 0.18f), "towing", withLowPoly: false);
                    AddChild(node);
                    _tugs[tug.Id] = node;
                }
                // 拖船艏向:推 → 面向自船(力方向 + 艏向);拉 → 背向;其他 → 與自船同向
                var heading = tug.Mode switch
                {
                    "push" => Units.NormalizeHeadingDeg(own.HeadingDeg + tug.DirectionDeg + 180.0),
                    "pull" => Units.NormalizeHeadingDeg(own.HeadingDeg + tug.DirectionDeg),
                    _ => own.HeadingDeg,
                };
                // 拖船位置相對自船是示意值(ENU):以 pos 的 x/y 為準
                node.SetPose(tug.Pos.X, tug.Pos.Y, heading);
                var dx = tug.Pos.X - own.East; var dy = tug.Pos.Y - own.North;
                node.ApplyLod(Math.Sqrt(dx * dx + dy * dy), double.MaxValue, drawDistance, lightsOn);
                if (tug.Status is "working" or "connecting" or "repositioning" && tug.LineParted != true)
                {
                    // 纜線:拖船艏 → 自船最近側(以自船中心往拖船方向、距離半船寬處)
                    var bow = SceneCoordinates.BodyToWorldOffset(14, 0, 2, heading);
                    var a = SceneCoordinates.FromEnu(tug.Pos.X, tug.Pos.Y, 0) + bow;
                    var len = Math.Max(1e-3, Math.Sqrt(dx * dx + dy * dy));
                    var b = SceneCoordinates.FromEnu(own.East + dx / len * ownHull.Beam * 0.5, own.North + dy / len * ownHull.Beam * 0.5, ownHull.Depth - ownHull.Draft);
                    _lines.SurfaceAddVertex(new Vector3((float)a.X, (float)a.Y, (float)a.Z));
                    _lines.SurfaceAddVertex(new Vector3((float)b.X, (float)b.Y, (float)b.Z));
                    any = true;
                }
            }
        }
        _remove.Clear();
        foreach (var id in _tugs.Keys) if (!_seen.Contains(id)) _remove.Add(id);
        foreach (var id in _remove) { _tugs[id].QueueFree(); _tugs.Remove(id); }

        // 錨鏈
        for (var i = 0; i < 2; i++) _anchorMarks[i].Visible = false;
        if (latest.Anchors is not null)
        {
            var depth = latest.WaterDepth ?? (latest.DepthBelowKeel + ownHull.Draft);
            var idx = 0;
            foreach (var an in latest.Anchors)
            {
                if (idx >= 2) break;
                if (an.Position is null || an.Status == "stowed") { idx++; continue; }
                var stbd = string.Equals(an.Side, "starboard", StringComparison.OrdinalIgnoreCase) ? ownHull.Beam * 0.15 : -ownHull.Beam * 0.15;
                var hawse = SceneCoordinates.FromEnu(own.East, own.North, 0) + SceneCoordinates.BodyToWorldOffset(ownHull.Loa * 0.44, stbd, ownHull.Depth - ownHull.Draft - 0.5, own.HeadingDeg);
                var anchor = SceneCoordinates.FromEnu(an.Position.X, an.Position.Y, -depth);
                _lines.SurfaceAddVertex(new Vector3((float)hawse.X, (float)hawse.Y, (float)hawse.Z));
                _lines.SurfaceAddVertex(new Vector3((float)anchor.X, (float)anchor.Y, (float)anchor.Z));
                any = true;
                _anchorMarks[idx].Position = new Vector3((float)anchor.X, (float)anchor.Y, (float)anchor.Z);
                _anchorMarks[idx].Visible = true;
                idx++;
            }
        }
        if (!any)
        {
            // ImmediateMesh 至少要有兩個頂點才能 SurfaceEnd;無線時放一條零長線
            _lines.SurfaceAddVertex(Vector3.Zero);
            _lines.SurfaceAddVertex(Vector3.Zero);
        }
        _lines.SurfaceEnd();
    }
}
