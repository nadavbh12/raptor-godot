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
        return new SpriteMetaLibrary(wrapper.Sprites);
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
