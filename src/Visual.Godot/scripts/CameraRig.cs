using Godot;
using SimosaBRM.Visual.Shared.Scene;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.State;
using SimosaBRM.Visual.Shared.View;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 視角:鍵盤 1–5 切換(駕駛台前視/左翼橋/右翼橋/艉視/鳥瞰),滑鼠左鍵拖曳環視(yaw/pitch 偏移)、滾輪縮放視野角、R 重設;
/// 眼位依 ViewDefinition(眼高取 particulars bridge 資料或估計)跟隨自船插值姿態;鳥瞰固定北向上。
/// </summary>
public partial class CameraRig : Node3D
{
    private Camera3D _camera = null!;
    private HullParameters? _hull;
    private double _mouseYaw;
    private double _mousePitch;
    private double _fovOffset;
    private bool _dragging;

    public ViewDefinition? View { get; private set; }
    public ViewKind Kind => View?.Kind ?? ViewKind.Bridge;
    public Camera3D Camera => _camera;
    public double FovDeg => Math.Clamp((View?.FovDeg ?? 60) + _fovOffset, 20, 110);

    public override void _Ready()
    {
        _camera = new Camera3D { Name = "camera", Near = 0.5f, Far = 7000f, Current = true };
        AddChild(_camera);
    }

    public void Configure(HullParameters hull, ViewKind kind, double? fovOverride = null)
    {
        _hull = hull;
        View = ViewDefinition.For(kind, hull);
        _mouseYaw = 0; _mousePitch = 0;
        _fovOffset = fovOverride is { } f ? f - View.FovDeg : 0;
    }

    public void SetView(ViewKind kind)
    {
        if (_hull is not null) Configure(_hull, kind, FovDeg);
    }

    public void SetDrawDistance(double metres) => _camera.Far = (float)Math.Max(1000, metres * 1.1);

    public void Update(Pose own)
    {
        if (View is null) return;
        var v = View;
        if (v.FixedNorthUp)
        {
            Position = new Vector3((float)own.East, (float)v.EyeUp, (float)-own.North);
            Rotation = new Vector3(Mathf.DegToRad((float)(v.PitchDeg + _mousePitch)), Mathf.DegToRad((float)-_mouseYaw), 0f);
        }
        else
        {
            var eye = SceneCoordinates.FromEnu(own.East, own.North, 0) + SceneCoordinates.BodyToWorldOffset(v.EyeForward, v.EyeStarboard, v.EyeUp, own.HeadingDeg);
            Position = new Vector3((float)eye.X, (float)eye.Y, (float)eye.Z);
            var yaw = SceneCoordinates.HeadingToYawRad(own.HeadingDeg + v.YawDeg + _mouseYaw);
            Rotation = new Vector3(Mathf.DegToRad((float)(v.PitchDeg + _mousePitch)), (float)yaw, 0f);
        }
        _camera.Fov = (float)FovDeg;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.ButtonIndex == MouseButton.Left) _dragging = mb.Pressed;
                else if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _fovOffset = Math.Clamp(_fovOffset - 3, -40, 50);
                else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _fovOffset = Math.Clamp(_fovOffset + 3, -40, 50);
                break;
            case InputEventMouseMotion mm when _dragging:
                _mouseYaw += mm.Relative.X * 0.15;
                _mousePitch = Math.Clamp(_mousePitch - mm.Relative.Y * 0.15, -80, 60);
                break;
            case InputEventKey k when k.Pressed && !k.Echo:
                switch (k.Keycode)
                {
                    case Key.Key1: SetView(ViewKind.Bridge); break;
                    case Key.Key2: SetView(ViewKind.PortWing); break;
                    case Key.Key3: SetView(ViewKind.StbdWing); break;
                    case Key.Key4: SetView(ViewKind.Aft); break;
                    case Key.Key5: SetView(ViewKind.Overview); break;
                    case Key.R: _mouseYaw = 0; _mousePitch = 0; _fovOffset = 0; break;
                    case Key.Left: _mouseYaw -= 10; break;
                    case Key.Right: _mouseYaw += 10; break;
                }
                break;
        }
    }
}
