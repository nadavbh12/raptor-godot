using System.Collections.Generic;
using System.IO;
using Godot;
using Raptor.Sim;

namespace Raptor.View;

/// <summary>
/// Renders the scrolling tile background and owns the per-tile texture cache.
/// Pure View: it reads the map layout off the supplied <see cref="WaveController"/>
/// and draws onto the supplied canvas. Mirrors HudRenderer's seam (canvas + wave
/// passed into Draw); the parent holds a single instance and calls it from _Draw.
/// </summary>
internal sealed class TileRenderer
{
    private readonly Dictionary<int, Texture2D?> _tileCache = new();
    private string? _tilesRoot;

    /// <param name="canvas">The CanvasItem to draw onto (called from its _Draw).</param>
    /// <summary>
    /// Render the scrolling tile background. Mirrors C's TILE_Think layout:
    ///   for loopy in 0..MAP_ONSCREEN, y starting at tileyoff:
    ///     for loopx in 0..MAP_COLS, x starting at MAP_LEFT:
    ///       draw titems[mapspot] at (x, y)
    /// titems[mapspot] = startflat[fgame] + flats — we resolve that via
    /// LoadTile(fgame, flats) which reads assets/tiles/g{fgame+1}/{flats:D4}.png.
    /// </summary>
    public void DrawTileMap(CanvasItem canvas, WaveController wave)
    {
        if (wave.MapTiles == null) return;
        var tiles = wave.MapTiles;
        int cols  = wave.MapCols;
        int rows  = wave.MapRows;
        int onscr = wave.MapOnScreen;
        int bs    = wave.MapBlockSize;
        int left  = wave.MapLeftPx;

        int y       = wave.TileYOff;
        int mapspot = wave.TilePos;

        for (int ly = 0; ly < onscr; ly++, y += bs)
        {
            int x = left;
            for (int lx = 0; lx < cols; lx++, x += bs, mapspot++)
            {
                if (mapspot < 0 || mapspot >= tiles.Count) continue;
                var t = tiles[mapspot];
                int flats = wave.RenderedFlatFor(mapspot);
                var tex = LoadTile(t.FGame, flats);
                if (tex != null) canvas.DrawTexture(tex, new Vector2(x, y));
            }
        }
    }

    /// <summary>
    /// Load tile graphic for the given (game-index, flats-index) pair.
    /// game=0 → tiles/g1/NNNN.png (mirrors C's TILE_Init: titems[i] = startflat[fgame] + flats).
    /// Returns null (cached) if the file is missing.
    /// </summary>
    private Texture2D? LoadTile(int game, int flats)
    {
        int key = (game << 16) | (flats & 0xffff);
        if (_tileCache.TryGetValue(key, out var cached)) return cached;
        _tilesRoot ??= ProjectSettings.GlobalizePath("res://assets/tiles");
        string path = Path.Combine(_tilesRoot, $"g{game + 1}", $"{flats:D4}.png");
        Texture2D? tex = null;
        if (File.Exists(path))
        {
            var img = Image.LoadFromFile(path);
            if (img != null) tex = ImageTexture.CreateFromImage(img);
        }
        _tileCache[key] = tex;  // cache misses too — avoid retrying every frame
        return tex;
    }
}
