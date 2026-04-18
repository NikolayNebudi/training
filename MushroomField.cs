using Godot;

/// <summary>
/// Стационарные неоновые грибы — служат как визуальная подсветка для пещер.
/// Спавнятся один раз при инициализации в свободных клетках с каменным
/// соседом (растут на стенах). Кластеризация через низкочастотный шум.
///
/// Storage: <c>byte[Width*Height]</c> где значение 0 = нет, 1..6 = индекс
/// цвета палитры. Один Sprite2D + аддитивный шейдер рендерит всё разом.
/// </summary>
public partial class MushroomField : Node2D
{
    [ExportGroup("Refs")]
    [Export] public MapGenerator Map;
    [Export] public RockField Rocks;
    [Export] public TileMapLayer SolidWalls;
    [Export] public Shader MushroomShader;

    [ExportGroup("Spawning")]
    /// <summary>Желаемое количество грибов на карте.</summary>
    [Export(PropertyHint.Range, "0,5000,10")] public int TargetCount = 1500;
    /// <summary>Кластеризация — низкая частота → большие «биомы грибов».
    /// Меньшее значение = реже разбросанные крупные пятна; большее = чаще,
    /// но мельче — даёт грибы во всех пещерах, без «мёртвых зон».</summary>
    [Export(PropertyHint.Range, "0.005,0.2,0.001")] public float ClusterFrequency = 0.06f;
    /// <summary>Порог шума для появления гриба. Выше = реже, кучнее.</summary>
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float ClusterThreshold = 0.20f;
    /// <summary>Какая доля TargetCount гарантированно расселяется
    /// «разбрасыванием» по любой стене любой пещеры (без шумового гейта).
    /// Это ловит большие открытые залы, в которые не попал ни один холм
    /// шума. 0 = только кластеры, 1 = только разброс.</summary>
    [Export(PropertyHint.Range, "0.0,1.0,0.05")] public float ScatterFraction = 0.35f;
    /// <summary>Сколько разных цветов в палитре (1..6).</summary>
    [Export(PropertyHint.Range, "1,6,1")] public int PaletteSize = 6;

    [ExportGroup("Visual")]
    [Export(PropertyHint.Range, "-10,10,1")] public int VisualZIndex = 0;

    [Export] public ulong Seed = 0;

    private byte[] _grid;
    private int _w, _h;
    public int Count { get; private set; }
    private int _tilePx = 128;
    private RandomNumberGenerator _rng;
    private Image _image;
    private ImageTexture _texture;
    private Sprite2D _visual;
    private bool _ready;

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
        Map.MapGenerated += OnMapReady;
    }

    private void OnMapReady()
    {
        _w = Map.Width;
        _h = Map.Height;
        if (_w <= 0 || _h <= 0) return;
        if (Map.TileSet != null) _tilePx = Map.TileSet.TileSize.X;

        _grid = new byte[_w * _h];

        ulong t0 = Time.GetTicksUsec();
        SpawnMushrooms();
        ulong t1 = Time.GetTicksUsec();

        CreateVisual();
        UpdateTexture();

        int placed = 0;
        for (int i = 0; i < _grid.Length; i++) if (_grid[i] > 0) placed++;
        Count = placed;
        GD.Print($"MushroomField: посеяно {placed} грибов за {(t1 - t0) / 1000} мс.");
        _ready = true;
    }

    private void SpawnMushrooms()
    {
        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = ClusterFrequency,
            Seed = (int)(_rng.Randi() & 0x7FFFFFFF),
        };

        int scatterTarget = (int)(TargetCount * Mathf.Clamp(ScatterFraction, 0f, 1f));
        int clusterTarget = TargetCount - scatterTarget;

        // Проход 1 — кластерный: даёт «биомы», концентрации в шумовых холмах.
        int placedCluster = SpawnPass(clusterTarget, useNoiseGate: true, noise);
        // Проход 2 — разброс: гарантирует представителей в каждой пещере,
        // даже там, куда не попал ни один холм шума (большие открытые залы).
        int placedScatter = SpawnPass(scatterTarget, useNoiseGate: false, noise);

        GD.Print($"MushroomField: кластеры={placedCluster}, разброс={placedScatter}.");
    }

    private int SpawnPass(int target, bool useNoiseGate, FastNoiseLite noise)
    {
        if (target <= 0) return 0;

        int placed = 0;
        int attempts = 0;
        int maxAttempts = target * 80;
        while (placed < target && attempts++ < maxAttempts)
        {
            int x = _rng.RandiRange(2, _w - 3);
            int y = _rng.RandiRange(2, _h - 3);
            int idx = y * _w + x;
            if (_grid[idx] != 0) continue;

            var cell = new Vector2I(x, y);

            if (Rocks != null && Rocks.HasRock(cell)) continue;
            if (SolidWalls != null && SolidWalls.GetCellSourceId(cell) >= 0) continue;

            if (!HasRockNeighbor(cell)) continue;

            if (useNoiseGate)
            {
                float n = (noise.GetNoise2D(x, y) + 1f) * 0.5f;
                if (n < ClusterThreshold) continue;
            }

            byte color = (byte)_rng.RandiRange(1, Mathf.Clamp(PaletteSize, 1, 6));
            _grid[idx] = color;
            placed++;
        }
        return placed;
    }

    private bool HasRockNeighbor(Vector2I cell)
    {
        if (Rocks == null && SolidWalls == null) return false;
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

    private void CreateVisual()
    {
        (_image, _texture) = WorldGrid.MakeL8Texture(_w, _h, _grid);

        _visual = new Sprite2D
        {
            Texture = _texture,
            Centered = false,
            Position = Vector2.Zero,
            Scale = new Vector2(_tilePx, _tilePx),
            // Nearest! Иначе цвет-индекс размажется и палитра поломается.
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            ZIndex = VisualZIndex,
        };

        if (MushroomShader != null)
        {
            var mat = new ShaderMaterial { Shader = MushroomShader };
            _visual.Material = mat;
        }
        AddChild(_visual);
    }

    private void UpdateTexture()
        => WorldGrid.UpdateL8Texture(_image, _texture, _w, _h, _grid);
}
