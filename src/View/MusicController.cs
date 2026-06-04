using System.Collections.Generic;
using System.IO;
using Godot;
using Raptor.Sim;

namespace Raptor.View;

/// <summary>
/// Selects and loops the background-music track for the current game state,
/// mirroring the C SND_PlaySong calls (WINDOWS.C / RAP.C). Pure View: reads
/// game state, never writes it. Disabled entirely in headless runs.
///
/// Tracks are the authentic Apogee-OPL2 render of the original MIDI (rendered
/// offline with libADLMIDI's "Apogee Sound System" FM bank to
/// assets/music/*.ogg). Selection mirrors the C tables:
///   - in a wave             -> songsg{episode}[wave]  (WINDOWS.C:68-104)
///   - hangar/store/briefing  -> HANGAR_MUS
///   - intro                 -> RINTRO_MUS
///   - main menu (default)    -> MAINMENU_MUS
/// </summary>
public partial class MusicController : Node
{
    // songsg1/2/3 from WINDOWS.C — indexed [episode 0..2][wave 1..9].
    private static readonly string[][] WaveSongs =
    {
        new[] { "RAP8_MUS", "RAP2_MUS", "RAP4_MUS", "RAP7_MUS", "RAP6_MUS", "RAP2_MUS", "RAP3_MUS", "RAP4_MUS", "RAP6_MUS" },
        new[] { "RAP3_MUS", "RAP2_MUS", "RAP4_MUS", "RAP8_MUS", "RAP6_MUS", "RAP2_MUS", "RAP8_MUS", "RAP1_MUS", "RAP6_MUS" },
        new[] { "RAP8_MUS", "RAP4_MUS", "RAP1_MUS", "RAP7_MUS", "RAP6_MUS", "RAP2_MUS", "RAP3_MUS", "RAP4_MUS", "RAP6_MUS" },
    };

    private WaveController? _wave;
    private MenuStateMachine? _menu;
    private AudioStreamPlayer? _player;
    private string _musicRoot = "";
    private string? _current;
    private readonly Dictionary<string, AudioStream?> _cache = new();

    public override void _Ready()
    {
        // Headless = parity/CI/test runs: no music, no AudioServer/filesystem.
        if (DisplayServer.GetName() == "headless")
            return;

        _musicRoot = ProjectSettings.GlobalizePath("res://assets/music");
        AudioBus.Ensure("Music");
        _player = new AudioStreamPlayer { Bus = "Music" };
        AddChild(_player);

        _wave = GetNodeOrNull<WaveController>("../WaveController");
        var menu = GetNodeOrNull<MenuController>("../MenuController");
        if (menu != null)
            _menu = menu.Menu;
    }

    public override void _Process(double delta)
    {
        if (_player == null)
            return;

        // Drive the Music bus from the in-game options "music volume" slider.
        AudioBus.SetVolume("Music", _menu?.OptionMusicVolume ?? 127, 0f);

        string? want = DesiredTrack();
        if (want == _current)
            return;

        _current = want;
        var stream = want == null ? null : Load(want);
        if (stream == null)
        {
            _player.Stop();
            return;
        }
        _player.Stream = stream;
        _player.Play();
    }

    private string? DesiredTrack()
    {
        if (_wave != null && _wave.GameplayVisualActive)
        {
            int ep = System.Math.Clamp(_menu?.GameNum ?? 0, 0, WaveSongs.Length - 1);
            int wave = System.Math.Clamp(_wave.WaveNum, 1, WaveSongs[ep].Length);
            return WaveSongs[ep][wave - 1];
        }

        return _menu?.State switch
        {
            WinState.Hangar or WinState.Store or WinState.Briefing or WinState.Order => "HANGAR_MUS",
            WinState.Intro => "RINTRO_MUS",
            _ => "MAINMENU_MUS",
        };
    }

    private AudioStream? Load(string track)
    {
        if (_cache.TryGetValue(track, out var cached))
            return cached;

        string path = Path.Combine(_musicRoot, track + ".ogg");
        AudioStream? stream = null;
        if (File.Exists(path))
        {
            var ogg = AudioStreamOggVorbis.LoadFromFile(path);
            if (ogg != null)
            {
                ogg.Loop = true;
                stream = ogg;
            }
        }
        else
        {
            GD.PushWarning($"MusicController: music file not found: {path}");
        }

        _cache[track] = stream;
        return stream;
    }
}
