using Godot;
using System.Collections.Generic;

/// <summary>
/// Колония подземных светлячков. Один Node2D управляет всей популяцией:
/// массив структур + один MultiMeshInstance2D рендерит всех. Это позволяет
/// держать сотни особей без overhead'а отдельных нод.
///
/// Поведение:
///   - Wander с плавным изменением направления.
///   - Obstacle avoidance + sliding по стенам/камням/зрелым кристаллам.
///   - Hunger ↓ со временем; ищут мшистые клетки (`MossField.GetDensity`),
///     едят (`MossField.Eat`).
///   - Boids-flocking через spatial hash (cohesion + alignment + separation).
///   - Реакция на игрока: лёгкое отталкивание в радиусе.
///   - После N успешных приёмов пищи сбрасывают «зерно» кристалла.
///   - Размножаются (если хорошо накормлены, в подходящем возрасте, рядом
///     второй особь). Cap популяции защищает от взрыва.
///   - Умирают по возрасту или от голода.
/// </summary>
public partial class FireflyColony : Node2D
{
    [ExportGroup("Refs")]
    [Export] public MapGenerator Map;
    [Export] public RockField Rocks;
    [Export] public TileMapLayer SolidWalls;
    [Export] public MossField Moss;
    [Export] public CrystalField Crystals;
    [Export] public Node2D Player;
    [Export] public Shader GlowShader;

    [ExportGroup("Population")]
    [Export(PropertyHint.Range, "0,500,1")] public int InitialPopulation = 100;
    [Export(PropertyHint.Range, "10,2000,10")] public int MaxPopulation = 400;
    /// <summary>Гарантированно спавнить столько светлячков рядом с центром
    /// карты (где появляется игрок). 0 = никаких, чисто случайно.</summary>
    [Export(PropertyHint.Range, "0,100,1")] public int InitialNearPlayerCount = 25;
    /// <summary>Радиус «рядом с игроком» в пикселях.</summary>
    [Export(PropertyHint.Range, "100,5000,50")] public float InitialNearPlayerRadius = 1500f;

    [ExportGroup("Movement")]
    [Export(PropertyHint.Range, "5,300,1")] public float Speed = 55f;
    /// <summary>Скорость случайного «колебания» направления.
    /// Меньше = плавнее, более «живо» выглядит.</summary>
    [Export(PropertyHint.Range, "0.1,10.0,0.1")] public float WanderTurnRate = 0.7f;
    [Export(PropertyHint.Range, "0.05,2.0,0.01")] public float SimulationTickRate = 0.10f;

    [ExportGroup("Lifecycle")]
    [Export(PropertyHint.Range, "10,600,1")] public float BaseMaxAge = 180f;
    [Export(PropertyHint.Range, "0.0,1.0,0.05")] public float MaxAgeJitter = 0.30f;
    [Export(PropertyHint.Range, "0.5,30.0,0.5")] public float HungerDecay = 1.8f;
    [Export(PropertyHint.Range, "1,200,1")] public int FeedAmountPerBite = 22;
    [Export(PropertyHint.Range, "1,100,1")] public int HungerPerBite = 32;
    [Export(PropertyHint.Range, "5,80,1")] public float SeekFoodHungerThreshold = 60f;

    [ExportGroup("Crystals")]
    /// <summary>Сколько раз светлячок должен поесть, прежде чем сбросить
    /// семя кристалла. Высокое значение → редкие кристаллы.</summary>
    [Export(PropertyHint.Range, "1,200,1")] public int FeedingsBeforeCrystal = 25;
    [Export(PropertyHint.Range, "1,10,1")] public int CrystalSeedRadius = 3;

    [ExportGroup("Flocking")]
    [Export(PropertyHint.Range, "0,1000,5")] public float FlockRadius = 220f;
    [Export(PropertyHint.Range, "0,200,1")] public float SeparationRadius = 36f;
    [Export(PropertyHint.Range, "0,2,0.05")] public float CohesionStrength = 0.20f;
    [Export(PropertyHint.Range, "0,2,0.05")] public float AlignmentStrength = 0.30f;
    [Export(PropertyHint.Range, "0,2,0.05")] public float SeparationStrength = 0.45f;

    [ExportGroup("Reproduction")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float BreedAgeMinFraction = 0.25f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BreedAgeMaxFraction = 0.80f;
    [Export(PropertyHint.Range, "0,100,1")] public float BreedHungerMin = 65f;
    [Export(PropertyHint.Range, "0,100,1")] public float BreedHungerCost = 28f;
    [Export(PropertyHint.Range, "0,1,0.005")] public float BreedChancePerTick = 0.05f;
    [Export(PropertyHint.Range, "10,500,5")] public float BreedSearchRadius = 60f;

    [ExportGroup("Replenishment (extinction prevention)")]
    /// <summary>Минимальная популяция. Если упало ниже — подсаживаем нового
    /// в случайной мшистой клетке (лор: «вылупились из щелей»).</summary>
    [Export(PropertyHint.Range, "0,200,1")] public int MinPopulation = 25;
    /// <summary>Период проверки и подсадки (сек).</summary>
    [Export(PropertyHint.Range, "5,300,1")] public float ReplenishInterval = 15f;
    /// <summary>Минимальная дистанция от игрока для подсадки (px).</summary>
    [Export(PropertyHint.Range, "0,5000,50")] public float ReplenishMinDistanceFromPlayer = 1500f;

    [ExportGroup("Moss-spawned births (organic ecosystem cycle)")]
    /// <summary>Каждый этот промежуток сэмплим N случайных клеток. Если в
    /// какой-то очень плотный мох — с шансом MossBirthChance рождается
    /// светлячок «из мха». Лор: личинки в густом мхе.</summary>
    [Export(PropertyHint.Range, "1,60,1")] public float MossBirthCheckInterval = 5f;
    [Export(PropertyHint.Range, "0,500,1")] public int MossBirthChecksPerInterval = 200;
    [Export(PropertyHint.Range, "1,255,1")] public int MossBirthMinDensity = 180;
    [Export(PropertyHint.Range, "0,1,0.005")] public float MossBirthChance = 0.04f;
    /// <summary>Если популяция выше этой доли cap — рожать перестаём
    /// (естественный регулятор, мох «не нужен» когда и так много).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float MossBirthPopulationGate = 0.70f;

    [ExportGroup("Player Interaction")]
    /// <summary>Радиус, в котором светлячок «замечает» игрока. Меньше = смелее.</summary>
    [Export(PropertyHint.Range, "0,1000,5")] public float PlayerAvoidRadius = 100f;
    /// <summary>Сила «бегства». 0 = не реагируют вообще. 0.4 = мягкое уворачивание.</summary>
    [Export(PropertyHint.Range, "0,5,0.05")] public float PlayerAvoidStrength = 0.4f;

    [ExportGroup("Visual")]
    [Export(PropertyHint.Range, "8,128,1")] public float GlowQuadSize = 56f;
    [Export(PropertyHint.Range, "-10,10,1")] public int VisualZIndex = 0;

    [Export] public ulong Seed = 0;

    private struct Firefly
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float WanderAngle;
        public float Age;
        public float MaxAge;
        public float Hunger;
        public float GlowPhase;
        public int FeedingsTotal;
        public int FeedingsSinceCrystal;
        public byte State;          // 0=wander, 1=seekFood, 2=feeding
    }

    private Firefly[] _fireflies;
    private int _count;

    // Stats для мониторинга экосистемы.
    public int BornInitial { get; private set; }
    public int BornBreed { get; private set; }
    public int BornReplenish { get; private set; }
    public int BornMoss { get; private set; }
    public int DiedAge { get; private set; }
    public int DiedHunger { get; private set; }
    public int DiedPredator { get; private set; }
    public int PeakPopulation { get; private set; }
    public int BornTotal => BornInitial + BornBreed + BornReplenish + BornMoss;
    public int DiedTotal => DiedAge + DiedHunger + DiedPredator;

    private RandomNumberGenerator _rng;
    private MultiMesh _multimesh;
    private MultiMeshInstance2D _renderer;
    private Dictionary<Vector2I, List<int>> _spatialHash = new();
    private const float SPATIAL_CELL = 256f;
    private float _hashRebuildAccum;
    private float _tickAccum;
    private float _replenishAccum;
    private float _mossBirthAccum;
    private bool _ready;
    private int _tilePx = 128;
    private int _w, _h;

    public override void _Ready()
    {
        _rng = new RandomNumberGenerator();
        _rng.Randomize();
        if (Seed != 0) _rng.Seed = Seed;

        Node parent = GetParent();
        if (Map == null && parent != null) Map = parent.GetNodeOrNull<MapGenerator>("MapGenerator");
        if (Rocks == null && parent != null) Rocks = parent.GetNodeOrNull<RockField>("Rocks");
        if (SolidWalls == null && parent != null) SolidWalls = parent.GetNodeOrNull<TileMapLayer>("SolidWalls");
        if (Moss == null && parent != null) Moss = parent.GetNodeOrNull<MossField>("MossField");
        if (Crystals == null && parent != null) Crystals = parent.GetNodeOrNull<CrystalField>("CrystalField");
        if (Player == null && parent != null) Player = parent.GetNodeOrNull<Node2D>("Player");
        if (GlowShader == null) GlowShader = ResourceLoader.Load<Shader>("res://firefly_glow.gdshader");

        if (Map == null)
        {
            GD.PushError("FireflyColony: ссылка на MapGenerator не найдена.");
            return;
        }

        _fireflies = new Firefly[MaxPopulation];

        if (Moss != null)
        {
            // Стартуем после готовности мха — нужно знать, где есть еда.
            Moss.MossUpdated += OnMossReadyOnce;
        }
        else
        {
            // Если мха почему-то нет — стартуем сразу после генерации карты.
            Map.MapGenerated += OnMapReady;
        }

        SetProcess(false);
    }

    private bool _initialized;

    private void OnMossReadyOnce()
    {
        if (_initialized) return;
        OnMapReady();
    }

    private void OnMapReady()
    {
        if (_initialized) return;
        _initialized = true;

        _w = Map.Width;
        _h = Map.Height;
        if (Map.TileSet != null) _tilePx = Map.TileSet.TileSize.X;

        CreateRenderer();
        SpawnInitialFireflies();
        UpdateRenderInstances();

        GD.Print($"FireflyColony: запущено {_count} светлячков.");
        _ready = true;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (!_ready) return;
        // Кап dt: при больших фризах (например, при первом нажатии 1) dt
        // может выйти на 0.5+ сек. Без капа светлячки за кадр пролетают
        // несколько клеток и перепрыгивают стены. С капом симуляция чуть
        // замедляется во время фриза, но не ломается физика.
        float dt = Mathf.Min((float)delta, 0.05f);

        UpdateMovement(dt);

        _tickAccum += dt;
        if (_tickAccum >= SimulationTickRate)
        {
            _tickAccum = 0f;
            SimulationTick();
        }

        _hashRebuildAccum += dt;
        if (_hashRebuildAccum >= 0.10f)
        {
            _hashRebuildAccum = 0f;
            RebuildSpatialHash();
        }

        _replenishAccum += dt;
        if (_replenishAccum >= ReplenishInterval)
        {
            _replenishAccum = 0f;
            TryReplenish();
        }

        _mossBirthAccum += dt;
        if (_mossBirthAccum >= MossBirthCheckInterval)
        {
            _mossBirthAccum = 0f;
            TryMossBirths();
        }

        UpdateRenderInstances();
    }

    /// <summary>«Споры» в густом мхе — изредка рождают нового светлячка.
    /// Срабатывает только пока популяция не превышает MossBirthPopulationGate
    /// от cap (если светлячков и так много, мху «не нужно» рожать).</summary>
    private void TryMossBirths()
    {
        if (Moss == null) return;
        int gate = (int)(MaxPopulation * Mathf.Clamp(MossBirthPopulationGate, 0f, 1f));
        if (_count >= gate) return;

        int spawned = 0;
        int maxSpawnsPerInterval = Mathf.Max(1, MaxPopulation / 50);  // ограничение всплеска
        for (int i = 0; i < MossBirthChecksPerInterval; i++)
        {
            if (_count >= gate || spawned >= maxSpawnsPerInterval) break;

            int cx = _rng.RandiRange(0, _w - 1);
            int cy = _rng.RandiRange(0, _h - 1);
            var cell = new Vector2I(cx, cy);
            if (Moss.GetDensity(cell) < MossBirthMinDensity) continue;
            if (IsCellBlocked(cell)) continue;

            if (_rng.Randf() < MossBirthChance)
            {
                SpawnFirefly(CellToWorld(cell), BIRTH_MOSS);
                spawned++;
            }
        }
    }

    /// <summary>Гарантия от вымирания: если светлячков меньше MinPopulation,
    /// подсаживаем одного в случайной мшистой клетке подальше от игрока.</summary>
    private void TryReplenish()
    {
        if (_count >= MinPopulation) return;
        if (_count >= MaxPopulation) return;
        if (Moss == null) return;

        for (int attempt = 0; attempt < 200; attempt++)
        {
            int cx = _rng.RandiRange(0, _w - 1);
            int cy = _rng.RandiRange(0, _h - 1);
            var cell = new Vector2I(cx, cy);
            if (IsCellBlocked(cell)) continue;
            if (Moss.GetDensity(cell) < 80) continue;

            Vector2 pos = CellToWorld(cell);
            if (Player != null && pos.DistanceTo(Player.GlobalPosition) < ReplenishMinDistanceFromPlayer)
                continue;

            SpawnFirefly(pos, BIRTH_REPLENISH);
            return;
        }
    }

    // ---- Public API for predators (used by WormColony etc.) -------------

    /// <summary>Текущее количество живых светлячков.</summary>
    public int Count => _count;

    /// <summary>Найти ближайшего светлячка в радиусе. Возвращает true, если
    /// нашёлся, и записывает его позицию в <paramref name="outPos"/>.</summary>
    public bool TryFindNearestFirefly(Vector2 pos, float maxRadius, out Vector2 outPos)
    {
        outPos = Vector2.Zero;
        if (!_ready || _count == 0) return false;

        // Для огромных радиусов spatial hash раздувается катастрофически
        // (radius=999999 → 60M пустых ячеек). Линейный скан 80 светлячков
        // в десятки тысяч раз быстрее.
        if (maxRadius > 5000f)
        {
            int bestI = 0;
            float bestSq = maxRadius * maxRadius;
            for (int i = 0; i < _count; i++)
            {
                float d = (_fireflies[i].Position - pos).LengthSquared();
                if (d < bestSq) { bestSq = d; bestI = i; }
            }
            if (bestSq >= maxRadius * maxRadius) return false;
            outPos = _fireflies[bestI].Position;
            return true;
        }

        QueryNeighbors(pos, maxRadius, _scratch);
        if (_scratch.Count == 0) return false;

        int bestIdx = -1;
        float bestDistSq = maxRadius * maxRadius;
        for (int k = 0; k < _scratch.Count; k++)
        {
            int j = _scratch[k];
            float dSq = (_fireflies[j].Position - pos).LengthSquared();
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                bestIdx = j;
            }
        }
        if (bestIdx < 0) return false;
        outPos = _fireflies[bestIdx].Position;
        return true;
    }

    /// <summary>Удалить любого светлячка, оказавшегося в радиусе вокруг точки.
    /// Используется хищниками при поимке. Возвращает true, если кто-то съеден.</summary>
    public bool TryKillFireflyAt(Vector2 pos, float radius, out Vector2 killedPos)
    {
        killedPos = Vector2.Zero;
        if (!_ready || _count == 0) return false;

        QueryNeighbors(pos, radius, _scratch);
        float rSq = radius * radius;
        for (int k = 0; k < _scratch.Count; k++)
        {
            int j = _scratch[k];
            if ((_fireflies[j].Position - pos).LengthSquared() <= rSq)
            {
                killedPos = _fireflies[j].Position;
                RemoveAt(j);
                DiedPredator++;
                return true;
            }
        }
        return false;
    }

    // ---- Spawning ------------------------------------------------------

    private void SpawnInitialFireflies()
    {
        // Сначала — гарантированный набор рядом с игроком (центр карты).
        int nearTarget = Mathf.Min(InitialNearPlayerCount, InitialPopulation);
        int spawnedNear = SpawnFirefliesNearCenter(nearTarget, InitialNearPlayerRadius);

        // Потом — обычный разброс по всей карте.
        int remaining = InitialPopulation - spawnedNear;
        int spawned = 0;
        int attempts = 0;
        int maxAttempts = remaining * 80;
        while (spawned < remaining && attempts++ < maxAttempts)
        {
            int cx = _rng.RandiRange(0, _w - 1);
            int cy = _rng.RandiRange(0, _h - 1);
            var cell = new Vector2I(cx, cy);
            if (IsCellBlocked(cell)) continue;
            if (Moss == null || Moss.GetDensity(cell) < 100) continue;

            Vector2 pos = CellToWorld(cell);
            SpawnFirefly(pos, BIRTH_INITIAL);
            spawned++;
        }
    }

    /// <summary>Спавн N светлячков в радиусе вокруг центра карты с
    /// постепенным послаблением требований к мху, чтобы гарантированно
    /// попадались хоть какие-то клетки.</summary>
    private int SpawnFirefliesNearCenter(int count, float radiusPx)
    {
        if (count <= 0) return 0;
        Vector2 center = new Vector2(_w * _tilePx * 0.5f, _h * _tilePx * 0.5f);

        int spawned = 0;
        // Три «прохода» с послаблением: сначала ищем мшистые клетки,
        // потом любые открытые.
        int[] mossThresholds = { 100, 50, 0 };
        foreach (int thr in mossThresholds)
        {
            if (spawned >= count) break;
            int attempts = 0;
            int maxAttempts = (count - spawned) * 200;
            while (spawned < count && attempts++ < maxAttempts)
            {
                float angle = _rng.RandfRange(0f, Mathf.Tau);
                float dist = _rng.RandfRange(50f, radiusPx);
                Vector2 pos = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
                Vector2I cell = new Vector2I((int)(pos.X / _tilePx), (int)(pos.Y / _tilePx));
                if (cell.X < 0 || cell.X >= _w || cell.Y < 0 || cell.Y >= _h) continue;
                if (IsCellBlocked(cell)) continue;
                if (thr > 0 && (Moss == null || Moss.GetDensity(cell) < thr)) continue;

                SpawnFirefly(CellToWorld(cell), BIRTH_INITIAL);
                spawned++;
            }
        }
        return spawned;
    }

    public const byte BIRTH_INITIAL = 0;
    public const byte BIRTH_BREED = 1;
    public const byte BIRTH_REPLENISH = 2;
    public const byte BIRTH_MOSS = 3;

    public const byte DEATH_AGE = 0;
    public const byte DEATH_HUNGER = 1;
    public const byte DEATH_PREDATOR = 2;

    private void SpawnFirefly(Vector2 pos, byte source = BIRTH_INITIAL)
    {
        if (_count >= MaxPopulation) return;

        ref Firefly f = ref _fireflies[_count];
        f.Position = pos;
        f.WanderAngle = _rng.RandfRange(0f, Mathf.Tau);
        f.Velocity = new Vector2(Mathf.Cos(f.WanderAngle), Mathf.Sin(f.WanderAngle)) * Speed;
        f.Age = 0f;
        f.MaxAge = BaseMaxAge * (1f + _rng.RandfRange(-MaxAgeJitter, MaxAgeJitter));
        f.Hunger = _rng.RandfRange(60f, 95f);
        f.GlowPhase = _rng.Randf();
        f.FeedingsTotal = 0;
        f.FeedingsSinceCrystal = 0;
        f.State = 0;
        _count++;

        switch (source)
        {
            case BIRTH_BREED:     BornBreed++; break;
            case BIRTH_REPLENISH: BornReplenish++; break;
            case BIRTH_MOSS:      BornMoss++; break;
            default:              BornInitial++; break;
        }
        if (_count > PeakPopulation) PeakPopulation = _count;
    }

    // ---- Per-frame movement --------------------------------------------

    private void UpdateMovement(float dt)
    {
        for (int i = 0; i < _count; i++)
        {
            ref Firefly f = ref _fireflies[i];

            // Плавное случайное «колебание» направления.
            f.WanderAngle += _rng.RandfRange(-WanderTurnRate, WanderTurnRate) * dt;

            // Реакция на игрока (легкое отталкивание).
            Vector2 desiredVel = new Vector2(Mathf.Cos(f.WanderAngle), Mathf.Sin(f.WanderAngle)) * Speed;
            if (Player != null)
            {
                Vector2 fromPlayer = f.Position - Player.GlobalPosition;
                float distSq = fromPlayer.LengthSquared();
                float r = PlayerAvoidRadius;
                if (distSq < r * r && distSq > 1f)
                {
                    float dist = Mathf.Sqrt(distSq);
                    float weight = (1f - dist / r) * PlayerAvoidStrength;
                    Vector2 away = fromPlayer / dist;
                    desiredVel = desiredVel.Lerp(away * Speed, Mathf.Clamp(weight, 0f, 1f));
                    f.WanderAngle = Mathf.Atan2(desiredVel.Y, desiredVel.X);
                }
            }

            f.Velocity = f.Velocity.Lerp(desiredVel, Mathf.Min(1f, dt * 4f));
            MoveWithSliding(ref f, dt);

            f.GlowPhase = (f.GlowPhase + dt * 2.0f) % 1f;
        }
    }

    private void MoveWithSliding(ref Firefly f, float dt)
    {
        Vector2 newPos = f.Position + f.Velocity * dt;

        var newCell = WorldToCell(newPos);
        if (!IsCellBlocked(newCell))
        {
            f.Position = newPos;
            return;
        }

        // Попробовать разделить движение на оси.
        var cellH = WorldToCell(new Vector2(newPos.X, f.Position.Y));
        var cellV = WorldToCell(new Vector2(f.Position.X, newPos.Y));
        bool blockedH = IsCellBlocked(cellH);
        bool blockedV = IsCellBlocked(cellV);

        if (!blockedH)
        {
            f.Position = new Vector2(newPos.X, f.Position.Y);
            f.Velocity = new Vector2(f.Velocity.X, 0f);
        }
        else if (!blockedV)
        {
            f.Position = new Vector2(f.Position.X, newPos.Y);
            f.Velocity = new Vector2(0f, f.Velocity.Y);
        }
        else
        {
            // Полностью заперт — резко разворачиваемся.
            f.WanderAngle += Mathf.Pi + _rng.RandfRange(-0.6f, 0.6f);
            f.Velocity = new Vector2(Mathf.Cos(f.WanderAngle), Mathf.Sin(f.WanderAngle)) * Speed;
        }
    }

    // ---- Logic tick (10 Hz) --------------------------------------------

    private readonly List<int> _scratch = new();

    private void SimulationTick()
    {
        // Сначала кормление + старение + смерть (свапим с последним).
        for (int i = _count - 1; i >= 0; i--)
        {
            ref Firefly f = ref _fireflies[i];

            f.Age += SimulationTickRate;
            f.Hunger -= HungerDecay * SimulationTickRate;
            if (f.Hunger > 100f) f.Hunger = 100f;

            // Кормление — если стоим на мшистой клетке.
            var cell = WorldToCell(f.Position);
            if (Moss != null && Moss.GetDensity(cell) > 30)
            {
                int eaten = Moss.Eat(cell, FeedAmountPerBite);
                if (eaten > 0)
                {
                    f.Hunger += HungerPerBite;
                    if (f.Hunger > 100f) f.Hunger = 100f;
                    f.FeedingsTotal++;
                    f.FeedingsSinceCrystal++;
                    f.State = 2;

                    // Сброс кристалла после N приёмов.
                    if (f.FeedingsSinceCrystal >= FeedingsBeforeCrystal)
                    {
                        if (TryDropCrystal(f.Position))
                        {
                            f.FeedingsSinceCrystal = 0;
                            // Чуть устаём после «родов» кристалла.
                            f.Hunger -= 15f;
                        }
                    }
                }
            }
            else if (f.Hunger < SeekFoodHungerThreshold)
            {
                // Поиск еды — поверни в сторону ближайшей мшистой клетки.
                if (TryFaceMoss(ref f))
                    f.State = 1;
                else
                    f.State = 0;
            }
            else
            {
                f.State = 0;
            }

            // Смерть.
            if (f.Age >= f.MaxAge)
            {
                RemoveAt(i);
                DiedAge++;
            }
            else if (f.Hunger <= 0f)
            {
                RemoveAt(i);
                DiedHunger++;
            }
        }

        // Boids
        for (int i = 0; i < _count; i++)
        {
            ApplyFlocking(i);
        }

        // Размножение
        TryReproductionPass();
    }

    private bool TryFaceMoss(ref Firefly f)
    {
        // Поищем мшистую клетку в радиусе 10 тайлов (больше даёт шансов
        // найти еду прежде чем умереть с голоду).
        var center = WorldToCell(f.Position);
        int bestDx = 0, bestDy = 0;
        int bestDensity = 0;
        const int R = 10;
        for (int dy = -R; dy <= R; dy++)
        {
            for (int dx = -R; dx <= R; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var c = new Vector2I(center.X + dx, center.Y + dy);
                int d = Moss.GetDensity(c);
                if (d > bestDensity)
                {
                    bestDensity = d;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }
        if (bestDensity == 0) return false;
        f.WanderAngle = Mathf.Atan2(bestDy, bestDx);
        return true;
    }

    private bool TryDropCrystal(Vector2 origin)
    {
        if (Crystals == null) return false;
        var center = WorldToCell(origin);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            int dx = _rng.RandiRange(-CrystalSeedRadius, CrystalSeedRadius);
            int dy = _rng.RandiRange(-CrystalSeedRadius, CrystalSeedRadius);
            var cell = new Vector2I(center.X + dx, center.Y + dy);
            if (Crystals.TrySeed(cell)) return true;
        }
        return false;
    }

    private void ApplyFlocking(int idx)
    {
        ref Firefly f = ref _fireflies[idx];
        QueryNeighbors(f.Position, FlockRadius, _scratch);

        Vector2 cohesion = Vector2.Zero;
        Vector2 alignment = Vector2.Zero;
        Vector2 separation = Vector2.Zero;
        int neighborCount = 0;
        int separationCount = 0;
        float sepRSq = SeparationRadius * SeparationRadius;

        for (int k = 0; k < _scratch.Count; k++)
        {
            int j = _scratch[k];
            if (j == idx) continue;
            ref Firefly g = ref _fireflies[j];
            Vector2 diff = g.Position - f.Position;
            float dSq = diff.LengthSquared();
            if (dSq < sepRSq && dSq > 0.01f)
            {
                separation -= diff / Mathf.Sqrt(dSq);
                separationCount++;
            }
            cohesion += g.Position;
            alignment += g.Velocity;
            neighborCount++;
        }

        if (neighborCount == 0) return;

        cohesion = cohesion / neighborCount - f.Position;
        alignment = alignment / neighborCount - f.Velocity;

        Vector2 force = cohesion.Normalized() * CohesionStrength
                      + alignment.Normalized() * AlignmentStrength
                      + (separationCount > 0 ? separation.Normalized() * SeparationStrength : Vector2.Zero);

        // Сдвиг wander angle под действием силы.
        Vector2 newDir = (new Vector2(Mathf.Cos(f.WanderAngle), Mathf.Sin(f.WanderAngle)) + force * 0.3f).Normalized();
        f.WanderAngle = Mathf.Atan2(newDir.Y, newDir.X);
    }

    private void TryReproductionPass()
    {
        if (_count >= MaxPopulation) return;

        for (int i = 0; i < _count; i++)
        {
            if (_count >= MaxPopulation) break;

            ref Firefly f = ref _fireflies[i];
            float ageT = f.Age / Mathf.Max(0.01f, f.MaxAge);
            if (ageT < BreedAgeMinFraction || ageT > BreedAgeMaxFraction) continue;
            if (f.Hunger < BreedHungerMin) continue;
            if (_rng.Randf() > BreedChancePerTick) continue;

            // Партнёр?
            QueryNeighbors(f.Position, BreedSearchRadius, _scratch);
            int partner = -1;
            for (int k = 0; k < _scratch.Count; k++)
            {
                int j = _scratch[k];
                if (j == i) continue;
                ref Firefly g = ref _fireflies[j];
                float gAgeT = g.Age / Mathf.Max(0.01f, g.MaxAge);
                if (gAgeT < BreedAgeMinFraction || gAgeT > BreedAgeMaxFraction) continue;
                if (g.Hunger < BreedHungerMin) continue;
                partner = j;
                break;
            }
            if (partner < 0) continue;

            // Дети!
            Vector2 spawnPos = (f.Position + _fireflies[partner].Position) * 0.5f;
            spawnPos += new Vector2(_rng.RandfRange(-12f, 12f), _rng.RandfRange(-12f, 12f));
            f.Hunger -= BreedHungerCost;
            _fireflies[partner].Hunger -= BreedHungerCost;
            SpawnFirefly(spawnPos, BIRTH_BREED);
        }
    }

    // ---- Spatial hash --------------------------------------------------

    private void RebuildSpatialHash()
    {
        _spatialHash.Clear();
        for (int i = 0; i < _count; i++)
        {
            var key = HashKey(_fireflies[i].Position);
            if (!_spatialHash.TryGetValue(key, out var list))
            {
                list = new List<int>();
                _spatialHash[key] = list;
            }
            list.Add(i);
        }
    }

    private static Vector2I HashKey(Vector2 pos)
        => new Vector2I((int)Mathf.Floor(pos.X / SPATIAL_CELL), (int)Mathf.Floor(pos.Y / SPATIAL_CELL));

    private void QueryNeighbors(Vector2 pos, float radius, List<int> output)
    {
        output.Clear();
        var center = HashKey(pos);
        int span = Mathf.Max(1, (int)Mathf.Ceil(radius / SPATIAL_CELL));
        float rSq = radius * radius;
        for (int dy = -span; dy <= span; dy++)
        {
            for (int dx = -span; dx <= span; dx++)
            {
                var key = new Vector2I(center.X + dx, center.Y + dy);
                if (!_spatialHash.TryGetValue(key, out var list)) continue;
                for (int k = 0; k < list.Count; k++)
                {
                    int j = list[k];
                    if ((_fireflies[j].Position - pos).LengthSquared() <= rSq)
                        output.Add(j);
                }
            }
        }
    }

    // ---- Render --------------------------------------------------------

    private void CreateRenderer()
    {
        _multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseCustomData = true,
            InstanceCount = MaxPopulation,
            VisibleInstanceCount = 0,
            Mesh = new QuadMesh { Size = new Vector2(GlowQuadSize, GlowQuadSize) },
        };
        _renderer = new MultiMeshInstance2D
        {
            Multimesh = _multimesh,
            ZIndex = VisualZIndex,
        };
        if (GlowShader != null)
        {
            var mat = new ShaderMaterial { Shader = GlowShader };
            _renderer.Material = mat;
        }
        AddChild(_renderer);
    }

    private void UpdateRenderInstances()
    {
        if (_multimesh == null) return;
        for (int i = 0; i < _count; i++)
        {
            ref Firefly f = ref _fireflies[i];
            _multimesh.SetInstanceTransform2D(i, new Transform2D(0f, f.Position));
            _multimesh.SetInstanceCustomData(i, new Color(
                f.GlowPhase,
                Mathf.Clamp(f.Hunger / 100f, 0f, 1f),
                Mathf.Clamp(f.Age / Mathf.Max(0.01f, f.MaxAge), 0f, 1f),
                1f));
        }
        _multimesh.VisibleInstanceCount = _count;
    }

    // ---- Helpers -------------------------------------------------------

    private void RemoveAt(int idx)
    {
        int last = _count - 1;
        if (idx != last) _fireflies[idx] = _fireflies[last];
        _count--;
    }

    private bool IsCellBlocked(Vector2I cell)
    {
        // Out-of-bounds = blocked. Стены рамки только в x=-1 и x=Width;
        // дальше пустота, и без этой защиты светлячки могут вылететь
        // в OOB и больше не вернуться.
        if (cell.X < 0 || cell.X >= _w || cell.Y < 0 || cell.Y >= _h) return true;
        if (Rocks != null && Rocks.HasRock(cell)) return true;
        if (SolidWalls != null && SolidWalls.GetCellSourceId(cell) >= 0) return true;
        if (Crystals != null && Crystals.IsMature(cell)) return true;
        return false;
    }

    private Vector2I WorldToCell(Vector2 pos)
        => new Vector2I((int)Mathf.Floor(pos.X / _tilePx), (int)Mathf.Floor(pos.Y / _tilePx));

    private Vector2 CellToWorld(Vector2I cell)
        => new Vector2(cell.X * _tilePx + _tilePx * 0.5f, cell.Y * _tilePx + _tilePx * 0.5f);
}
