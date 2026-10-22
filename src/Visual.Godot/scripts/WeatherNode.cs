using Godot;
using SimosaBRM.Visual.Shared.Env;
using SimosaBRM.Visual.Shared.Quality;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 天空、日照/夜間、霧、雨(示意):WorldEnvironment(ProceduralSky + 指數霧,密度由能見度換算)、DirectionalLight3D(太陽高度/方位)、
/// CpuParticles3D 雨(中/高品質;強度依 radarEnvironment 雨區)。只套用 EnvironmentMapper 的結果。
/// </summary>
public partial class WeatherNode : Node3D
{
    private WorldEnvironment? _world;
    private global::Godot.Environment? _env;
    private ProceduralSkyMaterial? _sky;
    private DirectionalLight3D? _sun;
    private CpuParticles3D? _rain;
    private bool _rainAllowed;

    public void Build(QualitySettings q)
    {
        _sky = new ProceduralSkyMaterial { SkyTopColor = new Color(0.25f, 0.45f, 0.75f), SkyHorizonColor = new Color(0.65f, 0.72f, 0.8f), GroundBottomColor = new Color(0.1f, 0.15f, 0.2f), GroundHorizonColor = new Color(0.6f, 0.68f, 0.75f), SunAngleMax = 20f, SunCurve = 0.2f };
        _env = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = _sky, RadianceSize = Sky.RadianceSizeEnum.Size64 },
            AmbientLightSource = global::Godot.Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 0.8f,
            ReflectedLightSource = global::Godot.Environment.ReflectionSource.Sky,
            FogEnabled = true,
            FogMode = global::Godot.Environment.FogModeEnum.Exponential,
            FogLightColor = new Color(0.7f, 0.75f, 0.8f),
            FogSunScatter = 0.2f,
            FogAerialPerspective = 0.3f,
            TonemapMode = global::Godot.Environment.ToneMapper.Filmic,
            GlowEnabled = false,
        };
        _world = new WorldEnvironment { Environment = _env, Name = "worldEnvironment" };
        AddChild(_world);
        _sun = new DirectionalLight3D { Name = "sun", LightEnergy = 1.2f, ShadowEnabled = q.Shadows, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits, DirectionalShadowMaxDistance = 600f };
        AddChild(_sun);
        _rain = new CpuParticles3D
        {
            Name = "rain",
            Emitting = false,
            Amount = 1500,
            Lifetime = 1.2f,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(60f, 1f, 60f),
            Direction = new Vector3(0f, -1f, 0f),
            Spread = 2f,
            InitialVelocityMin = 25f,
            InitialVelocityMax = 32f,
            Gravity = new Vector3(0f, -9.8f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(0.02f, 0.5f, 0.02f), Material = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.85f, 0.95f, 0.6f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } },
            Visible = false,
        };
        AddChild(_rain);
        Apply(q);
    }

    public void Apply(QualitySettings q)
    {
        if (_sun is not null) _sun.ShadowEnabled = q.Shadows;
        _rainAllowed = q.RainParticles;
        if (_env is not null)
        {
            _env.SsaoEnabled = false;
            _env.SsrEnabled = false;
            _env.GlowEnabled = q.PostProcessing && q.Tier == QualityTier.High;
            _env.AdjustmentEnabled = q.PostProcessing;
        }
        if (!_rainAllowed && _rain is not null) { _rain.Emitting = false; _rain.Visible = false; }
    }

    public void Update(EnvironmentLook look, Vector3 cameraPosition)
    {
        if (_env is null || _sun is null || _sky is null) return;
        _env.FogDensity = (float)look.FogDensity;
        var day = (float)look.Daylight;
        _env.FogLightColor = new Color(0.7f * day + 0.02f, 0.75f * day + 0.02f, 0.8f * day + 0.04f);
        _env.AmbientLightEnergy = 0.15f + 0.85f * day;
        _sky.SkyTopColor = new Color(0.25f * day + 0.01f, 0.45f * day + 0.015f, 0.75f * day + 0.04f);
        _sky.SkyHorizonColor = new Color(0.65f * day + 0.03f, 0.72f * day + 0.03f, 0.8f * day + 0.05f);
        _sky.GroundHorizonColor = _sky.SkyHorizonColor;
        _sky.GroundBottomColor = new Color(0.1f * day, 0.15f * day, 0.2f * day);
        // 太陽方向:高度/方位 → 光的方向(DirectionalLight3D 照向 −z 本地軸)
        var el = Mathf.DegToRad((float)Math.Max(look.SunElevationDeg, -10.0));
        var az = Mathf.DegToRad((float)look.SunAzimuthDeg);
        var dir = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el)); // 指向太陽(ENU → 場景)
        _sun.Position = cameraPosition;
        if (dir.LengthSquared() > 1e-6) _sun.LookAt(cameraPosition - dir, Vector3.Up);
        _sun.LightEnergy = look.SunElevationDeg > 0 ? 0.3f + 1.0f * day : 0.05f;
        _sun.LightColor = look.SunElevationDeg < 10 ? new Color(1f, 0.75f, 0.55f) : new Color(1f, 0.97f, 0.92f);

        if (_rain is not null)
        {
            var on = _rainAllowed && look.Rain > 0.05;
            if (_rain.Emitting != on) { _rain.Emitting = on; _rain.Visible = on; }
            if (on)
            {
                _rain.Position = cameraPosition + new Vector3(0f, 25f, 0f);
                _rain.SpeedScale = (float)(0.6 + 0.8 * Math.Clamp(look.Rain, 0.1, 1.0));
            }
        }
    }
}
