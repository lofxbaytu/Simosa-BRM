using Godot;
using SimosaBRM.Visual.Shared.Area;
using SimosaBRM.Visual.Shared.Scene;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.State;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 繫纜(state.berth.lines[];docs/design/visual-client.md 第 4 節):兩端與顏色由 Visual.Shared 的 <see cref="MooringLinePresentation"/> 決定
/// (導纜孔估計、繫船柱來自區域檔泊位、張力比 &lt; 50 % 本色 / 50–80 % 黃 / &gt; 80 % 紅),本節點只以 ImmediateMesh(頂點色)每幀重畫,
/// 斷纜不畫線、在導纜孔處以紅球閃爍 3 s(以第一次看到 parted 的時間起算;纜線再度受力或消失即清除)。
/// </summary>
public partial class MooringLinesNode : Node3D
{
    private ImmediateMesh? _mesh;
    private MeshInstance3D? _instance;
    private readonly Dictionary<string, double> _partedSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MeshInstance3D> _flashMarks = new(StringComparer.Ordinal);
    private readonly List<string> _remove = new();

    /// <summary>本幀畫出的纜線數</summary>
    public int DrawnCount { get; private set; }
    /// <summary>狀態中受力的纜線數(fast/heaving/slacking)</summary>
    public int InStateCount { get; private set; }

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        _instance = new MeshInstance3D
        {
            Mesh = _mesh,
            Name = "mooringLines",
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };
        AddChild(_instance);
    }

    public void Update(VisualSample sample, HullParameters ownHull, IReadOnlyList<BerthGeometry> berths, double nowSeconds)
    {
        var berth = sample.Latest.Berth;
        var visuals = MooringLinePresentation.Build(berth, sample.Own, ownHull, berths);
        InStateCount = 0;
        if (berth is not null) foreach (var l in berth.Lines) if (MooringLinePresentation.IsLoaded(l.Status)) InStateCount++;

        _mesh!.ClearSurfaces();
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        var drawn = 0;
        var partedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in visuals)
        {
            if (v.Parted)
            {
                partedIds.Add(v.Id);
                if (!_partedSince.ContainsKey(v.Id)) _partedSince[v.Id] = nowSeconds;
                var visible = MooringLinePresentation.PartedFlashVisible(nowSeconds - _partedSince[v.Id]);
                if (!_flashMarks.TryGetValue(v.Id, out var mark))
                {
                    mark = MeshFactory.Sphere(0.6f, MeshFactory.Emissive(new Color(1f, 0.15f, 0.1f), 6f), "parted_" + v.Id);
                    AddChild(mark);
                    _flashMarks[v.Id] = mark;
                }
                var p = SceneCoordinates.FromEnu(v.Fairlead.East, v.Fairlead.North, v.Fairlead.Up);
                mark.Position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
                mark.Visible = visible;
                continue;
            }
            if (!v.Drawn || v.Bollard is not { } b) continue;
            var (r, g, bl) = MooringLinePresentation.Colour(v.Level);
            var color = new Color((float)r, (float)g, (float)bl);
            var a = SceneCoordinates.FromEnu(v.Fairlead.East, v.Fairlead.North, v.Fairlead.Up);
            var c = SceneCoordinates.FromEnu(b.East, b.North, b.Up);
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(new Vector3((float)a.X, (float)a.Y, (float)a.Z));
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(new Vector3((float)c.X, (float)c.Y, (float)c.Z));
            drawn++;
        }
        if (drawn == 0)
        {
            // ImmediateMesh 至少要有兩個頂點才能 SurfaceEnd;無線時放一條零長線
            _mesh.SurfaceSetColor(Colors.White);
            _mesh.SurfaceAddVertex(Vector3.Zero);
            _mesh.SurfaceSetColor(Colors.White);
            _mesh.SurfaceAddVertex(Vector3.Zero);
        }
        _mesh.SurfaceEnd();
        DrawnCount = drawn;

        // 不再是 parted 的纜線:清除閃爍計時與標記(再度受力或 letGo/消失)
        _remove.Clear();
        foreach (var id in _partedSince.Keys) if (!partedIds.Contains(id)) _remove.Add(id);
        foreach (var id in _remove)
        {
            _partedSince.Remove(id);
            if (_flashMarks.Remove(id, out var mark)) mark.QueueFree();
        }
    }
}
