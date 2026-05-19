using System;
using System.IO;
using Godot;

namespace Raptor.View;

// Parses an SWD text-stream (ITEM00_TXT, HARW1_TXT, REGISTER prompts) and
// renders it within a clip rect, mirroring SWD_FillText + SWD_GetLine in
// dosraptor/GFX/swdapi.c.
//
// Stream is whitespace-tokenized (\n\v\r \t,;\b). The first token of a
// line is either a command keyword or plain text:
//   TEXT_IMAGE <PIC_NAME> [<x> <y>]  — blit PIC at (origin.x + x, origin.y + y),
//                                       advance cursor x by sprite.width + 1
//   TEXT_COLOR <n>                    — set the textcolor (palette index)
//   TEXT_POS   <x> <y>                — move cursor to (origin.x+x, origin.y+y)
//   TEXT_RIGHT <x>                    — advance cursor x by x
//   TEXT_DOWN  <y>                    — advance cursor y by y
// Otherwise the line is drawn as plain text at the current cursor in the
// current font + textcolor, then cursor.y += font.height + 3.
internal static class SwdTextStream
{
    public interface IHost
    {
        Texture2D? LoadSprite(string itemName);
        void DrawCanvasTexture(Texture2D tex, Vector2 pos, Color modulate);
        void DrawText(string text, int x, int y, string fontName, int basecolor, Color? modulate = null);
        int FontHeight(string fontName);
    }

    private static readonly char[] WhiteSpace =
        { '\n', '\v', '\r', ' ', '\t', ',', ';', '\b' };

    /// <summary>Read the raw bytes of <c>assets/text/&lt;name&gt;.txt</c>.</summary>
    public static string? LoadText(string itemName)
    {
        string path = ProjectSettings.GlobalizePath($"res://assets/text/{itemName}.txt");
        if (!Godot.FileAccess.FileExists(path)) return null;
        // 0x1a (^Z) marks end-of-stream in the DOS files; trim there + nulls.
        var raw = File.ReadAllBytes(path);
        int end = raw.Length;
        for (int i = 0; i < end; i++)
            if (raw[i] == 0x1a || raw[i] == 0x00) { end = i; break; }
        return System.Text.Encoding.ASCII.GetString(raw, 0, end);
    }

    /// <summary>
    /// Render <paramref name="text"/> within a rect at <paramref name="originX"/>/
    /// <paramref name="originY"/>. Commands are honored; plain lines render at the
    /// current cursor in <paramref name="fontName"/>, default <paramref name="basecolor"/>.
    /// </summary>
    public static void Render(IHost host, string text,
                              int originX, int originY,
                              int clipW, int clipH,
                              string fontName, int basecolor)
    {
        int cursorX = originX;
        int cursorY = originY;
        int color   = basecolor;
        int rowAdvance = host.FontHeight(fontName) + 3;
        // SWD_FillText doesn't clip on field height — it just walks the
        // whole stream and lets GFX_Print clip against the 320x200 frame.
        // Mirror that here: stop only when we'd render below the screen.
        const int ScreenHeight = 200;
        _ = clipW;  // SWD_FillText doesn't constrain on width either.

        // Walk line-by-line. The DOS code tokenizes the whole stream as a
        // single stream of tokens (commands inline), but newlines/blanks
        // still anchor where text lands. We re-implement that on a
        // per-line basis: split into "lines" (anything terminated by \r or
        // \n, possibly empty), peek the first whitespace-tokenized word
        // for a command, otherwise treat the trimmed line as plain text.
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (cursorY > ScreenHeight) break;

            // Skip control bytes; treat the line as a token string.
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                cursorY += rowAdvance;
                continue;
            }

            var tokens = trimmed.Split(WhiteSpace,
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            switch (tokens[0])
            {
                case "TEXT_IMAGE":
                    if (tokens.Length < 2) break;
                    {
                        int ix = (tokens.Length >= 4 && int.TryParse(tokens[2], out var px)) ? px : 0;
                        int iy = (tokens.Length >= 4 && int.TryParse(tokens[3], out var py)) ? py : 0;
                        var tex = host.LoadSprite(tokens[1]);
                        if (tex != null)
                        {
                            int dx = originX + ix;
                            int dy = originY + iy;
                            host.DrawCanvasTexture(tex,
                                new Vector2(dx, dy), Colors.White);
                            cursorX = dx + tex.GetWidth() + 1;
                            cursorY = dy;
                        }
                    }
                    break;

                case "TEXT_COLOR":
                    if (tokens.Length >= 2 && int.TryParse(tokens[1], out var ncolor))
                        color = ncolor;
                    break;

                case "TEXT_POS":
                    if (tokens.Length >= 3
                        && int.TryParse(tokens[1], out var tpx)
                        && int.TryParse(tokens[2], out var tpy))
                    {
                        cursorX = originX + tpx;
                        cursorY = originY + tpy;
                    }
                    break;

                case "TEXT_RIGHT":
                    if (tokens.Length >= 2 && int.TryParse(tokens[1], out var rx))
                        cursorX += rx;
                    break;

                case "TEXT_DOWN":
                    if (tokens.Length >= 2 && int.TryParse(tokens[1], out var dyAdv))
                        cursorY += dyAdv;
                    break;

                default:
                    // Plain text line: draw at the current cursor in the
                    // current color, then advance cursor down by font
                    // height + 3 like SWD_FillText.
                    host.DrawText(trimmed, cursorX, cursorY, fontName, color);
                    cursorY += rowAdvance;
                    break;
            }
        }
    }
}
