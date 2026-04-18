using Godot;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// Headless-симулятор экосистемы. Прогоняет серию сценариев с разными
/// параметрами, собирает временные ряды статистики и сохраняет их в CSV
/// плюс сводный markdown-отчёт.
///
/// Запуск:
///   godot --headless res://scenes/Sim.tscn
///
/// Каждый сценарий собирает свежий мини-мир (карта 400×400 — в 4 раза
/// меньше боевой, чтобы прогонять быстро), даёт ему N игровых секунд
/// и снимает срез статистики каждые M игровых секунд. Таймстеп
/// фиксированный (SimDt), что обеспечивает воспроизводимость.
/// </summary>
public partial class SimRunner : Node
{
    [ExportGroup("Run config")]
    [Export] public float ScenarioDuration = 1200f;     // 20 game-min per scenario
    [Export] public float SamplePeriod = 30f;
    [Export] public float SimDt = 0.05f;
    [Export] public int MaxTicksPerFrame = 800;
    [Export] public int RealTimeBudgetMs = 30;
    [Export] public ulong BaseSeed = 12345UL;

    [ExportGroup("World size")]
    /// <summary>Сторона карты в клетках. 400 = 1/4 боевой → быстрее в ~4×,
    /// но качественные тренды совпадают.</summary>
    [Export] public int MapSize = 400;

    [ExportGroup("Output")]
    [Export] public string OutputDir = "user://sim_runs";

    private List<Scenario> _scenarios = new();
    private int _scenarioIdx = -1;
    private Scenario _current;

    private Node _harness;
    private MapGenerator _map;
    private TileMapLayer _solidWalls;
    private RockField _rocks;
    private MossField _moss;
    private CrystalField _crystals;
    private FireflyColony _fireflies;
    private WormColony _worms;
    private MushroomField _mushrooms;

    private bool _harnessReady;          // карта сгенерирована, можно тикать
    private float _scenarioElapsed;
    private float _lastSampleTime;
    private int _stagedFrames;
    private List<Sample> _samples = new();
    private List<ScenarioResult> _results = new();
    private TileSet _tileSet;
    private string _outputDirAbs;

    public override void _Ready()
    {
        SimMode.Headless = true;
        ProcessMode = ProcessModeEnum.Always;

        // CSV/Markdown пишем из числовых значений — обязательно invariant
        // culture, иначе на ru-RU локали float'ы выходят с запятой и ломают CSV.
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        _outputDirAbs = ProjectSettings.GlobalizePath(OutputDir);
        DirAccess.MakeDirRecursiveAbsolute(_outputDirAbs);

        _tileSet = BuildTileSet();
        BuildScenarios();

        GD.Print("=== SimRunner ===");
        GD.Print($"Scenarios:           {_scenarios.Count}");
        GD.Print($"Per-scenario length: {ScenarioDuration} game-sec");
        GD.Print($"Sample period:       {SamplePeriod} game-sec");
        GD.Print($"Map size:            {MapSize}×{MapSize}");
        GD.Print($"Sim dt:              {SimDt} sec");
        GD.Print($"Output:              {_outputDirAbs}");
        GD.Print("==================");
    }

    // ---- TileSet (programmatic, no textures needed for sim) -------------

    private TileSet BuildTileSet()
    {
        // CreateTile валидирует, что регион внутри текстуры → нужны реальные
        // текстуры, даже если в headless их никто не отрисует.
        var floorTex = ResourceLoader.Load<Texture2D>("res://textures/Background_tiles.png");
        var wallTex = ResourceLoader.Load<Texture2D>("res://textures/SolidWalls.png");
        var rockTex = ResourceLoader.Load<Texture2D>("res://textures/Rock_tile.png");

        var floor = new TileSetAtlasSource
        {
            Texture = floorTex,
            TextureRegionSize = new Vector2I(128, 128),
            UseTexturePadding = false,
        };
        floor.CreateTile(new Vector2I(0, 0));
        floor.CreateTile(new Vector2I(1, 0));
        floor.CreateTile(new Vector2I(2, 0));

        var walls = new TileSetAtlasSource
        {
            Texture = wallTex,
            TextureRegionSize = new Vector2I(128, 128),
            UseTexturePadding = false,
        };
        walls.CreateTile(new Vector2I(0, 0));

        var rocks = new TileSetAtlasSource
        {
            Texture = rockTex,
            TextureRegionSize = new Vector2I(128, 128),
            UseTexturePadding = false,
        };
        rocks.CreateTile(new Vector2I(0, 0));

        var ts = new TileSet { TileSize = new Vector2I(128, 128) };
        ts.AddSource(floor, 0);
        ts.AddSource(walls, 1);
        ts.AddSource(rocks, 2);
        return ts;
    }

    // ---- Scenario list --------------------------------------------------

    private void BuildScenarios()
    {
        _scenarios.Add(new Scenario("00_baseline"));

        // ---- Generation ----
        _scenarios.Add(new Scenario("gen_dense_rock") { Set = { ["Rocks.NoiseThreshold"] = 0.32f } });
        _scenarios.Add(new Scenario("gen_sparse_rock") { Set = { ["Rocks.NoiseThreshold"] = 0.48f } });
        _scenarios.Add(new Scenario("gen_more_perlin") { Set = { ["Rocks.PerlinCaveDensity"] = 0.25f } });
        _scenarios.Add(new Scenario("gen_no_perlin") { Set = { ["Rocks.PerlinCaveDensity"] = 0.00f } });

        // ---- Fireflies hunger ----
        _scenarios.Add(new Scenario("ff_hunger_low") { Set = { ["Fireflies.HungerDecay"] = 1.2f } });
        _scenarios.Add(new Scenario("ff_hunger_high") { Set = { ["Fireflies.HungerDecay"] = 2.4f } });

        // ---- Fireflies lifecycle ----
        _scenarios.Add(new Scenario("ff_short_life") { Set = { ["Fireflies.BaseMaxAge"] = 120f } });
        _scenarios.Add(new Scenario("ff_long_life") { Set = { ["Fireflies.BaseMaxAge"] = 240f } });
        _scenarios.Add(new Scenario("ff_more_breed") { Set = { ["Fireflies.BreedChancePerTick"] = 0.10f } });

        // ---- Worms ----
        _scenarios.Add(new Scenario("worm_hunger_low") { Set = { ["Worms.HungerDecay"] = 0.5f } });
        _scenarios.Add(new Scenario("worm_hunger_high") { Set = { ["Worms.HungerDecay"] = 1.5f } });
        _scenarios.Add(new Scenario("worm_hunt_short") { Set = { ["Worms.HuntRadius"] = 300f } });
        _scenarios.Add(new Scenario("worm_hunt_long") { Set = { ["Worms.HuntRadius"] = 900f } });

        // ---- Crystals ----
        _scenarios.Add(new Scenario("crystal_slow") { Set = { ["Fireflies.FeedingsBeforeCrystal"] = 50 } });
        _scenarios.Add(new Scenario("crystal_fast") { Set = { ["Fireflies.FeedingsBeforeCrystal"] = 15 } });
    }

    // ---- Process loop ---------------------------------------------------

    public override void _Process(double delta)
    {
        if (_current == null)
        {
            StartNextScenario();
            return;
        }
        if (!_harnessReady)
        {
            // Дать движку доспавнить ноды и эмитнуть defer-сигналы.
            _stagedFrames++;
            if (_stagedFrames > 10 && _rocks != null)
            {
                // Аварийная страховка — если что-то пошло не так с сигналами.
                _harnessReady = true;
            }
            return;
        }

        // Пакетные тики.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SimMode.AllowProcess = true;
        try
        {
            int n = 0;
            while (n < MaxTicksPerFrame
                   && sw.ElapsedMilliseconds < RealTimeBudgetMs
                   && _scenarioElapsed < ScenarioDuration)
            {
                StepOnce(SimDt);
                _scenarioElapsed += SimDt;
                n++;

                if (_scenarioElapsed - _lastSampleTime >= SamplePeriod
                    || _scenarioElapsed >= ScenarioDuration)
                {
                    _samples.Add(TakeSample());
                    _lastSampleTime = _scenarioElapsed;
                }
            }
        }
        finally
        {
            SimMode.AllowProcess = false;
        }

        if (_scenarioElapsed >= ScenarioDuration)
        {
            FinishCurrentScenario();
        }
    }

    private void StepOnce(float dt)
    {
        // Порядок важен: камень может разрушаться от червей → мох обновляет
        // _obstacle, кристаллы зреют → коллижн… в headless коллижнов нет, но
        // сохраняем порядок для совпадения с боевым.
        _map?._Process(dt);
        _rocks?._Process(dt);
        _moss?._Process(dt);
        _crystals?._Process(dt);
        _fireflies?._Process(dt);
        _worms?._Process(dt);
    }

    // ---- Scenario lifecycle ---------------------------------------------

    private void StartNextScenario()
    {
        _scenarioIdx++;
        if (_scenarioIdx >= _scenarios.Count)
        {
            FinalizeAndQuit();
            return;
        }

        _current = _scenarios[_scenarioIdx];
        _samples.Clear();
        _scenarioElapsed = 0f;
        _lastSampleTime = 0f;
        _harnessReady = false;
        _stagedFrames = 0;

        GD.Print($"\n--- [{_scenarioIdx + 1}/{_scenarios.Count}] {_current.Name} ---");
        BuildHarness(_current);
    }

    private void FinishCurrentScenario()
    {
        // Снимаем финальный срез, если ещё не сделали.
        if (_samples.Count == 0 || _samples[_samples.Count - 1].T < _scenarioElapsed - 0.001f)
            _samples.Add(TakeSample());

        var result = new ScenarioResult
        {
            Name = _current.Name,
            Overrides = new Dictionary<string, object>(_current.Set),
            Samples = new List<Sample>(_samples),
        };
        _results.Add(result);
        WriteScenarioCsv(result);
        PrintScenarioSummary(result);

        // Тушим harness, освобождаем ресурсы.
        if (_harness != null)
        {
            _harness.QueueFree();
            _harness = null;
            _map = null; _solidWalls = null; _rocks = null;
            _moss = null; _crystals = null; _fireflies = null;
            _worms = null; _mushrooms = null;
        }
        _current = null;
    }

    private void FinalizeAndQuit()
    {
        WriteCombinedCsv();
        WriteMarkdownReport();
        GD.Print($"\n=== Done. {_results.Count} scenarios → {_outputDirAbs} ===");
        GetTree().Quit();
    }

    // ---- Harness build (programmatic) -----------------------------------

    private void BuildHarness(Scenario s)
    {
        _harness = new Node { Name = "Harness" };
        AddChild(_harness);

        ulong seed = BaseSeed + (ulong)_scenarioIdx * 7919UL;

        // 1) MapGenerator (TileMapLayer).
        _map = new MapGenerator
        {
            Name = "MapGenerator",
            Width = MapSize,
            Height = MapSize,
            Seed = seed,
            CellsPerFrame = int.MaxValue,
            TileSet = _tileSet,
            CollisionEnabled = false,
            ZIndex = -5,
        };

        // 2) SolidWalls (frame).
        _solidWalls = new TileMapLayer { Name = "SolidWalls", TileSet = _tileSet };

        // 3) Rocks.
        _rocks = new RockField
        {
            Name = "Rocks",
            TileSet = _tileSet,
            Seed = seed,
            // Применяем override'ы из сценария.
            NoiseThreshold = s.Get("Rocks.NoiseThreshold", 0.40f),
            PerlinCaveDensity = s.Get("Rocks.PerlinCaveDensity", 0.15f),
            CaIterations = (int)s.Get("Rocks.CaIterations", 3),
            CaWallNeighborMin = (int)s.Get("Rocks.CaWallNeighborMin", 5),
            // Wire references via Map property.
            Map = _map,
        };

        // 4) MossField.
        _moss = new MossField
        {
            Name = "MossField",
            Map = _map,
            Rocks = _rocks,
            SolidWalls = _solidWalls,
            Seed = seed,
        };

        // 5) CrystalField.
        _crystals = new CrystalField
        {
            Name = "CrystalField",
            Map = _map,
            Rocks = _rocks,
            SolidWalls = _solidWalls,
        };

        // 6) FireflyColony — обязательно ПОСЛЕ MossField (подписка на MossUpdated).
        _fireflies = new FireflyColony
        {
            Name = "FireflyColony",
            Map = _map,
            Rocks = _rocks,
            SolidWalls = _solidWalls,
            Moss = _moss,
            Crystals = _crystals,
            Seed = seed,
            // Population scaled to 1/4 area.
            InitialPopulation = (int)s.Get("Fireflies.InitialPopulation", 50),
            MaxPopulation = (int)s.Get("Fireflies.MaxPopulation", 160),
            InitialNearPlayerCount = (int)s.Get("Fireflies.InitialNearPlayerCount", 12),
            HungerDecay = s.Get("Fireflies.HungerDecay", 1.8f),
            BaseMaxAge = s.Get("Fireflies.BaseMaxAge", 180f),
            BreedChancePerTick = s.Get("Fireflies.BreedChancePerTick", 0.05f),
            FeedingsBeforeCrystal = (int)s.Get("Fireflies.FeedingsBeforeCrystal", 25),
            MinPopulation = (int)s.Get("Fireflies.MinPopulation", 12),
        };

        // 7) WormColony.
        _worms = new WormColony
        {
            Name = "WormColony",
            Map = _map,
            Rocks = _rocks,
            SolidWalls = _solidWalls,
            Crystals = _crystals,
            Fireflies = _fireflies,
            Seed = seed,
            InitialPopulation = (int)s.Get("Worms.InitialPopulation", 6),
            MaxPopulation = (int)s.Get("Worms.MaxPopulation", 25),
            HungerDecay = s.Get("Worms.HungerDecay", 0.9f),
            HuntRadius = s.Get("Worms.HuntRadius", 600f),
            MinPopulation = (int)s.Get("Worms.MinPopulation", 2),
        };

        // 8) MushroomField (one-shot at gen, не критично для динамики).
        _mushrooms = new MushroomField
        {
            Name = "MushroomField",
            Map = _map,
            Rocks = _rocks,
            SolidWalls = _solidWalls,
            Seed = seed,
            TargetCount = (int)s.Get("Mushrooms.TargetCount", 500),
        };

        // Сразу ссылаемся на SolidWalls в MapGenerator до AddChild — нужно в _Ready.
        _map.SolidWalls = _solidWalls;

        // Добавляем в дерево В ПРАВИЛЬНОМ ПОРЯДКЕ — для корректной фазы подписки
        // на MapGenerated/RocksGenerated.
        _harness.AddChild(_map);
        _harness.AddChild(_solidWalls);
        _harness.AddChild(_rocks);
        _harness.AddChild(_moss);
        _harness.AddChild(_crystals);
        _harness.AddChild(_fireflies);
        _harness.AddChild(_worms);
        _harness.AddChild(_mushrooms);

        // Слушаем «карта готова» — ждём конкретно RocksGenerated, чтоб _hp[]
        // был построен (от него зависит вся остальная флора/фауна).
        _rocks.RocksGenerated += () => { _harnessReady = true; };
    }

    // ---- Sample / output ------------------------------------------------

    private Sample TakeSample()
    {
        var s = new Sample
        {
            T = _scenarioElapsed,
            FfCount = _fireflies?.Count ?? 0,
            FfBornInit = _fireflies?.BornInitial ?? 0,
            FfBornBreed = _fireflies?.BornBreed ?? 0,
            FfBornReplenish = _fireflies?.BornReplenish ?? 0,
            FfBornMoss = _fireflies?.BornMoss ?? 0,
            FfDiedAge = _fireflies?.DiedAge ?? 0,
            FfDiedHunger = _fireflies?.DiedHunger ?? 0,
            FfDiedPredator = _fireflies?.DiedPredator ?? 0,
            FfPeak = _fireflies?.PeakPopulation ?? 0,
            WormCount = _worms?.Count ?? 0,
            WormBornInit = _worms?.BornInitial ?? 0,
            WormBornBreed = _worms?.BornBreed ?? 0,
            WormBornReplenish = _worms?.BornReplenish ?? 0,
            WormDiedAge = _worms?.DiedAge ?? 0,
            WormDiedHunger = _worms?.DiedHunger ?? 0,
            WormPeak = _worms?.PeakPopulation ?? 0,
            WormKilledFf = _worms?.KilledFirefliesTotal ?? 0,
            WormCellsDug = _worms?.CellsDugTotal ?? 0,
            CrystalGrowing = _crystals?.CurrentGrowing ?? 0,
            CrystalMature = _crystals?.CurrentMature ?? 0,
            CrystalSeeded = _crystals?.SeededTotal ?? 0,
            CrystalMatured = _crystals?.MaturedTotal ?? 0,
            CrystalDestroyed = _crystals?.DestroyedTotal ?? 0,
            MushroomCount = _mushrooms?.Count ?? 0,
            MossActive = _moss?.ActiveCells ?? 0,
            MossTotal = _moss?.TotalCells ?? 0,
            RockCount = _rocks?.CountRocks() ?? 0,
            RockTotal = _rocks?.TotalCells ?? 0,
        };
        return s;
    }

    private void WriteScenarioCsv(ScenarioResult r)
    {
        string path = Path.Combine(_outputDirAbs, $"{r.Name}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("t_sec,ff_count,ff_peak,ff_born_init,ff_born_breed,ff_born_replenish,ff_born_moss,"
            + "ff_died_age,ff_died_hunger,ff_died_predator,"
            + "worm_count,worm_peak,worm_born_init,worm_born_breed,worm_born_replenish,"
            + "worm_died_age,worm_died_hunger,worm_killed_ff,worm_cells_dug,"
            + "crystal_growing,crystal_mature,crystal_seeded,crystal_matured,crystal_destroyed,"
            + "mushroom_count,moss_active,moss_total,rock_count,rock_total");
        foreach (var s in r.Samples)
        {
            sb.AppendLine($"{s.T:F1},{s.FfCount},{s.FfPeak},{s.FfBornInit},{s.FfBornBreed},{s.FfBornReplenish},{s.FfBornMoss},"
                + $"{s.FfDiedAge},{s.FfDiedHunger},{s.FfDiedPredator},"
                + $"{s.WormCount},{s.WormPeak},{s.WormBornInit},{s.WormBornBreed},{s.WormBornReplenish},"
                + $"{s.WormDiedAge},{s.WormDiedHunger},{s.WormKilledFf},{s.WormCellsDug},"
                + $"{s.CrystalGrowing},{s.CrystalMature},{s.CrystalSeeded},{s.CrystalMatured},{s.CrystalDestroyed},"
                + $"{s.MushroomCount},{s.MossActive},{s.MossTotal},{s.RockCount},{s.RockTotal}");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private void PrintScenarioSummary(ScenarioResult r)
    {
        if (r.Samples.Count == 0) { GD.Print("  (no samples)"); return; }
        var last = r.Samples[r.Samples.Count - 1];
        var stats = ScenarioStats.From(r.Samples);

        GD.Print($"  ff: peak={last.FfPeak}, final={last.FfCount}, mean={stats.FfMean:F1}, min={stats.FfMin}");
        GD.Print($"  ff births: init={last.FfBornInit}, breed={last.FfBornBreed}, "
               + $"replenish={last.FfBornReplenish}, moss={last.FfBornMoss}");
        GD.Print($"  ff deaths: age={last.FfDiedAge}, hunger={last.FfDiedHunger}, predator={last.FfDiedPredator}");
        GD.Print($"  worms: peak={last.WormPeak}, final={last.WormCount}, killed_ff={last.WormKilledFf}, dug={last.WormCellsDug}");
        GD.Print($"  crystals: growing={last.CrystalGrowing}, mature={last.CrystalMature}, "
               + $"seeded={last.CrystalSeeded}, destroyed={last.CrystalDestroyed}");
        float rockPct = last.RockTotal > 0 ? 100f * last.RockCount / last.RockTotal : 0f;
        float mossPct = last.MossTotal > 0 ? 100f * last.MossActive / last.MossTotal : 0f;
        GD.Print($"  map: rock={rockPct:F1}%, moss={mossPct:F1}%, mushrooms={last.MushroomCount}");
    }

    private void WriteCombinedCsv()
    {
        string path = Path.Combine(_outputDirAbs, "_summary.csv");
        var sb = new StringBuilder();
        sb.AppendLine("scenario,ff_peak,ff_final,ff_mean,ff_min,ff_died_hunger,ff_died_predator,"
            + "worm_peak,worm_final,worm_killed_ff,worm_cells_dug,"
            + "crystal_mature_final,crystal_seeded,crystal_destroyed,"
            + "rock_pct_final,moss_pct_final,mushroom_count");
        foreach (var r in _results)
        {
            if (r.Samples.Count == 0) continue;
            var last = r.Samples[r.Samples.Count - 1];
            var stats = ScenarioStats.From(r.Samples);
            float rockPct = last.RockTotal > 0 ? 100f * last.RockCount / last.RockTotal : 0f;
            float mossPct = last.MossTotal > 0 ? 100f * last.MossActive / last.MossTotal : 0f;

            sb.AppendLine($"{r.Name},{last.FfPeak},{last.FfCount},{stats.FfMean:F1},{stats.FfMin},"
                + $"{last.FfDiedHunger},{last.FfDiedPredator},"
                + $"{last.WormPeak},{last.WormCount},{last.WormKilledFf},{last.WormCellsDug},"
                + $"{last.CrystalMature},{last.CrystalSeeded},{last.CrystalDestroyed},"
                + $"{rockPct:F1},{mossPct:F1},{last.MushroomCount}");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private void WriteMarkdownReport()
    {
        string path = Path.Combine(_outputDirAbs, "REPORT.md");
        var sb = new StringBuilder();
        sb.AppendLine("# Simulation Report");
        sb.AppendLine();
        sb.AppendLine($"- Scenarios: **{_results.Count}**");
        sb.AppendLine($"- Per-scenario duration: **{ScenarioDuration:F0} game-sec** ({ScenarioDuration / 60f:F1} min)");
        sb.AppendLine($"- Sample period: **{SamplePeriod:F0} game-sec**");
        sb.AppendLine($"- Map size: **{MapSize}×{MapSize}** (1/4 of live game's 800×800)");
        sb.AppendLine($"- Sim dt: **{SimDt:F2} sec**");
        sb.AppendLine();
        sb.AppendLine("## Summary table (final values)");
        sb.AppendLine();
        sb.AppendLine("| Scenario | FF peak | FF final | FF mean | FF min | FF died hunger | Worm peak | Worm final | Worm kills | Worm dug | Cryst mature | Rock % | Moss % | Mushrooms |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in _results)
        {
            if (r.Samples.Count == 0)
            {
                sb.AppendLine($"| {r.Name} | (no samples) | | | | | | | | | | | | |");
                continue;
            }
            var last = r.Samples[r.Samples.Count - 1];
            var stats = ScenarioStats.From(r.Samples);
            float rockPct = last.RockTotal > 0 ? 100f * last.RockCount / last.RockTotal : 0f;
            float mossPct = last.MossTotal > 0 ? 100f * last.MossActive / last.MossTotal : 0f;
            sb.AppendLine($"| {r.Name} | {last.FfPeak} | {last.FfCount} | {stats.FfMean:F1} | {stats.FfMin} | {last.FfDiedHunger} | "
                + $"{last.WormPeak} | {last.WormCount} | {last.WormKilledFf} | {last.WormCellsDug} | "
                + $"{last.CrystalMature} | {rockPct:F1} | {mossPct:F1} | {last.MushroomCount} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Per-scenario detail");
        foreach (var r in _results)
        {
            sb.AppendLine();
            sb.AppendLine($"### {r.Name}");
            if (r.Overrides.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Overrides:");
                foreach (var kv in r.Overrides) sb.AppendLine($"- `{kv.Key}` = `{kv.Value}`");
            }
            sb.AppendLine();
            if (r.Samples.Count == 0) { sb.AppendLine("*(no samples)*"); continue; }
            var last = r.Samples[r.Samples.Count - 1];
            var stats = ScenarioStats.From(r.Samples);

            sb.AppendLine("**Population dynamics**");
            sb.AppendLine();
            sb.AppendLine($"- Fireflies: peak **{last.FfPeak}**, final **{last.FfCount}**, mean {stats.FfMean:F1}, range {stats.FfMin}…{stats.FfMax}");
            sb.AppendLine($"- Worms: peak **{last.WormPeak}**, final **{last.WormCount}**, mean {stats.WormMean:F1}");
            sb.AppendLine();
            sb.AppendLine("**Lifetime totals**");
            sb.AppendLine();
            sb.AppendLine($"- FF born: init {last.FfBornInit}, breed {last.FfBornBreed}, replenish {last.FfBornReplenish}, moss {last.FfBornMoss}");
            sb.AppendLine($"- FF died: age {last.FfDiedAge}, hunger {last.FfDiedHunger}, predator {last.FfDiedPredator}");
            sb.AppendLine($"- Worm born: init {last.WormBornInit}, breed {last.WormBornBreed}, replenish {last.WormBornReplenish}");
            sb.AppendLine($"- Worm died: age {last.WormDiedAge}, hunger {last.WormDiedHunger}");
            sb.AppendLine($"- Worm interactions: kills **{last.WormKilledFf}**, cells dug **{last.WormCellsDug}**");
            sb.AppendLine($"- Crystals: growing {last.CrystalGrowing}, mature {last.CrystalMature}, seeded **{last.CrystalSeeded}**, destroyed {last.CrystalDestroyed}");

            float rockPct = last.RockTotal > 0 ? 100f * last.RockCount / last.RockTotal : 0f;
            float mossPct = last.MossTotal > 0 ? 100f * last.MossActive / last.MossTotal : 0f;
            sb.AppendLine($"- Map: rocks {rockPct:F1}%, moss {mossPct:F1}%, mushrooms {last.MushroomCount}");
        }
        File.WriteAllText(path, sb.ToString());
    }
}

// =====================================================================
// Helper data types
// =====================================================================

internal class Scenario
{
    public string Name;
    public Dictionary<string, object> Set = new();

    public Scenario(string name) { Name = name; }

    public float Get(string key, float def)
    {
        if (Set.TryGetValue(key, out var v))
        {
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is double d) return (float)d;
        }
        return def;
    }
}

internal struct Sample
{
    public float T;
    public int FfCount, FfPeak;
    public int FfBornInit, FfBornBreed, FfBornReplenish, FfBornMoss;
    public int FfDiedAge, FfDiedHunger, FfDiedPredator;
    public int WormCount, WormPeak;
    public int WormBornInit, WormBornBreed, WormBornReplenish;
    public int WormDiedAge, WormDiedHunger;
    public int WormKilledFf, WormCellsDug;
    public int CrystalGrowing, CrystalMature;
    public int CrystalSeeded, CrystalMatured, CrystalDestroyed;
    public int MushroomCount;
    public int MossActive, MossTotal;
    public int RockCount, RockTotal;
}

internal class ScenarioResult
{
    public string Name;
    public Dictionary<string, object> Overrides;
    public List<Sample> Samples;
}

internal struct ScenarioStats
{
    public float FfMean;
    public int FfMin, FfMax;
    public float WormMean;
    public int WormMin, WormMax;

    public static ScenarioStats From(List<Sample> samples)
    {
        var s = new ScenarioStats { FfMin = int.MaxValue, WormMin = int.MaxValue };
        long ffSum = 0, wormSum = 0;
        foreach (var x in samples)
        {
            ffSum += x.FfCount; wormSum += x.WormCount;
            if (x.FfCount < s.FfMin) s.FfMin = x.FfCount;
            if (x.FfCount > s.FfMax) s.FfMax = x.FfCount;
            if (x.WormCount < s.WormMin) s.WormMin = x.WormCount;
            if (x.WormCount > s.WormMax) s.WormMax = x.WormCount;
        }
        int n = System.Math.Max(1, samples.Count);
        s.FfMean = (float)ffSum / n;
        s.WormMean = (float)wormSum / n;
        if (samples.Count == 0) { s.FfMin = 0; s.WormMin = 0; }
        return s;
    }
}
