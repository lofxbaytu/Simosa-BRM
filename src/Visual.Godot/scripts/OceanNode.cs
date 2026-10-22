using Godot;
using SimosaBRM.Visual.Shared.Env;
using SimosaBRM.Visual.Shared.Quality;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// 海面:一塊跟隨相機(以格距吸附,避免游移)的 PlaneMesh + ocean.gdshader。
/// 低品質:平面 + 程序化法線貼圖(NoiseTexture2D,無資產)捲動;中/高:Gerstner 頂點位移 + 泡沫。網格解析度依品質。
/// 著色器參數名稱以 StringName 快取,避免每幀配置。
/// </summary>
public partial class OceanNode : MeshInstance3D
{
    private static readonly StringName PTime = new("time_s");
    private static readonly StringName PQuality = new("quality");
    private static readonly StringName PWaveHeight = new("wave_height");
    private static readonly StringName PWaveDir = new("wave_dir");
    private static readonly StringName PDaylight = new("daylight");
    private static readonly StringName PNormalMap = new("normal_map");

    private ShaderMaterial? _material;
    private double _cell = 50;
    private double _time;

    public void Build(QualitySettings q)
    {
        var shader = GD.Load<Shader>("res://shaders/ocean.gdshader");
        _material = new ShaderMaterial { Shader = shader };
        var noise = new NoiseTexture2D
        {
            Width = 256,
            Height = 256,
            Seamless = true,
            AsNormalMap = true,
            BumpStrength = 6f,
            Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.02f, FractalOctaves = 3 },
        };
        _material.SetShaderParameter(PNormalMap, noise);
        Apply(q);
        Name = "ocean";
    }

    public void Apply(QualitySettings q)
    {
        if (_material is null) return;
        var size = (float)q.OceanGridSizeM;
        Mesh = new PlaneMesh { Size = new Vector2(size, size), SubdivideWidth = q.OceanGridCells - 1, SubdivideDepth = q.OceanGridCells - 1, Material = _material };
        _cell = q.OceanGridSizeM / q.OceanGridCells;
        _material.SetShaderParameter(PQuality, q.GerstnerWaves ? (q.Tier == QualityTier.High ? 2 : 1) : 0);
        CastShadow = ShadowCastingSetting.Off;
    }

    public void Update(double dt, Vector3 cameraPosition, EnvironmentLook look)
    {
        if (_material is null) return;
        _time += dt;
        // 跟隨相機(吸附到格距),y 固定 0(水線)
        var x = Math.Round(cameraPosition.X / _cell) * _cell;
        var z = Math.Round(cameraPosition.Z / _cell) * _cell;
        Position = new Vector3((float)x, 0f, (float)z);
        _material.SetShaderParameter(PTime, (float)_time);
        _material.SetShaderParameter(PWaveHeight, (float)Math.Min(look.WaveHeightM, 4.0));
        var rad = Mathf.DegToRad((float)look.WaveDirectionDeg);
        _material.SetShaderParameter(PWaveDir, new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad))); // 去向:ENU → 場景 (x, −north)
        _material.SetShaderParameter(PDaylight, (float)look.Daylight);
    }
}
