using System;

namespace Raptor.Sim;

/// <summary>
/// Owns the OPTIONS dialog state (active flag, focused field, detail toggle,
/// music/FX volume) and the keyboard/pointer handlers mirroring WINDOWS.C's
/// options window. Extracted from <see cref="MenuStateMachine"/> (Phase 4
/// tranche A). This panel is self-contained: it triggers no win-state
/// transitions, so MenuStateMachine merely opens/resets it and routes the menu
/// input/click to it while <see cref="Active"/>. The View reads
/// <see cref="MenuStateMachine.InOptions"/>/<see cref="MenuStateMachine.OptionsField"/>
/// /etc., which forward here.
/// </summary>
internal sealed class OptionsPanel
{
    private bool _inOptions = false;
    private int _optionsField = 0; // 0=detail, 1=music volume, 2=sound FX volume.
    private bool _optionDetailHigh = true;
    // WINDOWS.C:39 opt_vol = {127,127}; FX.C:906/973 default both volume globals
    // to 127 (full) on first run. No persisted-prefs layer yet, so seed full.
    private int _optionMusicVolume = 127;
    private int _optionFxVolume = 127;

    public bool Active => _inOptions;
    public int Field => _optionsField;
    public bool DetailHigh => _optionDetailHigh;
    public int MusicVolume => _optionMusicVolume;
    public int FxVolume => _optionFxVolume;

    /// <summary>Open the OPTIONS dialog with focus on the detail field.</summary>
    public void Open()
    {
        _inOptions = true;
        _optionsField = 0;
    }

    /// <summary>Reset the dialog to closed/idle (EnterMenu). Volume + detail persist.</summary>
    public void Reset()
    {
        _inOptions = false;
        _optionsField = 0;
    }

    /// <summary>
    /// Close the dialog without touching the focused field. Mirrors the bare
    /// <c>_inOptions = false</c> in CompleteMission/PlayerDied (which left
    /// _optionsField untouched, since the closed panel's field is unobserved).
    /// </summary>
    public void Close() => _inOptions = false;

    public bool HandleInput(string action)
    {
        if (action == "Escape")
        {
            _inOptions = false;
            _optionsField = 0;
            return true;
        }
        if (action == "Down")
        {
            if (_optionsField < 2) _optionsField++;
            return true;
        }
        if (action == "Up")
        {
            if (_optionsField > 0) _optionsField--;
            return true;
        }
        if (action == "Left")
        {
            AdjustOptionVolume(-8);
            return true;
        }
        if (action == "Right")
        {
            AdjustOptionVolume(8);
            return true;
        }
        if (action == "Return" && _optionsField == 0)
        {
            _optionDetailHigh = !_optionDetailHigh;
            return true;
        }
        return true;
    }

    public bool HandlePointerClick(int x, int y)
    {
        if (InRect(x, y, 184, 159, 57, 12))
        {
            _inOptions = false;
            _optionsField = 0;
            return true;
        }
        if (InRect(x, y, 107, 58, 118, 12))
        {
            _optionsField = 0;
            _optionDetailHigh = !_optionDetailHigh;
            return true;
        }
        if (InRect(x, y, 107, 91, 127, 13))
        {
            _optionsField = 1;
            _optionMusicVolume = Math.Clamp(x - 107, 0, 127);
            return true;
        }
        if (InRect(x, y, 107, 131, 127, 13))
        {
            _optionsField = 2;
            _optionFxVolume = Math.Clamp(x - 107, 0, 127);
            return true;
        }
        return false;
    }

    private void AdjustOptionVolume(int delta)
    {
        if (_optionsField == 1)
            _optionMusicVolume = Math.Clamp(_optionMusicVolume + delta, 0, 127);
        else if (_optionsField == 2)
            _optionFxVolume = Math.Clamp(_optionFxVolume + delta, 0, 127);
    }

    private static bool InRect(int x, int y, int rx, int ry, int w, int h)
        => x >= rx && x < rx + w && y >= ry && y < ry + h;
}
