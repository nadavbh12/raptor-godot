namespace Raptor.Sim;

/// <summary>
/// Owns the pilot-creation multi-step dialog state machine (name entry →
/// callsign entry → difficulty → confirm) and its byte-identical text-entry
/// semantics. Extracted from <see cref="MenuStateMachine"/> (Phase 4 tranche B).
///
/// Step layout (mirrors the original MenuStateMachine comment):
///   Step 0: idle (not in pilot creation)
///   Step 1: name dialog — waiting for Return (confirm name)
///   Step 2: callsign dialog — waiting for Return (confirm callsign → difficulty)
///   Step 3: difficulty dialog — waiting for Return (accept → HANGAR)
/// Typed letters are absorbed; name/callsign cap at 12 chars and are upper-cased.
///
/// MenuStateMachine stays the parity frame-anchor and transition/event owner:
/// the F1→HELP open, the OnPilotCreated emission, and the WinState.Hangar
/// transition (+ hangar cursor write) are driven by MenuStateMachine off the
/// <see cref="Result"/> returned by <see cref="HandleInput"/>. This collaborator
/// also holds <see cref="PilotName"/>/<see cref="Callsign"/> (the pilot identity
/// the text entry builds); the load/save paths set them via <see cref="SetIdentity"/>.
/// The View reads <see cref="MenuStateMachine.PilotCreateStep"/>/
/// <see cref="MenuStateMachine.DifficultyFieldId"/>/<see cref="MenuStateMachine.PilotName"/>/
/// <see cref="MenuStateMachine.Callsign"/>, which forward here.
/// </summary>
internal sealed class PilotCreationFlow
{
    /// <summary>Signal returned by <see cref="HandleInput"/> telling the
    /// MenuStateMachine which transition/event (if any) to perform.</summary>
    internal enum Result
    {
        /// <summary>Input consumed; no transition (step advance / text edit / nav / no-op).</summary>
        Handled,
        /// <summary>F1: open the NEWPLAY1_TXT help page (MenuStateMachine performs EnterHelp).</summary>
        OpenHelp,
        /// <summary>Step-4 difficulty accepted: fire OnPilotCreated + enter HANGAR.</summary>
        Confirm,
    }

    private int _step = 0;
    private int _difficultyFieldId = 3; // ASKDIFF MED/VETERAN default.

    public int Step => _step;
    public int DifficultyFieldId => _difficultyFieldId;

    /// <summary>
    /// The DIFF value (0..3) chosen at the last accepted difficulty dialog,
    /// captured BEFORE <see cref="_difficultyFieldId"/> is reset for the next
    /// pilot. OnPilotCreated reads this — reading the live field instead gives
    /// the reset default (3→VETERAN) regardless of the choice.
    /// </summary>
    public int AcceptedDiff { get; private set; } = 2;   // DIFF_2 (VETERAN) default
    public string PilotName { get; private set; } = "";
    public string Callsign { get; private set; } = "";

    /// <summary>True while the multi-step dialog is active (step > 0).</summary>
    public bool Active => _step > 0;

    /// <summary>Begin the name dialog (Return on NEW). No win-state change.</summary>
    public void Begin()
    {
        _step = 1;
    }

    /// <summary>Reset to idle, clearing step/difficulty/name/callsign (EnterMenu).</summary>
    public void Reset()
    {
        _step = 0;
        _difficultyFieldId = 3;
        PilotName = "";
        Callsign = "";
    }

    /// <summary>
    /// Reset just the step machine to idle (CompleteMission / PlayerDied left
    /// PilotName/Callsign untouched, mirroring the original bare
    /// <c>_pilotCreateStep = 0</c>).
    /// </summary>
    public void ResetStep() => _step = 0;

    /// <summary>Set the pilot identity from outside the creation flow (loaded pilot).</summary>
    public void SetIdentity(string name, string callsign)
    {
        PilotName = name;
        Callsign = callsign;
    }

    /// <summary>Force the active step (pointer click on the name/callsign field).</summary>
    public void SetStep(int step) => _step = step;

    /// <summary>Force the difficulty field id (pointer click on a difficulty button).</summary>
    public void SetDifficultyField(int field) => _difficultyFieldId = field;

    /// <summary>
    /// Handle one input action while the dialog is active (step > 0). Step
    /// transitions, difficulty nav, and char append/backspace are applied here;
    /// the caller (MenuStateMachine) acts on the returned <see cref="Result"/>
    /// for the F1→help and step-4 confirm (OnPilotCreated + HANGAR) transitions.
    /// </summary>
    public Result HandleInput(string action)
    {
        if (action == "F1")
        {
            // C WINDOWS.C:800 — SC_F1 in registration → HELP_Win("NEWPLAY1_TXT").
            return Result.OpenHelp;
        }
        if (action == "Escape")
        {
            if (_step == 1)
            {
                _step = 0;
                PilotName = "";
                Callsign = "";
            }
            else
            {
                _step--;
                if (_step < 3)
                    _difficultyFieldId = 3;
            }
            return Result.Handled;
        }
        if (_step == 3)
        {
            if (action == "Down" || action == "Right")
            {
                _difficultyFieldId = _difficultyFieldId == 5 ? 1 : _difficultyFieldId + 1;
                return Result.Handled;
            }
            if (action == "Up" || action == "Left")
            {
                _difficultyFieldId = _difficultyFieldId == 1 ? 5 : _difficultyFieldId - 1;
                return Result.Handled;
            }
        }
        if (action == "Return")
        {
            // C WINDOWS.C WIN_Register: ENTER on REG_NAME advances to REG_CALLSIGN
            // only when the name is non-empty (`if (strlen(tp.name) && keypress ==
            // SC_ENTER)`), and accept needs a name too. Don't let an empty-name
            // Return walk the whole flow into the hangar.
            if (_step == 1 && PilotName.Length == 0)
                return Result.Handled;   // stay on the name field
            _step++;
            if (_step == 2)
            {
                // Name confirmed; now in callsign dialog.
                return Result.Handled;
            }
            if (_step == 3)
            {
                // Callsign confirmed; now in difficulty dialog.
                return Result.Handled;
            }
            if (_step == 4)
            {
                if (_difficultyFieldId == 5)
                {
                    _step = 0;
                    _difficultyFieldId = 3;
                    return Result.Handled;
                }
                // Difficulty accepted → enter HANGAR (with fade delay).
                // C: hangto defaults to HANGTOSTORE → pos=1 (SUPPLIES) on first entry.
                // Capture the choice (ASKDIFF field 1..4 → DIFF_0..3) BEFORE the
                // field is reset below, so OnPilotCreated can apply it.
                AcceptedDiff = _difficultyFieldId - 1;
                _step = 0;
                _difficultyFieldId = 3;
                return Result.Confirm;
            }
        }
        if (action == "Backspace")
        {
            if (_step == 1 && PilotName.Length > 0)
                PilotName = PilotName[..^1];
            else if (_step == 2 && Callsign.Length > 0)
                Callsign = Callsign[..^1];
            return Result.Handled;
        }
        if (action.Length == 1 && char.IsLetterOrDigit(action[0]))
        {
            if (_step == 1 && PilotName.Length < 12)
                PilotName += char.ToUpperInvariant(action[0]);
            else if (_step == 2 && Callsign.Length < 12)
                Callsign += char.ToUpperInvariant(action[0]);
            return Result.Handled;
        }
        // Other non-Return keys are absorbed silently.
        return Result.Handled;
    }
}
