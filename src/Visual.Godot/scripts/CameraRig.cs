using Godot;
using SimosaBRM.Visual.Shared.Scene;
using SimosaBRM.Visual.Shared.Ship;
using SimosaBRM.Visual.Shared.State;
using SimosaBRM.Visual.Shared.View;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 視角:鍵盤 1–5 切換(駕駛台前視/左翼橋/右翼橋/艉視/鳥瞰),滑鼠左鍵拖曳環視(yaw/pitch 偏移)、滾輪縮放視野角、R 重設;
/// 眼位依 ViewDefinition(眼高取 particulars bridge 資料或估計)跟隨自船插值姿態;鳥瞰固定北向上。
/// 船體運動(state.roll 橫搖、state.waveMotion 縱搖/升沉;docs/design/visual-client.md 第 4 節「橫搖、縱搖與升沉」):觀察者隨船運動,
/// 相機姿態 = 艏向 yaw · 船體縱搖(繞橫軸,艏仰正)· 船體橫搖(繞艏艉軸,右傾正)· 視角偏航 · 俯仰
/// (Basis 精確合成;合成順序 Euler ZYX 與 SceneCoordinates.BodyToWorldOffset / ObserverBasis 相同;艉視自動反號、翼橋側視時橫搖與縱搖互相混合),
/// 眼位亦隨船體姿態偏移並加上升沉(ViewDefinition.EyeOffset);低品質一樣套用(只多矩陣乘法)。
/// 無 roll / waveMotion 時對應量為 0,姿態與原本的 Euler 寫法完全相同;鳥瞰不隨船體運動。
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
            // 眼位:船體座標的眼位經橫搖 → 縱搖 → 艏向 → 升沉(固定順序,ViewDefinition.EyeOffset);姿態以同一順序合成
            var eye = SceneCoordinates.FromEnu(own.East, own.North, 0) + v.EyeOffset(own.HeadingDeg, own.RollDeg, own.PitchDeg, own.HeaveM);
            Position = new Vector3((float)eye.X, (float)eye.Y, (float)eye.Z);
            Basis = AttitudeBasis(own.HeadingDeg, v.CameraRollDeg(own.RollDeg), v.CameraPitchDeg(own.PitchDeg), v.YawDeg + _mouseYaw, v.PitchDeg + _mousePitch);
        }
        _camera.Fov = (float)FovDeg;
    }

    /// <summary>
    /// 艏向(度)、船體橫搖(度,右傾正)、船體縱搖(度,艏仰正)、相對艏向的偏航(度,右正)與俯仰(度,上正)→ 相機 Basis:
    /// Ry(−艏向) · Rx(船縱搖) · Rz(−橫搖) · Ry(−偏航) · Rx(俯仰)(Euler ZYX:艏搖 → 縱搖 → 橫搖;SceneCoordinates.ObserverBasis 為其雙精度對照,
    /// Visual.Shared.Tests 的 SceneWaveMotionTests 釘住順序與正負號)。橫搖與縱搖皆 0 時 = Ry(−(艏向 + 偏航)) · Rx(俯仰),即原本的 Rotation(俯仰, yaw, 0)。
    /// 右傾為繞船體 +z(艉向)旋轉 −φ:右舷下沉、高於水線的點往右舷移(SceneCoordinates.HeelOffset 同一慣例)。
    /// 艏仰為繞船體 +x(右舷向)旋轉 +θ:艏部上升(SceneCoordinates.PitchOffset 同一慣例;與視角俯仰「上正」同一轉向,
    /// 所以駕駛台前視時艏仰使視線上抬、地平線在畫面中下移)。
    /// </summary>
    public static Basis AttitudeBasis(double headingDeg, double rollDeg, double shipPitchDeg, double viewYawDeg, double pitchDeg)
    {
        var yawShip = new Basis(Vector3.Up, (float)SceneCoordinates.HeadingToYawRad(headingDeg));
        var viewYaw = new Basis(Vector3.Up, (float)SceneCoordinates.HeadingToYawRad(viewYawDeg));
        var pitch = new Basis(Vector3.Right, Mathf.DegToRad((float)pitchDeg));
        if (rollDeg == 0.0 && shipPitchDeg == 0.0) return yawShip * viewYaw * pitch;
        var attitude = yawShip;
        if (shipPitchDeg != 0.0) attitude *= new Basis(Vector3.Right, Mathf.DegToRad((float)shipPitchDeg));
        if (rollDeg != 0.0) attitude *= new Basis(Vector3.Back, -Mathf.DegToRad((float)rollDeg));
        return attitude * viewYaw * pitch;
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
