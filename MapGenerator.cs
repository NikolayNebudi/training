using Godot;
using System;
using System.Collections.Generic;

public partial class MapGenerator : TileMapLayer
{
    [ExportGroup("Map Settings")]
    /// <summary>Размер карты в клетках тайлмапа.</summary>
    [Export] public int Height = 800;
    [Export] public int Width = 800;
    /// <summary>Сколько клеток фона выставлять за один кадр (больше — быстрее, но выше пиковая нагрузка на кадр).</summary>
    [Export] public int CellsPerFrame = 16_384;

    [ExportGroup("Tile Settings")]
    [Export] public int SourceId = 0;
    /// <summary>
    /// Размер одной клетки тайлмапа в пикселях. Должен совпадать с размером тайла в TileSet (и с Texture Region Size в атласе), иначе тайлы визуально накладываются.
    /// </summary>
    [Export] public Vector2I CellSizePixels = new Vector2I(128, 128);

    [ExportGroup("Player")]
    /// <summary>После генерации карты позиция будет в геометрическом центре сетки (в глобальных координатах).</summary>
    [Export] public CharacterBody2D Player;

    [ExportGroup("Solid walls")]
    /// <summary>Слой с неразрушимыми стенами по периметру (тот же TileSet и CellSize, коллизия на тайле стены).</summary>
    [Export] public TileMapLayer SolidWalls;
    [Export] public int WallSourceId = 1;
    [Export] public Vector2I WallAtlasCoords = new Vector2I(0, 0);

    private bool _generatingFloor;
    private int _genX;
    private int _genY;
    private List<Vector2I> _floorAtlasCoords;
    private RandomNumberGenerator _floorRng;

    public override void _Ready()
    {
        GenerateMap();
    }

    public override void _Process(double delta)
    {
        if (!_generatingFloor)
            return;

        int budget = CellsPerFrame;
        if (budget < 1)
            budget = 1;

        while (budget-- > 0)
        {
            if (_genY >= Height)
            {
                _generatingFloor = false;
                GenerateSolidWalls();
                SchedulePlacePlayerAtMapCenter();
                SetProcess(false);
                return;
            }

            int randomIndex = _floorRng.RandiRange(0, _floorAtlasCoords.Count - 1);
            Vector2I coords = _floorAtlasCoords[randomIndex];
            SetCell(new Vector2I(_genX, _genY), SourceId, coords);

            _genX++;
            if (_genX >= Width)
            {
                _genX = 0;
                _genY++;
            }
        }
    }

    /// <summary>
    /// Собирает координаты атласа для каждого тайла один раз (origin клетка), в т.ч. для тайлов размером больше 1×1.
    /// </summary>
    private static List<Vector2I> CollectAtlasTileOrigins(TileSetAtlasSource atlas)
    {
        var origins = new List<Vector2I>();
        Vector2I grid = atlas.GetAtlasGridSize();
        for (int y = 0; y < grid.Y; y++)
        {
            for (int x = 0; x < grid.X; x++)
            {
                Vector2I cell = new Vector2I(x, y);
                Vector2I origin = atlas.GetTileAtCoords(cell);
                if (origin.X < 0)
                    continue;
                if (origin != cell)
                    continue;
                origins.Add(origin);
            }
        }

        return origins;
    }

    public void GenerateMap()
    {
        _generatingFloor = false;
        SetProcess(false);

        Clear();
        if (SolidWalls != null)
            SolidWalls.Clear();

        if (TileSet == null)
        {
            GD.PushError("MapGenerator: TileSet не назначен на слой.");
            return;
        }

        if (TileSet.GetSource(SourceId) is not TileSetAtlasSource atlas)
        {
            GD.PushError($"MapGenerator: источник с id {SourceId} не TileSetAtlasSource или отсутствует.");
            return;
        }

        if (CellSizePixels.X <= 0 || CellSizePixels.Y <= 0)
        {
            GD.PushError("MapGenerator: CellSizePixels должен быть положительным.");
            return;
        }

        if (Width <= 0 || Height <= 0)
        {
            GD.PushError("MapGenerator: Width и Height должны быть больше нуля.");
            return;
        }

        TileSet.TileSize = CellSizePixels;

        List<Vector2I> atlasCoords = CollectAtlasTileOrigins(atlas);
        if (atlasCoords.Count == 0)
        {
            GD.PushError("MapGenerator: в атласе нет ни одного тайла для случайного выбора.");
            return;
        }

        _floorAtlasCoords = atlasCoords;
        _floorRng = new RandomNumberGenerator();
        _floorRng.Randomize();
        _genX = 0;
        _genY = 0;
        _generatingFloor = true;
        SetProcess(true);
    }

    /// <summary>Рамка из одного тайла по краю сетки Width×Height.</summary>
    private void GenerateSolidWalls()
    {
        if (SolidWalls == null)
            return;

        if (TileSet.GetSource(WallSourceId) is not TileSetAtlasSource)
        {
            GD.PushError(
                $"MapGenerator: источник стен с id {WallSourceId} отсутствует или не TileSetAtlasSource. Проверьте TileSet.");
            return;
        }

        SolidWalls.TileSet = TileSet;
        SolidWalls.CollisionEnabled = true;

        for (int x = 0; x < Width; x++)
        {
            SolidWalls.SetCell(new Vector2I(x, 0), WallSourceId, WallAtlasCoords);
            SolidWalls.SetCell(new Vector2I(x, Height - 1), WallSourceId, WallAtlasCoords);
        }

        for (int y = 0; y < Height; y++)
        {
            SolidWalls.SetCell(new Vector2I(0, y), WallSourceId, WallAtlasCoords);
            SolidWalls.SetCell(new Vector2I(Width - 1, y), WallSourceId, WallAtlasCoords);
        }
    }

    /// <summary>Позиция после физики кадра, чтобы не перетёрлась; геометрический центр сетки через MapToLocal.</summary>
    private void SchedulePlacePlayerAtMapCenter()
    {
        if (Player == null)
            return;

        Callable.From(() => PlacePlayerAtMapCenter()).CallDeferred();
    }

    private void PlacePlayerAtMapCenter()
    {
        if (Player == null || TileSet == null)
            return;

        Vector2 tilePx = new Vector2(TileSet.TileSize.X, TileSet.TileSize.Y);
        Vector2 topLeft = MapToLocal(Vector2I.Zero);
        Vector2 bottomRight = MapToLocal(new Vector2I(Width - 1, Height - 1)) + tilePx;
        Vector2 centerLocal = (topLeft + bottomRight) * 0.5f;
        Vector2 globalCenter = ToGlobal(centerLocal);

        Player.SetDeferred(Node2D.PropertyName.GlobalPosition, globalCenter);
    }
}
