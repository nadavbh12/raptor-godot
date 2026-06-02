namespace Raptor.Sim;

/// <summary>
/// The end-of-wave fly-off + mission-complete sequence (RAP.C:599-617, 1039-1047).
/// Once the wave-end conditions hold (<see cref="ShouldCompleteMission"/>), a
/// countdown starts from <see cref="EndWaveSequence.Duration"/>; each tick yields
/// the player's forced fly-off displacement, and on the frame it reaches zero the
/// mission completes (one-shot). Owns the countdown and the notified latch;
/// WaveController drives it in phase order and applies the returned forced-move /
/// mission-complete signal (return-a-result pattern), since completion mutates
/// WaveController core (waveActive + the menu callback). The spawn-exhausted flag
/// (_endWaveFlag) stays in WaveController and is passed in.
/// </summary>
internal sealed class EndWaveSequencer
{
    private int _countdown = -1;
    private bool _notified = false;

    /// <summary>True during the end-wave fly-off (suppresses damage, gates input/recharge).</summary>
    public bool Active => _countdown >= 0;

    /// <summary>Remaining fly-off countdown (for the View HUD and the input-lock check).</summary>
    public int Countdown => _countdown;

    /// <summary>Reset per wave.</summary>
    public void Reset()
    {
        _countdown = -1;
        _notified = false;
    }

    internal readonly record struct TickResult(int ForcedDx, int ForcedDy, bool MissionComplete);

    /// <summary>
    /// One end-wave tick. Starts the countdown when the wave-end conditions first
    /// hold; while it runs, returns the per-tick forced fly-off displacement; on the
    /// frame it reaches zero returns MissionComplete=true (one-shot). No-op once the
    /// mission has completed.
    /// </summary>
    public TickResult Tick(bool waveActive, bool demoActive, bool spawnExhausted,
                           bool playerAlive, int playerX, bool enemiesRemaining)
    {
        if (_notified) return default;

        // Start the fly-off countdown once the wave-end conditions are met, instead
        // of jumping straight to the hangar (mirrors RAP.C:1039-1047 where
        // startendwave counts down from END_DURATION before end_wave fires).
        if (_countdown < 0
            && ShouldCompleteMission(waveActive, demoActive, spawnExhausted, playerAlive, enemiesRemaining))
        {
            _countdown = EndWaveSequence.Duration;
        }

        if (_countdown < 0) return default;

        // Per-tick fly-off displacement (RAP.C:599-617).
        var (dx, dy) = EndWaveSequence.PlayerDelta(_countdown, playerX, playerAlive);

        _countdown--;
        if (_countdown > 0) return new TickResult(dx, dy, false);

        // Countdown hit zero: the mission ends (mirrors end_wave=TRUE, RAP.C:1041-1046).
        _notified = true;
        return new TickResult(dx, dy, true);
    }

    internal static bool ShouldCompleteMission(bool waveActive, bool demoActive, bool endWave,
                                               bool playerAlive, bool enemiesRemaining) =>
        waveActive
        && !demoActive
        && endWave
        && playerAlive
        && !enemiesRemaining;
}
