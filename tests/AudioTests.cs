using System;
using System.IO;
using Raptor.View;
using Xunit;

namespace RaptorTests;

/// <summary>
/// Covers the pure audio plumbing: the RIFF/WAVE parser and the
/// SoundEmitter-label → .wav mapping. The Godot playback path (SfxBank.Load /
/// AudioController) is exercised by the headless game run, not here.
/// </summary>
public class AudioTests
{
    private static string SoundsDir()
    {
        var d = AppContext.BaseDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "raptor.csproj")))
            d = Directory.GetParent(d)?.FullName;
        Assert.NotNull(d);
        return Path.Combine(d!, "assets", "sounds");
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Parses_raptor_sfx_as_11025hz_16bit_mono_pcm()
    {
        var wav = WavData.Parse(File.ReadAllBytes(Path.Combine(SoundsDir(), "GUN_FX.wav")));
        Assert.Equal(11025, wav.SampleRate);
        Assert.Equal(16, wav.BitsPerSample);
        Assert.Equal(1, wav.Channels);
        Assert.False(wav.Stereo);
        Assert.True(wav.Pcm.Length > 0);
        Assert.Equal(0, wav.Pcm.Length % 2); // 2 bytes per 16-bit sample
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Every_mapped_label_resolves_to_an_existing_16bit_wav()
    {
        foreach (var (label, file) in SfxBank.LabelToFile)
        {
            string path = Path.Combine(SoundsDir(), file + ".wav");
            Assert.True(File.Exists(path), $"{label} → {file}.wav missing at {path}");
            Assert.Equal(16, WavData.Parse(File.ReadAllBytes(path)).BitsPerSample);
        }
    }

    [Fact]
    public void Parse_rejects_non_riff_input()
        => Assert.Throws<FormatException>(() => WavData.Parse(new byte[] { 1, 2, 3, 4 }));

    [Fact]
    public void Player_gunfire_is_trimmed_below_one_off_effects()
    {
        // Rapid forward-gun fire retriggers many times a second and stacks across
        // the voice pool, so it dominates the mix. It's trimmed below one-off
        // effects (explosions/pickups, which play at the bus level = 0 dB).
        Assert.True(SfxBank.TrimDbForLabel("sound.fx_gun") < 0f);
        Assert.True(SfxBank.TrimDbForLabel("sound.fx_missle") < 0f);
        Assert.True(SfxBank.TrimDbForLabel("sound.fx_gun")
                  < SfxBank.TrimDbForLabel("sound3d.fx_airexplo"));
        Assert.Equal(0f, SfxBank.TrimDbForLabel("sound3d.fx_airexplo"));  // explosion at bus level
        Assert.Equal(0f, SfxBank.TrimDbForLabel("sound.fx_bonus"));       // pickup at bus level
        Assert.Equal(0f, SfxBank.TrimDbForLabel("sound.fx_hit"));         // shield hit (not firing)
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Every_midi_track_has_a_rendered_ogg()
    {
        string music = Path.Combine(Path.GetDirectoryName(SoundsDir())!, "music");
        var mids = Directory.GetFiles(music, "*.mid");
        Assert.NotEmpty(mids);
        foreach (var mid in mids)
        {
            string ogg = Path.ChangeExtension(mid, ".ogg");
            Assert.True(File.Exists(ogg), $"missing OPL2 render: {Path.GetFileName(ogg)}");
        }
    }
}
