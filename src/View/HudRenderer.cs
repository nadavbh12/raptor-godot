using System;
using Godot;
using Raptor.Sim;

namespace Raptor.View;

/// <summary>
/// Draws the in-game HUD (score, shield bar, current weapon, mega-bomb count,
/// super-shield charges, scanner, low-shield/system-damage warnings). Pure View:
/// the layout math lives in the Hud* helpers (HudShieldBar, HudMegaBombIndicator,
/// HudScannerIndicator, …); this just resolves textures and draws onto the
/// supplied canvas. Owns the scanner animation state.
/// </summary>
internal sealed class HudRenderer
{
    private readonly HudScannerIndicator.State _scannerState = new();
    private int _lastScannerFrame = -1;

    /// <param name="canvas">The CanvasItem to draw onto (called from its _Draw).</param>
    /// <param name="loadSprite">Resolves an iname to a cached texture (null if absent).</param>
    /// <param name="digitTex">The 11 score-digit textures (0-9 plus "$" at index 10).</param>
    public void Draw(CanvasItem canvas, WaveController wave,
                     Func<string, Texture2D?> loadSprite, Texture2D?[] digitTex)
    {
        DrawScore(canvas, wave, digitTex);
        DrawShield(canvas, wave);
        DrawCurrentWeapon(canvas, wave, loadSprite);
        DrawMegaBomb(canvas, wave, loadSprite);
        DrawSuperShield(canvas, wave, loadSprite);
        DrawScanner(canvas, wave);
        DrawWarning(canvas, wave, loadSprite);
    }

    /// <summary>
    /// Draw the "$NNNNNNNN" score readout. Mirrors RAP.C lines 661-662:
    ///   sprintf(temp, "%08u", plr.score);
    ///   RAP_PrintNum(119, MAP_TOP, temp);
    /// RAP_PrintNum draws the $ sprite (numbers[10]) at (x, y) then steps +9
    /// pixels, then each digit advances +8 pixels.
    /// </summary>
    private static void DrawScore(CanvasItem canvas, WaveController wave, Texture2D?[] digitTex)
    {
        const int MapTop = 2;  // SOURCE/MAP.H
        int x = 119;
        // "$" prefix.
        if (digitTex[10] != null) canvas.DrawTexture(digitTex[10]!, new Vector2(x, MapTop));
        x += 9;
        string score = wave.Score.ToString("D8");
        foreach (char c in score)
        {
            int d = c - '0';
            if (d >= 0 && d <= 9 && digitTex[d] != null)
                canvas.DrawTexture(digitTex[d]!, new Vector2(x, MapTop));
            x += 8;
        }
    }

    private static void DrawShield(CanvasItem canvas, WaveController wave)
    {
        const int MapRight = 320 - 16;  // SOURCE/MAP.H
        foreach (var segment in HudShieldBar.Build(MapRight + 4, wave.PlayerLogic.Shield))
        {
            canvas.DrawRect(new Rect2(segment.X, segment.Y, segment.Width, segment.Height),
                ShieldPaletteColor(segment.PaletteIndex));
        }
    }

    private static Color ShieldPaletteColor(int paletteIndex)
    {
        if (paletteIndex == 0) return new Color(0, 0, 0, 1);
        var rgb = HudPalette.Color(paletteIndex);
        return new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, 1);
    }

    private static void DrawCurrentWeapon(CanvasItem canvas, WaveController wave, Func<string, Texture2D?> loadSprite)
    {
        if (wave.Inventory.EquippedSpecial is not ObjType weapon) return;
        const int MapTop = 2;           // SOURCE/MAP.H
        const int MapRight = 320 - 16;  // SOURCE/MAP.H
        string spriteName = HudWeaponIcon.SpriteNameFor(weapon);
        var tex = loadSprite(spriteName);
        if (tex != null)
            canvas.DrawTexture(tex, new Vector2(MapRight - 18, MapTop));
    }

    private static void DrawMegaBomb(CanvasItem canvas, WaveController wave, Func<string, Texture2D?> loadSprite)
    {
        int megaBombCount = wave.Inventory.GetAmt(ObjType.MegaBomb);
        if (megaBombCount <= 0) return;
        var tex = loadSprite("SMBOMB_PIC");
        if (tex == null) return;
        foreach (var pos in HudMegaBombIndicator.Build(megaBombCount))
            canvas.DrawTexture(tex, new Vector2(pos.X, pos.Y));
    }

    private static void DrawSuperShield(CanvasItem canvas, WaveController wave, Func<string, Texture2D?> loadSprite)
    {
        // One icon per super-shield CHARGE (C OBJECTS.C:665 counts discrete objects).
        // The port stores super-shield as a single point buffer, so convert points
        // → charges = ceil(points / per-charge). per-charge = SuperShield StartCnt.
        int points = wave.Inventory.GetAmt(ObjType.SuperShield);
        int count = HudSuperShieldIndicator.ChargeCount(
            points, ObjLib.Of(ObjType.SuperShield).StartCnt);
        if (count <= 0) return;
        var tex = loadSprite("SMSHIELD_PIC");
        if (tex == null) return;
        foreach (var p in HudSuperShieldIndicator.Build(count))
            canvas.DrawTexture(tex, new Vector2(p.X, p.Y));
    }

    private void DrawScanner(CanvasItem canvas, WaveController wave)
    {
        if (!wave.HasSecretsDetector) return;
        int dmg = wave.GetBaseDamage();
        if (dmg > 0)
        {
            foreach (var b in HudScannerIndicator.BuildDamage(dmg))
                canvas.DrawRect(new Rect2(b.X, b.Y, b.W, b.H), ScannerPaletteColor(b.PaletteIndex));
        }
        else
        {
            foreach (var line in HudScannerIndicator.BuildIdle(_scannerState.CurrentDpos))
                canvas.DrawRect(new Rect2(line.X, line.Y, 1, line.Height),
                    ScannerPaletteColor(line.PaletteIndex));
        }
        if (_lastScannerFrame != SimClock.Frame)
        {
            _scannerState.AfterSimTick();
            _lastScannerFrame = SimClock.Frame;
        }
        else
        {
            _scannerState.AfterRenderFrame();
        }
    }

    private static void DrawWarning(CanvasItem canvas, WaveController wave, Func<string, Texture2D?> loadSprite)
    {
        if (!wave.ShieldLowWarningVisible) return;
        if (wave.SystemDamageWarningVisible)
        {
            var damageTex = loadSprite("WEPDEST_PIC");
            if (damageTex != null)
                canvas.DrawTexture(damageTex, new Vector2(HudWarning.CenterX((int)damageTex.GetWidth()), HudWarning.SystemDamageY));
        }

        var tex = loadSprite("SHLDLOW_PIC");
        if (tex == null) return;
        canvas.DrawTexture(tex, new Vector2(HudWarning.CenterX((int)tex.GetWidth()), HudWarning.MapBottom));
    }

    private static Color ScannerPaletteColor(int paletteIndex)
    {
        var rgb = HudPalette.Color(paletteIndex);
        return new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, 1);
    }
}
