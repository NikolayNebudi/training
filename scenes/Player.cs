using Godot;

public partial class Player : CharacterBody2D
{
    [ExportGroup("Movement")]
    [Export] public float Speed = 480f;
    [Export(PropertyHint.Range, "1.0,3.0,0.1")] public float SprintMultiplier = 1.7f;

    [ExportGroup("Camera")]
    [Export] public Camera2D Camera;
    [Export] public float CameraLookAhead = 220f;
    [Export(PropertyHint.Range, "0.3,15.0,0.1")] public float CameraLookAheadSmoothing = 2.5f;
    [Export(PropertyHint.Range, "0.1,3.0,0.05")] public float CameraZoom = 0.9f;

    [ExportGroup("Drilling")]
    [Export] public RockField Rocks;
    /// <summary>Урон за один «удар» киркой.</summary>
    [Export(PropertyHint.Range, "1,255,1")] public int DrillDamagePerHit = 25;
    /// <summary>Минимальная пауза между ударами в секундах. Гарантирует, что
    /// спам ЛКМ не ускоряет бурение: и удержание, и быстрые клики дают одну
    /// и ту же DPS = DrillDamagePerHit / DrillCooldown.</summary>
    [Export(PropertyHint.Range, "0.05,2.0,0.01")] public float DrillCooldown = 0.18f;
    [Export] public AudioStreamPlayer2D DrillAudio;
    [Export] public CpuParticles2D DrillFx;
    [Export(PropertyHint.Range, "0.5,2.0,0.01")] public float DrillPitchMin = 0.92f;
    [Export(PropertyHint.Range, "0.5,2.0,0.01")] public float DrillPitchMax = 1.08f;

    [ExportGroup("Camera Shake")]
    [Export(PropertyHint.Range, "0,30,0.5")] public float DrillShakeAmount = 5f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float DrillShakeDecay = 12f;

    private Vector2 _cameraOffset;
    private Vector2 _cameraShake;
    private float _shakeIntensity;
    private float _drillCooldownLeft;
    private RandomNumberGenerator _rng;

    public override void _Ready()
    {
        _rng = new RandomNumberGenerator();
        _rng.Randomize();

        if (Camera == null)
        {
            foreach (var child in GetChildren())
                if (child is Camera2D cam) { Camera = cam; break; }
        }
        if (Camera != null) Camera.Zoom = new Vector2(CameraZoom, CameraZoom);

        // Фолбэк: при сохранённом старом формате Node-экспортов Godot 4.6
        // может не резолвнуть NodePath. Подбираем по фиксированному пути.
        Node parent = GetParent();
        if (Rocks == null && parent != null) Rocks = parent.GetNodeOrNull<RockField>("Rocks");
        if (DrillAudio == null) DrillAudio = GetNodeOrNull<AudioStreamPlayer2D>("DrillAudio");
        if (DrillFx == null && parent != null) DrillFx = parent.GetNodeOrNull<CpuParticles2D>("DrillParticles");
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        Vector2 input = ReadInput();
        float speed = Speed;
        if (Input.IsPhysicalKeyPressed(Key.Shift)) speed *= SprintMultiplier;

        Velocity = input * speed;
        MoveAndSlide();

        bool drilling = HandleDrilling(dt);
        UpdateCameraShake(drilling, dt);
        UpdateCameraLookAhead(input, dt);

        if (Camera != null) Camera.Offset = _cameraOffset + _cameraShake;
    }

    private bool HandleDrilling(float delta)
    {
        // Откат тикает всегда — даже когда не бурим. Это значит: после паузы
        // ≥ DrillCooldown первый клик сработает мгновенно, а спам кликов
        // вообще никак не ускоряет бурение.
        if (_drillCooldownLeft > 0f) _drillCooldownLeft -= delta;

        if (Rocks == null || !Input.IsMouseButtonPressed(MouseButton.Left)) return false;

        Vector2 mouseGlobal = GetGlobalMousePosition();
        Vector2I cell = Rocks.LocalToMap(Rocks.ToLocal(mouseGlobal));
        Vector2I playerCell = Rocks.LocalToMap(Rocks.ToLocal(GlobalPosition));
        Vector2I diff = cell - playerCell;

        if (!IsReachableTarget(cell, playerCell, diff)) return false;

        // Игрок «бурит» (для шейка камеры) даже между ударами в окне отката.
        if (_drillCooldownLeft > 0f) return true;

        Rocks.Damage(cell, DrillDamagePerHit);
        _drillCooldownLeft = DrillCooldown;
        EmitDrillHitFx(Rocks.ToGlobal(Rocks.MapToLocal(cell)));
        return true;
    }

    private bool IsReachableTarget(Vector2I cell, Vector2I playerCell, Vector2I diff)
    {
        // Не своя и в радиусе одной клетки по обоим осям (Чебышёв = 1).
        if (diff == Vector2I.Zero) return false;
        if (Mathf.Abs(diff.X) > 1 || Mathf.Abs(diff.Y) > 1) return false;

        // Цель должна быть камнем.
        if (!Rocks.HasRock(cell)) return false;

        // Для диагонали — нужно, чтобы хотя бы одна из двух «боковых»
        // (кардинальных) клеток между игроком и целью была свободна,
        // иначе кирка не пролезет в угол.
        if (diff.X != 0 && diff.Y != 0)
        {
            Vector2I sideX = new Vector2I(playerCell.X + diff.X, playerCell.Y);
            Vector2I sideY = new Vector2I(playerCell.X, playerCell.Y + diff.Y);
            if (Rocks.HasRock(sideX) && Rocks.HasRock(sideY)) return false;
        }

        return true;
    }

    private void EmitDrillHitFx(Vector2 globalPos)
    {
        if (DrillFx != null)
        {
            DrillFx.GlobalPosition = globalPos;
            DrillFx.Restart();
        }
        if (DrillAudio != null && DrillAudio.Stream != null)
        {
            DrillAudio.PitchScale = _rng.RandfRange(DrillPitchMin, DrillPitchMax);
            DrillAudio.Play();
        }
    }

    private void UpdateCameraShake(bool drilling, float delta)
    {
        if (drilling)
            _shakeIntensity = Mathf.Max(_shakeIntensity, DrillShakeAmount);

        // Кадрозависимое затухание: на любом fps одинаковая «тяжесть».
        float decayT = 1f - Mathf.Exp(-DrillShakeDecay * delta);
        _shakeIntensity = Mathf.Lerp(_shakeIntensity, 0f, decayT);
        if (_shakeIntensity < 0.05f) _shakeIntensity = 0f;

        _cameraShake = new Vector2(
            _rng.RandfRange(-1f, 1f),
            _rng.RandfRange(-1f, 1f)) * _shakeIntensity;
    }

    private static Vector2 ReadInput()
    {
        Vector2 input = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.A)) input.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D)) input.X += 1f;
        if (Input.IsPhysicalKeyPressed(Key.W)) input.Y -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S)) input.Y += 1f;
        return input.LengthSquared() > 0f ? input.Normalized() : Vector2.Zero;
    }

    private void UpdateCameraLookAhead(Vector2 input, float delta)
    {
        Vector2 target = input * CameraLookAhead;
        // Кадрозависимая интерполяция «exp-lerp» — корректно работает на любом fps.
        float t = 1f - Mathf.Exp(-CameraLookAheadSmoothing * delta);
        _cameraOffset = _cameraOffset.Lerp(target, t);
    }
}
