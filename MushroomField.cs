using Godot;
using System.Collections.Generic;

/// <summary>
/// Стационарные неоновые грибы. Размещение — двухфазное:
///   1) Flood-fill всех открытых регионов после генерации.
///   2) В каждом регионе посеять количество, пропорциональное sqrt(N),
///      чтобы И крошечные карманы получили хотя бы несколько штук, И
///      большие залы — десятки/сотни.
///
/// Рендер: <see cref="MultiMeshInstance2D"/> с одним quad на гриб (размер
/// QuadBaseSize × per-instance scale). Это решает проблему «квадратного
/// гало», когда гало гриба обрезалось границей ячейки L8-текстуры.
///
/// Размер per-instance: hash-based в диапазоне [SizeMin..SizeMax] плюс
/// редкий «гигантский» бонус для разнообразия (≈4% грибов в 1.6× больше).
/// </summary>
public partial class MushroomField : Node2D
{
    [ExportGroup("Refs")]
    [Export] public MapGenerator Map;
    [Export] public RockField Rocks;
    [Export] public TileMapLayer SolidWalls;
    [Export] public Shader MushroomShader;

    [ExportGroup("Spawning")]
    /// <summary>Целевое количество грибов на карте. Реальное значение —
    /// результат per-region рассеяния, может слегка отличаться.</summary>
    [Export(PropertyHint.Range, "100,10000,50")] public int TargetCount = 2000;
    /// <summary>Минимум грибов в любой пещере (если пещера хоть какая-то).</summary>
    [Export(PropertyHint.Range, "0,20,1")] public int MinPerRegion = 3;
    /// <summary>Максимум грибов в одной пещере (защищает огромные открытые
    /// залы от превращения в грибной бор).</summary>
    [Export(PropertyHint.Range, "10,1000,5")] public int MaxPerRegion = 250;
    /// <summary>Коэффициент в формуле count = sqrt(N) × Coefficient.
    /// Меньшее значение → меньше грибов в больших пещерах.</summary>
    [Export(PropertyHint.Range, "0.5,8.0,0.1")] public float SqrtCoefficient = 2.5f;
    /// <summary>Минимальный размер пещеры (клеток), чтобы вообще быть
    /// учтённой. Меньшие — пропускаются.</summary>
    [Export(PropertyHint.Range, "0,200,1")] public int MinRegionForSpawn = 8;
    /// <summary>Доля грибов, что должна вырасти у стен (RockNeighbor).
    /// Остальные — свободно стоящие в центре пещеры. 1.0 = только у стен.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float WallAdjacentFraction = 0.6f;

    [ExportGroup("Visual")]
    /// <summary>Базовый размер quad-меша для гриба в пикселях. Big enough
    /// to fit the biggest halo without клиппинга.</summary>
    [Export(PropertyHint.Range, "64,512,8")] public float QuadBaseSize = 192f;
    [Export(PropertyHint.Range, "0.2,2.0,0.05")] public float SizeMin = 0.55f;
    [Export(PropertyHint.Range, "0.5,3.0,0.05")] public float SizeMax = 1.6f;
    /// <summary>Шанс «гигантского» гриба — даёт сильный бонус к размеру.
    /// 0.05 = ~5% грибов вырастают огромными.</summary>
    [Export(PropertyHint.Range, "0,0.3,0.005")] public float GiantChance = 0.04f;
    [Export(PropertyHint.Range, "1.5,4.0,0.05")] public float GiantBonusMultiplier = 1.7f;
    [Export(PropertyHint.Range, "-10,10,1")] public int VisualZIndex = 0;

    [Export] public ulong Seed = 0;

    /// <summary>Фиксированная палитра неоновых цветов (передаётся в шейдер
    /// per-instance через custom_data).</summary>
    private static readonly Color[] Palette = new Color[]
    {
        new Color(0.40f, 1.00f, 1.00f),  // cyan
        new Color(0.80f, 0.35f, 1.00f),  // purple
        new Color(1.00f, 0.30f, 0.75f),  // magenta
        new Color(0.65f, 1.00f, 0.30f),  // lime
        new Color(1.00f, 0.55f, 0.15f),  // orange
        new Color(1.00f, 0.40f, 0.55f),  // pink
    };

    private struct Mushroom
    {
        public Vector2 Pos;
        public byte ColorIdx;
        public float SizeFactor;     // 0.5..3.0
        public float Phase;          // 0..1
    }

    private Mushroom[] _mushrooms;
    private int _count;
    private int _w, _h;
    private int _tilePx = 128;
    private RandomNumberGenerator _rng;
    private MultiMesh _multimesh;
    private MultiMeshInstance2D _renderer;
    private bool _ready;

    public int Count => _count;

    public override void _Ready()
    {
        _rng = new RandomNumberGenerator();
        _rng.Randomize();
        if (Seed != 0) _rng.Seed = Seed;

        Node parent = GetParent();
        if (Map == null && parent != null) Map = parent.GetNodeOrNull<MapGenerator>("MapGenerator");
        if (Rocks == null && parent != null) Rocks = parent.GetNodeOrNull<RockField>("Rocks");
        if (SolidWalls == null && parent != null) SolidWalls = parent.GetNodeOrNull<TileMapLayer>("SolidWalls");
        if (MushroomShader == null) MushroomShader = ResourceLoader.Load<Shader>("res://mushroom.gdshader");

        if (Map == null)
        {
            GD.PushError("MushroomField: ссылка на MapGenerator не найдена.");
            return;
        }

        // Спавним после готовности Rocks — нужны открытые регионы.
        if (Rocks != null) Rocks.RocksGenerated += OnRocksReady;
        else Map.MapGenerated += OnRocksReady;
    }

    private void OnRocksReady()
    {
        _w = Map.Width;
        _h = Map.Height;
        if (_w <= 0 || _h <= 0) return;
        if (Map.TileSet != null) _tilePx = Map.TileSet.TileSize.X;

        _mushrooms = new Mushroom[TargetCount];

        ulong t0 = Time.GetTicksUsec();
        var regions = FindOpenRegions();
        ulong t1 = Time.GetTicksUsec();
        ScatterAcrossRegions(regions);
        ulong t2 = Time.GetTicksUsec();

        CreateRenderer();
        UpdateInstances();

        GD.Print($"MushroomField: flood-fill={Ms(t0,t1)} мс ({regions.Count} регионов), " +
                 $"scatter={Ms(t1,t2)} мс, грибов={_count}.");
        _ready = true;
    }

    private static long Ms(ulong a, ulong b) => (long)((b - a) / 1000UL);

    // ---- Region detection -----------------------------------------------

    /// <summary>Flood-fill всех открытых клеток. Возвращает список регионов
    /// (списки клеток). Учитывает только реально доступные клетки (не камень,
    /// не стена рамки).</summary>
    private List<List<Vector2I>> FindOpenRegions()
    {
        var regions = new List<List<Vector2I>>();
        var visited = new bool[_w * _h];
        var stack = new Stack<int>();

        for (int y = 0; y < _h; y++)
        {
            int rowBase = y * _w;
            for (int x = 0; x < _w; x++)
            {
                int idx = rowBase + x;
                if (visited[idx]) continue;
                if (IsBlocked(new Vector2I(x, y))) { visited[idx] = true; continue; }

                var region = new List<Vector2I>();
                stack.Push(idx);

                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    if (visited[cur]) continue;
                    visited[cur] = true;

                    int cx = cur % _w;
                    int cy = cur / _w;
                    if (IsBlocked(new Vector2I(cx, cy))) continue;
                    region.Add(new Vector2I(cx, cy));

                    if (cx > 0)       stack.Push(cur - 1);
                    if (cx < _w - 1)  stack.Push(cur + 1);
                    if (cy > 0)       stack.Push(cur - _w);
                    if (cy < _h - 1)  stack.Push(cur + _w);
                }

                if (region.Count >= MinRegionForSpawn) regions.Add(region);
            }
        }
        return regions;
    }

    private bool IsBlocked(Vector2I cell)
    {
        if (Rocks != null && Rocks.HasRock(cell)) return true;
        if (SolidWalls != null && SolidWalls.GetCellSourceId(cell) >= 0) return true;
        return false;
    }

    // ---- Per-region scatter ---------------------------------------------

    private void ScatterAcrossRegions(List<List<Vector2I>> regions)
    {
        // Считаем целевые кванты для каждой пещеры. Сумма обычно выходит
        // близко к TargetCount; небольшие отклонения допустимы.
        foreach (var region in regions)
        {
            if (_count >= _mushrooms.Length) break;

            int n = region.Count;
            int target = (int)(Mathf.Sqrt(n) * SqrtCoefficient);
            target = Mathf.Clamp(target, MinPerRegion, MaxPerRegion);
            target = Mathf.Min(target, _mushrooms.Length - _count);

            ScatterInRegion(region, target);
        }
    }

    private void ScatterInRegion(List<Vector2I> region, int count)
    {
        if (count <= 0 || region.Count == 0) return;

        int wallAdjacentTarget = (int)(count * Mathf.Clamp(WallAdjacentFraction, 0f, 1f));
        int placedWall = 0;
        int placedFree = 0;

        // Build a HashSet для быстрого contains-check, чтобы не сажать дважды.
        var occupied = new HashSet<int>();

        // Проход 1: пристенные грибы.
        int attempts = 0;
        int maxAttempts = wallAdjacentTarget * 30;
        while (placedWall < wallAdjacentTarget && attempts++ < maxAttempts)
        {
            var cell = region[_rng.RandiRange(0, region.Count - 1)];
            int key = cell.Y * _w + cell.X;
            if (occupied.Contains(key)) continue;
            if (!HasRockNeighbor(cell)) continue;
            PlaceMushroom(cell);
            occupied.Add(key);
            placedWall++;
        }

        // Проход 2: свободно стоящие грибы (в центре пещеры, без требования
        // соседства со стеной).
        int freeTarget = count - placedWall;
        attempts = 0;
        maxAttempts = freeTarget * 20;
        while (placedFree < freeTarget && attempts++ < maxAttempts)
        {
            var cell = region[_rng.RandiRange(0, region.Count - 1)];
            int key = cell.Y * _w + cell.X;
            if (occupied.Contains(key)) continue;
            PlaceMushroom(cell);
            occupied.Add(key);
            placedFree++;
        }
    }

    private bool HasRockNeighbor(Vector2I cell)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var c = new Vector2I(cell.X + dx, cell.Y + dy);
                if (Rocks != null && Rocks.HasRock(c)) return true;
                if (SolidWalls != null && SolidWalls.GetCellSourceId(c) >= 0) return true;
            }
        }
        return false;
    }

    private void PlaceMushroom(Vector2I cell)
    {
        if (_count >= _mushrooms.Length) return;

        // Позиция — центр клетки + лёгкое смещение для разнообразия.
        Vector2 pos = WorldGrid.CellToWorld(cell, _tilePx);
        pos += new Vector2(_rng.RandfRange(-_tilePx * 0.25f, _tilePx * 0.25f),
                           _rng.RandfRange(-_tilePx * 0.25f, _tilePx * 0.25f));

        float size = _rng.RandfRange(SizeMin, SizeMax);
        if (_rng.Randf() < GiantChance) size *= GiantBonusMultiplier;

        ref Mushroom m = ref _mushrooms[_count];
        m.Pos = pos;
        m.ColorIdx = (byte)_rng.RandiRange(0, Palette.Length - 1);
        m.SizeFactor = size;
        m.Phase = _rng.Randf();
        _count++;
    }

    // ---- Render ---------------------------------------------------------

    private void CreateRenderer()
    {
        _multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseCustomData = true,
            InstanceCount = _mushrooms.Length,
            VisibleInstanceCount = 0,
            Mesh = new QuadMesh { Size = new Vector2(QuadBaseSize, QuadBaseSize) },
        };
        _renderer = new MultiMeshInstance2D
        {
            Multimesh = _multimesh,
            ZIndex = VisualZIndex,
        };
        if (MushroomShader != null)
        {
            _renderer.Material = new ShaderMaterial { Shader = MushroomShader };
        }
        AddChild(_renderer);
    }

    private void UpdateInstances()
    {
        for (int i = 0; i < _count; i++)
        {
            ref Mushroom m = ref _mushrooms[i];
            // Per-instance scale: применяется к QuadBaseSize, итог = QuadBaseSize × SizeFactor.
            var transform = new Transform2D(0f, m.Pos);
            transform = transform.ScaledLocal(new Vector2(m.SizeFactor, m.SizeFactor));
            _multimesh.SetInstanceTransform2D(i, transform);

            Color c = Palette[m.ColorIdx];
            _multimesh.SetInstanceCustomData(i, new Color(c.R, c.G, c.B, m.Phase));
        }
        _multimesh.VisibleInstanceCount = _count;
    }
}
