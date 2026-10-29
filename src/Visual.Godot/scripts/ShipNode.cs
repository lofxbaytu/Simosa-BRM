using Godot;
using SimosaBRM.Visual.Shared.Scene;
using SimosaBRM.Visual.Shared.Ship;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 一艘船(自船或目標船)的節點:程序化船體(完整與低面數兩套,依距離切換)、航行燈。
/// 位置由 Pose(ENU)設定,節點前方 −z 為艏,yaw = −heading(SceneCoordinates)。無每幀配置。
/// </summary>
public partial class ShipNode : Node3D
{
    private Node3D? _full;
    private Node3D? _low;
    private NavLights? _lights;
    private bool _lightsOn;

    public HullParameters? Hull { get; private set; }
    public string LightCategory { get; private set; } = "powerDriven";
    public int Triangles { get; private set; }

    public void Build(HullParameters hull, Color hullColor, string lightCategory, bool withLowPoly)
    {
        Hull = hull;
        LightCategory = lightCategory;
        foreach (var c in GetChildren()) c.QueueFree();
        var hullMat = MeshFactory.Flat(hullColor, 0.7f);
        var deckMat = MeshFactory.Flat(new Color(0.45f, 0.42f, 0.38f), 0.9f);
        var superMat = MeshFactory.Flat(new Color(0.92f, 0.92f, 0.88f), 0.85f);
        var mastMat = MeshFactory.Flat(new Color(0.8f, 0.6f, 0.2f), 0.6f);

        var full = ProceduralHullBuilder.Build(hull);
        _full = new Node3D { Name = "full" };
        _full.AddChild(MeshFactory.Instance(full.Hull, hullMat, "hull"));
        _full.AddChild(MeshFactory.Instance(full.Deck, deckMat, "deck"));
        _full.AddChild(MeshFactory.Instance(full.Superstructure, superMat, "superstructure"));
        _full.AddChild(MeshFactory.Instance(full.Forecastle, superMat, "forecastle"));
        _full.AddChild(MeshFactory.Instance(full.Mast, mastMat, "mast"));
        AddChild(_full);
        Triangles = full.TotalTriangles;

        if (withLowPoly)
        {
            var low = ProceduralHullBuilder.Build(hull, lowPoly: true);
            _low = new Node3D { Name = "low", Visible = false };
            _low.AddChild(MeshFactory.Instance(low.Hull, hullMat, "hull"));
            _low.AddChild(MeshFactory.Instance(low.Superstructure, superMat, "superstructure"));
            AddChild(_low);
        }

        _lights = new NavLights { Name = "lights", Visible = false };
        _lights.Configure(hull, lightCategory);
        AddChild(_lights);
    }

    public void SetLightCategory(string category)
    {
        if (string.Equals(category, LightCategory, StringComparison.OrdinalIgnoreCase) || Hull is null || _lights is null) return;
        LightCategory = category;
        _lights.Configure(Hull, category);
    }

    /// <summary>位置與艏向;rollDeg 為橫搖(度,右傾正;繞艏艉軸、以水線中心線為軸,右舷下沉),目標船與無 state.roll 時為 0(姿態與原本相同)。</summary>
    public void SetPose(double east, double north, double headingDeg, double rollDeg = 0.0)
    {
        Position = new Vector3((float)east, 0f, (float)-north);
        if (rollDeg == 0.0) Rotation = new Vector3(0f, (float)SceneCoordinates.HeadingToYawRad(headingDeg), 0f);
        else Basis = new Basis(Vector3.Up, (float)SceneCoordinates.HeadingToYawRad(headingDeg)) * new Basis(Vector3.Back, -Mathf.DegToRad((float)rollDeg));
    }

    /// <summary>依距離切換 LOD 與可見;回傳是否可見。</summary>
    public bool ApplyLod(double distanceM, double lodDistance, double drawDistance, bool lightsOn)
    {
        var visible = distanceM <= drawDistance;
        if (Visible != visible) Visible = visible;
        if (!visible) return false;
        var useLow = _low is not null && distanceM > lodDistance;
        if (_full is not null && _full.Visible == useLow) _full.Visible = !useLow;
        if (_low is not null && _low.Visible != useLow) _low.Visible = useLow;
        if (_lights is not null && _lightsOn != lightsOn)
        {
            _lightsOn = lightsOn;
            _lights.Visible = lightsOn;
        }
        return true;
    }
}
