using Raptor.Sim.Player;
using Raptor.Sim.Shots;

namespace Raptor.Sim;

internal static class LiveInputLogic
{
    public static InputState Resolve(
        bool usePlaythrough,
        int playthroughDx,
        int playthroughDy,
        bool playthroughFire,
        bool playthroughSpecial,
        bool playthroughMega,
        InputState interactive)
    {
        return usePlaythrough
            ? InputState.From(
                playthroughDx,
                playthroughDy,
                playthroughFire,
                playthroughSpecial,
                playthroughMega,
                b4: false)
            : interactive;
    }

    public static void ApplySpecialCycle(PlayerShooter shooter, bool held, ref bool latch)
    {
        if (held)
        {
            if (!latch)
            {
                latch = true;
                shooter.CycleSpecial();
            }
        }
        else
        {
            latch = false;
        }
    }
}
