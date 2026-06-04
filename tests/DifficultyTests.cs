using Raptor.Sim;
using Xunit;

namespace RaptorTests;

public class DifficultyTests
{
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
