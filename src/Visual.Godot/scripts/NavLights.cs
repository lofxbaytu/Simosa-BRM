using Godot;
using SimosaBRM.Visual.Shared.Scene;
using SimosaBRM.Visual.Shared.Ship;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 航行燈(COLREG 第 21–30 條的簡化示意):依 state.targets[].lights 類別配置;夜間(EnvironmentMapper.NavigationLightsOn)才顯示。
/// powerDriven:桅燈(前低後高)、左紅右綠舷燈、艉燈;anchored:錨燈(白色環照,前桅);notUnderCommand:兩紅垂直;
/// restrictedManoeuvre:紅白紅;fishing:紅白(拖網綠白);pilot:白紅;tug/towing:兩白桅燈;sailing:舷燈 + 艉燈。
/// 以發光小球表示(不投光,效能優先);距離遠時由父節點整體隱藏。
/// </summary>
public partial class NavLights : Node3D
{
    private static readonly Color White = new(1f, 0.98f, 0.9f);
    private static readonly Color Red = new(1f, 0.1f, 0.1f);
    private static readonly Color Green = new(0.1f, 1f, 0.2f);

    private readonly List<MeshInstance3D> _lights = new();

    public void Configure(HullParameters hull, string category)
    {
        foreach (var l in _lights) l.QueueFree();
        _lights.Clear();
        var deck = hull.Depth - hull.Draft;
        var mastTop = hull.MastTopAboveBase - hull.Draft;
        var bridgeTop = hull.BridgeDeckHeight + 2.8 - hull.Draft;
        var mastX = (hull.SuperstructureAftX + hull.SuperstructureForeX) / 2;
        var foreMastX = hull.Loa / 2 - hull.ForecastleLength * 0.6;
        var cat = category.ToLowerInvariant();
        var radius = (float)Math.Clamp(hull.Loa * 0.004, 0.25, 0.8);

        void Add(double fwd, double stbd, double up, Color c)
        {
            var s = MeshFactory.Sphere(radius, MeshFactory.Emissive(c), "light");
            var p = SceneCoordinates.FromBody(fwd, stbd, up);
            s.Position = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            AddChild(s);
            _lights.Add(s);
        }
        void SideLights()
        {
            Add(hull.BridgeX, -hull.WingHalfWidth, bridgeTop, Red);
            Add(hull.BridgeX, hull.WingHalfWidth, bridgeTop, Green);
        }
        void SternLight() => Add(-hull.Loa / 2 + 1, 0, deck + 2, White);
        void MastheadLights()
        {
            Add(foreMastX, 0, deck + hull.ForecastleHeight + 6, White);
            if (hull.Loa >= 50) Add(mastX, 0, mastTop, White);
        }

        switch (cat)
        {
            case "anchored":
                Add(foreMastX, 0, deck + hull.ForecastleHeight + 6, White);
                if (hull.Loa >= 50) Add(-hull.Loa / 2 + 3, 0, deck + 3, White);
                break;
            case "notundercommand":
                Add(mastX, 0, mastTop, Red); Add(mastX, 0, mastTop - 2, Red);
                SideLights(); SternLight();
                break;
            case "restrictedmanoeuvre":
            case "restrictedinabilitytomanoeuvre":
                Add(mastX, 0, mastTop, Red); Add(mastX, 0, mastTop - 2, White); Add(mastX, 0, mastTop - 4, Red);
                MastheadLights(); SideLights(); SternLight();
                break;
            case "fishing":
                Add(mastX, 0, mastTop, Red); Add(mastX, 0, mastTop - 1.5, White);
                SideLights(); SternLight();
                break;
            case "trawling":
                Add(mastX, 0, mastTop, Green); Add(mastX, 0, mastTop - 1.5, White);
                SideLights(); SternLight();
                break;
            case "pilot":
                Add(mastX, 0, mastTop, White); Add(mastX, 0, mastTop - 1.5, Red);
                SideLights(); SternLight();
                break;
            case "towing":
            case "tug":
                Add(mastX, 0, mastTop, White); Add(mastX, 0, mastTop - 2, White);
                SideLights(); SternLight();
                break;
            case "sailing":
                SideLights(); SternLight();
                break;
            case "constrainedbydraught":
                Add(mastX, 0, mastTop, Red); Add(mastX, 0, mastTop - 2, Red); Add(mastX, 0, mastTop - 4, Red);
                MastheadLights(); SideLights(); SternLight();
                break;
            default: // powerDriven
                MastheadLights(); SideLights(); SternLight();
                break;
        }
    }
}
