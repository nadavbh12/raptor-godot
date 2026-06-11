using System;
using System.Collections.Generic;
using Godot;

namespace Raptor.View;

// Walks an SwdWindow and renders each field via the host CanvasItem. Mirrors
// SWD_PutField in dosraptor/GFX/swdapi.c — but only the read-path / NORMAL
// state drawing we need for menu pixel parity. The host supplies sprite
// loading + bitmap-font drawing; this class owns layout decisions only.
internal sealed class SwdRenderer
{
    public interface IHost
    {
        void DrawCanvasTexture(Texture2D tex, Vector2 pos, Color modulate);
        void DrawCanvasTextureRegion(Texture2D tex, Rect2 dst, Rect2 src, Color modulate);
        void DrawCanvasRect(Rect2 rect, Color color);
        Texture2D? LoadSprite(string itemName);   // resolves "MENU1_PIC" → Texture2D
        void DrawText(string text, int x, int y, string fontName, int basecolor, Color? modulate = null);
        int MeasureText(string text, string fontName);
        int FontHeight(string fontName);
    }

    // Selected-field brighten: C's GFX_ShadeShape(LIGHT, ...). C's actual
    // implementation is a palette LUT that lightens each index toward white
    // preserving hue. A multiplicative modulate cannot replicate that — the
    // (1.30, 1.60, 3.30) tuned for orange MENU1 sprites turns gray
    // STEXTURE_PIC blue. Uniform 1.5× brighten preserves hue across sprite
    // types (orange / gray / portrait alike) at the cost of being slightly
    // less bright than C on the main menu.
    private static readonly Color LightShade = new(1.5f, 1.5f, 1.5f);

    // C's HShadeLine / VShadeLine palette-shift each pixel by ±36 (8-bit
    // equivalent of ±9 in the 6-bit DOS palette space) and snap to the
    // nearest palette entry. Translucent-overlay approximations:
    //   LIGHT: ~0.18 white   (base + 0.18*(255-base) ≈ base + 36 for low bases)
    //   DARK:  ~0.70 black   (heavy darken; clamps low bases to near 0)
    // The DARK alpha is intentionally high since C's subtractive shade
    // clamps dark pixels to 0; multiplicative blending can't replicate that
    // exactly, but high alpha gets close on the typical dark STEXTURE tile.
    private static readonly Color LineLight = new(1f, 1f, 1f, 0.18f);
    private static readonly Color LineDark  = new(0f, 0f, 0f, 0.70f);

    public static void Draw(IHost host, SwdWindow swd, int selectedFieldId = -1,
        System.Collections.Generic.HashSet<int>? skipFieldIndices = null)
    {
        DrawWindowShadow(host, swd);
        DrawWindowBackground(host, swd);

        foreach (var f in swd.Fields)
        {
            if (skipFieldIndices != null && skipFieldIndices.Contains(f.Index))
                continue;
            int sx = swd.Window.X + f.X;
            int sy = swd.Window.Y + f.Y;
            bool selected = (selectedFieldId >= 0 && f.Id == selectedFieldId);

            switch (f.OptCode)
            {
                case 0:  // FLD_OFF
                    break;

                case 1:  // FLD_TEXT
                    if (f.MaxChars > 0 && f.Text.Length > 0 && f.FontName.Length > 0)
                    {
                        // Center text within the field width (C uses text_x +
                        // (lx - GFX_StrPixelLen)/2 for FLD_BUTTON; FLD_TEXT is
                        // left-aligned at fld_x). For the bottom prompt strip
                        // C effectively centers via authoring, so just draw
                        // at field origin.
                        host.DrawText(f.Text, sx, sy, f.FontName, f.FontBaseColor);
                    }
                    break;

                case 2:  // FLD_BUTTON
                    DrawButton(host, f, sx, sy, selected);
                    break;

                case 3:  // FLD_INPUT (skip read-only render; caller overlays)
                    break;

                case 5:  // FLD_CLOSE — same draw as FLD_ICON: GFX_PutImage.
                    var close = host.LoadSprite(f.ItemName);
                    if (close != null)
                        host.DrawCanvasTexture(close, new Vector2(sx, sy), Colors.White);
                    break;

                case 7:  // FLD_BUMPIN — inset dark panel (sunken look)
                    DrawBumpIn(host, sx, sy, f.Lx, f.Ly, f.Color);
                    break;

                case 8:  // FLD_BUMPOUT — raised panel (placeholder)
                    DrawBumpIn(host, sx, sy, f.Lx, f.Ly, f.Color);
                    break;

                case 9:  // FLD_ICON — draw the item picture at field x/y
                    var icon = host.LoadSprite(f.ItemName);
                    if (icon != null)
                        host.DrawCanvasTexture(icon, new Vector2(sx, sy),
                            selected ? LightShade : Colors.White);
                    break;

                case 11: // FLD_VIEWAREA (click target only — no draw)
                    break;
            }
        }
    }

    // SWD_ShowWindow (swdapi.c:1295): when cwin->shadow is set, two DARK
    // bands form an L-shaped drop shadow on the lower-left of the dialog.
    // The shadow is 8 px thick and offset (-8, +8) from the window origin.
    private static void DrawWindowShadow(IHost host, SwdWindow swd)
    {
        if (swd.Window.Shadow == 0) return;
        int x = swd.Window.X - 8;
        int y = swd.Window.Y + 8;
        int y2 = swd.Window.Y + swd.Window.Ly;
        int lx = swd.Window.Lx;
        // GFX_ShadeArea(DARK, ...) palette-darkens the pixels underneath.
        // A ~55% black overlay reads close to the C build's dark band.
        var darken = new Color(0, 0, 0, 0.55f);
        host.DrawCanvasRect(new Rect2(x,  y,  8,  swd.Window.Ly - 8), darken);
        host.DrawCanvasRect(new Rect2(x,  y2, lx, 8), darken);
    }

    private static void DrawWindowBackground(IHost host, SwdWindow swd)
    {
        if (string.IsNullOrEmpty(swd.Window.ItemName)) return;
        var tex = host.LoadSprite(swd.Window.ItemName);
        if (tex == null) return;

        // picflag values (from GFX/SWDAPI.H DSTYLE):
        //   0 FILL, 1 TEXTURE, 2 PICTURE, 3 SEE_THRU, 4 INVISABLE.
        // PICTURE: blit at window origin.
        // TEXTURE: tile into the window rect.
        if (swd.Window.Picflag == 1)
        {
            int tw = tex.GetWidth(), th = tex.GetHeight();
            if (tw <= 0 || th <= 0) return;
            // Tile-and-clip: the last row/column of tiles needs to be cropped
            // to the window's actual height/width, or it extends past the
            // dialog edge and paints over other overlays below.
            for (int dy = 0; dy < swd.Window.Ly; dy += th)
            {
                int srcH = Math.Min(th, swd.Window.Ly - dy);
                for (int dx = 0; dx < swd.Window.Lx; dx += tw)
                {
                    int srcW = Math.Min(tw, swd.Window.Lx - dx);
                    host.DrawCanvasTextureRegion(tex,
                        new Rect2(swd.Window.X + dx, swd.Window.Y + dy, srcW, srcH),
                        new Rect2(0, 0, srcW, srcH), Colors.White);
                }
            }
        }
        else
        {
            host.DrawCanvasTexture(tex,
                new Vector2(swd.Window.X, swd.Window.Y),
                Colors.White);
        }
    }

    private static void DrawButton(IHost host, SwdWindow.Field f, int sx, int sy, bool selected)
    {
        // Button face. INVISABLE (picflag 4) draws nothing. A named item blits its
        // sprite (ASKDIFF buttons → STEXTURE_PIC; the LOAD arrows → NEXT/PREV_PIC).
        // FILL buttons (picflag 0) carry a button-face item that is UNNAMED in the GLB
        // (FILE0001.INC has no #define), so f.ItemName is empty though f.Item != 0 —
        // the LOAD-window DELETE/CANCEL/LOAD. There is no palette LUT for the field
        // colour and STEXTURE_PIC is a dark tile, so render those as a neutral raised
        // face (solid mid-grey + bevel) instead of bare text. Text-overlay buttons
        // whose face is baked into the window art (the hangar's) carry f.Item == 0
        // and draw no face.
        bool drewFace = false;
        if (f.PicFlag != 4 && !string.IsNullOrEmpty(f.ItemName))
        {
            var bg = host.LoadSprite(f.ItemName);
            if (bg != null)
            {
                int tw = bg.GetWidth(), th = bg.GetHeight();
                if (tw <= 0 || th <= 0) tw = th = 1;
                var mod = selected ? LightShade : Colors.White;
                for (int dy = 0; dy < f.Ly; dy += th)
                {
                    int srcH = Math.Min(th, f.Ly - dy);
                    for (int dx = 0; dx < f.Lx; dx += tw)
                    {
                        int srcW = Math.Min(tw, f.Lx - dx);
                        host.DrawCanvasTextureRegion(bg,
                            new Rect2(sx + dx, sy + dy, srcW, srcH),
                            new Rect2(0, 0, srcW, srcH), mod);
                    }
                }
                drewFace = true;
            }
        }
        else if (f.PicFlag != 4 && f.Item != 0)
        {
            // Unnamed button face → neutral raised grey, readable under the dark label.
            var face = selected ? new Color(0.74f, 0.74f, 0.74f) : new Color(0.60f, 0.60f, 0.60f);
            host.DrawCanvasRect(new Rect2(sx, sy, f.Lx, f.Ly), face);
            drewFace = true;
        }

        if (drewFace)
        {
            // SWD_ShadeButton(NORMAL) — raised-button bevel. C shadeline positions
            // match the source so 1-pixel edges land where they do in the C build.
            host.DrawCanvasRect(new Rect2(sx + 1, sy,            f.Lx - 1, 1), LineLight);
            host.DrawCanvasRect(new Rect2(sx + f.Lx - 1, sy + 1, 1, f.Ly - 2), LineLight);
            host.DrawCanvasRect(new Rect2(sx,     sy + f.Ly - 1, f.Lx,     1), LineDark);
            host.DrawCanvasRect(new Rect2(sx,     sy,            1, f.Ly - 1), LineDark);
        }

        // Overlay the button's text label. C centers via SWD_PutField
        // lines 326-328:
        //   text_x = ((lx - GFX_StrPixelLen(text)) >> 1) + fld_x
        //   text_y = ((ly - fontheight)            >> 1) + fld_y
        if (!string.IsNullOrEmpty(f.Text) && !string.IsNullOrEmpty(f.FontName))
        {
            int tw = host.MeasureText(f.Text, f.FontName);
            int fh = host.FontHeight(f.FontName);
            int x = sx + (f.Lx - tw) / 2;
            int y = sy + (f.Ly - fh) / 2;
            // C's GFX_ShadeShape(LIGHT) on the active field also lifts the
            // text pixels — important for INVISABLE (text-only) buttons like
            // the SHIPCOMP sector list, where there's no bg sprite to brighten.
            host.DrawText(f.Text, x, y, f.FontName, f.FontBaseColor,
                selected ? LightShade : (Color?)null);
        }
    }

    // FLD_BUMPIN: inner DARK shade + LOWER_LEFT light box. C uses palette
    // LUTs we can't replicate exactly without a shader, but a translucent
    // black overlay on the inner rect plus 1-px bevel lines approximates
    // the sunken-panel look closely enough for pixel-parity work.
    private static void DrawBumpIn(IHost host, int x, int y, int lx, int ly, int color)
    {
        if (lx <= 2 || ly <= 2) return;
        // GFX_ShadeArea(DARK, x+1, y, lx-1, ly-1) when color != 0, else
        // GFX_ColorBox(x+1, y+1, lx-2, ly-2, 0). Both produce a near-black
        // inset; ~45% alpha black sits naturally over the TEXTURE_PIC tile.
        if (color == 0)
            host.DrawCanvasRect(new Rect2(x + 1, y + 1, lx - 2, ly - 2), Colors.Black);
        else
            host.DrawCanvasRect(new Rect2(x + 1, y, lx - 1, ly - 1),
                new Color(0, 0, 0, 0.45f));
        // GFX_LightBox(LOWER_LEFT, ...): bottom + left edges look lit,
        // top + right edges look shadowed (sunken effect). Uses the same
        // ±36-equivalent alphas as the button bevel for consistency.
        host.DrawCanvasRect(new Rect2(x, y, lx, 1), LineDark);                  // top edge
        host.DrawCanvasRect(new Rect2(x + lx - 1, y, 1, ly), LineDark);          // right edge
        host.DrawCanvasRect(new Rect2(x, y + ly - 1, lx, 1), LineLight);         // bottom edge
        host.DrawCanvasRect(new Rect2(x, y, 1, ly), LineLight);                  // left edge
    }
}
