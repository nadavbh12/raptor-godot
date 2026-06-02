using System.Collections.Generic;
using Godot;

namespace Raptor.View;

/// <summary>
/// Draws enemy/player shadows (sky-projected and ground) and owns the flat-
/// silhouette shadow-texture cache. Pure View: callers pass the canvas + the
/// source sprite and a world position; this resolves a darkened silhouette and
/// draws onto the supplied canvas. Mirrors HudRenderer/TileRenderer's seam.
/// </summary>
internal sealed class ShadowRenderer
{
    // Flat-silhouette shadow textures, keyed by source Texture2D. Each is a
    // black image whose alpha tracks the source sprite's mask, replicating the
    // C SHADOW_Draw behaviour where shadows are solid dark patches with no
    // interior detail (the C version shades the underlying screen pixels, not
    // the sprite, but a flat-color silhouette is the closest CanvasItem-only
    // approximation without a shader).
    private readonly Dictionary<Texture2D, Texture2D> _shadowCache = new();

    /// <summary>
    /// Project an air-sprite to its shadow position on the ground and draw a
    /// darkened silhouette. Mirrors SHADOW_Draw (SOURCE/SHADOWS.C) which uses
    /// GFX_3DPoint with viewx=160, viewy=100, viewz=1000, G3D_DIST=200, and
    /// shadow plane z=MAXZ=1280. Effective scale = 200/(1280-1000) = 5/7.
    /// Pre-projection offset: x-=10, y+=20.
    /// </summary>
    public void DrawSkyShadow(CanvasItem canvas, Texture2D tex, int x, int y, int w, int h)
    {
        const float Scale = 200f / 280f;   // G3D_DIST / (MAXZ - viewz)
        const int ViewX = 160, ViewY = 100;
        int ox = x - 10;
        int oy = y + 20;
        float sx = Scale * (ox - ViewX) + ViewX;
        float sy = Scale * (oy - ViewY) + ViewY;
        float sx2 = Scale * (ox + w - 1 - ViewX) + ViewX;
        float sy2 = Scale * (oy + h - 1 - ViewY) + ViewY;
        var rect = new Rect2(sx, sy, sx2 - sx + 1, sy2 - sy + 1);
        // Use a flat-silhouette shadow texture so internal sprite detail
        // (engines, stripes) doesn't bleed through. C's SHADOW_Draw applies a
        // 6-step palette light table to the underlying screen pixels — a flat
        // dark silhouette is the closest approximation without a shader.
        var shadowTex = GetOrCreateShadow(tex);
        canvas.DrawTextureRect(shadowTex, rect, false, new Color(1, 1, 1, 0.3f));
    }

    /// <summary>
    /// Ground enemy shadow at (x-3, y+4) — no 3D projection. Mirrors
    /// SHADOW_GAdd + SHADOW_DisplayGround (SHADOWS.C:177-241) which call
    /// GFX_ShadeShape(DARK, pic, x-3, y+4) at the original sprite size.
    /// </summary>
    public void DrawGroundShadow(CanvasItem canvas, Texture2D tex, int x, int y)
    {
        var shadowTex = GetOrCreateShadow(tex);
        canvas.DrawTexture(shadowTex, new Vector2(x - 3, y + 4), new Color(1, 1, 1, DebugRenderer.GroundShadowAlpha));
    }

    /// <summary>
    /// Return a cached flat-black silhouette texture matching the given
    /// sprite's alpha mask. The result is solid black where the source has
    /// alpha &gt; 0 and transparent elsewhere — used for sky shadows.
    /// </summary>
    private Texture2D GetOrCreateShadow(Texture2D src)
    {
        if (_shadowCache.TryGetValue(src, out var cached)) return cached;
        var img = src.GetImage();
        int w = img.GetWidth();
        int h = img.GetHeight();
        var shadow = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = img.GetPixel(x, y).A;
                shadow.SetPixel(x, y, new Color(0, 0, 0, a));
            }
        }
        var tex = ImageTexture.CreateFromImage(shadow);
        _shadowCache[src] = tex;
        return tex;
    }
}
