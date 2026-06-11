using System;

namespace Raptor.Sim;

/// <summary>
/// One-way sound-event seam from the parity-locked sim to the View's audio
/// playback. The sim calls <see cref="Emit(string)"/> at exactly the points
/// where the C original issues SND_Patch / SND_3DPatch (mirroring its
/// game-logic sound calls); the View's AudioController installs
/// <see cref="Sink"/> to play the mapped clip.
///
/// Parity-inert by construction: in headless / CI / test runs no sink is
/// installed, so Emit is a null-check no-op — it never touches sim state, the
/// RNG stream, or the NDJSON checkpoint schema. The C# <c>?.</c> short-circuit
/// also means the SoundEvent struct is never even allocated when Sink is null.
///
/// Single-threaded: Godot runs _PhysicsProcess (sim) and the View on the same
/// main thread, so the sink may play audio synchronously when invoked.
/// </summary>
public static class SoundEmitter
{
    /// <summary>A sound the sim wants played, at an (optional) screen position.</summary>
    public readonly record struct SoundEvent(string Label, int X, int Y);

    /// <summary>
    /// Installed by the View's AudioController when audio is active. Null in
    /// headless runs and before any View attaches, making <see cref="Emit(string)"/>
    /// a no-op.
    /// </summary>
    public static Action<SoundEvent>? Sink;

    // Default screen centre for non-positional (player) sounds. v1 plays mono;
    // X/Y is carried for a future positional-panning pass.
    private const int CenterX = 160;
    private const int CenterY = 100;

    /// <summary>Emit a non-positional sound (player weapons, pickups).</summary>
    public static void Emit(string label) => Sink?.Invoke(new SoundEvent(label, CenterX, CenterY));

    /// <summary>Emit a positional sound (explosions, enemy shots).</summary>
    public static void Emit(string label, int x, int y) => Sink?.Invoke(new SoundEvent(label, x, y));

    /// <summary>
    /// Installed by the View's AudioController to stop all in-flight SFX voices. Used to
    /// cut a cutscene's lingering sound (the intro's final explosion) when it transitions
    /// out, so the tail doesn't bleed into the next screen. Null headless / before a View.
    /// </summary>
    public static Action? StopAllSink;

    /// <summary>Stop all currently-playing sound effects (no-op headless / before any View).</summary>
    public static void StopAll() => StopAllSink?.Invoke();
}
