using System;
using System.Globalization;
using System.IO;
using Raptor.Sim.Player;
using Raptor.Sim.Shots;

namespace Raptor.Sim;

/// <summary>
/// Owns the per-tick shield/HUD bookkeeping that WaveController runs in PhaseHud:
/// the shield-recharge counter, the alternating palette-stuff RNG draw, the
/// low-shield "system damage" warning, and the env-gated shield trace. Stateful —
/// think_cnt persists across waves (mirroring C's OBJS_Think counter); the
/// WaveController owns this collaborator and drives it in phase order. The shooter
/// RNG is passed in each tick so the single shared stream stays shared. Death-
/// explosion processing is NOT here: WaveController sequences it between the
/// initial-skip frame and the main tick, exactly as the original PhaseHud did.
/// </summary>
internal sealed class ShieldHudController
{
    // CHARGE_SHIELD = 24*4 = 96. When think_cnt > 96, heal 1 shield (curplr_diff < DIFF_3).
    private const int ChargeShield = 96;
    private const int ShieldLow = 10;
    private const int Empty = -1;   // mirrors C EMPTY sentinel for g_oldshield

    private int _thinkCnt = 0;              // shield-recharge counter; persists across waves
    private int _oldShieldForLowLoss = Empty;
    private int _paletteStuffCnt = 0;
    private bool _skipInitialPaletteStuff = false;
    private readonly View.HudWarning.State _hudWarningState = new();

    private static readonly Lazy<StreamWriter?> ShieldTrace = new(OpenShieldTrace);

    public bool ShieldLowVisible => _hudWarningState.ShieldLowVisible;
    public bool SystemDamageVisible => _hudWarningState.SystemDamageVisible;

    /// <summary>
    /// Per-wave reset (LoadWave): clears the palette phase and re-arms the first-
    /// pass palette skip. think_cnt is deliberately NOT reset — it is continuous
    /// across waves, like C's OBJS_Think counter.
    /// </summary>
    public void ResetForWave(int currentShield)
    {
        _paletteStuffCnt = 0;
        _skipInitialPaletteStuff = true;
        _oldShieldForLowLoss = currentShield;
        // Clear any low-shield warning left over from the previous life, so it
        // doesn't linger on-screen through the start-of-wave fade-in hold (during
        // which PhaseHud — and thus the warning's Tick — does not run).
        _hudWarningState.Reset();
    }

    /// <summary>Re-baseline the low-loss comparator (demo player setup).</summary>
    public void SyncOldShield(int currentShield) => _oldShieldForLowLoss = currentShield;

    /// <summary>
    /// Mirrors C OBJS_Add (OBJECTS.C:788) resetting <c>g_oldshield = EMPTY</c> on every
    /// object pickup. Call when the player picks up a bonus: it makes the low-shield
    /// OBJS_LoseObj gate (RAP.C:631 <c>shield &lt; g_oldshield</c>) FALSE that frame —
    /// the existing <c>_oldShieldForLowLoss &gt;= 0</c> guard in <see cref="Tick"/> skips
    /// LoseObj exactly like C's <c>shield &lt; -1</c>. Prevents a spurious special-weapon
    /// Del/cycle when a money pickup coincides with the shield dropping below SHIELD_LOW
    /// (the wave-5 bench iter~4296 weapon-cycle desync). Tick re-baselines to the live
    /// shield at frame end, so the suppression lasts only the pickup frame, as in C.
    /// </summary>
    public void MarkObjectAdded() => _oldShieldForLowLoss = Empty;

    /// <summary>
    /// Handles the wave's first HUD pass, which skips RAP_PaletteStuff's RNG draw
    /// but still advances think_cnt: C's OBJS_Think runs that frame (OBJECTS.C:1361,
    /// skipped only on OBJS_Use). Dropping it left Godot's recharge 1 tick behind C
    /// (a 1-bucket shield transient, death_wave2 @ iter 1260). Returns true if this
    /// was the skip frame, in which case the caller returns without the main tick.
    /// </summary>
    public bool TickSkipInitial(PlayerLogic player, int curPlayerDiff, bool deathActive,
                                bool endWaveActive, bool objUsed)
    {
        if (!_skipInitialPaletteStuff) return false;
        _skipInitialPaletteStuff = false;
        var (skipTc, skipHeal) = ShieldRechargeStep(
            _thinkCnt, curPlayerDiff, ChargeShield, deathActive, endWaveActive, objUsed);
        _thinkCnt = skipTc;
        if (skipHeal) player.Heal(1);
        return true;
    }

    /// <summary>Per-tick palette-stuff RNG, shield recharge, and low-shield warning.</summary>
    public void Tick(PlayerLogic player, Inventory inventory, int curPlayerDiff,
                     bool deathActive, bool endWaveActive, int gameLoopIter, Random? shooterRng,
                     bool objUsed)
    {
        // RAP_PaletteStuff() consumes random(3) only on alternating calls
        // (`if (cnt & 1)`). It is visual, but C shares rand() with weapons.
        if ((_paletteStuffCnt & 1) != 0)
            PlayerShooter.NextRandom(shooterRng, 3, "palette.stuff");
        _paletteStuffCnt++;

        // Shield recharge (mirrors OBJS_Think in OBJECTS.C). CHARGE_SHIELD = 96.
        // Every 97 game loops, heal 1 shield. Only on curplr_diff < DIFF_3.
        var (newThinkCnt, heal) = ShieldRechargeStep(
            _thinkCnt, curPlayerDiff, ChargeShield, deathActive, endWaveActive, objUsed);
        _thinkCnt = newThinkCnt;
        if (heal) player.Heal(1);

        bool systemDamaged = false;
        if (_oldShieldForLowLoss >= 0
            && player.Shield <= ShieldLow
            && player.Shield < _oldShieldForLowLoss)
        {
            systemDamaged = inventory.LoseObj();
        }
        _hudWarningState.Tick(player.Shield, gameLoopIter, systemDamaged);
        TraceShield(gameLoopIter, player.Shield);
        _oldShieldForLowLoss = player.Shield;
    }

    /// <summary>
    /// Pure-C# shield-recharge step (mirrors OBJS_Think, OBJECTS.C:1361-1368).
    /// think_cnt increments and, on crossing CHARGE_SHIELD, resets to 0; the heal
    /// itself fires only when the end sequence is inactive (startendwave == EMPTY)
    /// so the ship cannot recharge — or revive — during the death-explosion
    /// countdown or the end-of-wave fly-off.
    /// </summary>
    public static (int thinkCnt, bool heal) ShieldRechargeStep(
        int thinkCnt, int diff, int chargeShield,
        bool deathActive, bool endWaveActive, bool objUsed)
    {
        // C OBJS_Think: `if (curplr_diff >= DIFF_3) return;` — elite never recharges.
        if (diff >= 3) return (thinkCnt, false);
        // C OBJS_Use set think_cnt=0 + objuse_flag; OBJS_Think then consumes the flag
        // and returns WITHOUT incrementing (OBJECTS.C:1393-1397). Net: any iter the
        // player uses a weapon (e.g. holding fire → OBJS_Use(FORWARD_GUNS)) resets
        // the recharge counter and cannot heal — so a firing player barely recharges.
        if (objUsed) return (0, false);
        thinkCnt++;
        bool heal = false;
        if (thinkCnt > chargeShield)
        {
            thinkCnt = 0;
            if (!deathActive && !endWaveActive) heal = true;
        }
        return (thinkCnt, heal);
    }

    private void TraceShield(int gameLoopIter, int shield)
    {
        var trace = ShieldTrace.Value;
        if (trace == null) return;
        trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "i={0} shield={1} old={2}", gameLoopIter, shield, _oldShieldForLowLoss));
        trace.Flush();
    }

    private static StreamWriter? OpenShieldTrace()
    {
        string? path = Environment.GetEnvironmentVariable("RAPTOR_SHIELD_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return null;
        return new StreamWriter(path) { AutoFlush = true };
    }
}
