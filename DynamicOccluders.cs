using Godot;
using System.Collections.Generic;

/// <summary>
/// Создаёт <see cref="LightOccluder2D"/>-ноды только для тайлов в радиусе
/// <see cref="Radius"/> вокруг <see cref="Target"/> (обычно — игрока).
/// Это обходит ограничение Godot на количество RID-окклюдеров (TileSet
/// пытался бы создать ~600 000 для плотной 800×800 карты и сразу же ловил
/// «Element limit reached»). Здесь активных всегда ~250–300, остальные
/// тайлы освещение «не видит» — они и так за границей света.
/// </summary>
public partial class DynamicOccluders : Node2D
{
    [Export] public Node2D Target;
    [Export] public RockField Rocks;
    [Export] public TileMapLayer SolidWalls;
    [Export(PropertyHint.Range, "2,30,1")] public int Radius = 8;

    private OccluderPolygon2D _tilePolygon;
    private readonly Dictionary<Vector2I, LightOccluder2D> _active = new();
    private Vector2I _lastCenter = new(int.MinValue, int.MinValue);

    public override void _Ready()
    {
        _tilePolygon = new OccluderPolygon2D
        {
            Polygon = new Vector2[]
            {
                new(-64, -64), new(64, -64), new(64, 64), new(-64, 64),
            },
            CullMode = OccluderPolygon2D.CullModeEnum.Disabled,
        };

        // Фолбэки на случай нерезолвнутых NodePath.
        Node parent = GetParent();
        if (parent != null)
        {
            if (Target == null)      Target      = parent.GetNodeOrNull<Node2D>("Player");
            if (Rocks == null)       Rocks       = parent.GetNodeOrNull<RockField>("Rocks");
            if (SolidWalls == null)  SolidWalls  = parent.GetNodeOrNull<TileMapLayer>("SolidWalls");
        }

        if (Rocks != null) Rocks.RockDestroyed += OnRockDestroyed;
    }

    public override void _Process(double _delta)
    {
        if (Target == null || Rocks == null) return;

        Vector2I center = Rocks.LocalToMap(Rocks.ToLocal(Target.GlobalPosition));
        if (center == _lastCenter) return;
        _lastCenter = center;
        Refresh(center);
    }

    private void Refresh(Vector2I center)
    {
        var needed = new HashSet<Vector2I>();
        int r2 = (Radius + 1) * (Radius + 1);

        for (int dy = -Radius; dy <= Radius; dy++)
        {
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                var cell = new Vector2I(center.X + dx, center.Y + dy);
                if (IsObstacle(cell)) needed.Add(cell);
            }
        }

        // Удалить устаревшие.
        var stale = new List<Vector2I>();
        foreach (var c in _active.Keys)
            if (!needed.Contains(c)) stale.Add(c);
        foreach (var c in stale)
        {
            _active[c].QueueFree();
            _active.Remove(c);
        }

        // Создать новые.
        foreach (var c in needed)
        {
            if (_active.ContainsKey(c)) continue;
            var occ = new LightOccluder2D
            {
                Occluder = _tilePolygon,
                Position = Rocks.MapToLocal(c),
            };
            AddChild(occ);
            _active[c] = occ;
        }
    }

    private bool IsObstacle(Vector2I cell)
    {
        // Здесь не нужна OOB-защита (Refresh ходит вокруг игрока, не уйдёт
        // в бесконечность) и не нужен учёт кристаллов — они не загораживают
        // свет, а сами должны светиться.
        if (Rocks != null && Rocks.HasRock(cell)) return true;
        if (SolidWalls != null && SolidWalls.GetCellSourceId(cell) >= 0) return true;
        return false;
    }

    private void OnRockDestroyed(Vector2I cell)
    {
        if (_active.TryGetValue(cell, out var occ))
        {
            occ.QueueFree();
            _active.Remove(cell);
        }
    }
}
