using System;
using Raptor.Sim;
using Raptor.Sim.Enemy;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Enemy-death cascade RNG parity (ENEMY.C:1131-1222), findings #4/#5. The death
/// explosion cascade must draw the shared rand() stream in C order and count, so any
/// kill keeps the per-wave stream aligned for all downstream consumers. Masked under
/// the deterministic-RNG corpus (offsets collapse to center, no stream advance), so
/// these are unit-verified by exact draw counts.
/// </summary>
public class EnemyDeathEffectsTests
{
    private sealed class CountingRandom : Random
    {
        public int Count;
        public override int Next(int maxValue) { Count++; return 0; }
        public override int Next() { Count++; return 0; }
    }

    private static EnemyLogic Enemy(int expType, int width, int height, int bonus = -1)
    {
        var meta = new SpriteMeta
        {
            IName = "TEST", Hits = 1, FlightType = 1,
            Width = width, Height = height, ExpType = expType, Bonus = bonus,
        };
        return new EnemyLogic(meta, spawnX: 100, mapY: 50);
    }

    // Total shared-RNG draws in EnemyDeathEffects.Build minus the 1 FX_AIREXPLO
    // sound draw (random(40)). Bonus=-1 ⇒ no trailing bonus draw, so the remainder
    // is exactly the exptype cascade.
    private static int CascadeDraws(int expType, int width, int height)
    {
        var prev = DeterministicRandom.Override;
        DeterministicRandom.Override = false;   // force NextOrMidpoint to draw rng
        try
        {
            var rng = new CountingRandom();
            EnemyDeathEffects.Build(Enemy(expType, width, height), rng, startIterForHash: 7);
            return rng.Count - 1;
        }
        finally { DeterministicRandom.Override = prev; }
    }

    [Fact]
    public void AirSmall_death_draws_no_cascade()
    {
        // EXP_AIRSMALL1 (0): a single anim, no cascade RNG.
        Assert.Equal(0, CascadeDraws(expType: 0, width: 48, height: 48));
    }

    [Fact]
    public void AirLarge_death_draws_two_per_area_cell()
    {
        // ENEMY.C:1152-1163 — area=(w>>4)*(h>>4); each loop draws random(w),random(h).
        // Was a private prime-hash with ZERO shared draws (finding #5).
        Assert.Equal(2 * 9, CascadeDraws(expType: 2, width: 48, height: 48));  // area 3*3
        Assert.Equal(2 * 2, CascadeDraws(expType: 2, width: 32, height: 16));  // area 2*1
    }

    [Fact]
    public void GrdMed_death_draws_three()
    {
        // ENEMY.C:1170-1178 — random(w), random(h), random(2). NOT area-looped.
        Assert.Equal(3, CascadeDraws(expType: 4, width: 48, height: 48));
        Assert.Equal(3, CascadeDraws(expType: 4, width: 16, height: 16));
    }

    [Fact]
    public void GrdLarge_death_draws_three_per_area_cell()
    {
        // ENEMY.C:1180-1195 — area loop: random(w), random(h), random(2).
        Assert.Equal(3 * 9, CascadeDraws(expType: 5, width: 48, height: 48));  // area 3*3
    }

    [Fact]
    public void Boss_death_draws_two_per_area_cell()
    {
        // ENEMY.C:1197-1210 — area loop: random(w), random(h).
        Assert.Equal(2 * 9, CascadeDraws(expType: 6, width: 48, height: 48));  // area 3*3
    }

    [Fact]
    public void Trailing_bonus_draws_one_more_random16()
    {
        // ENEMY.C:1221-1222 — if curlib->bonus != EMPTY, BONUS_Add draws random(16).
        var prev = DeterministicRandom.Override;
        DeterministicRandom.Override = false;
        try
        {
            var withBonus = new CountingRandom();
            EnemyDeathEffects.Build(Enemy(expType: 0, width: 48, height: 48, bonus: 5), withBonus, 7);
            // 1 sound + 0 cascade (AIRSMALL) + 1 trailing bonus = 2.
            Assert.Equal(2, withBonus.Count);
        }
        finally { DeterministicRandom.Override = prev; }
    }
}
