using Godot;
using SimosaBRM.Visual.Shared.Quality;

namespace SimosaBRM.Visual.Godot;

/// <summary>
/// HUD(可關,鍵 H):航向/速度/ROT/能見度/品質等級/fps/連線狀態/視角;品質分級 UI(自動偵測結果、手動覆寫 auto/low/mid/high)。
/// 文字每 0.25 s 更新一次(避免每幀字串配置)。
/// </summary>
public partial class HudLayer : CanvasLayer
{
    private Label _label = null!;
    private Label _detect = null!;
    private OptionButton _quality = null!;
    private PanelContainer _panel = null!;
    private double _accum;
    private readonly System.Text.StringBuilder _sb = new();

    public event Action<QualityTier?>? QualityOverrideChanged;

    public override void _Ready()
    {
        Layer = 10;
        _panel = new PanelContainer { Position = new Vector2(12, 12) };
        _label = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Left };
        _label.AddThemeFontSizeOverride("font_size", 18);
        _panel.AddChild(_label);
        AddChild(_panel);

        var box = new VBoxContainer { Position = new Vector2(12, 12), AnchorLeft = 1, AnchorRight = 1, OffsetLeft = -360, OffsetRight = -12, OffsetTop = 12 };
        _detect = new Label { Text = "品質:偵測中", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _detect.AddThemeFontSizeOverride("font_size", 14);
        _quality = new OptionButton();
        _quality.AddItem("品質:自動", 0);
        _quality.AddItem("品質:低(手動)", 1);
        _quality.AddItem("品質:中(手動)", 2);
        _quality.AddItem("品質:高(手動)", 3);
        _quality.ItemSelected += idx => QualityOverrideChanged?.Invoke(idx switch { 1 => QualityTier.Low, 2 => QualityTier.Mid, 3 => QualityTier.High, _ => null });
        box.AddChild(_quality);
        box.AddChild(_detect);
        AddChild(box);
    }

    public void SetDetection(string reason) => _detect.Text = "自動偵測:" + reason;

    public void SetOverrideSelection(QualityTier? tier) => _quality.Selected = tier switch { QualityTier.Low => 1, QualityTier.Mid => 2, QualityTier.High => 3, _ => 0 };

    public void UpdateText(double dt, Func<System.Text.StringBuilder, System.Text.StringBuilder> fill)
    {
        _accum += dt;
        if (_accum < 0.25) return;
        _accum = 0;
        _sb.Clear();
        _label.Text = fill(_sb).ToString();
    }
}
