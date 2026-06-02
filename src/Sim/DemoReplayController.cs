using Raptor.Test;

namespace Raptor.Sim;

/// <summary>
/// Owns the demo-playback cursor and lifecycle (mirrors the DEMO_* state in
/// RAP.C): a pending replay scheduled to begin at a future frame, the active
/// replay, and the record index. Extracted from WaveController (E8).
///
/// The per-tick phase spine (the <c>_scheduler.Tick(); _gameLoopIter++;
/// OnIterEnd</c> triple and the fade-hold gating in <c>_PhysicsProcess</c>) and
/// the actual wave seed/load/player-setup stay in WaveController — this type only
/// tracks "are we (about to be) replaying a demo, and what is the next recorded
/// frame". Godot-free by construction so it is unit-testable.
/// </summary>
internal sealed class DemoReplayController
{
    private DemoReplay? _pending;
    private int _pendingStartFrame = -1;
    private DemoReplay? _replay;
    private int _recordIndex;

    /// <summary>True once a replay is actively playing.</summary>
    public bool Active => _replay != null;

    /// <summary>True while a replay is scheduled but has not begun yet.</summary>
    public bool PendingScheduled => _pendingStartFrame >= 0;

    /// <summary>The active replay (null when not playing); for header/debug reads.</summary>
    public DemoReplay? Replay => _replay;

    /// <summary>Records consumed so far in the active replay.</summary>
    public int RecordIndex => _recordIndex;

    /// <summary>Schedule a replay to begin at <paramref name="startFrame"/>.</summary>
    public void Schedule(DemoReplay replay, int startFrame)
    {
        _pending = replay;
        _pendingStartFrame = startFrame;
    }

    /// <summary>True when the scheduled replay should begin at <paramref name="currentFrame"/>.</summary>
    public bool ShouldBegin(int currentFrame) =>
        _pendingStartFrame >= 0 && currentFrame >= _pendingStartFrame;

    /// <summary>
    /// Promote the pending replay to active and reset the cursor. Returns the
    /// now-active replay so the caller can seed RNG / load the wave / set up the
    /// demo player. Only call when <see cref="PendingScheduled"/> is true.
    /// </summary>
    public DemoReplay Begin()
    {
        _replay = _pending;
        _pending = null;
        _pendingStartFrame = -1;
        _recordIndex = 0;
        return _replay!;
    }

    /// <summary>
    /// Yields the next recorded frame, or returns false (and ends the replay)
    /// once the records are exhausted.
    /// </summary>
    public bool TryNextFrame(out DemoReplay.Frame frame)
    {
        if (_replay == null || _recordIndex >= _replay.Records.Count)
        {
            _replay = null;
            frame = default;
            return false;
        }
        frame = _replay.Records[_recordIndex++];
        return true;
    }
}
