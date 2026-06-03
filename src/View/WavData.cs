using System;
using System.Text;

namespace Raptor.View;

/// <summary>
/// Minimal RIFF/WAVE PCM parser — pure (no Godot types), so it is unit-testable
/// without the engine runtime. Reads the fmt chunk (sample rate / bits /
/// channels) and the data chunk's raw PCM bytes by walking the chunk list.
/// The Raptor SFX (assets/sounds/*.wav) are all 11025 Hz / 16-bit / mono PCM.
/// </summary>
internal readonly record struct WavData(int SampleRate, int BitsPerSample, int Channels, byte[] Pcm)
{
    public bool Stereo => Channels >= 2;

    /// <summary>
    /// Parse a WAV byte buffer. Throws <see cref="FormatException"/> on
    /// malformed input or non-PCM (compressed) data.
    /// </summary>
    public static WavData Parse(byte[] bytes)
    {
        if (bytes.Length < 12 || ReadTag(bytes, 0) != "RIFF" || ReadTag(bytes, 8) != "WAVE")
            throw new FormatException("Not a RIFF/WAVE file.");

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        byte[]? pcm = null;

        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            string tag = ReadTag(bytes, pos);
            int size = ReadU32(bytes, pos + 4);
            int body = pos + 8;
            if (tag == "fmt " && body + 16 <= bytes.Length)
            {
                format     = ReadU16(bytes, body);
                channels   = ReadU16(bytes, body + 2);
                sampleRate = ReadU32(bytes, body + 4);
                bits       = ReadU16(bytes, body + 14);
            }
            else if (tag == "data")
            {
                int len = Math.Min(size, bytes.Length - body);
                pcm = new byte[len];
                Array.Copy(bytes, body, pcm, 0, len);
            }
            // RIFF chunks are word-aligned: an odd size carries one pad byte.
            pos = body + size + (size & 1);
        }

        if (format != 1) throw new FormatException($"Unsupported WAV format {format} (only PCM=1 supported).");
        if (pcm == null) throw new FormatException("WAV has no data chunk.");
        return new WavData(sampleRate, bits, channels, pcm);
    }

    private static string ReadTag(byte[] b, int off) => Encoding.ASCII.GetString(b, off, 4);
    private static int ReadU16(byte[] b, int off) => b[off] | (b[off + 1] << 8);
    private static int ReadU32(byte[] b, int off) =>
        b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
}
