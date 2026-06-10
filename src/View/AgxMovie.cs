using System;
using System.Collections.Generic;

namespace Raptor.View;

/// <summary>One AGX movie frame: the PNG path and how many ticks it holds.</summary>
public readonly record struct AgxMovieFrame(string Path, int DurationFrames);

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
}
