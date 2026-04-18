using Godot;
using System.Text;

/// <summary>
/// Realtime overlay со статистикой экосистемы. Включается/выключается клавишей T.
///
/// Создаёт собственный Label + полупрозрачный фон в коде, чтобы не возиться
/// с UI-нодами в сцене. Обновляется раз в 0.2 сек (тяжёлые подсчёты —
/// итерация 640k клеток камня — не каждый кадр).
/// </summary>
public partial class StatsPanel : CanvasLayer
{
    [Export] public FireflyColony Fireflies;
    [Export] public WormColony Worms;
    [Export] public CrystalField Crystals;
    [Export] public MossField Moss;
    [Export] public RockField Rocks;
    [Export] public MushroomField Mushrooms;

    [Export(PropertyHint.Range, "0.05,2.0,0.01")] public float UpdateInterval = 0.2f;
    [Export(PropertyHint.Range, "200,800,10")] public int PanelWidth = 380;
    [Export(PropertyHint.Range, "200,1000,10")] public int PanelHeight = 460;

    private Label _label;
    private ColorRect _background;
    private float _accum;
    private bool _visible;
    private StringBuilder _sb = new(2048);

    public override void _Ready()
    {
        Layer = 100;

        Node parent = GetParent();
        if (Fireflies == null && parent != null) Fireflies = parent.GetNodeOrNull<FireflyColony>("FireflyColony");
        if (Worms == null && parent != null) Worms = parent.GetNodeOrNull<WormColony>("WormColony");
        if (Crystals == null && parent != null) Crystals = parent.GetNodeOrNull<CrystalField>("CrystalField");
        if (Moss == null && parent != null) Moss = parent.GetNodeOrNull<MossField>("MossField");
        if (Rocks == null && parent != null) Rocks = parent.GetNodeOrNull<RockField>("Rocks");
        if (Mushrooms == null && parent != null) Mushrooms = parent.GetNodeOrNull<MushroomField>("MushroomField");

        BuildUI();
        SetPanelVisible(false);
    }

    private void BuildUI()
    {
        _background = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.65f),
            AnchorLeft = 0,
            AnchorTop = 0,
            OffsetLeft = 12,
            OffsetTop = 12,
            OffsetRight = 12 + PanelWidth,
            OffsetBottom = 12 + PanelHeight,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_background);

        _label = new Label
        {
            AnchorLeft = 0,
            AnchorTop = 0,
            OffsetLeft = 24,
            OffsetTop = 18,
            OffsetRight = 12 + PanelWidth - 12,
            OffsetBottom = 12 + PanelHeight - 12,
            Text = "loading…",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", 14);
        _label.AddThemeColorOverride("font_color", new Color(0.92f, 0.96f, 1.0f));
        AddChild(_label);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo
            && k.PhysicalKeycode == Key.T)
        {
            SetPanelVisible(!_visible);
        }
    }

    private void SetPanelVisible(bool v)
    {
        _visible = v;
        if (_background != null) _background.Visible = v;
        if (_label != null) _label.Visible = v;
        if (v) UpdateLabel();    // сразу обновим, не ждём аккумулятор
    }

    public override void _Process(double delta)
    {
        if (!_visible) return;
        _accum += (float)delta;
        if (_accum < UpdateInterval) return;
        _accum = 0f;
        UpdateLabel();
    }

    private void UpdateLabel()
    {
        if (_label == null) return;

        _sb.Clear();
        _sb.AppendLine("=== ECOSYSTEM STATS  (T: hide) ===");
        _sb.AppendLine($"FPS: {Engine.GetFramesPerSecond()}");
        _sb.AppendLine();

        if (Fireflies != null)
        {
            _sb.AppendLine($"Fireflies:  {Fireflies.Count,4} / cap {GetMaxPopFireflies(),-3}  | Peak: {Fireflies.PeakPopulation}");
            _sb.AppendLine($"  Born:    {Fireflies.BornTotal,5}  (init {Fireflies.BornInitial}, breed {Fireflies.BornBreed},");
            _sb.AppendLine($"                       replenish {Fireflies.BornReplenish}, moss {Fireflies.BornMoss})");
            _sb.AppendLine($"  Died:    {Fireflies.DiedTotal,5}  (age {Fireflies.DiedAge}, hunger {Fireflies.DiedHunger}, eaten {Fireflies.DiedPredator})");
            _sb.AppendLine();
        }

        if (Worms != null)
        {
            _sb.AppendLine($"Worms:      {Worms.Count,4} / cap {GetMaxPopWorms(),-3}  | Peak: {Worms.PeakPopulation}");
            _sb.AppendLine($"  Born:    {Worms.BornTotal,5}  (init {Worms.BornInitial}, breed {Worms.BornBreed}, replenish {Worms.BornReplenish})");
            _sb.AppendLine($"  Died:    {Worms.DiedTotal,5}  (age {Worms.DiedAge}, hunger {Worms.DiedHunger})");
            _sb.AppendLine($"  Killed fireflies: {Worms.KilledFirefliesTotal}");
            _sb.AppendLine($"  Cells dug:        {Worms.CellsDugTotal}");
            _sb.AppendLine();
        }

        if (Crystals != null)
        {
            _sb.AppendLine($"Crystals:   Growing {Crystals.CurrentGrowing}, Mature {Crystals.CurrentMature}");
            _sb.AppendLine($"  Seeded:    {Crystals.SeededTotal}");
            _sb.AppendLine($"  Matured:   {Crystals.MaturedTotal}");
            _sb.AppendLine($"  Destroyed: {Crystals.DestroyedTotal}");
            _sb.AppendLine();
        }

        if (Mushrooms != null)
        {
            _sb.AppendLine($"Mushrooms:  {Mushrooms.Count}");
            _sb.AppendLine();
        }

        _sb.AppendLine("Map:");
        if (Rocks != null)
        {
            int totalCells = Rocks.TotalCells;
            int rockCount = Rocks.CountRocks();
            float rockPct = totalCells > 0 ? 100f * rockCount / totalCells : 0f;
            _sb.AppendLine($"  Rocks: {rockCount,7}  ({rockPct:F1}%)");
        }
        if (Moss != null)
        {
            int total = Moss.TotalCells;
            int active = Moss.ActiveCells;
            float pct = total > 0 ? 100f * active / total : 0f;
            _sb.AppendLine($"  Moss:  {active,7}  ({pct:F1}%)");
        }

        _label.Text = _sb.ToString();
    }

    private int GetMaxPopFireflies() => Fireflies?.MaxPopulation ?? 0;
    private int GetMaxPopWorms() => Worms?.MaxPopulation ?? 0;
}
