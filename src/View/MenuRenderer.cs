using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Raptor.Sim;

namespace Raptor.View;

/// <summary>
/// Draws the menu-system overlays (main menu, options, ask-bool, win message,
/// load mission, pilot registration, hangar, difficulty, help, death movie).
/// Pure View: it resolves SWD layouts / fonts / sprites through the supplied
/// <see cref="IHost"/> surface (a thin adapter over DebugRenderer's shared
/// caches + canvas) and draws onto it. The dispatcher gate lives in
/// DebugRenderer (ShouldDrawMenuOverlayForState); this just renders.
///
/// A few overlays the dispatcher reaches — ship-computer, store, credits —
/// stay in DebugRenderer because they share helpers (DrawSwdCenteredText,
/// DrawSwdItemSprite) with other DebugRenderer code; the host exposes hooks
/// back to them so the dispatch order is unchanged.
/// </summary>
internal sealed class MenuRenderer
{
    /// <summary>
    /// The drawing surface + shared caches MenuRenderer needs from DebugRenderer.
    /// <paramref name="Canvas"/> is the CanvasItem to draw onto; the rest mirror
    /// DebugRenderer's existing private helpers so nothing is duplicated.
    /// </summary>
    public interface IHost
    {
        CanvasItem Canvas { get; }
        string? AgxRoot { get; }
        string? SpritesRoot { get; }
        SwdRenderer.IHost SwdRenderHost { get; }
        SwdTextStream.IHost SwdTextHost { get; }
        SwdWindow? LoadSwd(string name);
        void DrawDosFont(string text, int x, int y, string fontName, int basecolor, Color? modulate = null);
        // DrawMenuText stays in DebugRenderer (shared with DrawDosFont's
        // atlas-missing fallback); MenuRenderer reaches it through here.
        void DrawMenuText(string text, int x, int y, int size, Color color);
        void DrawUiSprite(MenuSpriteSpec spec);
        void DrawUiSprite(MenuSpriteSpec spec, Color modulate);
        Texture2D? LoadSpriteFromPath(string path);
        Texture2D? LoadSprite(string itemName);
        int MeasureText(string text, string fontName);
        int FontHeight(string fontName);
        // Overlays that remain in DebugRenderer but are reached via this
        // dispatcher (they share helpers with non-menu code).
        void DrawShipComputerOverlay(Font font);
        void DrawStoreOverlay(StoreLogic store);
        void DrawCreditsOverlay(Font font);
    }

    private IHost _host = null!;

    public void DrawMenuOverlay(MenuStateMachine menu, IHost host)
    {
        _host = host;
        var font = ThemeDB.FallbackFont;
        var canvas = host.Canvas;
        canvas.DrawRect(new Rect2(0, 0, 320, 200), Colors.Black);

        // BACKGRND_PIC (stars + Earth) only belongs behind the main menu.
        // Hangar and ship-computer screens own fullscreen sprites that may
        // have transparent regions — in C those reveal the cleared-to-black
        // framebuffer (GFX_FadeOut before SHIPCOMP_SWD), not the title art.
        bool isMainMenu = menu.State == WinState.Menu
                          && menu.PilotCreateStep == 0
                          && !menu.InSectorSelect;
        bool isMenuVisual = isMainMenu
                            || (menu.State == WinState.Unknown && !menu.InSectorSelect);
        if (isMenuVisual)
            host.DrawUiSprite(MenuChrome.Background);

        if (menu.State == WinState.Hangar)
        {
            host.DrawUiSprite(MenuChrome.Hangar);
            if ((SimClock.Frame / 5) % 3 == 0)
                host.DrawUiSprite(MenuChrome.HangarPilot);
            DrawHangarOverlay(font, menu);
            if (menu.InAskBool)
                DrawAskBoolOverlay(menu);
            return;
        }

        if (menu.State is WinState.Death or WinState.Landing or WinState.Intro or WinState.Victory)
        {
            DrawCutsceneOverlay(menu);
            return;
        }

        if (menu.InSectorSelect)
        {
            host.DrawShipComputerOverlay(font);
            return;
        }

        if (menu.State == WinState.Store && menu.Store != null)
        {
            host.DrawStoreOverlay(menu.Store);
            return;
        }

        if (menu.State == WinState.Help)
        {
            DrawHelpOverlay(menu);
            return;
        }

        if (menu.State == WinState.Credits)
        {
            host.DrawCreditsOverlay(font);
            return;
        }

        if (menu.PilotCreateStep > 0)
        {
            if (menu.PilotCreateStep == 3)
                DrawDifficultyOverlay(font, menu);
            else
                DrawRegisterOverlay(font, menu);
            return;
        }

        DrawMainMenuOverlay(menu);
        if (menu.InLoadMission)
            DrawLoadMissionOverlay(menu);
        if (menu.InOptions)
            DrawOptionsOverlay(menu);
        if (menu.InAskBool)
            DrawAskBoolOverlay(menu);
        if (menu.InWinMsg)
            DrawWinMsgOverlay(menu);
    }

    // Cutscene SFX dedup: the per-frame one-shot fires once, when the frame index
    // advances. Reset whenever the cutscene state changes.
    private WinState _sfxState = WinState.Unknown;
    private int _sfxFrameIndex = -1;

    private void DrawCutsceneOverlay(MenuStateMachine menu)
    {
        if (string.IsNullOrEmpty(_host.AgxRoot)) return;

        var movie = CutsceneLibrary.ForState(_host.AgxRoot, _host.SpritesRoot ?? "", menu.State);
        if (movie == null) return;

        int elapsed = SimClock.Frame - menu.StateEnteredFrame;
        if (!movie.TrySelectFrame(elapsed, out var frame, out var alpha)) return;

        // Fire the frame's one-shot sound effect once, when the index first advances to it
        // (INTRO.C per-frame soundfx). Headless leaves SoundEmitter.Sink null → no-op.
        if (menu.State != _sfxState) { _sfxState = menu.State; _sfxFrameIndex = -1; }
        int idx = movie.FrameIndexAt(elapsed);
        if (idx >= 0 && idx != _sfxFrameIndex)
        {
            _sfxFrameIndex = idx;
            if (frame.Sfx != null) SoundEmitter.Emit(frame.Sfx);
        }

        var tex = _host.LoadSpriteFromPath(frame.Path);
        if (tex != null)
            _host.Canvas.DrawTexture(tex, Vector2.Zero, new Color(1f, 1f, 1f, alpha));
    }

    private void DrawMainMenuOverlay(MenuStateMachine menu)
    {
        _host.DrawUiSprite(MenuChrome.RaptorLogo);
        _host.DrawUiSprite(MenuChrome.Copyright);
        for (int i = 0; i < MenuChrome.MainVisibleItems.Count; i++)
        {
            var item = MenuChrome.MainVisibleItems[i];
            // C uses GFX_ShadeShape(LIGHT, ...) on the selected SWD field; this
            // approximates the palette-lighten by scaling R/G/B unevenly so the
            // dim orange (146,52,12) maps roughly to the brighter (190,85,44).
            var modulate = i == menu.CurrentItem
                ? new Color(1.30f, 1.60f, 3.30f)
                : Colors.White;
            _host.DrawUiSprite(item, modulate);
        }
    }

    private void DrawOptionsOverlay(MenuStateMachine menu)
    {
        var swd = _host.LoadSwd("OPTS_SWD");
        if (swd == null) return;

        SwdRenderer.Draw(_host.SwdRenderHost, swd);

        if (!menu.OptionDetailHigh)
        {
            var detail = swd.Fields[6];
            _host.DrawDosFont("LOW DETAIL",
                swd.Window.X + detail.X + 27,
                swd.Window.Y + detail.Y + 2,
                detail.FontName,
                detail.FontBaseColor);
        }

        var music = swd.Fields[11];
        var fx = swd.Fields[12];
        _host.DrawUiSprite(MenuChrome.Slider with
        {
            X = swd.Window.X + music.X + menu.OptionMusicVolume - 2,
            Y = swd.Window.Y + music.Y
        });
        _host.DrawUiSprite(MenuChrome.Slider with
        {
            X = swd.Window.X + fx.X + menu.OptionFxVolume - 2,
            Y = swd.Window.Y + fx.Y
        });

        if (menu.OptionsField >= 0 && menu.OptionsField <= 2)
        {
            int fieldIndex = menu.OptionsField switch { 0 => 3, 1 => 4, _ => 5 };
            var target = swd.Fields[fieldIndex];
            _host.DrawUiSprite(MenuChrome.Pointer with
            {
                X = swd.Window.X + target.X,
                Y = swd.Window.Y + target.Y
            });
        }
    }

    /// <summary>Draw the "Abort Mission ?" prompt over an arbitrary host (the frozen
    /// in-game playfield). Reuses the menu AskBool overlay.</summary>
    public void DrawAbortPrompt(MenuStateMachine menu, IHost host)
    {
        _host = host;
        DrawAskBoolOverlay(menu);
    }

    private void DrawAskBoolOverlay(MenuStateMachine menu)
    {
        var swd = _host.LoadSwd("ASK_SWD");
        if (swd == null) return;

        int selectedFieldId = menu.AskBoolYesSelected ? 2 : 3; // YES id=2, NO id=3.
        SwdRenderer.Draw(_host.SwdRenderHost, swd, selectedFieldId: selectedFieldId);

        DrawDragBarText(swd, menu.AskBoolQuestion);
    }

    private void DrawWinMsgOverlay(MenuStateMachine menu)
    {
        // C WIN_Msg has its own SWD but it's not extracted; reuse ASK_SWD's
        // dragbar layout for a centered message panel, skipping the YES/NO
        // button fields (indices 6 and 7, ids 2 and 3).
        var swd = _host.LoadSwd("ASK_SWD");
        if (swd == null) return;

        var skip = new HashSet<int> { 6, 7 };
        SwdRenderer.Draw(_host.SwdRenderHost, swd, selectedFieldId: -1, skipFieldIndices: skip);

        DrawDragBarText(swd, menu.WinMsgText);
    }

    /// <summary>
    /// Render text centered inside a window's DRAGBAR field (index 5 in both
    /// ASK_SWD and other DRAGBAR-bearing SWDs). C's SWD_PutField centers the
    /// dragbar title via `text_x = (lx - GFX_StrPixelLen)/2 + fld_x`; without
    /// this Godot draws every AskBool/WinMsg title left-aligned at the field's
    /// x, which had read as a ~46 px (~15%) offset on `02_save_dialog`.
    /// </summary>
    private void DrawDragBarText(SwdWindow swd, string text)
    {
        var dragbar = swd.Fields[5];
        int tw = _host.MeasureText(text, dragbar.FontName);
        int fh = _host.FontHeight(dragbar.FontName);
        int x = swd.Window.X + dragbar.X + (dragbar.Lx - tw) / 2;
        int y = swd.Window.Y + dragbar.Y + (dragbar.Ly - fh) / 2;
        _host.DrawDosFont(text, x, y, dragbar.FontName, dragbar.FontBaseColor);
    }

    private void DrawLoadMissionOverlay(MenuStateMachine menu)
    {
        var swd = _host.LoadSwd("LOAD_SWD");
        if (swd == null) return;

        var skip = new HashSet<int> { 1, 9, 10, 12 };
        SwdRenderer.Draw(_host.SwdRenderHost, swd, selectedFieldId: 2, skipFieldIndices: skip);

        var pilot = menu.LoadMissionPilot;
        if (pilot == null) return;

        var idField = swd.Fields[1];
        string portrait = pilot.IdPic switch
        {
            1 => "BMALE_PIC",
            2 => "WFEMALE_PIC",
            3 => "BFEMALE_PIC",
            _ => "WMALE_PIC",
        };
        var portraitTex = _host.LoadSprite(portrait);
        if (portraitTex != null)
            _host.Canvas.DrawTexture(portraitTex, new Vector2(swd.Window.X + idField.X, swd.Window.Y + idField.Y));

        var name = swd.Fields[9];
        _host.DrawDosFont(pilot.Name,
            swd.Window.X + name.X,
            swd.Window.Y + name.Y,
            name.FontName,
            name.FontBaseColor);

        var call = swd.Fields[10];
        _host.DrawDosFont(pilot.Callsign,
            swd.Window.X + call.X,
            swd.Window.Y + call.Y,
            call.FontName,
            call.FontBaseColor);

        var credits = swd.Fields[12];
        _host.DrawDosFont(pilot.CreditsText,
            swd.Window.X + credits.X,
            swd.Window.Y + credits.Y,
            credits.FontName,
            credits.FontBaseColor);
    }

    private void DrawRegisterOverlay(Font font, MenuStateMachine menu)
    {
        _host.DrawUiSprite(MenuChrome.Register);
        // Draw the portrait variant the user is cycling (Alt/Ctrl or clicking the
        // badge) — C SWD_SetFieldItem(REG_IDPIC, sid_pics[cur_id]).
        var portraits = MenuChrome.RegisterPortraits;
        _host.DrawUiSprite(portraits[Math.Clamp(menu.RegisterIdPic, 0, portraits.Count - 1)]);
        DrawRegisterFieldText(ThemeDB.FallbackFont, menu);
        // REG_TEXT runtime content is "   CHANGE ID PICTURE" — see
        // WINDOWS.C regtext[1]; draw from the field's actual x=61.
        _host.DrawDosFont("   CHANGE ID PICTURE", 61, 181, "FONT1_FNT", 66);
        // CURSOR_PIC (4-point compass star) at REG_VIEWID center — mirrors C's
        // SWD_SetFieldPtr(window, REG_VIEWID) → PTR_SetPos to the badge center.
        _host.DrawUiSprite(MenuChrome.Cursor with { X = 37, Y = 118 });
    }

    private void DrawRegisterFieldText(Font font, MenuStateMachine menu)
    {
        // Field positions are authoritative in REGISTER_SWD.json: REG_NAME
        // (x=187, y=132) and REG_CALLSIGN (x=203, y=145). y is the field top;
        // DrawString anchors on the baseline, so add ~5px for the size-7 font.
        // (Previously the callsign reused the name's x=188 — should be 203 — and
        // a too-low y, which dropped it off its line on the clipboard.)
        var ink = Colors.Black;
        if (!string.IsNullOrEmpty(menu.PilotName))
            _host.DrawMenuText(menu.PilotName, 187, 137, 7, ink);
        if (!string.IsNullOrEmpty(menu.Callsign))
            _host.DrawMenuText(menu.Callsign, 203, 148, 7, ink);

        int caretX = menu.PilotCreateStep == 2 ? 203 : 187;
        int caretY = menu.PilotCreateStep == 2 ? 139 : 128;   // text baseline − caret height (9)
        string text = menu.PilotCreateStep == 2 ? menu.Callsign : menu.PilotName;
        if (!string.IsNullOrEmpty(text))
            caretX += MenuTextWidth(text, 7) + 1;
        _host.Canvas.DrawRect(new Rect2(caretX, caretY, 1, 9), ink);
    }

    private void DrawHangarOverlay(Font font, MenuStateMachine menu)
    {
        // C's WIN_Hangar maps HangarPosition -> FLD_VIEWAREA field name in
        // HANGAR_SWD, and the cursor + caption follow the active field:
        //   poslookup[4] = { HANG_MISSION, HANG_SUPPLIES, HANG_MAIN_MENU,
        //                    HANG_QSAVE };
        //   hangtext[4]  = { "FLY MISSION", "SUPPLY ROOM",
        //                    "EXIT HANGAR", "SAVE PILOT" };
        // The FLD_VIEWAREA fields are indices 2/3/4/5 in HANGAR_SWD.json,
        // and the caption goes through HANG_TEXT (index 1).
        var swd = _host.LoadSwd("HANGAR_SWD");
        if (swd == null) return;

        int viewIdx = menu.HangarPosition switch
        {
            0 => 2,  // HANG_MISSION
            1 => 3,  // HANG_SUPPLIES
            2 => 4,  // HANG_MAIN_MENU
            _ => 5,  // HANG_QSAVE
        };
        string caption = menu.HangarPosition switch
        {
            0 => "FLY MISSION",
            1 => "SUPPLY ROOM",
            2 => "EXIT HANGAR",
            _ => "SAVE PILOT",
        };

        // Cursor lands at the active FLD_VIEWAREA's center: PTR_SetPos(
        // fld.x + lx/2, fld.y + ly/2). CURSOR_PIC content centered at
        // sprite-local (7, 8).
        var area = swd.Fields[viewIdx];
        int cx = swd.Window.X + area.X + area.Lx / 2;
        int cy = swd.Window.Y + area.Y + area.Ly / 2;
        _host.DrawUiSprite(MenuChrome.Cursor with { X = cx - 7, Y = cy - 8 });

        // HANG_TEXT field (index 1): FONT1_FNT basecolor=66 at field origin.
        var caption_fld = swd.Fields[1];
        _host.DrawDosFont(caption,
            swd.Window.X + caption_fld.X,
            swd.Window.Y + caption_fld.Y,
            caption_fld.FontName,
            caption_fld.FontBaseColor);
    }

    private void DrawDifficultyOverlay(Font font, MenuStateMachine menu)
    {
        // In C, ASKDIFF is pushed on top of REGISTER — the registration page
        // (badge, bottom prompt) stays visible underneath. The mouse cursor
        // moves to the active field's center per WIN_AskDiff:
        //   SWD_SetActiveField(ASKDIFF_SWD, OKREG_MED);
        //   SWD_GetFieldXYL(...); PTR_SetPos(px+lx/2, py+ly/2);
        _host.DrawUiSprite(MenuChrome.Register);
        var diffPortraits = MenuChrome.RegisterPortraits;
        _host.DrawUiSprite(diffPortraits[Math.Clamp(menu.RegisterIdPic, 0, diffPortraits.Count - 1)]);
        // REG_TEXT field is at x=61, lx=181. The runtime sets the text to
        // regtext[1] = "   CHANGE ID PICTURE" (3 leading spaces). Each space
        // advances by width(9) + fontspacing(1) = 10, so 'C' lands at x=91.
        _host.DrawDosFont("   CHANGE ID PICTURE", 61, 181, "FONT1_FNT", 66);

        var swd = _host.LoadSwd("ASKDIFF_SWD");
        if (swd != null)
        {
            // OKREG_MED == field index 8 (id=3) → VETERAN. C's default new
            // pilot starts there. SWD_PutField applies GFX_ShadeShape(LIGHT)
            // on the active field; our SwdRenderer mirrors that via the
            // per-channel modulate.
            SwdRenderer.Draw(_host.SwdRenderHost, swd, selectedFieldId: menu.DifficultyFieldId);

            foreach (var f in swd.Fields)
            {
                if (f.Id != menu.DifficultyFieldId) continue;
                int cx = swd.Window.X + f.X + f.Lx / 2;
                int cy = swd.Window.Y + f.Y + f.Ly / 2;
                _host.DrawUiSprite(MenuChrome.Cursor with { X = cx - 7, Y = cy - 8 });
                break;
            }
        }
    }

    private void DrawHelpOverlay(MenuStateMachine menu)
    {
        var swd = _host.LoadSwd("HELP_SWD");
        if (swd == null) return;

        // Field 8 = HELP_TEXT (body), field 9 = HELP_HEADER (the "PAGE : NN"
        // label). C rewrites both every frame, so draw them manually and skip
        // them in the SWD pass — on every help page, not just the order screen.
        SwdRenderer.Draw(_host.SwdRenderHost, swd, skipFieldIndices: new HashSet<int> { 8, 9 });

        // C HELP.C:82 — sprintf("PAGE : %02u", curpage+1), set on HELP_HEADER for
        // every page. Was hardcoded "31" and gated to RAP1_TXT, so the number
        // never moved while paging the order info.
        _host.DrawDosFont($"PAGE : {menu.HelpPageIndex + 1:D2}", swd.Window.X + 247, swd.Window.Y + 10, "FONT2_FNT", 64);

        string? text = SwdTextStream.LoadText(menu.HelpTextName);
        if (string.IsNullOrEmpty(text)) return;

        var field = swd.Fields[8];
        SwdTextStream.Render(_host.SwdTextHost, text,
            swd.Window.X + field.X,
            swd.Window.Y + field.Y,
            field.Lx,
            field.Ly,
            field.FontName,
            field.FontBaseColor);
    }

    private void DrawRegisterIdTag(bool showCrosshair)
    {
        DrawSwdPanel(4, 110, 121, 58, new Color(0.10f, 0.10f, 0.10f), MenuChrome.MenuMid);
        _host.DrawUiSprite(MenuChrome.RegisterPortrait);
        _host.DrawMenuText("PILOT ID", 62, 120, 8, MenuChrome.MenuOrange);
        _host.DrawMenuText("RAPTOR", 62, 134, 8, new Color(0.72f, 0.70f, 0.62f));
        if (showCrosshair)
        {
            _host.Canvas.DrawLine(new Vector2(38, 112), new Vector2(38, 164), MenuChrome.MenuOrange, 1);
            _host.Canvas.DrawLine(new Vector2(18, 138), new Vector2(58, 138), MenuChrome.MenuOrange, 1);
        }
    }

    private void DrawBottomPrompt(string text)
    {
        DrawSwdPanel(68, 181, 183, 17, new Color(0.08f, 0.08f, 0.08f), MenuChrome.MenuMid);
        _host.DrawMenuText(text, 75, 195, 12, MenuChrome.MenuOrange);
    }

    private void DrawSwdPanel(int x, int y, int w, int h, Color fill, Color edge)
    {
        var canvas = _host.Canvas;
        canvas.DrawRect(new Rect2(x, y, w, h), fill);
        canvas.DrawLine(new Vector2(x, y), new Vector2(x + w - 1, y), edge, 1);
        canvas.DrawLine(new Vector2(x, y), new Vector2(x, y + h - 1), edge, 1);
        canvas.DrawLine(new Vector2(x, y + h - 1), new Vector2(x + w - 1, y + h - 1), Colors.Black, 1);
        canvas.DrawLine(new Vector2(x + w - 1, y), new Vector2(x + w - 1, y + h - 1), Colors.Black, 1);
    }

    private static int MenuTextWidth(string text, int size)
    {
        return (int)ThemeDB.FallbackFont.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
    }
}
