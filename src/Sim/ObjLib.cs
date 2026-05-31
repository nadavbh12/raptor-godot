using System;

namespace Raptor.Sim;

/// <summary>
/// Static metadata table for all purchasable/collectible object types.
/// Data sourced from OBJECTS.C:227-623 (OBJS_Init) in the original C codebase.
/// MAX_SHIELD = 100.
/// </summary>
public readonly record struct Entry(
    int  Cost,
    int  StartCnt,
    int  MaxCnt,
    bool Forever,
    bool OnlyFlag,
    bool SpecialW,
    bool MoneyFlag,
    bool LoseIt,
    bool Game1Flag
);

public static class ObjLib
{
    /// <summary>First weapon type that uses the special-weapon slot.</summary>
    public const ObjType FirstSpecial = ObjType.DumbMissile;  // 3

    /// <summary>Last weapon type (inclusive upper bound of weapon range).</summary>
    public const ObjType LastWeapon   = ObjType.DeathRay;     // 14

    // Indexed by (int)ObjType. Entries 0..23 are defined; type 24 has no C entry
    // (it falls in the gap between ItemBuy6=23 and LastObject=25), so the array
    // is sized to 24. Calling Of() with type 24 or LastObject(25) throws.
    private static readonly Entry[] s_table = new Entry[24]
    {
        //                                             cost    start  max    forever  only   special  money  loseit  game1
        /* 0  ForwardGuns  */ new(  12_000,       1,      1,  true,  false, false, false, false, true ),
        /* 1  PlasmaGuns   */ new(  78_800,       1,      1,  true,  false, false, false, true,  true ),
        /* 2  MicroMissile */ new( 175_600,       1,      1,  true,  false, false, false, true,  true ),
        /* 3  DumbMissile  */ new( 145_200,       1,      1,  true,  false, true,  false, true,  true ),
        /* 4  MiniGun      */ new( 250_650,       1,      1,  true,  false, true,  false, true,  true ),
        /* 5  Turret       */ new( 512_850,       1,      1,  true,  false, true,  false, true,  false),
        /* 6  MissilePods  */ new( 204_950,       1,      1,  true,  false, true,  false, true,  true ),
        /* 7  AirMissile   */ new(  63_500,       1,      1,  true,  false, true,  false, true,  true ),
        /* 8  GrdMissile   */ new( 110_000,       1,      1,  true,  false, true,  false, true,  true ),
        /* 9  Bomb         */ new(  98_200,       1,      1,  true,  false, true,  false, true,  false),
        /* 10 EnergyGrab   */ new( 300_750,       1,      1,  true,  false, true,  false, true,  false),
        /* 11 MegaBomb     */ new(  32_250,       1,      5,  false, true,  false, false, true,  true ),
        /* 12 PulseCannon  */ new( 725_000,       1,      1,  true,  false, true,  false, true,  true ),
        /* 13 ForwardLaser */ new(1_750_000,      1,      1,  true,  false, true,  false, true,  false),
        /* 14 DeathRay     */ new( 950_000,       1,      1,  true,  false, true,  false, true,  false),
        /* 15 SuperShield  */ new(  78_500,     100,    100,  false, false, false, false, false, true ),
        /* 16 Energy       */ new(     400,      25,    100,  true,  true,  false, false, false, true ),
        /* 17 Detect       */ new(  10_000,       1,      1,  false, true,  false, false, false, true ),
        /* 18 ItemBuy1     */ new(  93_800,  93_800, 93_800,  false, false, false, true,  true,  true ),
        /* 19 ItemBuy2     */ new(  76_000,  76_000, 76_000,  false, false, false, true,  true,  true ),
        /* 20 ItemBuy3     */ new(  55_700,  55_700, 55_700,  false, false, false, true,  true,  true ),
        /* 21 ItemBuy4     */ new(  35_200,  35_200, 35_200,  false, false, false, true,  true,  true ),
        /* 22 ItemBuy5     */ new( 122_500, 122_500,122_500,  false, false, false, true,  true,  true ),
        /* 23 ItemBuy6     */ new(      50,      50,     50,  false, false, false, true,  true,  true ),
    };

    /// <summary>
    /// Returns the metadata entry for the given object type.
    /// Throws <see cref="ArgumentOutOfRangeException"/> for undefined types (e.g. 24, LastObject=25).
    /// </summary>
    public static Entry Of(ObjType type)
    {
        int idx = (int)type;
        if ((uint)idx >= (uint)s_table.Length)
            throw new ArgumentOutOfRangeException(nameof(type), type, "No ObjLib entry for this ObjType.");
        return s_table[idx];
    }
}
