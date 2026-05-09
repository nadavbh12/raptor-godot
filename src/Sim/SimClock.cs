using Godot;

namespace Raptor.Sim;

/// <summary>
/// Per-tick frame counter. Exists as an autoload so every sim entity
/// can read SimClock.Frame without holding a reference. Replaces the
/// C version's `framecount` global.
/// </summary>
public partial class SimClock : Node
{
    public static int Frame { get; private set; }

    public override void _Ready()
    {
        // Re-confirm the project.godot setting at runtime in case
        // someone overrides it via env or test fixture.
        Engine.PhysicsTicksPerSecond = 70;
        Engine.MaxPhysicsStepsPerFrame = OS.GetEnvironment("RAPTOR_TEST_FAST") == "1"
            ? 256
            : 8;
        if (OS.GetEnvironment("RAPTOR_TEST_FAST") == "1") {
            AudioServer.SetBusMute(0, true);
        }
    }

    public override void _PhysicsProcess(double _)
    {
        Frame++;
    }
}
