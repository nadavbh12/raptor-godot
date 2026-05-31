namespace Raptor.Sim;

/// <summary>
/// Mirrors SOURCE/OBJECTS.H OBJ_TYPE enum. Integer values must match C — inventory
/// tables and parity emitters reference the raw int. LastObject (25) mirrors S_LAST_OBJECT.
/// </summary>
public enum ObjType
{
    ForwardGuns  = 0,   // dual machine guns
    PlasmaGuns   = 1,   // plasma guns
    MicroMissile = 2,   // dual small wing missiles
    DumbMissile  = 3,   // dumb-fire missile
    MiniGun      = 4,   // auto-tracking mini gun
    Turret       = 5,   // auto-tracking laser turret
    MissilePods  = 6,   // dual missile pods
    AirMissile   = 7,   // air-to-air missile
    GrdMissile   = 8,   // air-to-ground missile
    Bomb         = 9,   // ground bomb
    EnergyGrab   = 10,  // energy-suck shot
    MegaBomb     = 11,  // mega-bomb
    PulseCannon  = 12,  // pulse wave cannon
    ForwardLaser = 13,  // alternating laser beam
    DeathRay     = 14,  // death ray beam
    SuperShield  = 15,  // super shield upgrade
    Energy       = 16,  // energy upgrade
    Detect       = 17,  // detection item
    ItemBuy1     = 18,  // purchasable item slot 1
    ItemBuy2     = 19,  // purchasable item slot 2
    ItemBuy3     = 20,  // purchasable item slot 3
    ItemBuy4     = 21,  // purchasable item slot 4
    ItemBuy5     = 22,  // purchasable item slot 5
    ItemBuy6     = 23,  // purchasable item slot 6
    LastObject   = 25,  // sentinel — mirrors S_LAST_OBJECT
}
