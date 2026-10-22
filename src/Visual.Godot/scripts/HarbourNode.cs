using Godot;
using SimosaBRM.Visual.Shared.Area;
using SimosaBRM.Visual.Shared.Scene;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 港口(示意、低面數;規劃書第 7.3 節):區域資料的岸線/防波堤/碼頭以擠出幾何,碰墊與繫船柱為小方塊,導燈為發光球,
/// 淺灘為水下淡色面(鳥瞰可見),航道等水域多邊形只在鳥瞰視角畫出細線。幾何建立一次,不在每幀改動。
/// </summary>
public partial class HarbourNode : Node3D
{
    private readonly List<MeshInstance3D> _zoneLines = new();
    private readonly List<MeshInstance3D> _leadingLights = new();

    public int MeshCount { get; private set; }
    public int Triangles { get; private set; }

    public void Build(IEnumerable<AreaGeometry> areas)
    {
        var landMat = MeshFactory.Flat(new Color(0.36f, 0.40f, 0.26f), 0.95f);
        var hillMat = MeshFactory.Flat(new Color(0.30f, 0.36f, 0.24f), 0.95f);
        var breakwaterMat = MeshFactory.Flat(new Color(0.55f, 0.55f, 0.52f), 0.9f);
        var quayMat = MeshFactory.Flat(new Color(0.62f, 0.60f, 0.56f), 0.9f);
        var shoalMat = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.65f, 0.4f, 0.35f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 1f };
        var fenderMat = MeshFactory.Flat(new Color(0.08f, 0.08f, 0.08f), 0.9f);
        var bollardMat = MeshFactory.Flat(new Color(0.3f, 0.3f, 0.32f), 0.5f);
        var lineMat = new StandardMaterial3D { AlbedoColor = new Color(1f, 1f, 0.3f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };

        foreach (var area in areas)
        {
            var root = new Node3D { Name = "area_" + area.Id };
            AddChild(root);
            foreach (var land in area.Land) Add(root, HarbourMeshBuilder.Extrude(land), land.HeightM > 50 ? hillMat : landMat);
            foreach (var bw in area.Breakwaters) Add(root, HarbourMeshBuilder.Extrude(bw), breakwaterMat);
            foreach (var sh in area.Shoals) Add(root, HarbourMeshBuilder.Extrude(sh, baseY: sh.HeightM - 1.0), shoalMat);
            foreach (var b in area.Berths)
            {
                Add(root, HarbourMeshBuilder.Quay(b), quayMat);
                foreach (var (id, pos) in b.Fenders) Add(root, HarbourMeshBuilder.Box("fender_" + id, pos, -1.0, 1.0, 3.5, 2.5), fenderMat);
                foreach (var (id, pos) in b.Bollards) Add(root, HarbourMeshBuilder.Box("bollard_" + id, pos, AreaLoader.DefaultQuayHeightM, 0.6, 0.8, 0.6), bollardMat);
            }
            foreach (var zone in area.WaterZones)
            {
                var line = ZoneLine(zone, lineMat);
                line.Visible = false;
                root.AddChild(line);
                _zoneLines.Add(line);
            }
            if (area.LeadingLights is { } ll)
            {
                AddLight(root, ll.Front, 8.0, breakwaterMat);
                AddLight(root, ll.Rear, 16.0, breakwaterMat);
            }
            foreach (var m in area.Markers)
            {
                if (m.Kind == "breakwaterHead") AddLight(root, m.Pos, 7.0, breakwaterMat, new Color(1f, 0.2f, 0.2f));
            }
        }
    }

    private void Add(Node3D root, MeshData data, Material mat)
    {
        root.AddChild(MeshFactory.Instance(data, mat, data.Name));
        MeshCount++;
        Triangles += data.TriangleCount;
    }

    private void AddLight(Node3D root, EnuPoint pos, double height, Material poleMat, Color? color = null)
    {
        Add(root, HarbourMeshBuilder.Box("pole", pos, 0.0, 1.0, height, 1.0), poleMat);
        var lamp = MeshFactory.Sphere(0.8f, MeshFactory.Emissive(color ?? new Color(1f, 1f, 1f), 6f), "lamp");
        var p = SceneCoordinates.FromEnu(pos.East, pos.North, height + 0.5);
        lamp.Position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
        root.AddChild(lamp);
        _leadingLights.Add(lamp);
    }

    private static MeshInstance3D ZoneLine(ExtrudedPolygon zone, Material mat)
    {
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        foreach (var pt in zone.Points.Append(zone.Points[0]))
        {
            var p = SceneCoordinates.FromEnu(pt.East, pt.North, 0.3);
            im.SurfaceAddVertex(new Vector3((float)p.X, (float)p.Y, (float)p.Z));
        }
        im.SurfaceEnd();
        return new MeshInstance3D { Mesh = im, MaterialOverride = mat, Name = "zone_" + zone.Kind };
    }

    /// <summary>鳥瞰視角才顯示水域示意線。</summary>
    public void SetOverviewMode(bool overview)
    {
        foreach (var l in _zoneLines) if (l.Visible != overview) l.Visible = overview;
    }
}
