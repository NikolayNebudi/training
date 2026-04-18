using Godot;

/// <summary>
/// Debug-контроллер для отладки экосистемы. Переключает активную Camera2D
/// между игроком и debug-камерой, которая следит за выбранным NPC.
///
///   1 — переключиться на ближайшего светлячка (повторно — назад к игроку).
///   2 — переключиться на случайного червя (повторно — назад к игроку).
///
/// Игровая логика продолжает работать в фоне (игрок управляется как обычно).
/// </summary>
public partial class DebugController : Node2D
{
    [Export] public Camera2D DebugCamera;
    [Export] public Camera2D PlayerCamera;
    [Export] public Node2D Player;
    [Export] public FireflyColony Fireflies;
    [Export] public WormColony Worms;
    [Export(PropertyHint.Range, "0,1,0.01")] public float CameraFollowSpeed = 0.18f;

    private const int MODE_PLAYER = 0;
    private const int MODE_FIREFLY = 1;
    private const int MODE_WORM = 2;

    private int _mode = MODE_PLAYER;

    public override void _Ready()
    {
        Node parent = GetParent();
        if (Player == null && parent != null) Player = parent.GetNodeOrNull<Node2D>("Player");
        if (Fireflies == null && parent != null) Fireflies = parent.GetNodeOrNull<FireflyColony>("FireflyColony");
        if (Worms == null && parent != null) Worms = parent.GetNodeOrNull<WormColony>("WormColony");
        if (DebugCamera == null && parent != null) DebugCamera = parent.GetNodeOrNull<Camera2D>("DebugCamera");
        if (PlayerCamera == null && Player != null)
            PlayerCamera = Player.GetNodeOrNull<Camera2D>("Camera2D");

        if (PlayerCamera != null) PlayerCamera.MakeCurrent();

        GD.Print($"DebugController ready. Player={Player?.Name}, " +
                 $"Fireflies={Fireflies?.Name}, Worms={Worms?.Name}, " +
                 $"DebugCamera={DebugCamera?.Name}, PlayerCamera={PlayerCamera?.Name}");
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey k) return;
        if (!k.Pressed || k.Echo) return;

        if (k.PhysicalKeycode == Key.Key1)
        {
            if (_mode == MODE_FIREFLY) SetModePlayer();
            else SetModeFirefly();
        }
        else if (k.PhysicalKeycode == Key.Key2)
        {
            if (_mode == MODE_WORM) SetModePlayer();
            else SetModeWorm();
        }
    }

    public override void _Process(double _delta)
    {
        if (DebugCamera == null) return;

        if (_mode == MODE_FIREFLY)
        {
            if (Fireflies != null
                && Fireflies.TryFindNearestFirefly(DebugCamera.GlobalPosition, 999999f, out var pos))
            {
                DebugCamera.GlobalPosition = DebugCamera.GlobalPosition.Lerp(pos, CameraFollowSpeed);
            }
            else
            {
                GD.Print("DebugController: светлячков нет, возврат к игроку.");
                SetModePlayer();
            }
        }
        else if (_mode == MODE_WORM)
        {
            if (Worms != null
                && Worms.TryFindNearestWormHead(DebugCamera.GlobalPosition, out var pos))
            {
                DebugCamera.GlobalPosition = DebugCamera.GlobalPosition.Lerp(pos, CameraFollowSpeed);
            }
            else
            {
                GD.Print("DebugController: червей нет, возврат к игроку.");
                SetModePlayer();
            }
        }
    }

    private void SetModePlayer()
    {
        _mode = MODE_PLAYER;
        if (PlayerCamera != null) PlayerCamera.MakeCurrent();
        GD.Print("DebugController: камера → игрок.");
    }

    private void SetModeFirefly()
    {
        if (Fireflies == null || Player == null || DebugCamera == null) return;
        if (!Fireflies.TryFindNearestFirefly(Player.GlobalPosition, 999999f, out var pos))
        {
            GD.Print("DebugController: нет светлячков для слежения.");
            return;
        }
        _mode = MODE_FIREFLY;
        DebugCamera.GlobalPosition = pos;
        DebugCamera.MakeCurrent();
        GD.Print($"DebugController: камера → ближайший светлячок ({pos}).");
    }

    private void SetModeWorm()
    {
        if (Worms == null || DebugCamera == null) return;
        if (!Worms.TryFindRandomWormHead(out var pos))
        {
            GD.Print("DebugController: нет червей для слежения.");
            return;
        }
        _mode = MODE_WORM;
        DebugCamera.GlobalPosition = pos;
        DebugCamera.MakeCurrent();
        GD.Print($"DebugController: камера → случайный червь ({pos}).");
    }
}
