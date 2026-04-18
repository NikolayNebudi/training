using Godot;
using System.Collections.Generic;

/// <summary>
/// Сетка кристаллов: по образцу <see cref="MossField"/>. Каждая клетка хранит
/// «прогресс роста» (byte 0..255). Выше mature_threshold (~254) — зрелый
/// кристалл-блок с HP. Игрок может разбить (см. <see cref="Damage"/>).
/// Светлячки могут посеять зерно через <see cref="TrySeed"/>.
/// </summary>
public partial class CrystalField : Node2D
{
    [Signal] public delegate void CrystalSeededEventHandler(Vector2I cell);
    [Signal] public delegate void CrystalMaturedEventHandler(Vector2I cell);
    [Signal] public delegate void CrystalDamagedEventHandler(Vector2I cell, int hpLeft);
    [Signal] public delegate void CrystalDestroyedEventHandler(Vector2I cell);

    [ExportGroup("Refs")]
    [Export] public MapGenerator Map;
    [Export] public RockField Rocks;
    [Export] public TileMapLayer SolidWalls;
    [Export] public Shader CrystalShader;

    [ExportGroup("Growth")]
    /// <summary>Прирост роста за тик (тики — раз в TickInterval).</summary>
    [Export(PropertyHint.Range, "0.05,5.0,0.05")] public float GrowthPerTick = 1.0f;
    [Export(PropertyHint.Range, "0.05,5.0,0.05")] public float TickInterval = 0.5f;
    /// <summary>Сколько единиц HP у зрелого кристалла (по умолчанию 6 ударов × 25 урона).</summary>
    [Export(PropertyHint.Range, "10,1000,5")] public int CrystalMaxHp = 150;

    [ExportGroup("Visual")]
    [Export(PropertyHint.Range, "-10,10,1")] public int VisualZIndex = 0;

    private byte[] _growth;          // 0..255 для всех клеток
    private Dictionary<int, int> _hp = new();   // HP только для зрелых
    private int _w, _h;
    private int _tilePx = 128;
    private float _tickAccum;
    private bool _ready;

    private Image _growthImage;
    private ImageTexture _growthTexture;
    private Sprite2D _visual;
    private bool _dirty;

    private List<int> _growingIndices = new();   // index'ы клеток с растущими (быстрый Tick)
    private HashSet<int> _matureIndices = new(); // index'ы зрелых

    public override void _Ready()
    {
        Node parent = GetParent();
        if (Map == null && parent != null) Map = parent.GetNodeOrNull<MapGenerator>("MapGenerator");
        if (Rocks == null && parent != null) Rocks = parent.GetNodeOrNull<RockField>("Rocks");
        if (SolidWalls == null && parent != null) SolidWalls = parent.GetNodeOrNull<TileMapLayer>("SolidWalls");
        if (CrystalShader == null) CrystalShader = ResourceLoader.Load<Shader>("res://crystal.gdshader");

        if (Map == null)
        {
            GD.PushError("CrystalField: ссылка на MapGenerator не найдена.");
            return;
        }
        Map.MapGenerated += OnMapReady;
        SetProcess(false);
    }

    private void OnMapReady()
    {
        _w = Map.Width;
        _h = Map.Height;
        if (_w <= 0 || _h <= 0) return;
        if (Map.TileSet != null) _tilePx = Map.TileSet.TileSize.X;

        _growth = new byte[_w * _h];

        CreateVisual();

        _ready = true;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (!_ready) return;

        _tickAccum += (float)delta;
        if (_tickAccum >= TickInterval)
        {
            _tickAccum = 0f;
            GrowAll();
        }

        if (_dirty)
        {
            UpdateGrowthTexture();
            _dirty = false;
        }
    }

    // ---- Public API ----------------------------------------------------

    public bool InBounds(Vector2I cell) => cell.X >= 0 && cell.X < _w && cell.Y >= 0 && cell.Y < _h;

    public bool HasCrystal(Vector2I cell) => InBounds(cell) && _growth[cell.Y * _w + cell.X] > 0;

    public bool IsMature(Vector2I cell)
    {
        if (!InBounds(cell)) return false;
        return _growth[cell.Y * _w + cell.X] >= 254;
    }

    public byte GetGrowth(Vector2I cell)
        => InBounds(cell) ? _growth[cell.Y * _w + cell.X] : (byte)0;

    /// <summary>Может ли клетка принять семя кристалла. Должна быть полом
    /// (не камень, не стена), без существующего кристалла.</summary>
    public bool CanSeedAt(Vector2I cell)
    {
        if (!InBounds(cell)) return false;
        if (_growth[cell.Y * _w + cell.X] != 0) return false;
        if (Rocks != null && Rocks.HasRock(cell)) return false;
        if (SolidWalls != null && SolidWalls.GetCellSourceId(cell) >= 0) return false;
        return true;
    }

    public bool TrySeed(Vector2I cell)
    {
        if (!_ready || !CanSeedAt(cell)) return false;

        int idx = cell.Y * _w + cell.X;
        _growth[idx] = 1;
        _growingIndices.Add(idx);
        _dirty = true;
        EmitSignal(SignalName.CrystalSeeded, cell);
        return true;
    }

    /// <summary>Урон зрелому кристаллу. Возвращает true, если разрушен.</summary>
    public bool Damage(Vector2I cell, int amount)
    {
        if (!IsMature(cell) || amount <= 0) return false;

        int idx = cell.Y * _w + cell.X;
        if (!_hp.TryGetValue(idx, out int hp)) hp = CrystalMaxHp;
        hp -= amount;

        if (hp <= 0)
        {
            _growth[idx] = 0;
            _hp.Remove(idx);
            _matureIndices.Remove(idx);
            _dirty = true;
            EmitSignal(SignalName.CrystalDestroyed, cell);
            return true;
        }

        _hp[idx] = hp;
        EmitSignal(SignalName.CrystalDamaged, cell, hp);
        return false;
    }

    // ---- Growth tick ----------------------------------------------------

    private void GrowAll()
    {
        if (_growingIndices.Count == 0) return;

        int amount = Mathf.Max(1, Mathf.RoundToInt(GrowthPerTick));
        for (int i = _growingIndices.Count - 1; i >= 0; i--)
        {
            int idx = _growingIndices[i];
            int g = _growth[idx] + amount;
            if (g >= 254)
            {
                _growth[idx] = 254;
                _growingIndices.RemoveAt(i);
                _matureIndices.Add(idx);
                _hp[idx] = CrystalMaxHp;

                int x = idx % _w;
                int y = idx / _w;
                EmitSignal(SignalName.CrystalMatured, new Vector2I(x, y));
            }
            else
            {
                _growth[idx] = (byte)g;
            }
            _dirty = true;
        }
    }

    // ---- Visual ---------------------------------------------------------

    private void CreateVisual()
    {
        _growthImage = Image.CreateFromData(_w, _h, false, Image.Format.L8, _growth);
        _growthTexture = ImageTexture.CreateFromImage(_growthImage);

        _visual = new Sprite2D
        {
            Texture = _growthTexture,
            Centered = false,
            Position = Vector2.Zero,
            Scale = new Vector2(_tilePx, _tilePx),
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,  // важно: иначе межклеточные градиенты ломают diamond
            ZIndex = VisualZIndex,
        };

        if (CrystalShader != null)
        {
            var mat = new ShaderMaterial { Shader = CrystalShader };
            _visual.Material = mat;
        }

        AddChild(_visual);
    }

    private void UpdateGrowthTexture()
    {
        if (_growthImage == null || _growthTexture == null) return;
        _growthImage.SetData(_w, _h, false, Image.Format.L8, _growth);
        _growthTexture.Update(_growthImage);
    }
}
