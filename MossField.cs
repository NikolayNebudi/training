using Godot;
using System.Collections.Generic;

/// <summary>
/// Процедурный мох: cellular-automaton-симуляция плотности на сетке Map.Width × Map.Height,
/// плюс шейдерный рендер через 800×800 L8-текстуру плотности, растянутую на всю карту.
///
/// Шейдер дополнительно получает wetness-текстуру для биомных цветов и для
/// «мокрого» оверлея в очень сырых местах. Игрок «приминает» мох под ногами,
/// в сухих зонах мох высыхает, всё ограничено логистическим cap'ом.
/// </summary>
public partial class MossField : Node2D
{
    [Signal] public delegate void MossUpdatedEventHandler();

    [ExportGroup("Refs")]
    [Export] public MapGenerator Map;
    [Export] public RockField Rocks;
    [Export] public TileMapLayer SolidWalls;
    [Export] public Node2D Player;
    [Export] public Shader MossShader;
    [Export] public Shader WetnessShader;
    [Export] public Shader CorrosionShader;

    [ExportGroup("Wetness map")]
    [Export] public float WetnessNoiseFrequency = 0.012f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WetnessNearRockBonus = 0.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WetnessBaseLevel = 0.15f;

    [ExportGroup("Growth (post-init evolution)")]
    [Export(PropertyHint.Range, "0.05,10.0,0.05")] public float TickInterval = 2.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MaxCoverage = 0.18f;
    [Export(PropertyHint.Range, "0,50,0.1")] public float GrowthRate = 0.8f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float SpreadChance = 0.005f;
    [Export(PropertyHint.Range, "0,30,0.5")] public float DryDecayRate = 6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DrynessThreshold = 0.32f;

    [ExportGroup("Initial seeding (noise-based)")]
    /// <summary>Низкочастотный шум — определяет «биомы» мха (большие пятна).</summary>
    [Export] public float SeedNoiseFrequency = 0.030f;
    /// <summary>Высокочастотный шум — модулирует плотность внутри пятна.</summary>
    [Export] public float SeedDensityNoiseFrequency = 0.10f;
    /// <summary>Минимальная влажность для появления мха при стартовом посеве.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float SeedWetMin = 0.45f;
    /// <summary>Порог шума для самых сырых клеток (низкий = больше мха).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float SeedThresholdWet = 0.45f;
    /// <summary>Порог шума для умеренно сырых клеток (выше — мох реже).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float SeedThresholdDry = 0.78f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RockDestroyedSeedChance = 0.30f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RockDestroyedSeedWetMin = 0.45f;
    [Export] public ulong Seed = 0;

    [ExportGroup("Weathering (rock corrosion + floor wear)")]
    [Export] public float CorrosionNoiseFrequency = 0.022f;
    [Export] public float CorrosionDetailNoiseFrequency = 0.10f;
    /// <summary>Порог появления коррозии на КАМНЕ. Ниже = больше выветренных камней.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float CorrosionThreshold = 0.55f;
    /// <summary>Порог появления износа на ПОЛУ. Обычно ниже камневого, чтобы
    /// пол был неоднороден на бо́льшей площади.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float FloorWearThreshold = 0.15f;

    [ExportGroup("Trample (player walking on moss)")]
    /// <summary>Скорость «приминания» мха под ногами, ед. плотности в секунду.</summary>
    [Export(PropertyHint.Range, "0,300,1")] public float TrampleRate = 80f;

    [ExportGroup("Visual")]
    /// <summary>z_index для слоя мха. «Мокрый» слой кладётся на VisualZIndex - 1.</summary>
    [Export(PropertyHint.Range, "-10,10,1")] public int VisualZIndex = -1;

    private byte[] _density;
    private byte[] _wetness;
    private byte[] _obstacle;    // 255 = камень/стена (возвышение), 0 = пол
    private byte[] _corrosion;   // 0..255 коррозия камня (0 везде на полу)
    private int _w, _h;
    private int _capCells;
    private int _activeCells;
    private float _tickAccumulator;
    private bool _textureDirty;

    private RandomNumberGenerator _rng;
    private Image _densityImage;
    private ImageTexture _densityTexture;
    private Image _wetnessImage;
    private ImageTexture _wetnessTexture;
    private Image _obstacleImage;
    private ImageTexture _obstacleTexture;
    private bool _obstacleDirty;
    private Image _corrosionImage;
    private ImageTexture _corrosionTexture;
    private Sprite2D _visual;
    private Sprite2D _wetnessVisual;
    private Sprite2D _corrosionVisual;
    private ShaderMaterial _shaderMaterial;
    private bool _ready;
    private int _tilePx = 128;

    public override void _Ready()
    {
        _rng = new RandomNumberGenerator();
        _rng.Randomize();
        if (Seed != 0) _rng.Seed = Seed;

        Node parent = GetParent();
        if (Map == null && parent != null) Map = parent.GetNodeOrNull<MapGenerator>("MapGenerator");
        if (Rocks == null && parent != null) Rocks = parent.GetNodeOrNull<RockField>("Rocks");
        if (SolidWalls == null && parent != null) SolidWalls = parent.GetNodeOrNull<TileMapLayer>("SolidWalls");
        if (Player == null && parent != null) Player = parent.GetNodeOrNull<Node2D>("Player");
        if (MossShader == null) MossShader = ResourceLoader.Load<Shader>("res://moss.gdshader");
        if (WetnessShader == null) WetnessShader = ResourceLoader.Load<Shader>("res://wetness.gdshader");
        if (CorrosionShader == null) CorrosionShader = ResourceLoader.Load<Shader>("res://corrosion.gdshader");

        if (Map == null)
        {
            GD.PushError("MossField: ссылка на MapGenerator не найдена.");
            return;
        }
        Map.MapGenerated += OnMapReady;
        if (Rocks != null) Rocks.RockDestroyed += OnRockDestroyed;

        SetProcess(false);
    }

    private void OnMapReady()
    {
        _w = Map.Width;
        _h = Map.Height;
        if (_w <= 0 || _h <= 0) return;

        if (Map.TileSet != null) _tilePx = Map.TileSet.TileSize.X;

        _density = new byte[_w * _h];
        _wetness = new byte[_w * _h];
        _obstacle = new byte[_w * _h];
        _corrosion = new byte[_w * _h];
        _capCells = (int)(_w * _h * MaxCoverage);

        ulong t0 = Time.GetTicksUsec();
        BuildWetnessMap();
        BuildObstacleMap();
        BuildCorrosionMap();
        ulong t1 = Time.GetTicksUsec();
        NoiseSeedMoss();
        ulong t2 = Time.GetTicksUsec();

        CreateVisual();
        UpdateDensityTexture();
        _textureDirty = false;

        GD.Print($"MossField: maps={Ms(t0,t1)} мс, noise-seed={Ms(t1,t2)} мс, " +
                 $"мшистых клеток={_activeCells}, коррозия={CountNonZero(_corrosion)} клеток.");

        _ready = true;
        SetProcess(true);
    }

    private static int CountNonZero(byte[] arr)
    {
        int n = 0;
        for (int i = 0; i < arr.Length; i++) if (arr[i] > 0) n++;
        return n;
    }

    public override void _Process(double delta)
    {
        if (!_ready) return;

        ApplyTrample((float)delta);

        _tickAccumulator += (float)delta;
        if (_tickAccumulator >= TickInterval)
        {
            _tickAccumulator = 0f;
            Tick();
            _textureDirty = true;
            EmitSignal(SignalName.MossUpdated);
        }

        if (_textureDirty)
        {
            UpdateDensityTexture();
            _textureDirty = false;
        }

        if (_obstacleDirty)
        {
            UpdateObstacleTexture();
            _obstacleDirty = false;
        }
    }

    private void UpdateObstacleTexture()
        => WorldGrid.UpdateL8Texture(_obstacleImage, _obstacleTexture, _w, _h, _obstacle);

    // ---- Public API -----------------------------------------------------

    public void SeedAt(Vector2I cell, byte density)
    {
        if (!_ready) return;
        if (cell.X < 0 || cell.X >= _w || cell.Y < 0 || cell.Y >= _h) return;
        int idx = cell.Y * _w + cell.X;
        if (!CanHaveMoss(cell)) return;
        if (_density[idx] >= density) return;
        _density[idx] = density;
        _activeCells++;
        _textureDirty = true;
    }

    public byte GetDensity(Vector2I cell)
    {
        if (cell.X < 0 || cell.X >= _w || cell.Y < 0 || cell.Y >= _h) return 0;
        return _density[cell.Y * _w + cell.X];
    }

    /// <summary>Текущее число клеток с мхом (для статистики).</summary>
    public int ActiveCells => _activeCells;
    public int TotalCells => _density?.Length ?? 0;

    /// <summary>Снимает <paramref name="amount"/> плотности мха в клетке.
    /// Возвращает реально съеденное количество (может быть меньше, если мха
    /// меньше). Используется светлячками при питании.</summary>
    public int Eat(Vector2I cell, int amount)
    {
        if (cell.X < 0 || cell.X >= _w || cell.Y < 0 || cell.Y >= _h) return 0;
        int idx = cell.Y * _w + cell.X;
        if (_density[idx] == 0 || amount <= 0) return 0;

        int eaten = Mathf.Min(_density[idx], amount);
        int newD = _density[idx] - eaten;
        _density[idx] = (byte)newD;
        if (newD == 0) _activeCells--;
        _textureDirty = true;
        return eaten;
    }

    // ---- Wetness map ----------------------------------------------------

    private void BuildWetnessMap()
    {
        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = WetnessNoiseFrequency,
            Seed = (int)(_rng.Randi() & 0x7FFFFFFF),
        };

        for (int y = 0; y < _h; y++)
        {
            int rowBase = y * _w;
            for (int x = 0; x < _w; x++)
            {
                float n = noise.GetNoise2D(x, y);
                float wet = WetnessBaseLevel + (n + 1f) * 0.5f * (1f - WetnessBaseLevel);

                int rockNeighbours = CountRockNeighbours(x, y);
                wet += rockNeighbours * (WetnessNearRockBonus / 8f);

                wet = Mathf.Clamp(wet, 0f, 1f);
                _wetness[rowBase + x] = (byte)(wet * 255f);
            }
        }
    }

    private void BuildObstacleMap()
    {
        for (int y = 0; y < _h; y++)
        {
            int rowBase = y * _w;
            for (int x = 0; x < _w; x++)
            {
                var cell = new Vector2I(x, y);
                bool isObstacle = false;
                if (Rocks != null && Rocks.HasRock(cell)) isObstacle = true;
                if (SolidWalls != null && SolidWalls.GetCellSourceId(cell) >= 0) isObstacle = true;
                _obstacle[rowBase + x] = isObstacle ? (byte)255 : (byte)0;
            }
        }
    }

    private int CountRockNeighbours(int x, int y)
    {
        if (Rocks == null) return 0;
        int count = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = y + dy;
            if (ny < 0 || ny >= _h) continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx;
                if (nx < 0 || nx >= _w) continue;
                if (Rocks.HasRock(new Vector2I(nx, ny))) count++;
            }
        }
        return count;
    }

    // ---- Seeding --------------------------------------------------------

    /// <summary>
    /// Стартовый посев мха через два слоя шума:
    ///   - Низкочастотный шум определяет «биомы» (большие пятна).
    ///   - Высокочастотный модулирует плотность внутри пятна.
    /// Порог адаптируется по влажности: в сырых клетках мох появляется при
    /// низком значении шума, в сухих — только в пиках. Это даёт уже готовые
    /// органичные кусты мха при старте, без необходимости «ловить рост на глазах».
    /// </summary>
    private void NoiseSeedMoss()
    {
        var seedNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = SeedNoiseFrequency,
            Seed = (int)(_rng.Randi() & 0x7FFFFFFF),
        };
        var densityNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = SeedDensityNoiseFrequency,
            Seed = (int)(_rng.Randi() & 0x7FFFFFFF),
        };

        for (int y = 0; y < _h; y++)
        {
            int rowBase = y * _w;
            for (int x = 0; x < _w; x++)
            {
                int idx = rowBase + x;
                float wet = _wetness[idx] / 255f;
                if (wet < SeedWetMin) continue;

                var cell = new Vector2I(x, y);
                if (!CanHaveMoss(cell)) continue;

                float n = (seedNoise.GetNoise2D(x, y) + 1f) * 0.5f;     // 0..1
                // Чем сырее — тем ниже порог → мха больше.
                float wetT = Mathf.InverseLerp(SeedWetMin, 1f, wet);
                float threshold = Mathf.Lerp(SeedThresholdDry, SeedThresholdWet, wetT);
                if (n < threshold) continue;

                // Глубина в шумовом «холме» определяет базовую плотность.
                float depth = (n - threshold) / Mathf.Max(0.01f, 1f - threshold);
                float dn = (densityNoise.GetNoise2D(x, y) + 1f) * 0.5f;
                float densityFactor = Mathf.Clamp(depth * 0.6f + dn * 0.5f, 0f, 1f);
                int density = 70 + (int)(densityFactor * 175f);

                _density[idx] = (byte)density;
                _activeCells++;
            }
        }
    }

    /// <summary>
    /// Карта «выветривания» поверхностей. Хранит интенсивность 0..255 для всех
    /// клеток (и пола, и камня) — шейдер уже сам ветвится по obstacle_texture
    /// и применяет соответствующий вид (ржавчина у камня, дёрн/грязь у пола).
    /// На стенах рамки — всегда 0 (рамка не выветривается).
    /// </summary>
    private void BuildCorrosionMap()
    {
        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = CorrosionNoiseFrequency,
            Seed = (int)(_rng.Randi() & 0x7FFFFFFF),
        };
        var detail = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = CorrosionDetailNoiseFrequency,
            Seed = (int)(_rng.Randi() & 0x7FFFFFFF),
        };

        for (int y = 0; y < _h; y++)
        {
            int rowBase = y * _w;
            for (int x = 0; x < _w; x++)
            {
                int idx = rowBase + x;
                var cell = new Vector2I(x, y);

                // Стены рамки — без выветривания.
                if (SolidWalls != null && SolidWalls.GetCellSourceId(cell) >= 0)
                {
                    _corrosion[idx] = 0;
                    continue;
                }

                bool isRock = Rocks != null && Rocks.HasRock(cell);
                float threshold = isRock ? CorrosionThreshold : FloorWearThreshold;

                float n1 = (noise.GetNoise2D(x, y) + 1f) * 0.5f;
                float n2 = (detail.GetNoise2D(x, y) + 1f) * 0.5f;
                float combined = n1 * 0.7f + n2 * 0.3f;

                if (combined < threshold)
                {
                    _corrosion[idx] = 0;
                }
                else
                {
                    float t = (combined - threshold) / Mathf.Max(0.01f, 1f - threshold);
                    _corrosion[idx] = (byte)(Mathf.Clamp(t, 0f, 1f) * 255f);
                }
            }
        }
    }

    private void OnRockDestroyed(Vector2I cell)
    {
        if (!_ready) return;
        int idx = cell.Y * _w + cell.X;
        if (idx < 0 || idx >= _density.Length) return;

        // Камень исчез → клетка больше не «возвышение». Шейдер должен
        // перекрасить мох тут как «низину».
        if (_obstacle[idx] != 0)
        {
            _obstacle[idx] = 0;
            _obstacleDirty = true;
        }

        float wet = _wetness[idx] / 255f;
        if (wet < RockDestroyedSeedWetMin) return;
        if (_rng.Randf() > RockDestroyedSeedChance) return;

        if (_density[idx] == 0) _activeCells++;
        _density[idx] = (byte)Mathf.Max(_density[idx], _rng.RandiRange(30, 60));
        _textureDirty = true;
    }

    // ---- Tick (CA growth + spread + decay) ------------------------------

    private void Tick()
    {
        float coverage = _capCells > 0 ? (float)_activeCells / _capCells : 1f;
        float coverageFactor = Mathf.Clamp(1f - coverage, 0f, 1f);

        // Инкрементально обновляем _activeCells вместо полного rescan'а
        // в конце тика (640k байт каждые 2.5 сек — лишнее).
        int activeDelta = 0;

        for (int idx = 0; idx < _density.Length; idx++)
        {
            int d = _density[idx];
            if (d == 0) continue;

            float w = _wetness[idx] / 255f;

            if (w < DrynessThreshold)
            {
                float dryAmount = (DrynessThreshold - w) / Mathf.Max(0.01f, DrynessThreshold);
                d -= (int)(DryDecayRate * dryAmount);
            }
            else
            {
                d += (int)(GrowthRate * w * coverageFactor);
            }

            d = Mathf.Clamp(d, 0, 255);
            _density[idx] = (byte)d;
            if (d == 0)
            {
                activeDelta--;     // клетка высохла полностью
                continue;
            }

            if (_rng.Randf() < SpreadChance * w * coverageFactor)
            {
                int x = idx % _w;
                int y = idx / _w;

                int dir = _rng.RandiRange(0, 3);
                int nx = x + (dir == 0 ? 1 : dir == 1 ? -1 : 0);
                int ny = y + (dir == 2 ? 1 : dir == 3 ? -1 : 0);

                if (nx >= 0 && nx < _w && ny >= 0 && ny < _h)
                {
                    int nidx = ny * _w + nx;
                    if (_density[nidx] == 0)
                    {
                        var ncell = new Vector2I(nx, ny);
                        if (CanHaveMoss(ncell))
                        {
                            float nw = _wetness[nidx] / 255f;
                            if (nw > DrynessThreshold * 0.5f)
                            {
                                _density[nidx] = (byte)_rng.RandiRange(15, 35);
                                activeDelta++;     // распространились в новую клетку
                            }
                        }
                    }
                }
            }
        }

        _activeCells += activeDelta;
        if (_activeCells < 0) _activeCells = 0;
    }

    private bool CanHaveMoss(Vector2I cell)
    {
        if (SolidWalls != null && SolidWalls.GetCellSourceId(cell) >= 0) return false;
        if (Rocks == null || !Rocks.HasRock(cell)) return true;

        int x = cell.X, y = cell.Y;
        if (x > 0     && !Rocks.HasRock(new Vector2I(x - 1, y))) return true;
        if (x < _w-1  && !Rocks.HasRock(new Vector2I(x + 1, y))) return true;
        if (y > 0     && !Rocks.HasRock(new Vector2I(x, y - 1))) return true;
        if (y < _h-1  && !Rocks.HasRock(new Vector2I(x, y + 1))) return true;
        return false;
    }

    // ---- Trample under player ------------------------------------------

    private void ApplyTrample(float delta)
    {
        if (Player == null || TrampleRate <= 0f) return;

        Vector2 local = ToLocal(Player.GlobalPosition);
        int px = (int)(local.X / _tilePx);
        int py = (int)(local.Y / _tilePx);
        if (px < 0 || px >= _w || py < 0 || py >= _h) return;

        int idx = py * _w + px;
        if (_density[idx] == 0) return;

        int reduce = (int)(TrampleRate * delta);
        if (reduce <= 0) return;

        int newD = Mathf.Max(0, _density[idx] - reduce);
        if (newD == _density[idx]) return;

        _density[idx] = (byte)newD;
        if (newD == 0) _activeCells--;
        _textureDirty = true;
    }

    // ---- Visual ---------------------------------------------------------

    private void CreateVisual()
    {
        (_densityImage,   _densityTexture)   = WorldGrid.MakeL8Texture(_w, _h, _density);
        (_wetnessImage,   _wetnessTexture)   = WorldGrid.MakeL8Texture(_w, _h, _wetness);
        (_obstacleImage,  _obstacleTexture)  = WorldGrid.MakeL8Texture(_w, _h, _obstacle);
        (_corrosionImage, _corrosionTexture) = WorldGrid.MakeL8Texture(_w, _h, _corrosion);

        // Слой 1a: «коррозия камня» (blend_mul). Рендерится ПОД wetness и мхом,
        // но ПОВЕРХ Rocks — выветренные камни смотрятся темнее/желтее.
        _corrosionVisual = new Sprite2D
        {
            Texture = _corrosionTexture,
            Centered = false,
            Position = Vector2.Zero,
            Scale = new Vector2(_tilePx, _tilePx),
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
            ZIndex = VisualZIndex - 1,
        };
        if (CorrosionShader != null)
        {
            var corrMat = new ShaderMaterial { Shader = CorrosionShader };
            corrMat.SetShaderParameter("obstacle_texture", _obstacleTexture);
            _corrosionVisual.Material = corrMat;
        }
        AddChild(_corrosionVisual);

        // Слой 1b: «мокрая поверхность» (blend_mul). Лежит ПОД мхом, ПОВЕРХ
        // коррозии и rocks. Через умножение затемняет/тонит то, что снизу.
        _wetnessVisual = new Sprite2D
        {
            Texture = _wetnessTexture,
            Centered = false,
            Position = Vector2.Zero,
            Scale = new Vector2(_tilePx, _tilePx),
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
            ZIndex = VisualZIndex - 1,
        };
        if (WetnessShader != null)
        {
            var wetMat = new ShaderMaterial { Shader = WetnessShader };
            _wetnessVisual.Material = wetMat;
        }
        AddChild(_wetnessVisual);

        // Слой 2: сам мох (alpha blend). Поверх мокрой поверхности.
        _visual = new Sprite2D
        {
            Texture = _densityTexture,
            Centered = false,
            Position = Vector2.Zero,
            Scale = new Vector2(_tilePx, _tilePx),
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
            ZIndex = VisualZIndex,
        };
        if (MossShader != null)
        {
            _shaderMaterial = new ShaderMaterial { Shader = MossShader };
            _shaderMaterial.SetShaderParameter("wetness_texture", _wetnessTexture);
            _shaderMaterial.SetShaderParameter("obstacle_texture", _obstacleTexture);
            _visual.Material = _shaderMaterial;
        }
        AddChild(_visual);
    }

    private void UpdateDensityTexture()
        => WorldGrid.UpdateL8Texture(_densityImage, _densityTexture, _w, _h, _density);

    private static long Ms(ulong a, ulong b) => (long)((b - a) / 1000UL);
}
