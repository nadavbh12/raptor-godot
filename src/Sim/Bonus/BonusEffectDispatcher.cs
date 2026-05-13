using Raptor.Sim.Shots;

namespace Raptor.Sim.Bonus;

/// <summary>
/// Pure-C# helper that maps a bonus OBJ_TYPE to its in-game effect, mirroring
/// the C OBJS_Add / OBJS_AddEnergy dispatch (OBJECTS.C:691-720, BONUS.C:208-216):
///
///   S_FORWARD_GUNS (0)        — always owned; pickup is a no-op.
///   S_PLASMA_GUNS (1)         — adds to weapon inventory.
///   S_MICRO_MISSLE (2)        — adds to weapon inventory.
///   S_DUMB_MISSLE..S_DEATH_RAY (3..14) — adds to OwnedSpecials and sets active.
///   S_SUPER_SHIELD (15)       — Heal = MaxShield (full restore).
///   S_ENERGY (16)             — Heal = MaxShield / 4 (BONUS.C:214).
///   S_DETECT (17)             — sets DetectorActivated; moneyflag = FALSE → no score.
///   S_ITEMBUY1..S_ITEMBUY6 (18..23) — adds lib->cost to plr.score (OBJECTS.C:706-710).
///
/// Lives outside <see cref="WaveController"/> so it can be unit-tested without
/// constructing a Godot Node.
/// </summary>
public static class BonusEffectDispatcher
{
    /// <summary>
    /// Per-slot cost table for S_ITEMBUY1..6, ordered by objType - 18.
    /// Values from OBJECTS.C:481-572 lib->cost — must stay in sync with C.
    /// </summary>
    public static readonly int[] ItemBuyCost = { 93800, 76000, 55700, 35200, 122500, 50 };

    public struct Result
    {
        /// <summary>True iff GrantWeapon claimed the type (0..14).</summary>
        public bool GrantedWeapon;
        /// <summary>Shield delta to apply (S_SUPER_SHIELD / S_ENERGY).</summary>
        public int HealAmount;
        /// <summary>Score delta to apply (S_ITEMBUYn).</summary>
        public uint ScoreAdd;
        /// <summary>True iff S_DETECT was picked up.</summary>
        public bool DetectorActivated;
    }

    public static Result Apply(int objType, PlayerShooter shooter, int maxShield)
    {
        var r = new Result();
        if (shooter.GrantWeapon(objType))
        {
            r.GrantedWeapon = true;
            return r;
        }
        switch (objType)
        {
            case 15:                    // S_SUPER_SHIELD
                r.HealAmount = maxShield;
                break;
            case 16:                    // S_ENERGY
                r.HealAmount = maxShield / 4;
                break;
            case 17:                    // S_DETECT
                r.DetectorActivated = true;
                break;
            case >= 18 and <= 23:       // S_ITEMBUY1..6
                r.ScoreAdd = (uint)ItemBuyCost[objType - 18];
                break;
        }
        return r;
    }
}
