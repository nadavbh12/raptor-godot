using System;
using System.Collections.Generic;
using Raptor.Sim.Enemy;
using Raptor.Sim.Shots;

namespace Raptor.Sim;

/// <summary>
/// Builds the side-effects of an enemy's death (mirrors ENEMY.C:1066-1115, 1154-1155):
/// the death-sound RNG draw, the primary explosion plus the EXP_AIRLARGE debris
/// cascade, and the bonus drop(s). Pure builder — it draws the shared RNG stream in
/// C order (sound, then the energy-explosion bonus pos, then the enemy's own bonus
/// pos) and returns the explosions and bonuses to spawn; WaveController applies them
/// via AddExplosion / _bonuses.Add (keeping the lists and the bonus trace there).
/// RNG is passed in so the draw order stays byte-identical.
/// </summary>
internal static class EnemyDeathEffects
{
    private const int ExpAirLargeCode = 2;   // EXP_AIRLARGE (SOURCE/MAP.H)
    private const int ExpEnergy       = 8;   // EXP_ENERGY → NRGBANG_BLK + S_ITEMBUY6 bonus
    private const int ItemBuy6ObjType = 23;

    internal readonly record struct Spawn(int ExpType, int X, int Y, int StartDelayIters);
    internal readonly record struct BonusDrop(int ObjType, int X, int Y, int InitialPos);

    internal sealed class Result
    {
        public List<Spawn> Explosions { get; } = new();
        public List<BonusDrop> Bonuses { get; } = new();
    }

    internal static int BonusSpawnXFromEnemyX(int enemyX) => enemyX + 16; // BONUS_Add adds MAP_LEFT.
    internal static int? BonusForExplosionType(int expType) => expType == ExpEnergy ? ItemBuy6ObjType : null;

    /// <param name="startIterForHash">AnimationStartIterForSpawn(gameLoopIter) — seeds the cascade offsets.</param>
    public static Result Build(EnemyLogic e, Random? rng, int startIterForHash)
    {
        var r = new Result();

        // ENEMY.C plays SND_3DPatch(FX_AIREXPLO) before the explosion; FX_AIREXPLO
        // has random pitch, so it consumes the shared rand() stream even when muted.
        PlayerShooter.NextRandom(rng, 40, "sound3d.fx_airexplo");

        // Primary explosion at the enemy centre.
        r.Explosions.Add(new Spawn(e.Meta.ExpType, e.X + e.Meta.HalfX, e.Y + e.Meta.HalfY, 0));

        // EXP_ENERGY explosions also drop an S_ITEMBUY6 bonus (drawn before the cascade).
        if (BonusForExplosionType(e.Meta.ExpType) is { } energyBonus)
            AddBonus(r, energyBonus, e.X, e.Y, rng);

        // EXP_AIRLARGE fires (width/16 * height/16) medium explosions at deterministic
        // pseudo-random offsets inside the sprite bounds (no RNG state mutated — the
        // mixing hash uses prime multipliers).
        if (e.Meta.ExpType == ExpAirLargeCode)
        {
            int w = e.Meta.Width;
            int h = e.Meta.Height;
            int count = (w >> 4) * (h >> 4);
            uint hash = (uint)(e.X * 73856093 ^ e.Y * 19349663 ^ startIterForHash * 83492791);
            for (int i = 0; i < count; i++)
            {
                hash = hash * 1103515245u + 12345u;
                int ox = (int)((hash >> 8) % (uint)Math.Max(1, w));
                hash = hash * 1103515245u + 12345u;
                int oy = (int)((hash >> 8) % (uint)Math.Max(1, h));
                int t = (i & 1) == 1 ? 1 /* EXP_AIRMED → A_MED_AIR_EXPLO */
                                     : 10 /* EXP_AIRSMALL2 → A_MED_AIR_EXPLO2 */;
                // Stagger start iteration slightly so the cascade doesn't appear all
                // at once (matches C's per-loop ANIMS_StartAnim spacing).
                r.Explosions.Add(new Spawn(t, e.X + ox, e.Y + oy, i % 4));
            }
        }

        // ENEMY.C:1154-1155 drops the sprite's own bonus if lib->bonus is set.
        if (e.Meta.Bonus >= 0)
            AddBonus(r, e.Meta.Bonus, e.X, e.Y, rng);

        return r;
    }

    private static void AddBonus(Result r, int objType, int enemyX, int enemyY, Random? rng)
    {
        // C: BONUS_Add(type, sprite->x, sprite->y); BONUS_Add stores cur->x = x + MAP_LEFT.
        int initialPos = PlayerShooter.NextRandom(rng, 16, "bonus.pos");
        r.Bonuses.Add(new BonusDrop(objType, BonusSpawnXFromEnemyX(enemyX), enemyY, initialPos));
    }
}
