using Godot;
using System.Collections.Generic;

/// <summary>
/// Подземные черви-хищники. Каждый червь = голова + 4 сегмента, соединённых
/// chain-physics (segment[i] подтягивается к segment[i-1] на фиксированную
/// дистанцию). Голова движется в одном из режимов:
///
///   WANDER — блуждание в каналах, поиск света.
///   HUNT   — заметил светлячка в радиусе → гонит к нему с ускорением.
///   EAT    — поймал → короткий фриз, +Hunger.
///   DIG    — упёрся в камень/кристалл → стоит N сек, потом разрушает клетку.
///
/// Вся симуляция data-driven: <see cref="Worm"/> структуры в массиве, позиции
/// сегментов в параллельном Vector2[]-массиве, единый MultiMeshInstance2D
/// рендерит ВСЕ сегменты ВСЕХ червей одной отрисовкой.
/// </summary>
public partial class WormColony : Node2D
{
    public const int SegmentCount = 5;

    [ExportGroup("Refs")]
    [Export] public MapGenerator Map;
    [Export] public RockField Rocks;
    [Export] public TileMapLayer SolidWalls;
    [Export] public CrystalField Crystals;
    [Export] public FireflyColony Fireflies;
    [Export] public Node2D Player;
    [Export] public Shader BodyShader;
    [Export] public Shader HaloShader;

    [ExportGroup("Population")]
    [Export(PropertyHint.Range, "0,100,1")] public int InitialPopulation = 14;
    [Export(PropertyHint.Range, "1,200,1")] public int MaxPopulation = 60;

    [ExportGroup("Body")]
    [Export(PropertyHint.Range, "8,64,1")] public float SegmentDistance = 22f;
    [Export(PropertyHint.Range, "8,64,1")] public float SegmentVisualSize = 28f;
    [Export(PropertyHint.Range, "8,128,1")] public float HaloVisualSize = 56f;

    [ExportGroup("Movement")]
    [Export(PropertyHint.Range, "5,200,1")] public float WanderSpeed = 60f;
    [Export(PropertyHint.Range, "5,300,1")] public float HuntSpeed = 120f;
    [Export(PropertyHint.Range, "0.05,5.0,0.05")] public float WanderTurnRate = 1.4f;
    [Export(PropertyHint.Range, "0.05,2.0,0.01")] public float SimulationTickRate = 0.10f;
    /// <summary>Шанс «броска» каждый тик: червь резко ускоряется на пару секунд.
    /// Делает поведение визуально более живым.</summary>
    [Export(PropertyHint.Range, "0,1,0.005")] public float BurstChancePerTick = 0.04f;
    [Export(PropertyHint.Range, "0.5,8,0.1")] public float BurstSpeedMultiplier = 2.0f;
    [Export(PropertyHint.Range, "0.2,5,0.1")] public float BurstDuration = 1.2f;

    [ExportGroup("Lifecycle")]
    [Export(PropertyHint.Range, "30,600,5")] public float BaseMaxAge = 180f;
    [Export(PropertyHint.Range, "0.0,1.0,0.05")] public float MaxAgeJitter = 0.30f;
    [Export(PropertyHint.Range, "0.1,10.0,0.1")] public float HungerDecay = 0.9f;
    [Export(PropertyHint.Range, "1,200,1")] public float HungerPerKill = 35f;

    [ExportGroup("Hunting")]
    /// <summary>Радиус видимости добычи. 220px было ~1.7 тайла — мало для
    /// поиска мобильных целей. 600 = ~5 тайлов.</summary>
    [Export(PropertyHint.Range, "10,2000,5")] public float HuntRadius = 600f;
    [Export(PropertyHint.Range, "5,80,1")] public float CatchDistance = 22f;
    [Export(PropertyHint.Range, "5,100,1")] public float HungerThresholdHunt = 70f;
    [Export(PropertyHint.Range, "0.1,5.0,0.1")] public float EatDuration = 0.7f;

    [ExportGroup("Exploration drive (carving territory, when sated)")]
    /// <summary>Шанс на тик WANDER зайти в режим «исследования» — выбрать
    /// случайную точку поодаль и идти к ней, копая стены по пути.</summary>
    [Export(PropertyHint.Range, "0,0.1,0.001")] public float ExploreChancePerTick = 0.012f;
    [Export(PropertyHint.Range, "30,1000,5")] public float ExploreMinDistance = 120f;
    [Export(PropertyHint.Range, "100,3000,10")] public float ExploreMaxDistance = 700f;
    [Export(PropertyHint.Range, "5,120,1")] public float ExploreDuration = 25f;
    [Export(PropertyHint.Range, "0,100,1")] public float ExploreHungerMin = 35f;

    [ExportGroup("Hungry seek (desperate search for food)")]
    /// <summary>Когда голодный червь не нашёл светлячка рядом, с этим
    /// шансом на тик отправляется искать в случайном направлении —
    /// через стены, в чужие пещеры. Не находит → умирает.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.005")] public float SeekChancePerTick = 0.06f;
    [Export(PropertyHint.Range, "100,3000,10")] public float SeekMinDistance = 250f;
    [Export(PropertyHint.Range, "200,5000,10")] public float SeekMaxDistance = 1400f;
    [Export(PropertyHint.Range, "5,180,1")] public float SeekDuration = 35f;
    /// <summary>Минимальный голод чтобы вообще предпринять seek-trip.
    /// Слишком слабый червь уже не способен на путешествие.</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float SeekHungerMin = 15f;

    [ExportGroup("Digging (heavy ecosystem constraints)")]
    /// <summary>Сколько секунд грызёт одну клетку.</summary>
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float DigDuration = 5f;
    /// <summary>Минимальный голод для начала копания. Голодный червь не может копать.</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float DigHungerMin = 55f;
    /// <summary>Сколько голода тратится на одно разрушение клетки.</summary>
    [Export(PropertyHint.Range, "0,100,1")] public float DigHungerCost = 25f;
    /// <summary>Откат после успешного прокопа (сек) — нельзя копать снова.</summary>
    [Export(PropertyHint.Range, "0,60,0.5")] public float DigCooldownAfterBreak = 8f;
    /// <summary>Лимит разрушений за всю жизнь червя. Ноль = безлимит.</summary>
    [Export(PropertyHint.Range, "0,500,1")] public int DigLifetimeBudget = 35;
    /// <summary>Сила «anti-claustrophobia»: 0 = чистый wander, выше = сильнее
    /// тянет в открытые направления (червь меньше упирается в стены).</summary>
    [Export(PropertyHint.Range, "0,10,0.1")] public float OpennessBias = 1.5f;

    [ExportGroup("Reproduction")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float BreedAgeMinFraction = 0.25f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BreedAgeMaxFraction = 0.80f;
    [Export(PropertyHint.Range, "0,100,1")] public float BreedHungerMin = 70f;
    [Export(PropertyHint.Range, "0,100,1")] public float BreedHungerCost = 40f;
    [Export(PropertyHint.Range, "0,1,0.005")] public float BreedChancePerTick = 0.025f;
    [Export(PropertyHint.Range, "10,500,5")] public float BreedSearchRadius = 90f;

    [ExportGroup("Replenishment (extinction prevention)")]
    /// <summary>Минимальная популяция. Если упало ниже — раз в
    /// ReplenishInterval спавнится новый (лор: «приползли из глубин»).</summary>
    [Export(PropertyHint.Range, "0,50,1")] public int MinPopulation = 3;
    /// <summary>Период проверки и подсадки (сек).</summary>
    [Export(PropertyHint.Range, "5,300,1")] public float ReplenishInterval = 25f;
    /// <summary>Минимальная дистанция от игрока для подсадки (px). Не спавнить
    /// прямо на голове — лор/иммерсия.</summary>
    [Export(PropertyHint.Range, "0,5000,50")] public float ReplenishMinDistanceFromPlayer = 1500f;

    [ExportGroup("Visual")]
    [Export(PropertyHint.Range, "-10,10,1")] public int VisualZIndex = 0;

    [Export] public ulong Seed = 0;

    private const byte STATE_WANDER = 0;
    private const byte STATE_HUNT = 1;
    private const byte STATE_DIG = 2;
    private const byte STATE_EAT = 3;
    private const byte STATE_EXPLORE = 4;

    private struct Worm
    {
        public Vector2 HeadVelocity;
        public float WanderAngle;
        public float Age;
        public float MaxAge;
        public float Hunger;
        public byte State;
        public Vector2 HuntTarget;
        public Vector2I DigTargetCell;
        public float DigProgress;
        public float EatTimer;
        public float DigCooldownLeft;       // ноль = можно копать
        public int   DigBudgetLeft;          // оставшийся лимит разрушений
        public float BurstTimeLeft;         // > 0 → бросок (ускорение)
        public Vector2 ExploreTarget;       // куда идёт в STATE_EXPLORE
        public float   ExploreUntilAge;     // когда выходит из STATE_EXPLORE
    }

    private Worm[] _worms;
    private Vector2[] _segments;        // _maxPopulation * SegmentCount
    private int _count;

    public const byte BIRTH_INITIAL = 0;
    public const byte BIRTH_BREED = 1;
    public const byte BIRTH_REPLENISH = 2;

    public int BornInitial { get; private set; }
    public int BornBreed { get; private set; }
    public int BornReplenish { get; private set; }
    public int DiedAge { get; private set; }
    public int DiedHunger { get; private set; }
    public int PeakPopulation { get; private set; }
    public int KilledFirefliesTotal { get; private set; }
    public int CellsDugTotal { get; private set; }
    public int BornTotal => BornInitial + BornBreed + BornReplenish;
    public int DiedTotal => DiedAge + DiedHunger;

    private RandomNumberGenerator _rng;
    private MultiMesh _multimesh;
    private MultiMeshInstance2D _renderer;
    private MultiMesh _haloMesh;
    private MultiMeshInstance2D _haloRenderer;
    private float _tickAccum;
    private float _replenishAccum;
    private bool _ready;
    private bool _initialized;
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
        if (Crystals == null && parent != null) Crystals = parent.GetNodeOrNull<CrystalField>("CrystalField");
        if (Fireflies == null && parent != null) Fireflies = parent.GetNodeOrNull<FireflyColony>("FireflyColony");
        if (Player == null && parent != null) Player = parent.GetNodeOrNull<Node2D>("Player");
        if (BodyShader == null) BodyShader = ResourceLoader.Load<Shader>("res://worm_body.gdshader");
        if (HaloShader == null) HaloShader = ResourceLoader.Load<Shader>("res://worm_halo.gdshader");

        if (Map == null)
        {
            GD.PushError("WormColony: ссылка на MapGenerator не найдена.");
            return;
        }

        _worms = new Worm[MaxPopulation];
        _segments = new Vector2[MaxPopulation * SegmentCount];

        Map.MapGenerated += OnMapReady;
        SetProcess(false);
    }

    private void OnMapReady()
    {
        if (_initialized) return;
        _initialized = true;

        _w = Map.Width;
        _h = Map.Height;
        if (Map.TileSet != null) _tilePx = Map.TileSet.TileSize.X;

        CreateRenderer();
        SpawnInitialWorms();
        UpdateRenderInstances();

        GD.Print($"WormColony: запущено {_count} червей.");
        _ready = true;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (!_ready) return;
        // Кап dt, как у светлячков — чтоб при больших фризах червь не
        // прыгнул через стену.
        float dt = Mathf.Min((float)delta, 0.05f);

        UpdateMovement(dt);

        _tickAccum += dt;
        if (_tickAccum >= SimulationTickRate)
        {
            _tickAccum = 0f;
            SimulationTick();
        }

        _replenishAccum += dt;
        if (_replenishAccum >= ReplenishInterval)
        {
            _replenishAccum = 0f;
            TryReplenish();
        }

        UpdateRenderInstances();
    }

    /// <summary>Гарантия от вымирания: если популяция ниже MinPopulation —
    /// подсаживаем нового подальше от игрока.</summary>
    private void TryReplenish()
    {
        if (_count >= MinPopulation) return;
        if (_count >= MaxPopulation) return;

        for (int attempt = 0; attempt < 200; attempt++)
        {
            int cx = _rng.RandiRange(20, _w - 20);
            int cy = _rng.RandiRange(20, _h - 20);
            var cell = new Vector2I(cx, cy);
            if (IsCellBlocked(cell)) continue;

            Vector2 pos = CellToWorld(cell);
            if (Player != null && pos.DistanceTo(Player.GlobalPosition) < ReplenishMinDistanceFromPlayer)
                continue;

            SpawnWorm(pos, _rng.RandfRange(0f, Mathf.Tau), BIRTH_REPLENISH);
            return;
        }
    }

    // ---- Public API for debug/external -------------------------------

    public int Count => _count;

    /// <summary>Случайная голова червя. False если червей нет.</summary>
    public bool TryFindRandomWormHead(out Vector2 pos)
    {
        if (_count == 0) { pos = Vector2.Zero; return false; }
        int idx = _rng.RandiRange(0, _count - 1);
        pos = _segments[idx * SegmentCount];
        return true;
    }

    /// <summary>Голова ближайшего к точке червя. False если червей нет.</summary>
    public bool TryFindNearestWormHead(Vector2 nearPos, out Vector2 pos)
    {
        if (_count == 0) { pos = Vector2.Zero; return false; }
        int bestIdx = 0;
        float bestDistSq = float.MaxValue;
        for (int i = 0; i < _count; i++)
        {
            Vector2 head = _segments[i * SegmentCount];
            float d = (head - nearPos).LengthSquared();
            if (d < bestDistSq) { bestDistSq = d; bestIdx = i; }
        }
        pos = _segments[bestIdx * SegmentCount];
        return true;
    }

    // ---- Spawning ------------------------------------------------------

    private void SpawnInitialWorms()
    {
        int spawned = 0;
        int attempts = 0;
        int maxAttempts = InitialPopulation * 100;
        while (spawned < InitialPopulation && attempts++ < maxAttempts)
        {
            int cx = _rng.RandiRange(20, _w - 20);
            int cy = _rng.RandiRange(20, _h - 20);
            var cell = new Vector2I(cx, cy);
            if (IsCellBlocked(cell)) continue;   // червяки начинают в открытых полостях

            Vector2 pos = CellToWorld(cell);
            SpawnWorm(pos, _rng.RandfRange(0f, Mathf.Tau));
            spawned++;
        }
    }

    private void SpawnWorm(Vector2 headPos, float facingAngle, byte source = BIRTH_INITIAL)
    {
        if (_count >= MaxPopulation) return;

        ref Worm w = ref _worms[_count];
        w.WanderAngle = facingAngle;
        w.HeadVelocity = new Vector2(Mathf.Cos(facingAngle), Mathf.Sin(facingAngle)) * WanderSpeed;
        w.Age = 0f;
        w.MaxAge = BaseMaxAge * (1f + _rng.RandfRange(-MaxAgeJitter, MaxAgeJitter));
        w.Hunger = _rng.RandfRange(60f, 90f);
        w.State = STATE_WANDER;
        w.DigProgress = 0f;
        w.EatTimer = 0f;
        w.DigCooldownLeft = 0f;
        w.DigBudgetLeft = DigLifetimeBudget > 0 ? DigLifetimeBudget : int.MaxValue;
        w.BurstTimeLeft = 0f;

        switch (source)
        {
            case BIRTH_BREED:     BornBreed++; break;
            case BIRTH_REPLENISH: BornReplenish++; break;
            default:              BornInitial++; break;
        }

        // Сегменты выкладываются в линию ПРОТИВ направления движения.
        Vector2 back = new Vector2(-Mathf.Cos(facingAngle), -Mathf.Sin(facingAngle));
        int baseIdx = _count * SegmentCount;
        for (int s = 0; s < SegmentCount; s++)
        {
            _segments[baseIdx + s] = headPos + back * (s * SegmentDistance);
        }

        _count++;
        if (_count > PeakPopulation) PeakPopulation = _count;
    }

    // ---- Per-frame: movement + body chain ------------------------------

    private void UpdateMovement(float dt)
    {
        for (int i = 0; i < _count; i++)
        {
            ref Worm w = ref _worms[i];
            int baseIdx = i * SegmentCount;
            Vector2 head = _segments[baseIdx];

            // Откат прокопа и burst-таймер тикают всегда.
            if (w.DigCooldownLeft > 0f) w.DigCooldownLeft -= dt;
            if (w.BurstTimeLeft > 0f) w.BurstTimeLeft -= dt;

            float burstMult = w.BurstTimeLeft > 0f ? BurstSpeedMultiplier : 1f;
            float speed = WanderSpeed * burstMult;
            Vector2 desiredVel;

            switch (w.State)
            {
                case STATE_HUNT:
                    speed = HuntSpeed * burstMult;
                    Vector2 toPrey = w.HuntTarget - head;
                    if (toPrey.LengthSquared() < 0.01f)
                        desiredVel = new Vector2(Mathf.Cos(w.WanderAngle), Mathf.Sin(w.WanderAngle)) * speed;
                    else
                        desiredVel = toPrey.Normalized() * speed;
                    w.WanderAngle = Mathf.Atan2(desiredVel.Y, desiredVel.X);
                    break;

                case STATE_EXPLORE:
                    // Идём к выбранной точке. Скорость средняя между Wander и Hunt.
                    speed = (WanderSpeed + HuntSpeed) * 0.5f * burstMult;
                    Vector2 toExp = w.ExploreTarget - head;
                    if (toExp.LengthSquared() < 0.01f)
                        desiredVel = new Vector2(Mathf.Cos(w.WanderAngle), Mathf.Sin(w.WanderAngle)) * speed;
                    else
                        desiredVel = toExp.Normalized() * speed;
                    w.WanderAngle = Mathf.Atan2(desiredVel.Y, desiredVel.X);
                    break;

                case STATE_EAT:
                    desiredVel = Vector2.Zero;
                    break;

                case STATE_DIG:
                    desiredVel = Vector2.Zero;
                    w.DigProgress += dt;
                    if (w.DigProgress >= DigDuration)
                    {
                        BreakCell(w.DigTargetCell);
                        w.Hunger -= DigHungerCost;
                        w.DigBudgetLeft--;
                        w.DigCooldownLeft = DigCooldownAfterBreak;
                        w.DigProgress = 0f;
                        // После прокопа возвращаемся в режим, который имеет смысл
                        // (если до этого охотились — продолжаем).
                        w.State = STATE_WANDER;
                    }
                    break;

                default:    // WANDER
                    w.WanderAngle += _rng.RandfRange(-WanderTurnRate, WanderTurnRate) * dt;
                    if (OpennessBias > 0.01f) ApplyOpennessBias(ref w, head, dt);
                    desiredVel = new Vector2(Mathf.Cos(w.WanderAngle), Mathf.Sin(w.WanderAngle)) * speed;
                    break;
            }

            w.HeadVelocity = w.HeadVelocity.Lerp(desiredVel, Mathf.Min(1f, dt * 6f));
            Vector2 newHead = head + w.HeadVelocity * dt;
            Vector2I newCell = WorldToCell(newHead);

            if (w.State == STATE_EAT)
            {
                // Стоим
            }
            else if (IsCellBlocked(newCell))
            {
                bool canDigCell = (Rocks != null && Rocks.HasRock(newCell))
                                  || (Crystals != null && Crystals.IsMature(newCell));

                bool wantsToDig = canDigCell
                                  && (w.State == STATE_HUNT || w.State == STATE_EXPLORE)
                                  && w.Hunger >= DigHungerMin          // не голодный
                                  && w.DigCooldownLeft <= 0f           // откат истёк
                                  && w.DigBudgetLeft > 0;              // лимит остался

                if (wantsToDig)
                {
                    w.State = STATE_DIG;
                    w.DigTargetCell = newCell;
                    w.DigProgress = 0f;
                }
                else
                {
                    // НЕ копаем — отскок. Это убирает «эрозию мира» в режиме wander.
                    w.WanderAngle += Mathf.Pi + _rng.RandfRange(-0.6f, 0.6f);
                    w.HeadVelocity = new Vector2(Mathf.Cos(w.WanderAngle), Mathf.Sin(w.WanderAngle)) * WanderSpeed;
                    if (w.State == STATE_HUNT || w.State == STATE_EXPLORE)
                        w.State = STATE_WANDER;  // сорвалась охота/исследование
                }
            }
            else
            {
                _segments[baseIdx] = newHead;
                if (w.State == STATE_DIG) w.State = STATE_WANDER;
            }

            UpdateBodyChain(baseIdx);
        }
    }

    /// <summary>
    /// «Anti-claustrophobia»: червь во время блуждания плавно поворачивает к
    /// направлению с большим количеством открытых клеток в окружении 5×5.
    /// Удерживает червей в существующих пещерах, минимизирует упирание в стены.
    /// </summary>
    private void ApplyOpennessBias(ref Worm w, Vector2 head, float dt)
    {
        var cell = WorldToCell(head);
        Vector2 openSum = Vector2.Zero;
        int openCount = 0;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var c = new Vector2I(cell.X + dx, cell.Y + dy);
                if (!IsCellBlocked(c))
                {
                    openSum += new Vector2(dx, dy);
                    openCount++;
                }
            }
        }
        if (openCount == 0) return;

        Vector2 openDir = openSum / openCount;
        if (openDir.LengthSquared() < 0.01f) return;
        float targetAngle = Mathf.Atan2(openDir.Y, openDir.X);
        float diff = Mathf.AngleDifference(w.WanderAngle, targetAngle);
        w.WanderAngle += diff * Mathf.Min(1f, dt * OpennessBias);
    }

    private void UpdateBodyChain(int baseIdx)
    {
        for (int s = 1; s < SegmentCount; s++)
        {
            Vector2 prev = _segments[baseIdx + s - 1];
            Vector2 curr = _segments[baseIdx + s];
            Vector2 toPrev = prev - curr;
            float dist = toPrev.Length();
            if (dist > SegmentDistance && dist > 0.001f)
            {
                _segments[baseIdx + s] = curr + toPrev * (1f - SegmentDistance / dist);
            }
        }
    }

    private void BreakCell(Vector2I cell)
    {
        if (Rocks != null && Rocks.HasRock(cell))
        {
            Rocks.Damage(cell, 9999);
            CellsDugTotal++;
        }
        else if (Crystals != null && Crystals.IsMature(cell))
        {
            Crystals.Damage(cell, 9999);
            CellsDugTotal++;
        }
    }

    // ---- Logic tick (10 Hz) --------------------------------------------

    private void SimulationTick()
    {
        for (int i = _count - 1; i >= 0; i--)
        {
            ref Worm w = ref _worms[i];
            int baseIdx = i * SegmentCount;
            Vector2 head = _segments[baseIdx];

            w.Age += SimulationTickRate;
            w.Hunger -= HungerDecay * SimulationTickRate;
            if (w.Hunger > 100f) w.Hunger = 100f;

            // Случайный «бросок» — резкое ускорение для живости.
            if (w.BurstTimeLeft <= 0f && _rng.Randf() < BurstChancePerTick)
            {
                w.BurstTimeLeft = BurstDuration;
            }

            // Логика по состояниям
            switch (w.State)
            {
                case STATE_EAT:
                    w.EatTimer -= SimulationTickRate;
                    if (w.EatTimer <= 0f) w.State = STATE_WANDER;
                    break;

                case STATE_HUNT:
                    // Проверка поимки
                    if ((w.HuntTarget - head).LengthSquared() <= CatchDistance * CatchDistance
                        && Fireflies != null
                        && Fireflies.TryKillFireflyAt(head, CatchDistance, out var _))
                    {
                        w.Hunger += HungerPerKill;
                        if (w.Hunger > 100f) w.Hunger = 100f;
                        w.State = STATE_EAT;
                        w.EatTimer = EatDuration;
                        KilledFirefliesTotal++;
                    }
                    else
                    {
                        // Цель ещё актуальна? Можно перезапросить ближайшую.
                        if (Fireflies == null
                            || !Fireflies.TryFindNearestFirefly(head, HuntRadius, out var newTarget))
                        {
                            w.State = STATE_WANDER;
                        }
                        else
                        {
                            w.HuntTarget = newTarget;
                        }
                    }
                    break;

                case STATE_WANDER:
                    // Если голодный и есть жертва — переключаемся на охоту.
                    // 1) Голодный — пытаемся охотиться на светлячка В РАДИУСЕ HuntRadius.
                    if (w.Hunger < HungerThresholdHunt
                        && Fireflies != null
                        && Fireflies.TryFindNearestFirefly(head, HuntRadius, out var prey))
                    {
                        w.HuntTarget = prey;
                        w.State = STATE_HUNT;
                        break;
                    }
                    // 2) Голодный, но рядом еды нет → отправляемся на дальнее
                    //    «отчаянное путешествие» в случайном направлении.
                    //    Будем копать стены если хватит голода — может найдём
                    //    другую пещеру с едой. Если нет — умрём в пути.
                    //    Это «случайный поиск как живое существо».
                    if (w.Hunger < HungerThresholdHunt
                        && w.Hunger > SeekHungerMin
                        && _rng.Randf() < SeekChancePerTick)
                    {
                        float angle = _rng.RandfRange(0f, Mathf.Tau);
                        float dist = _rng.RandfRange(SeekMinDistance, SeekMaxDistance);
                        w.ExploreTarget = head + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
                        w.ExploreUntilAge = w.Age + SeekDuration;
                        w.State = STATE_EXPLORE;
                        break;
                    }
                    // 3) Сытый и есть желание поисследовать — выбираем
                    //    случайную точку поодаль и идём туда (копая стены по
                    //    пути). Это даёт червям естественную «цель» копать,
                    //    расширять территорию.
                    if (w.Hunger > ExploreHungerMin
                        && _rng.Randf() < ExploreChancePerTick)
                    {
                        float angle = _rng.RandfRange(0f, Mathf.Tau);
                        float dist = _rng.RandfRange(ExploreMinDistance, ExploreMaxDistance);
                        w.ExploreTarget = head + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
                        w.ExploreUntilAge = w.Age + ExploreDuration;
                        w.State = STATE_EXPLORE;
                    }
                    break;

                case STATE_EXPLORE:
                    // Достигли цели или истёк таймер — обратно в WANDER.
                    if ((w.ExploreTarget - head).LengthSquared() < 400f
                        || w.Age >= w.ExploreUntilAge)
                    {
                        w.State = STATE_WANDER;
                    }
                    // Проголодался во время исследования — переключаемся на охоту.
                    else if (w.Hunger < HungerThresholdHunt
                             && Fireflies != null
                             && Fireflies.TryFindNearestFirefly(head, HuntRadius, out var prey2))
                    {
                        w.HuntTarget = prey2;
                        w.State = STATE_HUNT;
                    }
                    break;

                // STATE_DIG обновляется в UpdateMovement (timer)
            }

            // Смерть
            if (w.Age >= w.MaxAge)
            {
                RemoveAt(i);
                DiedAge++;
            }
            else if (w.Hunger <= 0f)
            {
                RemoveAt(i);
                DiedHunger++;
            }
        }

        // Размножение: один общий проход.
        TryReproductionPass();
    }

    private void TryReproductionPass()
    {
        if (_count >= MaxPopulation) return;
        if (Fireflies == null && _count < 2) return;

        for (int i = 0; i < _count; i++)
        {
            if (_count >= MaxPopulation) break;

            ref Worm w = ref _worms[i];
            float ageT = w.Age / Mathf.Max(0.01f, w.MaxAge);
            if (ageT < BreedAgeMinFraction || ageT > BreedAgeMaxFraction) continue;
            if (w.Hunger < BreedHungerMin) continue;
            if (_rng.Randf() > BreedChancePerTick) continue;

            // Партнёр в радиусе.
            Vector2 myHead = _segments[i * SegmentCount];
            int partner = -1;
            float bestDistSq = BreedSearchRadius * BreedSearchRadius;
            for (int j = 0; j < _count; j++)
            {
                if (j == i) continue;
                Vector2 oHead = _segments[j * SegmentCount];
                float dSq = (oHead - myHead).LengthSquared();
                if (dSq > bestDistSq) continue;

                ref Worm g = ref _worms[j];
                float gAgeT = g.Age / Mathf.Max(0.01f, g.MaxAge);
                if (gAgeT < BreedAgeMinFraction || gAgeT > BreedAgeMaxFraction) continue;
                if (g.Hunger < BreedHungerMin) continue;

                bestDistSq = dSq;
                partner = j;
            }
            if (partner < 0) continue;

            Vector2 spawnHead = (myHead + _segments[partner * SegmentCount]) * 0.5f;
            spawnHead += new Vector2(_rng.RandfRange(-12f, 12f), _rng.RandfRange(-12f, 12f));
            float facing = _rng.RandfRange(0f, Mathf.Tau);

            w.Hunger -= BreedHungerCost;
            _worms[partner].Hunger -= BreedHungerCost;
            SpawnWorm(spawnHead, facing, BIRTH_BREED);
        }
    }

    // ---- Render --------------------------------------------------------

    private void CreateRenderer()
    {
        // Halo (additive, под телом) — рисуется ПЕРВЫМ, т.е. ниже z.
        _haloMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseCustomData = true,
            InstanceCount = MaxPopulation * SegmentCount,
            VisibleInstanceCount = 0,
            Mesh = new QuadMesh { Size = new Vector2(HaloVisualSize, HaloVisualSize) },
        };
        _haloRenderer = new MultiMeshInstance2D
        {
            Multimesh = _haloMesh,
            ZIndex = VisualZIndex - 1,
        };
        if (HaloShader != null)
        {
            var haloMat = new ShaderMaterial { Shader = HaloShader };
            _haloRenderer.Material = haloMat;
        }
        AddChild(_haloRenderer);

        // Body (alpha, поверх halo).
        _multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseCustomData = true,
            InstanceCount = MaxPopulation * SegmentCount,
            VisibleInstanceCount = 0,
            Mesh = new QuadMesh { Size = new Vector2(SegmentVisualSize, SegmentVisualSize) },
        };
        _renderer = new MultiMeshInstance2D
        {
            Multimesh = _multimesh,
            ZIndex = VisualZIndex,
        };
        if (BodyShader != null)
        {
            var mat = new ShaderMaterial { Shader = BodyShader };
            _renderer.Material = mat;
        }
        AddChild(_renderer);
    }

    private void UpdateRenderInstances()
    {
        if (_multimesh == null) return;
        for (int i = 0; i < _count; i++)
        {
            ref Worm w = ref _worms[i];
            int baseIdx = i * SegmentCount;
            float hungerN = Mathf.Clamp(w.Hunger / 100f, 0f, 1f);
            float ageN = Mathf.Clamp(w.Age / Mathf.Max(0.01f, w.MaxAge), 0f, 1f);
            float stateFlag = w.State == STATE_DIG || w.State == STATE_EAT ? 1f : 0f;

            for (int s = 0; s < SegmentCount; s++)
            {
                int instIdx = baseIdx + s;
                var transform = new Transform2D(0f, _segments[instIdx]);
                var customData = new Color(
                    hungerN,
                    s == 0 ? 1f : 0f,
                    ageN,
                    stateFlag);

                _multimesh.SetInstanceTransform2D(instIdx, transform);
                _multimesh.SetInstanceCustomData(instIdx, customData);

                if (_haloMesh != null)
                {
                    _haloMesh.SetInstanceTransform2D(instIdx, transform);
                    _haloMesh.SetInstanceCustomData(instIdx, customData);
                }
            }
        }
        _multimesh.VisibleInstanceCount = _count * SegmentCount;
        if (_haloMesh != null) _haloMesh.VisibleInstanceCount = _count * SegmentCount;
    }

    // ---- Helpers -------------------------------------------------------

    private void RemoveAt(int idx)
    {
        int last = _count - 1;
        if (idx != last)
        {
            _worms[idx] = _worms[last];
            int dst = idx * SegmentCount;
            int src = last * SegmentCount;
            for (int s = 0; s < SegmentCount; s++)
                _segments[dst + s] = _segments[src + s];
        }
        _count--;
    }

    private bool IsCellBlocked(Vector2I cell)
    {
        // OOB = blocked.
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
