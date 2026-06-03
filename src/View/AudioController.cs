using System;
using Godot;
using Raptor.Sim;

namespace Raptor.View;

/// <summary>
/// Plays the sim's sound events. Installs <see cref="SoundEmitter.Sink"/> in
/// <see cref="_Ready"/> (unless running headless), loads the SFX bank lazily,
/// and plays each emitted clip on a round-robin pool of
/// <see cref="AudioStreamPlayer"/>s so overlapping sounds (rapid gunfire +
/// explosions) don't cut each other off.
///
/// Pure View: reads nothing from and writes nothing to sim state. Headless /
/// CI / parity runs (launched with --headless) leave the sink null, so the sim
/// never even allocates a sound event.
/// </summary>
public partial class AudioController : Node
{
    private const int PoolSize = 16;

    private SfxBank? _bank;
    private AudioStreamPlayer[] _players = Array.Empty<AudioStreamPlayer>();
    private int _next;

    public override void _Ready()
    {
        // Headless = parity / CI / test runs. Audio is pointless there and we
        // must not touch the filesystem or the AudioServer, so leave
        // SoundEmitter.Sink null — Emit stays a no-op and parity is unaffected.
        if (DisplayServer.GetName() == "headless")
            return;

        _bank = new SfxBank(ProjectSettings.GlobalizePath("res://assets/sounds"));

        _players = new AudioStreamPlayer[PoolSize];
        for (int i = 0; i < PoolSize; i++)
        {
            _players[i] = new AudioStreamPlayer();
            AddChild(_players[i]);
        }

        SoundEmitter.Sink = Play;
    }

    public override void _ExitTree()
    {
        // Don't leave a freed callback installed if the tree tears down.
        if (SoundEmitter.Sink == Play)
            SoundEmitter.Sink = null;
    }

    private void Play(SoundEmitter.SoundEvent ev)
    {
        var stream = _bank?.ForLabel(ev.Label);
        if (stream == null)
            return;

        var player = _players[_next];
        _next = (_next + 1) % _players.Length;
        player.Stream = stream;
        player.Play();
    }
}
