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
        void DrawText(string text, int x, int y, string fontName, int basecolor);
        int MeasureText(string text, string fontName);
    }

    // Selected-field brighten: C's GFX_ShadeShape(LIGHT, ...). Approximated
    // by the same per-channel modulate the main-menu highlight uses. Same
    // numbers — keep them in one place.
    private static readonly Color LightShade = new(1.30f, 1.60f, 3.30f);

    public static void Draw(IHost host, SwdWindow swd, int selectedFieldId = -1)
    {
        DrawWindowBackground(host, swd);

        foreach (var f in swd.Fields)
        {
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

                case 7:  // FLD_BUMPIN (inset panel — used as a frame)
                case 8:  // FLD_BUMPOUT
                    // No-op for now: the field's purpose in C is to draw a
                    // beveled fill, but the textured background sprite often
                    // supplies the same look at the same coords. Revisit if
                    // a screen turns out to need it.
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
        // Background sprite (STEXTURE_PIC, MENUn_PIC, etc.). picflag values
        // 0=FILL, 2=PICTURE — the read-path just blits the item; INVISABLE
        // (4) skips it entirely.
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
            }
        }

        // Overlay the button's text label. C centers via
        //   text_x = fld_x + (lx - GFX_StrPixelLen(text)) / 2
        //   text_y = fld_y + (ly - fontheight) / 2
        if (!string.IsNullOrEmpty(f.Text) && !string.IsNullOrEmpty(f.FontName))
        {
            int tw = host.MeasureText(f.Text, f.FontName);
            int x = sx + Math.Max(0, (f.Lx - tw) / 2);
            int y = sy + Math.Max(0, (f.Ly - 6) / 2);  // ~6 = typical glyph cap height
            host.DrawText(f.Text, x, y, f.FontName, f.FontBaseColor);
        }
    }
}
