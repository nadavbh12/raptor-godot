using System;
using System.Collections.Generic;

namespace Raptor.View;

/// <summary>
/// One AGX movie frame: the PNG path, how many ticks it holds, and an optional
/// SoundEmitter label fired once when the frame is first shown (INTRO.C per-frame soundfx).
/// </summary>
public readonly record struct AgxMovieFrame(string Path, int DurationFrames, string? Sfx = null);

/// <summary>
/// An AGX cinematic: an ordered list of held frames plus an optional trailing palette
/// fade. Generalizes the old hardcoded death sequence. Pure data + a frame selector;
/// the renderer draws <see cref="TrySelectFrame"/> for elapsed = SimClock.Frame -
/// StateEnteredFrame, and the Sim auto-advance uses <see cref="TotalFrames"/>.
/// </summary>
public sealed class AgxMovie
{
    private readonly IReadOnlyList<AgxMovieFrame> _frames;

    public AgxMovie(IReadOnlyList<AgxMovieFrame> frames, int fadeOutFrames)
    {
        _frames = frames;
        FadeOutFrames = fadeOutFrames;
        int content = 0;
        foreach (var f in frames) content += f.DurationFrames;
        ContentFrames = content;
    }

    public IReadOnlyList<AgxMovieFrame> Frames => _frames;
    public int FadeOutFrames { get; }
    public int ContentFrames { get; }
    public int TotalFrames => ContentFrames + FadeOutFrames;

    /// <summary>
    /// Pick the frame shown at <paramref name="elapsed"/> ticks since the movie started.
    /// While inside the content window each frame is fully opaque; during the trailing
    /// fade the last frame is held and <paramref name="alpha"/> ramps 1→0. Returns false
    /// once <paramref name="elapsed"/> reaches <see cref="TotalFrames"/>.
    /// </summary>
    public bool TrySelectFrame(int elapsed, out AgxMovieFrame frame, out float alpha)
    {
        frame = default;
        alpha = 1f;
        if (elapsed < 0) elapsed = 0;

        int acc = 0;
        foreach (var f in _frames)
        {
            if (elapsed < acc + f.DurationFrames)
            {
                frame = f;
                return true;
            }
            acc += f.DurationFrames;
        }

        if (_frames.Count > 0 && elapsed < TotalFrames)
        {
            frame = _frames[^1];
            int fadeElapsed = elapsed - ContentFrames;
            alpha = FadeOutFrames > 0
                ? 1f - Math.Clamp(fadeElapsed / (float)FadeOutFrames, 0f, 1f)
                : 1f;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The content-frame index shown at <paramref name="elapsed"/>, or -1 once past the
    /// content (in the trailing fade or beyond). Used to fire a frame's one-shot sound
    /// exactly once, when the index advances.
    /// </summary>
    public int FrameIndexAt(int elapsed)
    {
        if (elapsed < 0) elapsed = 0;
        int acc = 0;
        for (int i = 0; i < _frames.Count; i++)
        {
            if (elapsed < acc + _frames[i].DurationFrames) return i;
            acc += _frames[i].DurationFrames;
        }
        return -1;
    }
}
