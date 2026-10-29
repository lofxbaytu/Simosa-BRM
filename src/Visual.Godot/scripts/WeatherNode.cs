using Godot;
using SimosaBRM.Visual.Shared.Env;
using SimosaBRM.Visual.Shared.Quality;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 天空、日照/夜間、霧、雨(示意):WorldEnvironment(ProceduralSky + 指數霧,密度由能見度換算)、DirectionalLight3D(太陽高度/方位)、
/// 月光 DirectionalLight3D(偏冷色;方向由 look.Moon 高度/方位、強度由 look.Moon.Light(已含月相、仰角、霧/雨衰減);低品質無月光陰影)、
/// 夜間環境光/天空底色依 look.NightSkyBrightness(星光底值 + 月光)、CpuParticles3D 雨(中/高品質;強度依 radarEnvironment 雨區)。
/// 只套用 EnvironmentMapper 的結果(docs/design/visual-client.md 第 4 節「月光」)。
/// </summary>
public partial class WeatherNode : Node3D
{
    /// <summary>滿月天頂、晴空時月光 DirectionalLight 的能量(相對太陽正午 1.3;實機亮度待確認)</summary>
    public const float MoonLightEnergyFullMoon = 0.35f;
    /// <summary>月光低於此值不開燈(省一次光照/陰影計算)</summary>
    public const float MoonLightMinimum = 0.002f;
    private static readonly Color MoonColor = new(0.72f, 0.80f, 1.0f);

    private WorldEnvironment? _world;
    private global::Godot.Environment? _env;
    private ProceduralSkyMaterial? _sky;
    private DirectionalLight3D? _sun;
    private DirectionalLight3D? _moon;
    private CpuParticles3D? _rain;
    private bool _rainAllowed;
    private bool _moonShadowsAllowed;

    /// <summary>月光目前是否點亮(煙霧報告/HUD 用)</summary>
    public bool MoonLightOn => _moon?.Visible == true;

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
        // 月光:偏冷色、能量由 look.Moon.Light 決定;陰影依品質(低品質關閉),投影距離較短(夜間只需近處船體/碼頭)
        _moon = new DirectionalLight3D
        {
            Name = "moon",
            LightColor = MoonColor,
            LightEnergy = 0f,
            LightSpecular = 0.3f,
            ShadowEnabled = q.MoonShadows,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = 300f,
            Visible = false,
        };
        AddChild(_moon);
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
        _moonShadowsAllowed = q.MoonShadows;
        if (_moon is not null) _moon.ShadowEnabled = q.MoonShadows;
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
        // 夜間:環境光與天空底色依夜空亮度(星光底值 + 衰減後月光;NightSkyBrightness 已乘 (1 − Daylight)),再與日光部分相加
        var night = (float)look.NightSkyBrightness;
        _env.FogLightColor = new Color(0.7f * day + 0.02f + 0.10f * night, 0.75f * day + 0.02f + 0.12f * night, 0.8f * day + 0.04f + 0.18f * night);
        _env.AmbientLightEnergy = 0.85f * day + 0.05f + 0.25f * night; // 無月夜 0.05 + 0.25 × 0.08 ≈ 0.07、滿月晴夜 0.30、白天 1.0
        _sky.SkyTopColor = new Color(0.25f * day + 0.005f + 0.04f * night, 0.45f * day + 0.008f + 0.06f * night, 0.75f * day + 0.02f + 0.12f * night);
        _sky.SkyHorizonColor = new Color(0.65f * day + 0.02f + 0.08f * night, 0.72f * day + 0.02f + 0.10f * night, 0.8f * day + 0.03f + 0.15f * night);
        _sky.GroundHorizonColor = _sky.SkyHorizonColor;
        _sky.GroundBottomColor = new Color(0.1f * day + 0.02f * night, 0.15f * day + 0.03f * night, 0.2f * day + 0.05f * night);
        // 太陽方向:高度/方位 → 光的方向(DirectionalLight3D 照向 −z 本地軸)
        var el = Mathf.DegToRad((float)Math.Max(look.SunElevationDeg, -10.0));
        var az = Mathf.DegToRad((float)look.SunAzimuthDeg);
        var dir = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el)); // 指向太陽(ENU → 場景)
        _sun.Position = cameraPosition;
        if (dir.LengthSquared() > 1e-6) _sun.LookAt(cameraPosition - dir, Vector3.Up);
        _sun.LightEnergy = look.SunElevationDeg > 0 ? 0.3f + 1.0f * day : 0.05f;
        _sun.LightColor = look.SunElevationDeg < 10 ? new Color(1f, 0.75f, 0.55f) : new Color(1f, 0.97f, 0.92f);
        UpdateMoon(look, cameraPosition);

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

    /// <summary>
    /// 月光:能量 = MoonLightEnergyFullMoon × look.Moon.Light × (1 − Daylight)(白天月光被日光淹沒,不另加燈);
    /// 方向同太陽的換算(高度/方位 → 指向月球的向量);低於門檻時隱藏燈(省光照/陰影計算)。
    /// </summary>
    private void UpdateMoon(EnvironmentLook look, Vector3 cameraPosition)
    {
        if (_moon is null) return;
        var energy = MoonLightEnergyFullMoon * (float)look.Moon.Light * (1f - (float)look.Daylight);
        var on = energy >= MoonLightMinimum && look.Moon.ElevationDeg > 0.0;
        if (_moon.Visible != on) _moon.Visible = on;
        if (!on) return;
        var el = Mathf.DegToRad((float)look.Moon.ElevationDeg);
        var az = Mathf.DegToRad((float)look.Moon.AzimuthDeg);
        var dir = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el)); // 指向月球(ENU → 場景)
        _moon.Position = cameraPosition;
        if (dir.LengthSquared() > 1e-6) _moon.LookAt(cameraPosition - dir, Vector3.Up);
        _moon.LightEnergy = energy;
        _moon.ShadowEnabled = _moonShadowsAllowed;
    }
}
