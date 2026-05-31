namespace Raptor.Sim.Shots;

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
