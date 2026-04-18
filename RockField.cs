using Godot;
using System.Collections.Generic;

/// <summary>
/// Слой разрушаемого камня. Генерация — гибридный пайплайн:
///   шум (FastNoiseLite, 2 октавы) → клеточный автомат → flood fill регионов
///   → A* тоннели между регионами → зачистка спавна → покраска по кадрам.
/// Хранит HP каждой клетки в плоском <c>byte[]</c>: <c>0</c> — камня нет,
/// <c>1..255</c> — текущее здоровье. Урон, добивание и сигналы — через
/// <see cref="Damage"/>, <see cref="HasRock"/>, <see cref="GetHp"/>.
/// </summary>
public partial class RockField : TileMapLayer
{
    [Signal] public delegate void RockDestroyedEventHandler(Vector2I cell);
    [Signal] public delegate void RockDamagedEventHandler(Vector2I cell, int hpLeft);
    [Signal] public delegate void RocksGeneratedEventHandler();

    [ExportGroup("Refs")]
    [Export] public MapGenerator Map;
    [Export] public Node2D Player;

    [ExportGroup("Tile")]
    [Export] public int SourceId = 2;
    [Export] public Vector2I AtlasCoords = new Vector2I(0, 0);

    [ExportGroup("Generation - Seed")]
    [Export] public ulong Seed = 0;

    [ExportGroup("Generation - Noise")]
    [Export] public float NoiseFrequencyLow = 0.012f;
    [Export] public float NoiseFrequencyHigh = 0.06f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float NoiseThreshold = 0.40f;
    [Export(PropertyHint.Range, "0.0,1.0,0.01")] public float NoiseLowWeight = 0.65f;

    [ExportGroup("Generation - Cellular Automaton")]
    [Export(PropertyHint.Range, "0,12,1")] public int CaIterations = 3;
    [Export(PropertyHint.Range, "1,9,1")] public int CaWallNeighborMin = 5;

    [ExportGroup("Generation - Perlin caves (additional pass)")]
    /// <summary>Доля камня, которую дополнительно вырубим Perlin-проходом.
    /// 0 = никаких дополнительных пещер, 0.20 = ~20% оставшегося камня
    /// превращается в полости. Формы органичные, не «пузырьки» CA.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float PerlinCaveDensity = 0.15f;
    /// <summary>Очень низкая частота → большие связные пещеры.</summary>
    [Export(PropertyHint.Range, "0.001,0.05,0.001")] public float PerlinCaveFrequency = 0.005f;

    [ExportGroup("Generation - Connectivity")]
    /// <summary>Регионы меньше этого размера заливаются обратно камнем (визуальный мусор).</summary>
    [Export(PropertyHint.Range, "0,5000,1")] public int MinRegionSize = 50;
    /// <summary>Тоннели прокладываются ТОЛЬКО к регионам не меньше этого размера.
    /// Регионы между MinRegionSize и этим значением остаются как изолированные
    /// «секретные карманы» — их надо находить бурением.</summary>
    [Export(PropertyHint.Range, "0,5000,1")] public int MinTunnelRegionSize = 120;
    /// <summary>Если A* путь длиннее этого числа клеток — тоннель не строится.
    /// Регион остаётся изолированным.</summary>
    [Export(PropertyHint.Range, "0,2000,1")] public int MaxTunnelLength = 200;
    /// <summary>Минимальный радиус тоннеля (1 = 3 клетки шириной).</summary>
    [Export(PropertyHint.Range, "0,5,1")] public int TunnelMinRadius = 1;
    /// <summary>Максимальный радиус тоннеля (2 = 5 клеток шириной).</summary>
    [Export(PropertyHint.Range, "0,5,1")] public int TunnelMaxRadius = 2;
    /// <summary>Сила «волнистости» тоннеля в клетках. 0 = прямой A*-путь.</summary>
    [Export(PropertyHint.Range, "0,8,0.5")] public float TunnelMeanderStrength = 3.0f;
    /// <summary>Частота шума для извилистости. Меньше = плавнее, реже волны.</summary>
    [Export(PropertyHint.Range, "0.005,0.2,0.001")] public float TunnelMeanderFrequency = 0.04f;
    /// <summary>Шанс на каждом шаге сделать «зал» (расширение).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float TunnelChamberChance = 0.06f;
    /// <summary>Радиус «зала» (в дополнение к базовому радиусу).</summary>
    [Export(PropertyHint.Range, "1,8,1")] public int TunnelChamberExtraRadius = 3;
    [Export(PropertyHint.Range, "0,80,1")] public int PlayerClearingRadius = 5;

    [ExportGroup("Health")]
    [Export(PropertyHint.Range, "1,255,1")] public int RockMaxHp = 100;

    [ExportGroup("Audio")]
    [Export] public AudioStreamPlayer2D DestroyAudio;
    [Export(PropertyHint.Range, "0.5,2.0,0.01")] public float DestroyPitchMin = 0.92f;
    [Export(PropertyHint.Range, "0.5,2.0,0.01")] public float DestroyPitchMax = 1.08f;

    [ExportGroup("Painting")]
    [Export(PropertyHint.Range, "1024,500000,1024")] public int CellsPerFrame = 30000;

    private byte[] _hp;
    private int _w;
    private int _h;

    private int _paintIdx;
    private bool _painting;

    // Кэш для CountRocks(). StatsPanel дёргает каждые 0.2с — без кэша
    // линейный скан 640k байт делается заново каждый раз. Инвалидация
    // через Damage() (минус 1 за каждое разрушение) и через сигнал
    // RocksGenerated (полный пересчёт после генерации).
    private int _rockCountCache = -1;

    private RandomNumberGenerator _audioRng;

    public override void _Ready()
    {
        _audioRng = new RandomNumberGenerator();
        _audioRng.Randomize();

        Node parent = GetParent();
        if (Map == null && parent != null) Map = parent.GetNodeOrNull<MapGenerator>("MapGenerator");
        if (Player == null && parent != null) Player = parent.GetNodeOrNull<Node2D>("Player");
        if (DestroyAudio == null) DestroyAudio = GetNodeOrNull<AudioStreamPlayer2D>("DestroyAudio");

        if (Map == null)
        {
            GD.PushError("RockField: ссылка на MapGenerator не найдена.");
            return;
        }
        Map.MapGenerated += GenerateRocks;
    }

    public override void _Process(double _delta)
    {
        if (!SimMode.ShouldProcess) return;
        if (!_painting) return;

        int total = _w * _h;
        int budget = CellsPerFrame;
        while (budget-- > 0 && _paintIdx < total)
        {
            if (_hp[_paintIdx] > 0)
            {
                int x = _paintIdx % _w;
                int y = _paintIdx / _w;
                SetCell(new Vector2I(x, y), SourceId, AtlasCoords);
            }
            _paintIdx++;
        }

        if (_paintIdx >= total)
        {
            _painting = false;
            EmitSignal(SignalName.RocksGenerated);
            GD.Print("RockField: камень отрисован.");
        }
    }

    // ---- Public API -----------------------------------------------------

    public bool InBounds(Vector2I cell)
        => cell.X >= 0 && cell.X < _w && cell.Y >= 0 && cell.Y < _h;

    public int Width => _w;
    public int Height => _h;
    public int TotalCells => (_hp?.Length) ?? 0;

    /// <summary>Текущее количество каменных клеток. Кэшируется и
    /// инкрементально обновляется на разрушении; полный пересчёт делается
    /// только лениво при первом вызове после генерации.</summary>
    public int CountRocks()
    {
        if (_hp == null) return 0;
        if (_rockCountCache >= 0) return _rockCountCache;
        int n = 0;
        for (int i = 0; i < _hp.Length; i++) if (_hp[i] > 0) n++;
        _rockCountCache = n;
        return n;
    }

    public bool HasRock(Vector2I cell)
        => InBounds(cell) && _hp[cell.Y * _w + cell.X] > 0;

    public int GetHp(Vector2I cell)
        => InBounds(cell) ? _hp[cell.Y * _w + cell.X] : 0;

    public int GetMaxHp() => RockMaxHp;

    /// <summary>
    /// Наносит урон камню в клетке. Возвращает true, если камень разрушен.
    /// При разрушении тайл удаляется, эмитится <see cref="RockDestroyed"/>;
    /// иначе эмитится <see cref="RockDamaged"/>.
    /// </summary>
    public bool Damage(Vector2I cell, int amount)
    {
        if (!HasRock(cell) || amount <= 0) return false;

        int idx = cell.Y * _w + cell.X;
        int hp = _hp[idx] - amount;

        if (hp <= 0)
        {
            _hp[idx] = 0;
            if (_rockCountCache > 0) _rockCountCache--;
            EraseCell(cell);
            PlayDestroyAudio(cell);
            EmitSignal(SignalName.RockDestroyed, cell);
            return true;
        }

        _hp[idx] = (byte)hp;
        EmitSignal(SignalName.RockDamaged, cell, hp);
        return false;
    }

    private void PlayDestroyAudio(Vector2I cell)
    {
        if (DestroyAudio == null || DestroyAudio.Stream == null) return;
        DestroyAudio.GlobalPosition = ToGlobal(MapToLocal(cell));
        DestroyAudio.PitchScale = _audioRng.RandfRange(DestroyPitchMin, DestroyPitchMax);
        DestroyAudio.Play();
    }

    // ---- Generation pipeline -------------------------------------------

    public void GenerateRocks()
    {
        Clear();
        _painting = false;

        if (Map == null) { GD.PushError("RockField: Map=null."); return; }

        _w = Map.Width;
        _h = Map.Height;
        if (_w <= 0 || _h <= 0)
        {
            GD.PushError($"RockField: некорректный размер карты {_w}x{_h}.");
            return;
        }

        _hp = new byte[_w * _h];
        _rockCountCache = -1;

        ulong t0 = Time.GetTicksUsec();

        InitFromNoise();
        ulong t1 = Time.GetTicksUsec();

        ApplyCellularAutomaton();
        ulong t1b = Time.GetTicksUsec();

        AddPerlinCaves();
        ulong t2 = Time.GetTicksUsec();

        var regions = FindOpenRegions();
        ulong t3 = Time.GetTicksUsec();

        ConnectRegions(regions);
        ulong t4 = Time.GetTicksUsec();

        ClearAroundSpawn();
        ulong t5 = Time.GetTicksUsec();

        int mainSize = regions.Count > 0 ? regions[0].Count : 0;
        int rockCells = 0;
        foreach (byte b in _hp) if (b > 0) rockCells++;
        // Прайм кэш: только что просчитали — сохраним.
        _rockCountCache = rockCells;
        float rockPct = 100f * rockCells / _hp.Length;
        GD.Print($"RockField: noise={Ms(t0,t1)} мс, CA={Ms(t1,t2)} мс, " +
                 $"flood={Ms(t2,t3)} мс ({regions.Count} регионов, главная={mainSize}), " +
                 $"tunnels={Ms(t3,t4)} мс (соединено={_connectedCount}, " +
                 $"скрытых_карманов={_skippedSmall}, далеко={_skippedFar}), " +
                 $"clearing={Ms(t4,t5)} мс. Камня {rockPct:F1}%.");

        // Headless: тайлы не рисуем, источник истины — _hp[]. Шлём готовность сразу.
        if (SimMode.Headless)
        {
            EmitSignal(SignalName.RocksGenerated);
            return;
        }

        _paintIdx = 0;
        _painting = true;
    }

    private static long Ms(ulong a, ulong b) => (long)((b - a) / 1000UL);

    // -- A. Noise --

    private void InitFromNoise()
    {
        var rng = MakeRng();
        int seed1 = (int)(rng.Randi() & 0x7FFFFFFF);
        int seed2 = (int)(rng.Randi() & 0x7FFFFFFF);

        var nLow = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = NoiseFrequencyLow, Seed = seed1 };
        var nHigh = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex, Frequency = NoiseFrequencyHigh, Seed = seed2 };
        float wLow = Mathf.Clamp(NoiseLowWeight, 0f, 1f);
        float wHigh = 1f - wLow;

        for (int y = 0; y < _h; y++)
        {
            int rowBase = y * _w;
            for (int x = 0; x < _w; x++)
            {
                float n = nLow.GetNoise2D(x, y) * wLow + nHigh.GetNoise2D(x, y) * wHigh;
                float density = (n + 1f) * 0.5f; // -1..1 → 0..1
                _hp[rowBase + x] = density > NoiseThreshold ? (byte)RockMaxHp : (byte)0;
            }
        }
    }

    // -- B. Cellular automaton (5-of-9 majority by default) --

    private void ApplyCellularAutomaton()
    {
        if (CaIterations <= 0) return;

        byte[] current = _hp;
        byte[] buffer = new byte[current.Length];

        for (int iter = 0; iter < CaIterations; iter++)
        {
            for (int y = 0; y < _h; y++)
            {
                int rowBase = y * _w;
                for (int x = 0; x < _w; x++)
                {
                    int wallCount = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= _h) { wallCount += 3; continue; }   // граница = камень
                        int nRow = ny * _w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= _w) { wallCount++; continue; }
                            if (current[nRow + nx] > 0) wallCount++;
                        }
                    }
                    buffer[rowBase + x] = wallCount >= CaWallNeighborMin ? (byte)RockMaxHp : (byte)0;
                }
            }
            (current, buffer) = (buffer, current);
        }

        if (!ReferenceEquals(current, _hp)) System.Array.Copy(current, _hp, current.Length);
    }

    // -- B2. Дополнительные пещеры Perlin-шумом --
    //
    // CA даёт характерные «пузырьки» — органичные, но с похожими очертаниями.
    // Низкочастотный Perlin даёт большие плавные «зоны» совершенно другой формы,
    // что разнообразит ландшафт. Каменные клетки, попавшие в пик шума, становятся
    // полостями. Дальше FindOpenRegions автоматически найдёт их как новые регионы,
    // и ConnectRegions попытается прокинуть к ним тоннели.

    private void AddPerlinCaves()
    {
        if (PerlinCaveDensity <= 0.001f) return;

        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = PerlinCaveFrequency,
            Seed = (int)(MakeRng().Randi() & 0x7FFFFFFF),
        };

        // SimplexSmooth на низкой частоте даёт узкое распределение ≈ 0.2..0.8.
        // Эмпирическая формула: density 0.10 → ~10%, 0.15 → ~15%, 0.20 → ~20%.
        float threshold = Mathf.Clamp(0.78f - PerlinCaveDensity * 0.85f, 0.0f, 1.0f);

        int carved = 0;
        for (int y = 0; y < _h; y++)
        {
            int rowBase = y * _w;
            for (int x = 0; x < _w; x++)
            {
                int idx = rowBase + x;
                if (_hp[idx] == 0) continue;   // уже пустота

                float n = (noise.GetNoise2D(x, y) + 1f) * 0.5f;
                if (n > threshold)
                {
                    _hp[idx] = 0;
                    carved++;
                }
            }
        }
        GD.Print($"RockField: Perlin-проход вырубил {carved} клеток ({100f * carved / _hp.Length:F1}%)");
    }

    // -- C. Flood fill: open regions --

    private List<List<int>> FindOpenRegions()
    {
        var regions = new List<List<int>>();
        var visited = new bool[_hp.Length];
        var stack = new Stack<int>();

        for (int idx = 0; idx < _hp.Length; idx++)
        {
            if (visited[idx] || _hp[idx] > 0) continue;

            var region = new List<int>();
            stack.Push(idx);

            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                if (visited[cur] || _hp[cur] > 0) continue;
                visited[cur] = true;
                region.Add(cur);

                int x = cur % _w;
                int y = cur / _w;
                if (x > 0)        stack.Push(cur - 1);
                if (x < _w - 1)   stack.Push(cur + 1);
                if (y > 0)        stack.Push(cur - _w);
                if (y < _h - 1)   stack.Push(cur + _w);
            }
            regions.Add(region);
        }

        // Маленькие изолированные карманы — заливаем камнем обратно (визуальный шум).
        if (MinRegionSize > 0)
        {
            for (int i = regions.Count - 1; i >= 0; i--)
            {
                if (regions[i].Count >= MinRegionSize) continue;
                foreach (int idx in regions[i]) _hp[idx] = (byte)RockMaxHp;
                regions.RemoveAt(i);
            }
        }
        return regions;
    }

    // -- D. Tunnels: A* от центра «крупных» регионов к самой большой пещере.
    //    Регионы меньше MinTunnelRegionSize намеренно остаются изолированными —
    //    они и есть стимул для игрока бурить и искать секретные карманы.
    //    Слишком длинные тоннели тоже пропускаем, чтобы карта не превращалась
    //    в швейцарский сыр и не было «магистралей» через всю карту.

    private int _connectedCount;
    private int _skippedFar;
    private int _skippedSmall;

    private void ConnectRegions(List<List<int>> regions)
    {
        _connectedCount = _skippedFar = _skippedSmall = 0;
        if (regions.Count <= 1) return;

        regions.Sort((a, b) => b.Count.CompareTo(a.Count));
        int mainStart = Centroid(regions[0]);

        for (int i = 1; i < regions.Count; i++)
        {
            if (regions[i].Count < MinTunnelRegionSize)
            {
                _skippedSmall++;
                continue;
            }

            int from = Centroid(regions[i]);
            var path = AStar(from, mainStart);
            if (path == null) { _skippedFar++; continue; }

            if (MaxTunnelLength > 0 && path.Count > MaxTunnelLength)
            {
                _skippedFar++;
                continue;
            }

            CarvePath(path);
            _connectedCount++;
        }
    }

    private int Centroid(List<int> region)
    {
        long sx = 0, sy = 0;
        foreach (int idx in region)
        {
            sx += idx % _w;
            sy += idx / _w;
        }
        int cx = (int)(sx / region.Count);
        int cy = (int)(sy / region.Count);
        // На случай, если центроид — камень: берём ближайший элемент региона.
        int target = cy * _w + cx;
        if (region.Contains(target)) return target;
        int bestIdx = region[0];
        long bestDist = long.MaxValue;
        foreach (int idx in region)
        {
            int dx = (idx % _w) - cx;
            int dy = (idx / _w) - cy;
            long d = (long)dx * dx + (long)dy * dy;
            if (d < bestDist) { bestDist = d; bestIdx = idx; }
        }
        return bestIdx;
    }

    private List<int> AStar(int startIdx, int endIdx)
    {
        var open = new PriorityQueue<int, float>();
        var cameFrom = new Dictionary<int, int>();
        var gScore = new Dictionary<int, float> { [startIdx] = 0f };

        open.Enqueue(startIdx, 0f);
        int endX = endIdx % _w;
        int endY = endIdx / _w;

        int[] dx4 = { 1, -1, 0, 0 };
        int[] dy4 = { 0, 0, 1, -1 };

        // Ограничение, чтобы не уходить в бесконечный поиск на патологических картах.
        int maxVisits = _w * _h;
        int visits = 0;

        while (open.Count > 0)
        {
            if (++visits > maxVisits) return null;
            int current = open.Dequeue();
            if (current == endIdx) return Reconstruct(cameFrom, current);

            int cx = current % _w;
            int cy = current / _w;

            for (int k = 0; k < 4; k++)
            {
                int nx = cx + dx4[k];
                int ny = cy + dy4[k];
                if (nx < 0 || nx >= _w || ny < 0 || ny >= _h) continue;

                int neighbor = ny * _w + nx;
                // Проход через камень дороже, через пустоту почти бесплатен.
                float step = _hp[neighbor] > 0 ? 1f : 0.1f;
                float tentative = gScore[current] + step;
                if (gScore.TryGetValue(neighbor, out float existing) && tentative >= existing) continue;

                gScore[neighbor] = tentative;
                cameFrom[neighbor] = current;
                float h = Mathf.Abs(nx - endX) + Mathf.Abs(ny - endY);
                open.Enqueue(neighbor, tentative + h);
            }
        }
        return null;
    }

    private List<int> Reconstruct(Dictionary<int, int> cameFrom, int end)
    {
        var path = new List<int> { end };
        while (cameFrom.TryGetValue(end, out int prev))
        {
            end = prev;
            path.Add(end);
        }
        return path;
    }

    /// <summary>
    /// Вырезает тоннель по A*-пути с органическими модификациями:
    ///   - Каждая точка пути смещается на низкочастотный noise → волнистая
    ///     форма вместо прямого коридора.
    ///   - Радиус варьируется по другому noise → ширина «дышит» от 1 до 5
    ///     клеток вдоль пути.
    ///   - С небольшим шансом возникают «залы» (расширенные карвы) — даёт
    ///     природные пещерные комнаты в случайных местах тоннеля.
    /// </summary>
    private void CarvePath(List<int> path)
    {
        if (path.Count == 0) return;

        var carveRng = MakeRng();
        var meanderNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = TunnelMeanderFrequency,
            Seed = (int)(carveRng.Randi() & 0x7FFFFFFF),
        };
        var radiusNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = TunnelMeanderFrequency * 1.5f,
            Seed = (int)(carveRng.Randi() & 0x7FFFFFFF),
        };

        int rmin = Mathf.Min(TunnelMinRadius, TunnelMaxRadius);
        int rmax = Mathf.Max(TunnelMinRadius, TunnelMaxRadius);

        for (int i = 0; i < path.Count; i++)
        {
            int idx = path[i];
            int cx = idx % _w;
            int cy = idx / _w;

            // Смещение по двум независимым осям noise — даёт «петляющую» траекторию.
            float n1 = meanderNoise.GetNoise2D(cx, cy);
            float n2 = meanderNoise.GetNoise2D(cx + 113, cy + 257);
            int dx = (int)Mathf.Round(n1 * TunnelMeanderStrength);
            int dy = (int)Mathf.Round(n2 * TunnelMeanderStrength);

            // Радиус интерполируется между min и max по своему noise.
            float rt = (radiusNoise.GetNoise2D(cx, cy) + 1f) * 0.5f;
            int radius = rmin + (int)Mathf.Round(rt * (rmax - rmin));

            // Случайный «зал» — резко расширяемся в этой точке.
            if (carveRng.Randf() < TunnelChamberChance)
            {
                radius += TunnelChamberExtraRadius;
            }

            CarveCircle(cx + dx, cy + dy, radius);
        }
    }

    private void CarveCircle(int cx, int cy, int radius)
    {
        if (radius < 0) radius = 0;
        int r2 = radius * radius;
        for (int ddy = -radius; ddy <= radius; ddy++)
        {
            int ny = cy + ddy;
            if (ny < 0 || ny >= _h) continue;
            int rowBase = ny * _w;
            for (int ddx = -radius; ddx <= radius; ddx++)
            {
                int nx = cx + ddx;
                if (nx < 0 || nx >= _w) continue;
                if (ddx * ddx + ddy * ddy > r2) continue;
                _hp[rowBase + nx] = 0;
            }
        }
    }

    // -- E. Зачистка вокруг спавна игрока --

    private void ClearAroundSpawn()
    {
        if (PlayerClearingRadius <= 0) return;
        int cx = _w / 2;
        int cy = _h / 2;
        int r = PlayerClearingRadius;
        int r2 = r * r;
        for (int dy = -r; dy <= r; dy++)
        {
            int ny = cy + dy;
            if (ny < 0 || ny >= _h) continue;
            int rowBase = ny * _w;
            for (int dx = -r; dx <= r; dx++)
            {
                int nx = cx + dx;
                if (nx < 0 || nx >= _w) continue;
                if (dx * dx + dy * dy > r2) continue;
                _hp[rowBase + nx] = 0;
            }
        }
    }

    private RandomNumberGenerator MakeRng()
    {
        var rng = new RandomNumberGenerator();
        if (Seed == 0) rng.Randomize();
        else rng.Seed = Seed;
        return rng;
    }
}
