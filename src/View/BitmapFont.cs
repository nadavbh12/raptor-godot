using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace Raptor.View;

// Renders text using a DOS-extracted bitmap font. The atlas PNG stores glyph
// pixels as palette offsets in the alpha channel; final color comes from
// palette[(basecolor - 1) + offset]. We pre-build one tinted Texture2D per
// (atlas, basecolor) pair on first use and reuse it on subsequent draws.
//
// See dosraptor/tools/extract_assets/font_dumper.c for the producer side.
internal sealed class BitmapFont
{
    public int Height { get; }
    public int FontSpacing { get; }

    private readonly Image _atlas;       // alpha = glyph-pixel offset
    private readonly Glyph[] _glyphs;    // indexed by char code (0..255), W=0 if absent
    private readonly Color[] _palette;   // 256 entries

    private readonly Dictionary<int, Texture2D> _tintCache = new();

    private readonly record struct Glyph(int X, int W);

    private BitmapFont(int height, int fontSpacing, Image atlas, Glyph[] glyphs, Color[] palette)
    {
        Height = height;
        FontSpacing = fontSpacing;
        _atlas = atlas;
        _glyphs = glyphs;
        _palette = palette;
    }

    public static BitmapFont Load(string atlasResPath, string metaResPath, Color[] palette)
    {
        string atlasPath = ProjectSettings.GlobalizePath(atlasResPath);
        string metaPath = ProjectSettings.GlobalizePath(metaResPath);

        var atlas = Image.LoadFromFile(atlasPath)
            ?? throw new InvalidOperationException($"Failed to load font atlas: {atlasPath}");
        atlas.Convert(Image.Format.Rgba8);

        var json = File.ReadAllText(metaPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int height = root.GetProperty("height").GetInt32();
        int spacing = root.GetProperty("fontspacing").GetInt32();

        var glyphs = new Glyph[256];
        foreach (var prop in root.GetProperty("glyphs").EnumerateObject())
        {
            int code = int.Parse(prop.Name);
            int x = prop.Value.GetProperty("x").GetInt32();
            int w = prop.Value.GetProperty("w").GetInt32();
            if (code >= 0 && code < 256)
                glyphs[code] = new Glyph(x, w);
        }
        return new BitmapFont(height, spacing, atlas, glyphs, palette);
    }

    public static Color[] LoadPalette(string paletteResPath)
    {
        string p = ProjectSettings.GlobalizePath(paletteResPath);
        var json = File.ReadAllText(p);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("colors");
        var pal = new Color[256];
        int i = 0;
        var it = arr.EnumerateArray();
        while (i < 256 && it.MoveNext())
        {
            int r = it.Current.GetInt32(); it.MoveNext();
            int g = it.Current.GetInt32(); it.MoveNext();
            int b = it.Current.GetInt32();
            pal[i++] = new Color(r / 255f, g / 255f, b / 255f, 1f);
        }
        return pal;
    }

    // Mirrors GFX_StrPixelLen: sums width + fontspacing for every character
    // (including the last). The DOS code uses this for centering math, so
    // matching its exact value matters for FLD_BUTTON layout.
    public int Measure(string text)
    {
        int w = 0;
        foreach (char c in text)
        {
            int code = (byte)c;
            var g = _glyphs[code];
            w += g.W + FontSpacing;
        }
        return w;
    }

    // Mirrors GFX_Print + GFX_DrawChar: for each char with charofs != EMPTY,
    // blit the glyph from the per-basecolor cached atlas at the current cursor
    // and advance x by width + fontspacing.
    public void Draw(CanvasItem target, string text, int x, int y, int basecolor, Color? modulate = null)
    {
        var tex = GetOrBuildTintedAtlas(basecolor);
        var tint = modulate ?? Colors.White;
        foreach (char c in text)
        {
            int code = (byte)c;
            var g = _glyphs[code];
            if (g.W == 0) continue;
            var src = new Rect2(g.X, 0, g.W, Height);
            var dst = new Rect2(x, y, g.W, Height);
            target.DrawTextureRectRegion(tex, dst, src, tint);
            x += g.W + FontSpacing;
        }
    }

    private Texture2D GetOrBuildTintedAtlas(int basecolor)
    {
        if (_tintCache.TryGetValue(basecolor, out var cached))
            return cached;

        int w = _atlas.GetWidth();
        int h = _atlas.GetHeight();
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        // GFX_Print does basecolor-- before calling GFX_PutChar; mirror it.
        int baseIdx = basecolor - 1;
        for (int yy = 0; yy < h; yy++)
        {
            for (int xx = 0; xx < w; xx++)
            {
                int offset = (int)(_atlas.GetPixel(xx, yy).A8);
                if (offset == 0)
                {
                    img.SetPixel(xx, yy, new Color(0, 0, 0, 0));
                    continue;
                }
                int idx = (baseIdx + offset) & 0xff;
                var rgb = _palette[idx];
                img.SetPixel(xx, yy, new Color(rgb.R, rgb.G, rgb.B, 1f));
            }
        }
        var tex = ImageTexture.CreateFromImage(img);
        _tintCache[basecolor] = tex;
        return tex;
    }
}
