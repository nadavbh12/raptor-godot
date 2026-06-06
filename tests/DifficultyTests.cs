using Raptor.Sim;
using Raptor.Test;
using Xunit;

namespace RaptorTests;

public class DifficultyTests
{
    // The demo-replay path must establish difficulty BEFORE LoadWave (mirroring C,
    // where RAP_SetPlayerDiff runs before RAP_LoadMap, INPUT.C:70->270) so the
    // iter-0 spawn is filtered by the correct mask. The difficulty comes from the
    // loadout snapshot (v2 demos); legacy demos (no snapshot) run at DIFF_3, since
    // C's DEMO_MakePlayer sets plr.diff = DIFF_3 (INPUT.C:64-71).
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]   // ROOKIE loadout → DIFF_1 (the parity-bug case: must NOT default to 2/3)
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void Demo_start_diff_uses_loadout_difficulty(int loadoutDiff, int expected)
    {
        var loadout = new DemoReplay.LoadoutData { Diff = loadoutDiff };
        Assert.Equal(expected, WaveController.DemoStartDiff(loadout));
    }

    [Fact]
    public void Demo_start_diff_defaults_to_elite_for_legacy_demos_without_loadout()
        => Assert.Equal(3, WaveController.DemoStartDiff(null));

    // Mirrors RAP_SetPlayerDiff (LOADSAVE.C): EB_EASY=8, EB_MED=16, EB_HARD=32.
    [Theory]
    [InlineData(0, 8)]   // DIFF_0 (training) → EASY
    [InlineData(1, 8)]   // DIFF_1 (rookie)   → EASY
    [InlineData(2, 24)]  // DIFF_2 (veteran)  → EASY|MED  (the default — parity baseline)
    [InlineData(3, 56)]  // DIFF_3 (elite)    → EASY|MED|HARD
    public void Spawn_mask_matches_c_set_player_diff(int diff, int expectedMask)
        => Assert.Equal(expectedMask, WaveController.SpawnMaskForDiff(diff));

    // Regression: PilotCreationFlow reset _difficultyFieldId to 3 (VETERAN) before
    // OnPilotCreated read it, so every pilot ran at DIFF_2 regardless of choice.
    // AcceptedDiff must capture the chosen difficulty at accept time.
    [Theory]
    [InlineData(1, 0)]  // TRAINING → DIFF_0
    [InlineData(2, 1)]  // ROOKIE   → DIFF_1
    [InlineData(3, 2)]  // VETERAN  → DIFF_2
    [InlineData(4, 3)]  // ELITE    → DIFF_3
    public void Accepted_difficulty_captures_choice_before_field_reset(int field, int expectedDiff)
    {
        var flow = new PilotCreationFlow();
        flow.SetStep(3);                  // in the difficulty dialog
        flow.SetDifficultyField(field);
        flow.HandleInput("Return");       // accept

        Assert.Equal(expectedDiff, flow.AcceptedDiff);
        Assert.Equal(3, flow.DifficultyFieldId);  // live field still resets — AcceptedDiff must not
    }
}
