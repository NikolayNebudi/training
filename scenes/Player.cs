using Godot;
using System;

public partial class Player : CharacterBody2D
{
    [Export] public float Speed = 300f;
    public override void _PhysicsProcess(double delta)
    {
        Vector2 input = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.A))
            input.X -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.D))
            input.X += 1f;
        if (Input.IsPhysicalKeyPressed(Key.W))
            input.Y -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.S))
            input.Y += 1f;
        if (input.LengthSquared() > 0f)
            input = input.Normalized();

        Velocity = input * Speed;
        MoveAndSlide();
    }   
}
