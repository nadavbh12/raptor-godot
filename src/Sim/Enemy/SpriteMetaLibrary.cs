using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raptor.Sim.Enemy;

/// <summary>
/// Loads SPRITE1_ITM.json (an object with a "sprites" array) once and
/// exposes entries by index. Not a singleton — pass via DI so tests can
/// inject synthetic libraries.
/// </summary>
public sealed class SpriteMetaLibrary
{
    private readonly List<SpriteMeta> _all;

    private SpriteMetaLibrary(List<SpriteMeta> all) { _all = all; }

    public int Count => _all.Count;

    public SpriteMeta Get(int index) => _all[index];

    public static SpriteMetaLibrary LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var wrapper = JsonSerializer.Deserialize<SpriteMetaFile>(json)
                      ?? throw new InvalidOperationException($"Failed to parse {path}");
        if (wrapper.Sprites is null)
            throw new InvalidOperationException($"Missing 'sprites' array in {path}");

        // Attempt to populate Width/Height from PNG files in assets/sprites/.
        // PNG files are in a sibling directory two levels up: sprites_meta → assets → sprites.
        string spritesDir = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
            "..", "sprites");
        TryLoadDimensions(wrapper.Sprites, spritesDir);

        return new SpriteMetaLibrary(wrapper.Sprites);
    }

    /// <summary>
    /// Reads PNG image dimensions (from the IHDR chunk) for each sprite whose
    /// name matches a file in <paramref name="spritesDir"/>. Silently skips any
    /// sprite for which no PNG can be found or read.
    /// PNG IHDR layout (after 8-byte signature + 4-byte length + 4-byte "IHDR"):
    ///   bytes 16-19 = width (big-endian uint32)
    ///   bytes 20-23 = height (big-endian uint32)
    /// </summary>
    private static void TryLoadDimensions(List<SpriteMeta> sprites, string spritesDir)
    {
        if (!Directory.Exists(spritesDir)) return;

        // Build name → file lookup once. File names are like "0303_SHIP01G1_PIC.png".
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(spritesDir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            // Strip the leading numeric prefix (e.g. "0303_SHIP01G1_PIC" → "SHIP01G1_PIC").
            var under = name.IndexOf('_');
            if (under >= 0)
                lookup[name[(under + 1)..]] = file;
        }

        var buf = new byte[24];
        foreach (var sprite in sprites)
        {
            if (!lookup.TryGetValue(sprite.IName, out var pngPath)) continue;
            try
            {
                using var fs = File.OpenRead(pngPath);
                if (fs.Read(buf, 0, 24) < 24) continue;
                // Verify PNG signature (first 8 bytes).
                if (buf[0] != 0x89 || buf[1] != 'P' || buf[2] != 'N' || buf[3] != 'G') continue;
                int w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                int h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                if (w > 0 && h > 0)
                {
                    sprite.Width  = w;
                    sprite.Height = h;
                }
            }
            catch { /* best-effort; keep defaults */ }
        }
    }

    /// <summary>Used by tests — synthesize a library from in-memory entries.</summary>
    public static SpriteMetaLibrary FromList(IEnumerable<SpriteMeta> entries)
        => new SpriteMetaLibrary(new List<SpriteMeta>(entries));

    // Private helper type matching the outer JSON wrapper object.
    private sealed class SpriteMetaFile
    {
        [JsonPropertyName("name")]        public string?        Name       { get; set; }
        [JsonPropertyName("num_sprites")] public int            NumSprites { get; set; }
        [JsonPropertyName("sprites")]     public List<SpriteMeta>? Sprites { get; set; }
    }
}
