using SimosaBRM.Visual.Shared.Quality;
using Xunit;

namespace SimosaBRM.Visual.Shared.Tests;

public class QualityDetectorTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", 8192, 32768, "discrete", QualityTier.High)]
    [InlineData("NVIDIA GeForce GTX 1650", 4096, 16384, "discrete", QualityTier.Mid)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", null, 16384, "integrated", QualityTier.Mid)]
    [InlineData("Intel(R) UHD Graphics 620", null, 8192, "integrated", QualityTier.Low)]
    [InlineData("AMD Radeon(TM) Graphics", null, 32768, "integrated", QualityTier.Mid)]
    [InlineData("AMD Radeon 780M", null, 16384, null, QualityTier.Mid)]
    [InlineData("AMD Radeon RX 7700S", 8192, 16384, "discrete", QualityTier.High)]
    [InlineData("llvmpipe (LLVM 15.0.7, 256 bits)", null, 16384, "cpu", QualityTier.Low)]
    [InlineData("", null, null, null, QualityTier.Low)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", 16384, 32768, "discrete", QualityTier.High)]
    [InlineData("Intel(R) Arc(TM) Graphics", null, 32768, "integrated", QualityTier.Mid)]
    public void ClassifiesKnownGpus(string gpu, int? vram, int? ram, string? type, QualityTier expected)
    {
        var d = QualityDetector.Detect(new HardwareInfo { GpuName = gpu, VramMB = vram, SystemMemoryMB = ram, DeviceType = type });
        Assert.Equal(expected, d.Tier);
        Assert.False(string.IsNullOrWhiteSpace(d.Reason));
    }

    [Fact]
    public void MeasuredFpsBelowThirtyDowngradesOneTier()
    {
        var d = QualityDetector.Detect(new HardwareInfo { GpuName = "Intel Iris Xe Graphics", SystemMemoryMB = 16384, MeasuredFps = 22 });
        Assert.Equal(QualityTier.Low, d.Tier);
        Assert.Contains("22 fps", d.Reason);
    }

    [Fact]
    public void MeasuredFpsAboveNinetyPromotesMidToHigh()
    {
        var d = QualityDetector.Detect(new HardwareInfo { GpuName = "NVIDIA GeForce GTX 1650", SystemMemoryMB = 16384, MeasuredFps = 120 });
        Assert.Equal(QualityTier.High, d.Tier);
    }

    [Fact]
    public void SettingsFollowPlanDrawDistances()
    {
        Assert.InRange(QualitySettings.For(QualityTier.Low).DrawDistanceM, 3 * 1852, 4 * 1852);
        Assert.Equal(6 * 1852, QualitySettings.For(QualityTier.Mid).DrawDistanceM);
        Assert.True(QualitySettings.For(QualityTier.High).DrawDistanceM >= 10 * 1852);
        Assert.Equal(RendererKind.Compatibility, QualitySettings.For(QualityTier.Low).Renderer);
        Assert.True(QualitySettings.For(QualityTier.Low).RenderScale < 1.0);
        Assert.False(QualitySettings.For(QualityTier.Low).GerstnerWaves);
        Assert.True(QualitySettings.For(QualityTier.Mid).GerstnerWaves);
        Assert.Equal(QualityTier.Mid, QualitySettings.ParseTier("medium"));
        Assert.Throws<ArgumentException>(() => QualitySettings.ParseTier("ultra"));
    }
}

public class AdaptiveQualityControllerTests
{
    private static void Run(AdaptiveQualityController c, double fps, double seconds, ref double now)
    {
        var dt = 1.0 / fps;
        var end = now + seconds;
        while (now < end) { c.Update(dt, now); now += dt; }
    }

    [Fact]
    public void SustainedLowFpsDowngradesOneTierAndLogs()
    {
        var c = new AdaptiveQualityController(QualityTier.High);
        double now = 0;
        Run(c, 20, 4.0, ref now);
        Assert.Equal(QualityTier.High, c.Current); // 未滿 5 s
        Run(c, 20, 1.5, ref now);
        Assert.Equal(QualityTier.Mid, c.Current);
        Assert.Single(c.Changes);
        Assert.Contains("25 fps", c.Changes[0].Reason);
    }

    [Fact]
    public void BriefDipsDoNotDowngrade()
    {
        var c = new AdaptiveQualityController(QualityTier.Mid);
        double now = 0;
        for (var i = 0; i < 10; i++)
        {
            Run(c, 20, 3.0, ref now);
            Run(c, 60, 1.0, ref now); // 回復 → 歸零
        }
        Assert.Equal(QualityTier.Mid, c.Current);
    }

    [Fact]
    public void HysteresisBandKeepsAccumulating()
    {
        // 介於 25 與 27 fps 之間不歸零:20 fps 3 s + 26 fps 3 s 後再 20 fps 2 s 應降級
        var c = new AdaptiveQualityController(QualityTier.Mid);
        double now = 0;
        Run(c, 20, 3.0, ref now);
        Run(c, 26, 3.0, ref now);
        Assert.Equal(QualityTier.Mid, c.Current);
        Run(c, 20, 2.5, ref now);
        Assert.Equal(QualityTier.Low, c.Current);
    }

    [Fact]
    public void CooldownPreventsImmediateSecondDowngradeAndLowIsFloor()
    {
        var c = new AdaptiveQualityController(QualityTier.High) { CooldownSeconds = 30 };
        double now = 0;
        Run(c, 20, 6.0, ref now);
        Assert.Equal(QualityTier.Mid, c.Current);
        Run(c, 20, 20.0, ref now); // 冷卻中
        Assert.Equal(QualityTier.Mid, c.Current);
        Run(c, 20, 20.0, ref now); // 冷卻結束後再 5 s
        Assert.Equal(QualityTier.Low, c.Current);
        Run(c, 10, 60.0, ref now);
        Assert.Equal(QualityTier.Low, c.Current);
        Assert.Equal(2, c.Changes.Count);
    }

    [Fact]
    public void ManualOverrideDisablesAutoAndClearingRestoresIt()
    {
        var c = new AdaptiveQualityController(QualityTier.Mid);
        double now = 0;
        c.SetOverride(QualityTier.High, now);
        Assert.Equal(QualityTier.High, c.Current);
        Run(c, 15, 30.0, ref now);
        Assert.Equal(QualityTier.High, c.Current);
        c.SetOverride(null, now);
        Run(c, 15, 31.0, ref now); // 清除覆寫後有觀察期
        Run(c, 15, 6.0, ref now);
        Assert.Equal(QualityTier.Mid, c.Current);
    }

    [Fact]
    public void HugeFrameTimesAreIgnored()
    {
        var c = new AdaptiveQualityController(QualityTier.Mid);
        for (var i = 0; i < 100; i++) c.Update(2.0, i * 2.0); // 載入卡頓
        Assert.Equal(QualityTier.Mid, c.Current);
    }

    [Fact]
    public void DetectedTierAppliedOnlyWithoutOverride()
    {
        var c = new AdaptiveQualityController(QualityTier.Low);
        c.SetDetected(QualityTier.Mid, 0, "偵測");
        Assert.Equal(QualityTier.Mid, c.Current);
        c.SetOverride(QualityTier.Low, 1);
        c.SetDetected(QualityTier.High, 2, "偵測");
        Assert.Equal(QualityTier.Low, c.Current);
    }
}

public class QualityPersistenceTests
{
    [Fact]
    public void RoundTripsWithinProjectBuildFolder()
    {
        var dir = TestPaths.Scratch("quality");
        var path = Path.Combine(dir, "quality.json");
        Assert.Null(QualityPersistence.Load(path).ManualOverride);
        QualityPersistence.Save(path, new PersistedQuality { ManualOverride = "mid", LastAutoTier = "low", LastGpu = "Iris", HudVisible = false, FovDeg = 65 });
        var q = QualityPersistence.Load(path);
        Assert.Equal(QualityTier.Mid, QualityPersistence.ParseOverride(q.ManualOverride));
        Assert.Equal("low", q.LastAutoTier);
        Assert.False(q.HudVisible);
        Assert.Equal(65, q.FovDeg);
        Assert.NotNull(q.UpdatedUtc);
        Assert.StartsWith(TestPaths.Root, path);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        var dir = TestPaths.Scratch("quality-bad");
        var path = Path.Combine(dir, "quality.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(QualityPersistence.Load(path).ManualOverride);
        Assert.Null(QualityPersistence.ParseOverride("auto"));
        Assert.Null(QualityPersistence.ParseOverride("bogus"));
        Directory.Delete(dir, true);
    }
}
