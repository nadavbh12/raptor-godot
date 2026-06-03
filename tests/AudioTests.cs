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
}
