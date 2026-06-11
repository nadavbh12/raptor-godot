namespace Raptor.Sim;

/// <summary>
/// Owns the LOAD-mission pilot list and its cursor navigation (the C
/// RAP_LoadWin selection arithmetic). Extracted from <see cref="MenuStateMachine"/>
/// (Phase 4 tranche B).
///
/// MenuStateMachine remains the parity frame-anchor and the I/O / transition
/// owner: it performs the <c>PilotSaveStore.LoadAll(PilotSaveDirectory)</c> read,
/// decides whether to open this panel or raise the "No Pilots to Load" WIN_Msg,
/// and on confirm fires <c>OnPilotLoaded</c> + the <see cref="WinState.Hangar"/>
/// transition via <c>ApplyLoadedPilot</c>. This collaborator only holds the
/// pilot list + selected index and advances the cursor. The View reads
/// <see cref="MenuStateMachine.InLoadMission"/>/<see cref="MenuStateMachine.LoadMissionPilots"/>/
/// <see cref="MenuStateMachine.LoadMissionSelectedIndex"/>/<see cref="MenuStateMachine.LoadMissionPilot"/>,
/// which forward here.
/// </summary>
internal sealed class LoadMissionPanel
{
    /// <summary>Signal returned by <see cref="HandleInput"/> telling the
    /// MenuStateMachine which transition (if any) to perform.</summary>
    internal enum Result
    {
        /// <summary>Input consumed; no transition (nav / no-op).</summary>
        Handled,
        /// <summary>Escape: panel closed, return to the menu (no win-state change).</summary>
        Closed,
        /// <summary>Return: load the <see cref="SelectedPilot"/> and transition to Hangar.</summary>
        Confirm,
        /// <summary>Delete: open the "Delete Pilot X ?" confirm dialog.</summary>
        Delete,
    }

    private bool _active = false;
    private System.Collections.Generic.List<PilotSaveSummary> _pilots = new();

    public bool Active => _active;
    public int SelectedIndex { get; private set; }
    public System.Collections.Generic.IReadOnlyList<PilotSaveSummary> Pilots => _pilots;
    public PilotSaveSummary? SelectedPilot =>
        _pilots.Count == 0 ? null : _pilots[SelectedIndex];

    /// <summary>Open the dialog over the given (non-empty) pilot list, cursor at slot 0.</summary>
    public void Open(System.Collections.Generic.List<PilotSaveSummary> pilots)
    {
        _pilots = pilots;
        SelectedIndex = 0;
        _active = true;
    }

    /// <summary>Reset to closed/idle with an empty list (EnterMenu).</summary>
    public void Reset()
    {
        _active = false;
        _pilots = new();
        SelectedIndex = 0;
    }

    /// <summary>
    /// Handle one input action while the dialog is open. Selection nav is applied
    /// here; the caller (MenuStateMachine) acts on the returned <see cref="Result"/>
    /// for the Escape (close) and Return (confirm load) transitions.
    /// </summary>
    public Result HandleInput(string action)
    {
        if (action == "Escape")
        {
            _active = false;
            return Result.Closed;
        }

        if (action == "Return")
        {
            // C LOADSAVE.C:608-612: Return on LOAD_LOAD calls RAP_LoadPlayer
            // which copies the saved PLAYEROBJ into active game state, then
            // returns to the hangar (ingameflag=FALSE in WIN_MainMenu exits
            // the menu loop). The MenuStateMachine applies the selected pilot's
            // data, fires OnPilotLoaded so WaveController can pick up Score etc.,
            // then transitions to Hangar.
            _active = false;
            return Result.Confirm;
        }

        if (action == "Delete")
            return _pilots.Count == 0 ? Result.Handled : Result.Delete;

        if (_pilots.Count == 0)
            return Result.Handled;

        // C RAP_LoadWin: Down/PageDown/Left → next; Up/PageUp/Right → prev; wrap.
        int delta = action switch
        {
            "Down" or "PageDown" or "Left" => +1,
            "Up" or "PageUp" or "Right" => -1,
            _ => 0,
        };
        if (delta != 0)
        {
            int n = _pilots.Count;
            SelectedIndex = ((SelectedIndex + delta) % n + n) % n;
        }
        return Result.Handled;
    }

    /// <summary>Remove the selected pilot from the list (after its file is deleted) and
    /// clamp the cursor. Returns true if the list is now empty.</summary>
    public bool RemoveSelected()
    {
        if (_pilots.Count == 0) return true;
        _pilots.RemoveAt(SelectedIndex);
        if (_pilots.Count == 0) { _active = false; SelectedIndex = 0; return true; }
        if (SelectedIndex >= _pilots.Count) SelectedIndex = _pilots.Count - 1;
        return false;
    }
}
