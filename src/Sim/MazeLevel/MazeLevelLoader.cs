using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raptor.Sim.MazeLevel;

/// <summary>
/// Represents one sprite-instance entry from a MAP*G*_MAP.json "sprites" array.
/// Coordinates (x, y) are tile-grid coordinates; x ∈ [0,8], y ∈ [0,149].
/// </summary>
public sealed class MapSpriteEntry
{
    [JsonPropertyName("link")]  public int Link  { get; set; }
    [JsonPropertyName("slib")]  public int Slib  { get; set; }
    [JsonPropertyName("x")]     public int X     { get; set; }
    [JsonPropertyName("y")]     public int Y     { get; set; }
    [JsonPropertyName("game")]  public int Game  { get; set; }
    [JsonPropertyName("level")] public int Level { get; set; }
}

/// <summary>
/// Deserialization root for MAP*G*_MAP.json.
/// </summary>
/// <summary>One tile entry in the maze grid: which graphic + which game-context.</summary>
public sealed class MapTileEntry
{
    [JsonPropertyName("flats")] public int Flats { get; set; }
    [JsonPropertyName("fgame")] public int FGame { get; set; }
}

public sealed class MapLevelData
{
    [JsonPropertyName("name")]        public string              Name        { get; set; } = "";
    [JsonPropertyName("rows")]        public int                 Rows        { get; set; }
    [JsonPropertyName("cols")]        public int                 Cols        { get; set; }
    [JsonPropertyName("numsprites")]  public int                 NumSprites  { get; set; }
    [JsonPropertyName("tiles")]       public List<MapTileEntry>?  Tiles      { get; set; }
    [JsonPropertyName("sprites")]     public List<MapSpriteEntry>? Sprites   { get; set; }
}

/// <summary>
/// Loads MAP*G*_MAP.json files from the assets/levels/ directory.
/// The sprite coordinates are in tile space (9×150 grid). Pixel conversion:
///   pixel_x = tile_x * 36 + 18   (tile center, tile width = 36px)
///   pixel_y = tile_y * 36 + 18   (tile center, tile height = 36px — scrolling map)
///
/// Note: the scroll-position during spawning determines when a sprite becomes
/// active. For Stage 5 first-cut we load ALL sprites up front and let the
/// WaveController decide which to spawn based on a "level" threshold or just
/// spawn the first batch immediately.
/// </summary>
public static class MazeLevelLoader
{
    // Width of one tile in pixels (from the C source map renderer).
    public const int TileWidthPx  = 36;
    public const int TileHeightPx = 36;

    /// <summary>Loads and returns the raw map data for the given wave.</summary>
    /// <remarks>Alias: <see cref="LoadFromFile"/> accepts the same argument.</remarks>
    public static MapLevelData LoadFromFile(string mapJsonPath) => Load(mapJsonPath);

    /// <summary>Loads and returns the raw map data for the given wave.</summary>
    public static MapLevelData Load(string mapJsonPath)
    {
        var json = File.ReadAllText(mapJsonPath);
        var data = JsonSerializer.Deserialize<MapLevelData>(json)
                   ?? throw new InvalidOperationException($"Failed to parse {mapJsonPath}");
        if (data.Sprites is null)
            throw new InvalidOperationException($"Missing 'sprites' array in {mapJsonPath}");
        return data;
    }

    /// <summary>
    /// Returns the canonical JSON asset path for a given wave number.
    /// waveNum is 1-based (Mission 1 = wave 1 → MAP1G1_MAP.json).
    /// </summary>
    public static string WaveMapPath(string assetsRoot, int waveNum) =>
        Path.Combine(assetsRoot, "levels", $"MAP{waveNum}G1_MAP.json");

    /// <summary>
    /// Convert tile-grid X to approximate screen pixel X.
    /// </summary>
    public static int TileXToPixel(int tileX) => tileX * TileWidthPx + TileWidthPx / 2;

    /// <summary>
    /// Convert tile-grid Y to approximate screen pixel Y.
    /// Tile row 0 is at the bottom of the map; we flip so row 149 appears at top.
    /// The screen is 200px tall; enemies scroll from bottom to top (positive Y = down).
    /// For spawn, use a value off-screen at the bottom (y > 200) and let the flight
    /// path move the enemy up.
    /// </summary>
    public static int TileYToPixel(int tileY) => 200 - tileY;
}
