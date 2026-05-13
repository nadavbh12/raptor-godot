namespace Raptor.Sim.Shots;

/// <summary>
/// Mirrors SOURCE/OBJECTS.H OBJ_TYPE enum. Integer values must match C — the
/// shot_lib table is indexed by this enum and parity emitters reference the
/// raw int. Anything past S_DeathRay is not a weapon (LAST_WEAPON = S_DeathRay).
/// </summary>
public enum WeaponType
{
    ForwardGuns  = 0,   // dual machine guns (BUT_1 default, forever)
    PlasmaGuns   = 1,   // plasma guns
    MicroMissile = 2,   // dual small wing missiles
    DumbMissile  = 3,   // dumb-fire missile (scatters)
    MiniGun      = 4,   // auto-tracking mini gun
    Turret       = 5,   // auto-tracking laser turret (line)
    MissilePods  = 6,   // dual missile pods (with smoke)
    AirMissile   = 7,   // air-to-air missile
    GrdMissile   = 8,   // air-to-ground missile
    Bomb         = 9,   // ground bomb (tile bomb)
    EnergyGrab   = 10,  // energy-suck shot
    MegaBomb     = 11,  // mega-bomb clears all air
    PulseCannon  = 12,  // pulse wave cannon
    ForwardLaser = 13,  // alternating laser (beam, follows player)
    DeathRay     = 14,  // death ray (beam, follows player)
}

/// <summary>Mirrors SOURCE/SHOTS.H HIT_TYPE. Controls which entities a bullet can hit.</summary>
public enum HitType
{
    All    = 0,   // S_ALL    — hits air + ground + tiles
    Air    = 1,   // S_AIR    — hits flying enemies only
    Ground = 2,   // S_GROUND — hits ground enemies + tiles
    GrAll  = 3,   // S_GRALL  — hits everything (used by guns)
    GTile  = 4,   // S_GTILE  — bombs tiles + ground
    Suck   = 5,   // S_SUCK   — energy-grab
}

/// <summary>Mirrors SOURCE/SHOTS.H BEAM_TYPE.</summary>
public enum BeamType
{
    Shoot = 0,    // standard projectile (S_SHOOT)
    Line  = 1,    // instant line beam (S_LINE; S_TURRET only)
    Beam  = 2,    // sustained vertical beam (S_BEAM; lasers)
}
