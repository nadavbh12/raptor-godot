using Godot;

namespace Raptor.Sim.Player;

/// <summary>
/// Godot wrapper around PlayerLogic. Lives in WaveController's tree.
/// Position is read from PlayerLogic each tick and pushed to the Sprite2D.
/// </summary>
public partial class Player : Node2D
{
    public PlayerLogic Logic { get; } = new();

    public void Tick(int dx, int dy)
    {
        Logic.Tick(dx, dy);
        Position = new Vector2I(Logic.X, Logic.Y);  // LINT-OK: position is for view; sim state is in Logic.X/Y
    }
}
