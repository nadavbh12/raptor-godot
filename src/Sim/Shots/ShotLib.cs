using System;
using System.Collections.Generic;

namespace Raptor.Sim.Shots;

/// <summary>
/// Per-weapon parameters. Mirrors SOURCE/SHOTS.H SHOT_LIB struct, populated by
/// SHOTS_Init in SOURCE/SHOTS.C. Immutable; cur_shoot lives on PlayerShooter.
/// </summary>
public sealed record ShotLib(
    ObjType Type,
    int        Hits,        // damage dealt per hit
    int        Speed,        // initial per-tick speed
    int        MaxSpeed,     // max per-tick speed (speed ramps each tick)
    int        StartFrame,
    int        NumFrames,
    int        ShootRate,    // cooldown frames after a shot
    bool       DelayFlag,
    bool       Smoke,
    bool       UsePlot,      // true = Bresenham move; false = straight-up `move.y -= speed`
    bool       MoveFlag,     // false = stationary (beams)
    bool       FplrX,        // follow player X each tick
    bool       FplrY,        // follow player Y each tick
    bool       Meffect,      // bullet tracks its move for separate hit detection (beams)
    HitType    Ht,
    BeamType   Beam,
    int        Hlx,          // sprite half-width (display offset, parity)
    int        Hly)          // sprite half-height
{
    /// <summary>
    /// The 15-entry table, indexed by ObjType. Values match SHOTS_Init in
    /// SOURCE/SHOTS.C verbatim. Hlx/Hly come from the sprite headers
    /// (NMSHOT_BLK frame 0 = 8x8 → hlx=hly=4; PLASMA_BLK = 8x8;
    /// MICROM_BLK = 8x8; etc.). When a weapon's pic[0] is null in C (S_TURRET
    /// has numframes=0), the wrapper falls back to 0; we use the same default.
    /// </summary>
    public static readonly IReadOnlyList<ShotLib> Table = BuildTable();

    public static ShotLib Get(ObjType t) => Table[(int)t];

    private static List<ShotLib> BuildTable()
    {
        var t = new List<ShotLib>(15);
        // S_FORWARD_GUNS — NMSHOT_BLK, 8x8 (hlx=hly=4)
        t.Add(new ShotLib(ObjType.ForwardGuns,
            Hits: 1, Speed: 8, MaxSpeed: 16, StartFrame: 0, NumFrames: 4,
            ShootRate: 2, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.All, Beam: BeamType.Shoot, Hlx: 4, Hly: 4));
        // S_PLASMA_GUNS — PLASMA_BLK, 8x8
        t.Add(new ShotLib(ObjType.PlasmaGuns,
            Hits: 2, Speed: 4, MaxSpeed: 8, StartFrame: 0, NumFrames: 2,
            ShootRate: 10, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.Air, Beam: BeamType.Shoot, Hlx: 4, Hly: 4));
        // S_MICRO_MISSLE — MICROM_BLK, 8x8
        t.Add(new ShotLib(ObjType.MicroMissile,
            Hits: 2, Speed: 2, MaxSpeed: 8, StartFrame: 0, NumFrames: 2,
            ShootRate: 4, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.GrAll, Beam: BeamType.Shoot, Hlx: 4, Hly: 4));
        // S_DUMB_MISSLE — MISDUM_BLK, 8x16 (hlx=4, hly=8)
        t.Add(new ShotLib(ObjType.DumbMissile,
            Hits: 4, Speed: 2, MaxSpeed: 12, StartFrame: 1, NumFrames: 3,
            ShootRate: 10, DelayFlag: true, Smoke: false, UsePlot: true,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.All, Beam: BeamType.Shoot, Hlx: 4, Hly: 12));  // MISDUM_BLK 8x24
        // S_MINI_GUN — NMSHOT_BLK, 8x8
        t.Add(new ShotLib(ObjType.MiniGun,
            Hits: 1, Speed: 8, MaxSpeed: 10, StartFrame: 1, NumFrames: 4,
            ShootRate: 1, DelayFlag: false, Smoke: false, UsePlot: true,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.GrAll, Beam: BeamType.Shoot, Hlx: 4, Hly: 4));
        // S_TURRET — no pic (numframes=0); S_LINE beam, instant
        t.Add(new ShotLib(ObjType.Turret,
            Hits: 5, Speed: 0, MaxSpeed: 0, StartFrame: 0, NumFrames: 0,
            ShootRate: 6, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: false, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.All, Beam: BeamType.Line, Hlx: 0, Hly: 0));
        // S_MISSLE_PODS — MISRAT_BLK, 8x16
        t.Add(new ShotLib(ObjType.MissilePods,
            Hits: 4, Speed: 1, MaxSpeed: 16, StartFrame: 0, NumFrames: 2,
            ShootRate: 5, DelayFlag: false, Smoke: true, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.Air, Beam: BeamType.Shoot, Hlx: 4, Hly: 8));
        // S_AIR_MISSLE — MISRAT_BLK, 8x16
        t.Add(new ShotLib(ObjType.AirMissile,
            Hits: 4, Speed: 1, MaxSpeed: 12, StartFrame: 0, NumFrames: 2,
            ShootRate: 10, DelayFlag: false, Smoke: true, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.Air, Beam: BeamType.Shoot, Hlx: 4, Hly: 8));
        // S_GRD_MISSLE — MISGRD_BLK, 8x16
        t.Add(new ShotLib(ObjType.GrdMissile,
            Hits: 20, Speed: 1, MaxSpeed: 6, StartFrame: 0, NumFrames: 2,
            ShootRate: 20, DelayFlag: false, Smoke: true, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.Ground, Beam: BeamType.Shoot, Hlx: 4, Hly: 8));
        // S_BOMB — BLDGBOMB_PIC, 16x16
        t.Add(new ShotLib(ObjType.Bomb,
            Hits: 50, Speed: 1, MaxSpeed: 4, StartFrame: 0, NumFrames: 1,
            ShootRate: 30, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.GTile, Beam: BeamType.Shoot, Hlx: 8, Hly: 8));
        // S_ENERGY_GRAB — POWDIS_BLK, 8x8
        t.Add(new ShotLib(ObjType.EnergyGrab,
            Hits: 3, Speed: 4, MaxSpeed: 8, StartFrame: 0, NumFrames: 6,
            ShootRate: 2, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.Suck, Beam: BeamType.Shoot, Hlx: 8, Hly: 8));  // POWDIS_BLK 16x16
        // S_MEGA_BOMB — MEGABM_BLK, 16x16
        t.Add(new ShotLib(ObjType.MegaBomb,
            Hits: 50, Speed: 2, MaxSpeed: 2, StartFrame: 0, NumFrames: 4,
            ShootRate: 60, DelayFlag: false, Smoke: false, UsePlot: true,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: true,
            Ht: HitType.All, Beam: BeamType.Shoot, Hlx: 4, Hly: 4));  // MEGABM_BLK 8x8
        // S_PULSE_CANNON — SHOKWV_BLK, 16x16 → hlx = width>>1 = 8 (C SHOTS.C:556).
        // (Was 16: a stale 32-wide assumption that shifted every pulse bullet 8px
        //  left of C, destroying ground tiles early — the wave-5 iter-558 parity bug.)
        t.Add(new ShotLib(ObjType.PulseCannon,
            Hits: 5, Speed: 8, MaxSpeed: 8, StartFrame: 0, NumFrames: 2,
            ShootRate: 3, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: true, FplrX: false, FplrY: false, Meffect: false,
            Ht: HitType.All, Beam: BeamType.Shoot, Hlx: 8, Hly: 8));  // SHOKWV_BLK 16x16
        // S_FORWARD_LASER — FRNTLAS_BLK, 8x8; beam follows player
        t.Add(new ShotLib(ObjType.ForwardLaser,
            Hits: 10, Speed: 0, MaxSpeed: 0, StartFrame: 0, NumFrames: 4,
            ShootRate: 7, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: false, FplrX: true, FplrY: true, Meffect: true,
            Ht: HitType.Air, Beam: BeamType.Beam, Hlx: 4, Hly: 4));
        // S_DEATH_RAY — DETHRY_BLK, 8x8; beam follows player
        t.Add(new ShotLib(ObjType.DeathRay,
            Hits: 6, Speed: 0, MaxSpeed: 0, StartFrame: 0, NumFrames: 4,
            ShootRate: 7, DelayFlag: false, Smoke: false, UsePlot: false,
            MoveFlag: false, FplrX: true, FplrY: true, Meffect: true,
            Ht: HitType.GrAll, Beam: BeamType.Beam, Hlx: 4, Hly: 4));
        return t;
    }

    /// <summary>Total number of weapon entries in the table.</summary>
    public static int Count => Table.Count;
}

/// <summary>
/// Banking-frame gun offsets — SOURCE/RAP.C:68-70 o_gun1/2/3. Indexed by
/// playerpic (0..6). 7 valid frames; the 8th C slot is zero-init padding.
/// </summary>
public static class GunOffsets
{
    public static readonly int[] OGun1 = { 1, 3, 5, 6, 5, 3, 1, 0 };
    public static readonly int[] OGun2 = { 1, 3, 6, 9, 6, 3, 2, 0 };
    public static readonly int[] OGun3 = { 2, 6, 8, 11, 8, 6, 2, 0 };
}
