using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raptor.Sim.MazeLevel;

/// <summary>
/// One entry from FLATSG{N}_ITM.json — mirrors the C FLATS struct in MAP.H.
///   linkflat: INT — if == flat index, tile is indestructible
///                  (C: eitems[loop] == titems[loop]); otherwise destructible.
///   bonus:    SHORT — tile HP for destructible flats (C: hits[loop] = lib->bonus
///                     when destructible; = 1 otherwise).
///   bounty:   SHORT — score awarded on destruction (C: money[loop] = lib->bounty).
/// </summary>
public sealed class FlatEntry
{
    [JsonPropertyName("linkflat")] public int   LinkFlat { get; set; }
    [JsonPropertyName("bonus")]    public short Bonus    { get; set; }
    [JsonPropertyName("bounty")]   public short Bounty   { get; set; }
}

/// <summary>JSON root for one FLATSG{N}_ITM file.</summary>
public sealed class FlatLibrary
{
    [JsonPropertyName("name")]      public string          Name      { get; set; } = "";
    [JsonPropertyName("num_flats")] public int             NumFlats  { get; set; }
    [JsonPropertyName("flats")]     public List<FlatEntry>? Flats    { get; set; }

    public int Count => Flats?.Count ?? 0;
    public FlatEntry Get(int i) => (Flats ?? throw new InvalidOperationException("No flats loaded"))[i];

    /// <summary>True iff this flat is destructible (mirrors C eitems != titems).</summary>
    public bool IsDestructible(int flatIndex)
    {
        var f = Get(flatIndex);
        return f.LinkFlat != flatIndex;
    }

    /// <summary>HP of this flat (1 for indestructible, lib->bonus otherwise).</summary>
    public int HitsFor(int flatIndex) => IsDestructible(flatIndex) ? Get(flatIndex).Bonus : 1;

    /// <summary>Score awarded on destruction (lib->bounty).</summary>
    public int BountyFor(int flatIndex) => Get(flatIndex).Bounty;

    /// <summary>Flat index drawn after destruction (mirrors C eitems / linkflat).</summary>
    public int DestroyedFlatFor(int flatIndex) => Get(flatIndex).LinkFlat;

    public static FlatLibrary LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var lib  = JsonSerializer.Deserialize<FlatLibrary>(json)
                   ?? throw new InvalidOperationException($"Failed to parse {path}");
        return lib;
    }
}
