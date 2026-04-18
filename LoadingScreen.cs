using Godot;

/// <summary>
/// Полноэкранная заглушка на время генерации мира. Подписывается на сигналы
/// <see cref="MapGenerator.MapGenerated"/> и <see cref="RockField.RocksGenerated"/>,
/// обновляет статус и плавно тает после готовности обеих стадий.
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
    [Export] public MapGenerator Map;
    [Export] public RockField Rocks;
    [Export] public Label TitleLabel;
    [Export] public Label StatusLabel;
    [Export] public ColorRect Background;
    [Export(PropertyHint.Range, "0.1,3.0,0.05")] public float FadeOutDuration = 0.6f;
    [Export(PropertyHint.Range, "0.0,2.0,0.05")] public float DoneDelay = 0.25f;

    private bool _floorDone;
    private bool _rocksDone;

    public override void _Ready()
    {
        Node parent = GetParent();
        if (Map == null && parent != null) Map = parent.GetNodeOrNull<MapGenerator>("MapGenerator");
        if (Rocks == null && parent != null) Rocks = parent.GetNodeOrNull<RockField>("Rocks");

        if (Map != null) Map.MapGenerated += OnFloorReady;
        if (Rocks != null) Rocks.RocksGenerated += OnRocksReady;

        SetStatus("Генерация ландшафта…");
    }

    private void OnFloorReady()
    {
        _floorDone = true;
        SetStatus("Создание пещер и тоннелей…");
        TryFinish();
    }

    private void OnRocksReady()
    {
        _rocksDone = true;
        TryFinish();
    }

    private void TryFinish()
    {
        if (!_floorDone || !_rocksDone) return;

        SetStatus("Готово!");

        Tween tween = CreateTween();
        tween.TweenInterval(DoneDelay);
        if (Background != null)
            tween.TweenProperty(Background, "modulate:a", 0f, FadeOutDuration);
        if (TitleLabel != null)
            tween.Parallel().TweenProperty(TitleLabel, "modulate:a", 0f, FadeOutDuration);
        if (StatusLabel != null)
            tween.Parallel().TweenProperty(StatusLabel, "modulate:a", 0f, FadeOutDuration);
        tween.TweenCallback(Callable.From(QueueFree));
    }

    private void SetStatus(string text)
    {
        if (StatusLabel != null) StatusLabel.Text = text;
    }
}
