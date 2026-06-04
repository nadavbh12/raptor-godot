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
}
