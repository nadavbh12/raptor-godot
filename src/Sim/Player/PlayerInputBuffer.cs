using Godot;

namespace Raptor.Sim.Player;

/// <summary>
/// Reads Godot Input action map per tick and produces an InputState.
/// Action names (configure in project.godot in a later task; for now we
/// look up these strings — Godot returns false if they aren't mapped):
///   "move_up", "move_down", "move_left", "move_right"
///   "fire_main", "fire_special", "drop_bomb", "pause"
/// </summary>
public partial class PlayerInputBuffer : Node
{
    public InputState Current { get; private set; } = InputState.Idle;

    public override void _PhysicsProcess(double _) { Tick(); }

    public void Tick()
    {
        int dx = (Input.IsActionPressed("move_right") ? 1 : 0)
               - (Input.IsActionPressed("move_left")  ? 1 : 0);
        int dy = (Input.IsActionPressed("move_down")  ? 1 : 0)
               - (Input.IsActionPressed("move_up")    ? 1 : 0);
        Current = InputState.From(
            dx, dy,
            b1: Input.IsActionPressed("fire_main"),
            b2: Input.IsActionPressed("fire_special"),
            b3: Input.IsActionPressed("drop_bomb"),
            b4: Input.IsActionPressed("pause"));
    }
}
