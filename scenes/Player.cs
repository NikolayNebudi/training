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
    [Export(PropertyHint.Range, "1,1000,1")] public int DrillPower = 160;
    [Export(PropertyHint.Range, "0.5,12.0,0.1")] public float DrillRange = 2.5f;

    private Vector2 _cameraOffset;

    public override void _Ready()
    {
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
    }

    public override void _PhysicsProcess(double delta)
    {
        Vector2 input = ReadInput();
        float speed = Speed;
        if (Input.IsPhysicalKeyPressed(Key.Shift)) speed *= SprintMultiplier;

        Velocity = input * speed;
        MoveAndSlide();

        UpdateCameraLookAhead(input, (float)delta);
        HandleDrilling((float)delta);
    }

    private void HandleDrilling(float delta)
    {
        if (Rocks == null) return;
        if (!Input.IsMouseButtonPressed(MouseButton.Left)) return;

        Vector2 mouseGlobal = GetGlobalMousePosition();
        Vector2I cell = Rocks.LocalToMap(Rocks.ToLocal(mouseGlobal));

        Vector2I playerCell = Rocks.LocalToMap(Rocks.ToLocal(GlobalPosition));
        Vector2I diff = cell - playerCell;
        if (diff.X * diff.X + diff.Y * diff.Y > DrillRange * DrillRange) return;

        if (!Rocks.HasRock(cell)) return;

        int dmg = Mathf.Max(1, Mathf.RoundToInt(DrillPower * delta));
        Rocks.Damage(cell, dmg);
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
        if (Camera == null) return;

        Vector2 target = input * CameraLookAhead;
        // Кадрозависимая интерполяция «exp-lerp» — корректно работает на любом fps.
        float t = 1f - Mathf.Exp(-CameraLookAheadSmoothing * delta);
        _cameraOffset = _cameraOffset.Lerp(target, t);
        Camera.Offset = _cameraOffset;
    }
}
