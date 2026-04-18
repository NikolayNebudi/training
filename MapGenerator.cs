using Godot;

public partial class MapGenerator : TileMapLayer
{
    [Signal] public delegate void MapGeneratedEventHandler();

    [ExportGroup("Map Settings")]
    [Export] public int Width = 800;
    [Export] public int Height = 800;
    [Export(PropertyHint.Range, "256,200000,256")] public int CellsPerFrame = 20000;
    [Export] public ulong Seed = 0;

    [ExportGroup("Floor (Cellular Automaton)")]
    [Export] public int SourceId = 0;
    [Export(PropertyHint.Range, "0,12,1")] public int CaIterations = 4;

    [ExportGroup("Solid walls")]
    [Export] public TileMapLayer SolidWalls;
    [Export] public int WallSourceId = 1;

    [ExportGroup("Player")]
    [Export] public Node2D Player;
    [Export(PropertyHint.Range, "0,200,1")] public int PlayerSafeRadius = 8;

    private FloorPainter _floor;

    public override void _Ready()
    {
        // Фолбэк: если редактор сохранил Node-экспорты в устаревшем формате
        // (NodePath вместо нового node-ref по unique_id), они приходят сюда как
        // null. Подбираем ноды по фиксированным относительным путям сцены.
        Node parent = GetParent();
        if (Player == null && parent != null) Player = parent.GetNodeOrNull<Node2D>("Player");
        if (SolidWalls == null && parent != null) SolidWalls = parent.GetNodeOrNull<TileMapLayer>("SolidWalls");

        GenerateMap();
    }

    public override void _Process(double _delta)
    {
        if (!SimMode.ShouldProcess) return;
        if (_floor is not { IsRunning: true }) return;

        _floor.Step(CellsPerFrame);
        if (!_floor.IsRunning) FinishGeneration();
    }

    public void GenerateMap()
    {
        ResetState();

        if (!ValidateDimensions()) return;
        if (!TryCollectFloorAtlas(out Vector2I[] floorCoords)) return;
        if (!TryDrawWallFrame()) return;

        // Клеточный автомат для пола: считаем целиком в массиве байтов,
        // затем красим порциями по CellsPerFrame.
        byte[] caGrid = BuildFloorGrid(floorCoords.Length);

        // Игрок появляется в центре сразу, не дожидаясь прорисовки.
        PlacePlayerAtMapCenter();

        // Headless-режим: пол не рисуется (TileMap-данные не нужны для
        // симуляции). Сразу финишируем — пусть подписчики берут данные
        // прямо сейчас. Defer делает это после _Ready всех соседей.
        if (SimMode.Headless)
        {
            CallDeferred(MethodName.FinishGeneration);
            return;
        }

        // И вокруг игрока мгновенно красим небольшое окно, чтобы не висеть в пустоте.
        PaintAreaImmediately(caGrid, floorCoords, Width / 2, Height / 2, PlayerSafeRadius);

        _floor = new FloorPainter(this, SourceId, Width, Height, floorCoords, caGrid);
        SetProcess(true);
    }

    private void ResetState()
    {
        _floor = null;
        SetProcess(false);
        Clear();
        SolidWalls?.Clear();
    }

    private bool ValidateDimensions()
    {
        if (Width > 0 && Height > 0) return true;
        GD.PushError($"MapGenerator: некорректные размеры карты {Width}x{Height}.");
        return false;
    }

    private bool TryCollectFloorAtlas(out Vector2I[] coords)
    {
        coords = System.Array.Empty<Vector2I>();

        if (TileSet == null || TileSet.GetSource(SourceId) is not TileSetAtlasSource atlas)
        {
            GD.PushError($"MapGenerator: SourceId={SourceId} не найден или не TileSetAtlasSource.");
            return false;
        }

        coords = CollectAtlasOrigins(atlas);
        if (coords.Length == 0)
        {
            GD.PushError($"MapGenerator: в источнике пола (id={SourceId}) нет тайлов.");
            return false;
        }
        return true;
    }

    private bool TryDrawWallFrame()
    {
        if (SolidWalls == null)
        {
            GD.PushError("MapGenerator: SolidWalls не назначен в инспекторе — стены не будут отрисованы.");
            return true;
        }

        if (SolidWalls.TileSet == null
            || SolidWalls.TileSet.GetSource(WallSourceId) is not TileSetAtlasSource wallAtlas)
        {
            GD.PushError($"MapGenerator: WallSourceId={WallSourceId} не найден на слое {SolidWalls.Name}.");
            return false;
        }

        Vector2I[] wallCoords = CollectAtlasOrigins(wallAtlas);
        if (wallCoords.Length == 0)
        {
            GD.PushError($"MapGenerator: в источнике стен (id={WallSourceId}) нет тайлов.");
            return false;
        }

        RandomNumberGenerator wallRng = CreateRng();
        Vector2I PickWall() => wallCoords[wallRng.RandiRange(0, wallCoords.Length - 1)];

        // Рамка снаружи игрового поля: x ∈ [-1; Width], y ∈ [-1; Height].
        for (int x = -1; x <= Width; x++)
        {
            SolidWalls.SetCell(new Vector2I(x, -1), WallSourceId, PickWall());
            SolidWalls.SetCell(new Vector2I(x, Height), WallSourceId, PickWall());
        }
        for (int y = 0; y < Height; y++)
        {
            SolidWalls.SetCell(new Vector2I(-1, y), WallSourceId, PickWall());
            SolidWalls.SetCell(new Vector2I(Width, y), WallSourceId, PickWall());
        }
        return true;
    }

    private void FinishGeneration()
    {
        SetProcess(false);
        EmitSignal(SignalName.MapGenerated);
        GD.Print("MapGenerator: генерация завершена.");
    }

    private void PlacePlayerAtMapCenter()
    {
        if (Player == null)
        {
            GD.PushError("MapGenerator: Player не назначен в инспекторе — спавн в центр карты не выполнен.");
            return;
        }

        Vector2I center = new Vector2I(Width / 2, Height / 2);
        Player.GlobalPosition = ToGlobal(MapToLocal(center));
    }

    private RandomNumberGenerator CreateRng()
    {
        var rng = new RandomNumberGenerator();
        if (Seed == 0) rng.Randomize();
        else rng.Seed = Seed;
        return rng;
    }

    private static Vector2I[] CollectAtlasOrigins(TileSetAtlasSource atlas)
    {
        int count = atlas.GetTilesCount();
        var origins = new Vector2I[count];
        for (int i = 0; i < count; i++) origins[i] = atlas.GetTileId(i);
        return origins;
    }

    /// <summary>
    /// Запускает клеточный автомат с правилом «большинства Мура» (3×3, включая саму
    /// клетку). Начальное состояние — равномерный случайный шум по индексам тайлов.
    /// На выходе — линейный <c>byte[]</c> размера Width*Height со значениями
    /// в диапазоне [0, variants).
    /// </summary>
    private byte[] BuildFloorGrid(int variants)
    {
        ulong startUsec = Time.GetTicksUsec();

        int w = Width;
        int h = Height;
        var rng = CreateRng();

        byte[] current = new byte[w * h];
        for (int i = 0; i < current.Length; i++)
            current[i] = (byte)rng.RandiRange(0, variants - 1);

        if (variants <= 1 || CaIterations <= 0) return current;

        byte[] next = new byte[w * h];
        var counts = new int[variants];

        for (int iter = 0; iter < CaIterations; iter++)
        {
            for (int y = 0; y < h; y++)
            {
                int yMin = y == 0 ? 0 : -1;
                int yMax = y == h - 1 ? 0 : 1;
                int rowBase = y * w;

                for (int x = 0; x < w; x++)
                {
                    int xMin = x == 0 ? 0 : -1;
                    int xMax = x == w - 1 ? 0 : 1;

                    for (int v = 0; v < variants; v++) counts[v] = 0;

                    for (int dy = yMin; dy <= yMax; dy++)
                    {
                        int rowOff = rowBase + dy * w;
                        for (int dx = xMin; dx <= xMax; dx++)
                            counts[current[rowOff + x + dx]]++;
                    }

                    int best = 0;
                    int bestCount = counts[0];
                    for (int v = 1; v < variants; v++)
                    {
                        if (counts[v] > bestCount)
                        {
                            bestCount = counts[v];
                            best = v;
                        }
                    }
                    next[rowBase + x] = (byte)best;
                }
            }
            (current, next) = (next, current);
        }

        ulong elapsedMs = (Time.GetTicksUsec() - startUsec) / 1000;
        GD.Print($"MapGenerator: CA {w}x{h}, итераций={CaIterations}, вариантов={variants}, время={elapsedMs} мс.");
        return current;
    }

    private void PaintAreaImmediately(byte[] grid, Vector2I[] floorCoords, int cx, int cy, int radius)
    {
        if (radius <= 0) return;

        int x0 = System.Math.Max(0, cx - radius);
        int y0 = System.Math.Max(0, cy - radius);
        int x1 = System.Math.Min(Width - 1, cx + radius);
        int y1 = System.Math.Min(Height - 1, cy + radius);

        for (int y = y0; y <= y1; y++)
        {
            int rowBase = y * Width;
            for (int x = x0; x <= x1; x++)
                SetCell(new Vector2I(x, y), SourceId, floorCoords[grid[rowBase + x]]);
        }
    }

    /// <summary>
    /// Пошаговая отрисовка пола из заранее посчитанной CA-сетки. Идёт «полосами»
    /// от центральной строки наружу, чтобы у игрока сразу появлялась земля под
    /// ногами и расширялась во все стороны.
    /// </summary>
    private sealed class FloorPainter
    {
        private readonly TileMapLayer _layer;
        private readonly int _sourceId;
        private readonly int _width;
        private readonly int _height;
        private readonly Vector2I[] _atlas;
        private readonly byte[] _grid;
        private readonly int _centerY;

        // Состояние обхода: текущая «полоса» (band), сторона (0 = верхняя, 1 = нижняя)
        // и позиция X внутри текущей строки.
        private int _band;
        private int _side;
        private int _x;

        public bool IsRunning { get; private set; } = true;

        public FloorPainter(TileMapLayer layer, int sourceId, int width, int height,
            Vector2I[] atlas, byte[] grid)
        {
            _layer = layer;
            _sourceId = sourceId;
            _width = width;
            _height = height;
            _atlas = atlas;
            _grid = grid;
            _centerY = height / 2;
            _band = 0;
            _side = 0;
            _x = 0;
        }

        public void Step(int budget)
        {
            while (budget > 0 && IsRunning)
            {
                int y = CurrentRow();
                if (y < 0 || y >= _height)
                {
                    if (!Advance()) { IsRunning = false; return; }
                    continue;
                }

                int rowBase = y * _width;
                int painted = 0;
                int chunk = System.Math.Min(budget, _width - _x);
                int xEnd = _x + chunk;
                for (int x = _x; x < xEnd; x++)
                {
                    _layer.SetCell(new Vector2I(x, y), _sourceId, _atlas[_grid[rowBase + x]]);
                    painted++;
                }
                _x = xEnd;
                budget -= painted;

                if (_x >= _width)
                {
                    _x = 0;
                    if (!Advance()) { IsRunning = false; return; }
                }
            }
        }

        private int CurrentRow() => _side == 0 ? _centerY - _band : _centerY + _band;

        // Переход к следующей строке/полосе. Возвращает false, когда красить нечего.
        private bool Advance()
        {
            if (_side == 0 && _band > 0)
            {
                _side = 1;
                if (CurrentRow() < _height) return true;
            }

            _side = 0;
            _band++;

            int top = _centerY - _band;
            int bot = _centerY + _band;
            return top >= 0 || bot < _height;
        }
    }
}
