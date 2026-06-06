using System;
using System.Collections.Generic;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;
using Raptor.Sim.Player;
using Raptor.Sim.Shots;
using Raptor.Test;

namespace Raptor.Sim;

/// <summary>
/// Applies the player's three fire buttons (BUT_1 fire / BUT_2 special-cycle /
/// BUT_3 megabomb) for both live/interactive play and demo replay, mirroring the
/// RAP.C:1000 BUT_n cascade. Owns the cross-tick edge-detection latches for the
/// cycle and megabomb buttons (held state must only fire on the rising edge).
///
/// Extracted from WaveController (E7). WaveController still owns the per-tick phase
/// order and calls this once in the input phase; the shared <see cref="Random"/> is
/// passed in so the single RNG stream stays single, and fired bullets are appended
/// to WaveController's <c>_playerBullets</c> list. Fire order (fire → special-cycle
/// → mega) and the trailing <c>Shooter.TickCooldowns()</c> stay in WaveController's
/// phase method, unchanged.
/// </summary>
internal sealed class PlayerButtonInput
{
    private bool _liveB2Latch;
    private bool _liveB3Latch;
    private bool _demoB2Latch;
    private bool _demoB3Latch;

    /// <summary>
    /// StartDemoPlayback resets the demo edge-latches before a fresh replay
    /// (mirrors the old per-demo-start reset of _demoB2Latch/_demoB3Latch).
    /// </summary>
    public void ResetDemoLatches()
    {
        _demoB2Latch = false;
        _demoB3Latch = false;
    }

    /// <summary>
    /// Live/interactive buttons. Mirrors RAP.C:1000 BUT_1 → OBJS_Use cascade:
    /// fire happens BEFORE SHOTS_Think decrements cooldowns, so the cooldown set
    /// this tick can't be cleared the same tick. The held-state edge model matches
    /// what the demo records (b1 is a held flag; C resets BUT_1 after firing).
    /// </summary>
    /// <returns>True if the player used an owned object this iter (C objuse_flag):
    /// holding fire uses the always-owned forward guns, or a megabomb fires on its
    /// rising edge. Consumed by the shield recharge (OBJS_Think skips that tick).</returns>
    public bool ApplyLive(
        bool fireHeld, bool fireSpHeld, bool megaHeld,
        PlayerShooter shooter, PlayerLogic player, bool waveActive,
        List<EnemyLogic> targetEnemies, List<EnemyLogic> enemies,
        List<BulletLogic> playerBullets, Random? rng)
    {
        int cx = player.X + 16;
        int cy = player.Y + 16;
        bool objUsed = false;

        if (waveActive && fireHeld)
        {
            // RAP.C:1005-1013 BUT_1 → OBJS_Use(FORWARD_GUNS): forward guns are always
            // owned (Forever), so the objuse_flag is set every iter fire is held —
            // independent of weapon cooldown (OBJS_Use sets it before lib->actf).
            objUsed = true;
            var fired = shooter.ApplyButton1(cx, cy, player.Pic, targetEnemies, rng);
            foreach (var b in fired) playerBullets.Add(b);
        }

        LiveInputLogic.ApplySpecialCycle(shooter, fireSpHeld, ref _liveB2Latch);

        if (megaHeld)
        {
            if (!_liveB3Latch)
            {
                _liveB3Latch = true;
                if (waveActive && shooter.MegaBombCount > 0)
                {
                    objUsed = true;   // OBJS_Use(MEGA_BOMB) when owned (RAP.C:1030-1037)
                    var fired = new List<BulletLogic>(1);
                    if (shooter.Shoot(ObjType.MegaBomb, cx, cy, player.Pic, fired, enemies, rng))
                    {
                        shooter.ConsumeMegaBomb();
                        foreach (var b in fired) playerBullets.Add(b);
                    }
                }
            }
        }
        else
        {
            _liveB3Latch = false;
        }

        return objUsed;
    }

    /// <summary>Demo-replay buttons (same cascade, driven by the recorded frame).</summary>
    /// <returns>True if an owned object was used this iter (see <see cref="ApplyLive"/>).</returns>
    public bool ApplyDemo(
        DemoReplay.Frame frame,
        PlayerShooter shooter, PlayerLogic player,
        List<EnemyLogic> targetEnemies, List<EnemyLogic> enemies,
        List<BulletLogic> playerBullets, Random? rng)
    {
        int cx = player.X + 16;
        int cy = player.Y + 16;
        bool objUsed = false;

        if (frame.B1 != 0)
        {
            objUsed = true;   // OBJS_Use(FORWARD_GUNS): always owned → objuse_flag set
            var fired = shooter.ApplyButton1(cx, cy, player.Pic, targetEnemies, rng);
            foreach (var b in fired) playerBullets.Add(b);
        }

        if (frame.B2 != 0)
        {
            if (!_demoB2Latch)
            {
                _demoB2Latch = true;
                shooter.CycleSpecial();   // BUT_2 → OBJS_GetNext (no OBJS_Use → no objuse)
            }
        }
        else
        {
            _demoB2Latch = false;
        }

        if (frame.B3 != 0)
        {
            if (!_demoB3Latch)
            {
                _demoB3Latch = true;
                if (shooter.MegaBombCount > 0)
                {
                    objUsed = true;   // OBJS_Use(MEGA_BOMB) when owned
                    var fired = new List<BulletLogic>(1);
                    if (shooter.Shoot(ObjType.MegaBomb, cx, cy, player.Pic, fired, enemies, rng))
                    {
                        shooter.ConsumeMegaBomb();
                        foreach (var b in fired) playerBullets.Add(b);
                    }
                }
            }
        }
        else
        {
            _demoB3Latch = false;
        }

        return objUsed;
    }
}
