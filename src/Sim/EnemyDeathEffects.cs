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
    private const int ExpGrdMed       = 4;   // EXP_GRDMED
    private const int ExpGrdLarge     = 5;   // EXP_GRDLARGE
    private const int ExpBoss         = 6;   // EXP_BOSS
    private const int ExpEnergy       = 8;   // EXP_ENERGY → NRGBANG_BLK + S_ITEMBUY6 bonus
    private const int ItemBuy6ObjType = 23;

    // View explosion/anim codes used by the cascade spawns (DebugRenderer.ExpAnim).
    // Visual-only (not in the parity checkpoint schema); the RNG draws are what must
    // match C. Sub-anims without an ExpAnim slot (sparkle/flare) are not spawned.
    private const int AMedAirExplo   = 1;    // A_MED_AIR_EXPLO   → LGFLAK_BLK
    private const int AMedAirExplo2  = 10;   // A_MED_AIR_EXPLO2  → SMFLAK_BLK
    private const int ALargeAirExplo = 1;    // A_LARGE_AIR_EXPLO → LGFLAK_BLK
    private const int AGroundExplo   = 5;    // A_LARGE/SMALL_GROUND_EXPLO → GEXPLO_BLK

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

        // Exptype debris cascade. C draws the shared rand() stream here
        // (ENEMY.C:1152-1210), in this exact order, between the primary explosion
        // and the trailing bonus. Masked under deterministic RNG (offsets collapse to
        // center, no stream advance) but the draw count/order must match for
        // non-deterministic parity (findings #4/#5).
        AppendCascade(r, e, rng);

        // ENEMY.C:1221-1222 drops the sprite's own bonus if lib->bonus is set.
        if (e.Meta.Bonus >= 0)
            AddBonus(r, e.Meta.Bonus, e.X, e.Y, rng);

        return r;
    }

    private static void AppendCascade(Result r, EnemyLogic e, Random? rng)
    {
        int w  = Math.Max(1, e.Meta.Width);
        int h  = Math.Max(1, e.Meta.Height);
        int x0 = e.X, y0 = e.Y;
        int area = (e.Meta.Width >> 4) * (e.Meta.Height >> 4);

        switch (e.Meta.ExpType)
        {
            case ExpAirLargeCode:   // EXP_AIRLARGE: per area cell random(w), random(h)
                for (int i = 0; i < area; i++)
                {
                    int ox = PlayerShooter.NextRandom(rng, w, "airlarge.x");
                    int oy = PlayerShooter.NextRandom(rng, h, "airlarge.y");
                    int t  = (i & 1) == 1 ? AMedAirExplo : AMedAirExplo2;
                    r.Explosions.Add(new Spawn(t, x0 + ox, y0 + oy, i % 4));
                }
                break;

            case ExpGrdMed:         // EXP_GRDMED: random(w), random(h), random(2)
            {
                int gx = PlayerShooter.NextRandom(rng, w, "grdmed.x");
                int gy = PlayerShooter.NextRandom(rng, h, "grdmed.y");
                PlayerShooter.NextRandom(rng, 2, "grdmed.pick");   // sparkle/flare select (View)
                r.Explosions.Add(new Spawn(AGroundExplo, x0 + e.Meta.HalfX, y0 + e.Meta.HalfY, 0));
                _ = gx; _ = gy;
                break;
            }

            case ExpGrdLarge:       // EXP_GRDLARGE: per area cell random(w), random(h), random(2)
                r.Explosions.Add(new Spawn(AGroundExplo, x0 + e.Meta.HalfX, y0 + e.Meta.HalfY, 0));
                for (int i = 0; i < area; i++)
                {
                    int gx = PlayerShooter.NextRandom(rng, w, "grdlarge.x");
                    int gy = PlayerShooter.NextRandom(rng, h, "grdlarge.y");
                    PlayerShooter.NextRandom(rng, 2, "grdlarge.pick");   // flare/sparkle select (View)
                    r.Explosions.Add(new Spawn(AGroundExplo, x0 + gx, y0 + gy, i % 4));
                }
                break;

            case ExpBoss:           // EXP_BOSS: per area cell random(w), random(h)
                for (int i = 0; i < area; i++)
                {
                    int bx = PlayerShooter.NextRandom(rng, w, "boss.x");
                    int by = PlayerShooter.NextRandom(rng, h, "boss.y");
                    int t  = (i & 1) == 1 ? ALargeAirExplo : AMedAirExplo2;
                    r.Explosions.Add(new Spawn(t, x0 + bx, y0 + by, i % 4));
                }
                break;
        }
    }

    private static void AddBonus(Result r, int objType, int enemyX, int enemyY, Random? rng)
    {
        // C: BONUS_Add(type, sprite->x, sprite->y); BONUS_Add stores cur->x = x + MAP_LEFT.
        int initialPos = PlayerShooter.NextRandom(rng, 16, "bonus.pos");
        r.Bonuses.Add(new BonusDrop(objType, BonusSpawnXFromEnemyX(enemyX), enemyY, initialPos));
    }
}
