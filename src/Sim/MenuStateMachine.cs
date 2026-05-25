using System;

namespace Raptor.Sim;

/// <summary>
/// Pure C# state machine mirroring the C version's WIN_MainMenu/WIN_Credits
/// win-state transitions. No Godot types — safe to instantiate in xUnit tests.
///
/// Menu item layout (from SOURCE/MAIN.INC field indices 1..7):
///   0 = MAIN_NEW     (field 0x0001)
///   1 = MAIN_LOAD    (field 0x0002)
///   2 = MAIN_OPTS    (field 0x0003)
///   3 = MAIN_ORDER   (field 0x0004)
///   4 = MAIN_CREDITS (field 0x0005)
///   5 = MAIN_QUIT    (field 0x0006)
///   6 = MAIN_RETURN  (field 0x0007) — only active when ingame
///
/// mission_start.txt path:
///   1. Return on NEW → enters pilot-creation sub-flow
///   2. (text input) Return → confirm name
///   3. (text input) Return → confirm callsign → enter DIFFICULTY dialog
///   4. Return → accept difficulty → HANGAR (win=5)
///   5. Down → highlight Mission option (position 0 in HANGAR)
///   6. Return → leave HANGAR → UNKNOWN (sector select)
///   7. Return → raptor_parity_game_enter → MISSION_1 (game enter; in-game win)
///
/// Anchor semantics mirror parity.c's raptor_parity_set_win_state:
///   - Entering any non-Unknown state: re-anchor (reset StateEnteredFrame).
///   - Entering Unknown: keep the previous anchor so relative fc continues
///     from where it left off (C parity.c only re-anchors when win != 0).
/// </summary>
public sealed class MenuStateMachine
{
    public const int DeathMovieFrames = 574;
    // Normal menu has 7 items (indices 0-6): NEW, LOAD, OPTS, ORDER, CREDITS, QUIT, RETURN.
    public const int ItemCount = 7;
    public const int CreditsItemIndex = 4;
    public const int OrderItemIndex   = 3;
    public const int OptionsItemIndex = 2;
    public const int LoadItemIndex    = 1;
    public const int NewItemIndex     = 0;

    /// <summary>
    /// Simulated animation delay (in frames at 70 Hz) before CREDITS state is anchored.
    /// In the C version, WIN_Credits runs GFX_FadeOut(16) + ShowWindow + GFX_FadeIn(16)
    /// before calling raptor_parity_set_win_state(2). Empirically ~70 frames.
    /// </summary>
    public const int CreditsFadeFrames = 70;

    /// <summary>
    /// Simulated animation delay before HELP state is anchored.
    /// WIN_Help / WIN_Order call HELP_Win which calls set_win_state(3) immediately.
    /// </summary>
    public const int HelpFadeFrames = 0;

    /// <summary>
    /// Simulated delay (frames) between accepting difficulty and HANGAR being anchored.
    ///
    /// C path: WIN_Register() completes → menu_exit → raptor_parity_set_win_state(0) →
    /// GFX_FadeOut → SWD_DestroyWindow → WIN_Hangar → GFX_FadeOut(2) → SWD_InitMasterWindow
    /// → SWD_ShowAllWindows → GFX_FadeIn(16) → raptor_parity_set_win_state(5).
    ///
    /// Derivation from golden: difficulty Return fires at absolute frame ~622.
    /// HANGAR exits (key Return on MISSION) at frame ~1326.
    /// For fc=630 (curSec=9) to emit as UNKNOWN (not HANGAR), HANGAR must exit before
    /// anchor+630 = 622 + delay + 630. Solving: delay > 1326-630-622 = 74. Use 100.
    /// Verify: anchor=722, fc=560 at 1282 (still HANGAR ✓), fc=630 at 1352 → UNKNOWN ✓.
    /// </summary>
    public const int HangarFadeFrames = 100;

    // ── Pilot-creation sub-flow ──────────────────────────────────────────────
    // When the player presses Return on NEW, we enter a multi-step dialog:
    //   Step 0: idle (not in pilot creation)
    //   Step 1: name dialog — waiting for Return (confirm name)
    //   Step 2: callsign dialog — waiting for Return (confirm callsign → difficulty)
    //   Step 3: difficulty dialog — waiting for Return (accept → HANGAR)
    // Typed letters are no-ops in the state machine (text input is absorbed).
    private int _pilotCreateStep = 0;
    private int _difficultyFieldId = 3; // ASKDIFF MED/VETERAN default.

    // ── Hangar sub-state ─────────────────────────────────────────────────────
    // Hangar has 4 positions: 0=MISSION, 1=SUPPLIES, 2=MAINMENU, 3=QSAVE.
    // C default hangto=HANGTOSTORE(0) → case HANGTOSTORE → pos=1 (SUPPLIES).
    // From golden: 02_hangar_supplies = initial dump, then Down to 03_hangar_mission.
    // Down in C: pos-- (so 1→0=MISSION). Then Return on MISSION → hangar_exit → UNKNOWN.
    private int _hangarPos = 1;  // default: SUPPLIES (hangto=HANGTOSTORE)

    // ── Sector-select sub-state ──────────────────────────────────────────────
    // After HANGAR exit → UNKNOWN (sector select dialog). One Return → game enter.
    private bool _inSectorSelect = false;
    private bool _inOptions = false;
    private bool _inLoadMission = false;
    private bool _inAskBool = false;
    private string _askBoolQuestion = "";
    private bool _askBoolYes = true;
    private Action? _askBoolOnYes;
    private int _optionsField = 0; // 0=detail, 1=music volume, 2=sound FX volume.
    private bool _optionDetailHigh = true;
    private int _optionMusicVolume = 64;
    private int _optionFxVolume = 64;
    private string _helpTextName = "HELP1_TXT";

    public WinState State { get; private set; } = WinState.Unknown;

    /// <summary>
    /// True once raptor_parity_game_enter has fired.
    /// Signals WaveController to start the game loop.
    /// </summary>
    public bool InGame { get; private set; } = false;

    /// <summary>
    /// Which game slot (0=cur_game=0 → MISSION_1).
    /// </summary>
    public int GameNum { get; private set; } = 0;

    /// <summary>
    /// The highlighted menu item index (0-based, cycles 0..ItemCount-1).
    /// Only meaningful while State == WinState.Menu.
    /// </summary>
    public int CurrentItem { get; private set; } = 0;

    public int PilotCreateStep => _pilotCreateStep;
    public string PilotName { get; private set; } = "";
    public string Callsign { get; private set; } = "";
    public int DifficultyFieldId => _difficultyFieldId;
    public int HangarPosition => _hangarPos;
    public bool InSectorSelect => _inSectorSelect;
    public bool InOptions => _inOptions;
    public bool InLoadMission => _inLoadMission;
    public bool InAskBool => _inAskBool;
    public string AskBoolQuestion => _askBoolQuestion;
    public bool AskBoolYesSelected => _askBoolYes;
    public int OptionsField => _optionsField;
    public bool OptionDetailHigh => _optionDetailHigh;
    public int OptionMusicVolume => _optionMusicVolume;
    public int OptionFxVolume => _optionFxVolume;
    public string HelpTextName => _helpTextName;
    public string? PilotSaveDirectory { get; init; }
    public System.Collections.Generic.IReadOnlyList<PilotSaveSummary> LoadMissionPilots => _loadMissionPilots;
    public int LoadMissionSelectedIndex { get; private set; }
    public PilotSaveSummary? LoadMissionPilot =>
        _loadMissionPilots.Count == 0 ? null : _loadMissionPilots[LoadMissionSelectedIndex];

    private System.Collections.Generic.List<PilotSaveSummary> _loadMissionPilots = new();

    /// <summary>
    /// The frame number at which the CURRENT (non-Unknown) context was entered.
    /// Mirrors g_menu_fc0 in parity.c: only updated when entering a non-Unknown state.
    /// </summary>
    public int StateEnteredFrame { get; private set; } = 0;

    /// <summary>
    /// Frame at which raptor_parity_game_enter was called.
    /// Used by ParityEmitter to anchor MISSION_* relative fc.
    /// </summary>
    public int GameEnteredFrame { get; private set; } = 0;

    /// <summary>
    /// Fired when a state transition (into or out of a named win-state) occurs.
    /// </summary>
    public event Action? OnStateChanged;

    /// <summary>
    /// Fired when a new pilot is created (after difficulty accepted → before HANGAR).
    /// WaveController subscribes to set initial player stats (score, shield).
    /// </summary>
    public event Action? OnPilotCreated;

    /// <summary>
    /// Fired when the game starts (raptor_parity_game_enter).
    /// WaveController subscribes to begin loading wave and ticking.
    /// </summary>
    public event Action<int>? OnGameEnter;   // arg: gameNum (0=Mission1)

    public void CompleteMission(int currentFrame)
    {
        InGame = false;
        _inSectorSelect = false;
        _inOptions = false;
        _pilotCreateStep = 0;
        _hangarPos = 1;
        EnterState(WinState.Hangar, currentFrame, reAnchor: true);
    }

    public void PlayerDied(int currentFrame)
    {
        InGame = false;
        _inSectorSelect = false;
        _inOptions = false;
        _pilotCreateStep = 0;
        EnterState(WinState.Death, currentFrame, reAnchor: true);
    }

    public bool CompleteDeathMovieIfDone(int currentFrame, int deathMovieFrames)
    {
        if (State != WinState.Death) return false;
        if (currentFrame - StateEnteredFrame < deathMovieFrames) return false;
        EnterMenu(currentFrame);
        return true;
    }

    /// <summary>
    /// Notify the machine that the menu is now visible and ready for input.
    /// Mirrors raptor_parity_set_win_state(1) called right after ShowAllWindows.
    /// </summary>
    public void EnterMenu(int currentFrame)
    {
        CurrentItem = 0;
        _pilotCreateStep = 0;
        _difficultyFieldId = 3;
        PilotName = "";
        Callsign = "";
        _hangarPos = 1;
        _inSectorSelect = false;
        _inOptions = false;
        _inLoadMission = false;
        _loadMissionPilots = new();
        LoadMissionSelectedIndex = 0;
        _inAskBool = false;
        _askBoolQuestion = "";
        _askBoolYes = true;
        _askBoolOnYes = null;
        _optionsField = 0;
        _helpTextName = "HELP1_TXT";
        EnterState(WinState.Menu, currentFrame, reAnchor: true);
    }

    /// <summary>
    /// Handle one input action. Returns true if a win-state transition occurred.
    /// Key names: "Down", "Up", "Return", "Escape", single letters, "F1", etc.
    /// </summary>
    public bool HandleInput(string action, int currentFrame)
    {
        switch (State)
        {
            case WinState.Menu:
                return HandleMenuInput(action, currentFrame);

            case WinState.Credits:
                if (action == "Return" || action == "Escape")
                {
                    // Exit credits → back to UNKNOWN (anchor not reset on unknown).
                    EnterState(WinState.Unknown, currentFrame, reAnchor: false);
                    return true;
                }
                break;

            case WinState.Help:
                if (action == "Return" || action == "Escape")
                {
                    EnterState(WinState.Unknown, currentFrame, reAnchor: false);
                    return true;
                }
                break;

            case WinState.Hangar:
                return HandleHangarInput(action, currentFrame);

            case WinState.Store:
                return HandleStoreInput(action, currentFrame);

            case WinState.Unknown:
                if (_inSectorSelect && action == "Escape")
                {
                    _inSectorSelect = false;
                    EnterState(WinState.Hangar, currentFrame, reAnchor: false);
                    return true;
                }
                if (_inSectorSelect && action == "Return")
                {
                    // Sector select → game enter (raptor_parity_game_enter).
                    EnterGame(currentFrame);
                    return true;
                }
                break;
        }
        return false;
    }

    /// <summary>
    /// Handle one pointer click in the 320x200 Raptor viewport. Returns true
    /// when the click was inside a known interactive region.
    /// </summary>
    public bool HandlePointerClick(int x, int y, int currentFrame)
    {
        if (State != WinState.Menu && State != WinState.Hangar && State != WinState.Store && State != WinState.Unknown)
            return false;

        if (_pilotCreateStep == 1 || _pilotCreateStep == 2)
        {
            if (InRect(x, y, 183, 128, 110, 12))
            {
                _pilotCreateStep = 1;
                return true;
            }
            if (InRect(x, y, 183, 144, 110, 12))
            {
                _pilotCreateStep = 2;
                return true;
            }
            return false;
        }

        if (_pilotCreateStep == 3)
        {
            int field = DifficultyFieldAt(x, y);
            if (field == 0) return false;
            _difficultyFieldId = field;
            return HandleInput("Return", currentFrame);
        }

        if (State == WinState.Menu)
        {
            if (_inOptions)
                return HandleOptionsPointerClick(x, y);

            int item = MainMenuItemAt(x, y);
            if (item < 0) return false;
            CurrentItem = item;
            return HandleInput("Return", currentFrame);
        }

        if (State == WinState.Hangar)
        {
            int pos = HangarPositionAt(x, y);
            if (pos < 0) return false;
            _hangarPos = pos;
            return HandleInput("Return", currentFrame);
        }

        if (State == WinState.Unknown && _inSectorSelect)
        {
            return HandleInput("Return", currentFrame);
        }

        return false;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private bool HandleMenuInput(string action, int currentFrame)
    {
        if (_inOptions)
            return HandleOptionsInput(action);
        if (_inLoadMission)
            return HandleLoadMissionInput(action);

        // Pilot-creation sub-flow: absorb inputs until we've consumed enough Returns.
        if (_pilotCreateStep > 0)
        {
            if (action == "Escape")
            {
                if (_pilotCreateStep == 1)
                {
                    _pilotCreateStep = 0;
                    PilotName = "";
                    Callsign = "";
                }
                else
                {
                    _pilotCreateStep--;
                    if (_pilotCreateStep < 3)
                        _difficultyFieldId = 3;
                }
                return true;
            }
            if (_pilotCreateStep == 3)
            {
                if (action == "Down" || action == "Right")
                {
                    _difficultyFieldId = _difficultyFieldId == 5 ? 1 : _difficultyFieldId + 1;
                    return true;
                }
                if (action == "Up" || action == "Left")
                {
                    _difficultyFieldId = _difficultyFieldId == 1 ? 5 : _difficultyFieldId - 1;
                    return true;
                }
            }
            if (action == "Return")
            {
                _pilotCreateStep++;
                if (_pilotCreateStep == 2)
                {
                    // Name confirmed; now in callsign dialog.
                    return true;
                }
                if (_pilotCreateStep == 3)
                {
                    // Callsign confirmed; now in difficulty dialog.
                    return true;
                }
                if (_pilotCreateStep == 4)
                {
                    if (_difficultyFieldId == 5)
                    {
                        _pilotCreateStep = 0;
                        _difficultyFieldId = 3;
                        return true;
                    }
                    // Difficulty accepted → enter HANGAR (with fade delay).
                    // C: hangto defaults to HANGTOSTORE → pos=1 (SUPPLIES) on first entry.
                    _pilotCreateStep = 0;
                    _difficultyFieldId = 3;
                    _hangarPos = 1;  // HANGTOSTORE → pos=1=SUPPLIES
                    // Notify that a new pilot was created (triggers stat initialization).
                    OnPilotCreated?.Invoke();
                    // Delay anchor by HangarFadeFrames to simulate fade transitions.
                    EnterState(WinState.Hangar, currentFrame + HangarFadeFrames, reAnchor: true);
                    return true;
                }
            }
            if (action == "Backspace")
            {
                if (_pilotCreateStep == 1 && PilotName.Length > 0)
                    PilotName = PilotName[..^1];
                else if (_pilotCreateStep == 2 && Callsign.Length > 0)
                    Callsign = Callsign[..^1];
                return true;
            }
            if (action.Length == 1 && char.IsLetterOrDigit(action[0]))
            {
                if (_pilotCreateStep == 1 && PilotName.Length < 12)
                    PilotName += char.ToUpperInvariant(action[0]);
                else if (_pilotCreateStep == 2 && Callsign.Length < 12)
                    Callsign += char.ToUpperInvariant(action[0]);
                return true;
            }
            // Other non-Return keys are absorbed silently.
            return true;
        }

        // Normal menu navigation.
        if (action == "Down")
        {
            CurrentItem = (CurrentItem + 1) % ItemCount;
            return false;
        }
        if (action == "Up")
        {
            CurrentItem = (CurrentItem - 1 + ItemCount) % ItemCount;
            return false;
        }
        if (action == "F1")
        {
            _helpTextName = "HELP1_TXT";
            EnterState(WinState.Help, currentFrame + HelpFadeFrames, reAnchor: true);
            return true;
        }
        if (action == "Return")
        {
            if (CurrentItem == CreditsItemIndex)
            {
                EnterState(WinState.Credits, currentFrame + CreditsFadeFrames, reAnchor: true);
                return true;
            }
            if (CurrentItem == OrderItemIndex)
            {
                _helpTextName = "RAP1_TXT";
                EnterState(WinState.Help, currentFrame + HelpFadeFrames, reAnchor: true);
                return true;
            }
            if (CurrentItem == OptionsItemIndex)
            {
                _inOptions = true;
                _optionsField = 0;
                return true;
            }
            if (CurrentItem == NewItemIndex)
            {
                // Enter pilot-creation sub-flow: step 1 = name dialog.
                _pilotCreateStep = 1;
                // No win-state change; still MENU during character creation.
                return true;
            }
            if (CurrentItem == LoadItemIndex)
            {
                _loadMissionPilots = PilotSaveStore.LoadAll(PilotSaveDirectory);
                if (_loadMissionPilots.Count == 0)
                    return false;
                LoadMissionSelectedIndex = 0;
                _inLoadMission = true;
                return true;
            }
            // QUIT, RETURN: stub.
            return false;
        }
        if (action == "Escape")
        {
            _hangarPos = 2;
            EnterMenu(currentFrame);
            return true;
        }
        return false;
    }

    private bool HandleAskBoolInput(string action)
    {
        switch (action)
        {
            case "Left":
            case "Right":
            case "Tab":
            case "Up":
            case "Down":
                _askBoolYes = !_askBoolYes;
                return true;
            case "Escape":
                _inAskBool = false;
                _askBoolOnYes = null;
                return true;
            case "Return":
            case "Space":
                bool yes = _askBoolYes;
                var cb = _askBoolOnYes;
                _inAskBool = false;
                _askBoolOnYes = null;
                if (yes) cb?.Invoke();
                return true;
        }
        return true;
    }

    private void OpenAskBoolSave()
    {
        _askBoolQuestion = $"Save {PilotName} - {Callsign} ?";
        _askBoolYes = true;
        string saveDir = PilotSaveDirectory ?? System.IO.Directory.GetCurrentDirectory();
        string name = PilotName;
        string callsign = Callsign;
        _askBoolOnYes = () => PilotSaveStore.Save(saveDir, name, callsign, idPic: 0, score: 0);
        _inAskBool = true;
    }

    private bool HandleLoadMissionInput(string action)
    {
        if (action == "Escape")
        {
            _inLoadMission = false;
            return true;
        }

        if (action == "Return")
        {
            _inLoadMission = false;
            return true;
        }

        if (_loadMissionPilots.Count == 0)
            return true;

        // C RAP_LoadWin: Down/PageDown/Left → next; Up/PageUp/Right → prev; wrap.
        int delta = action switch
        {
            "Down" or "PageDown" or "Left" => +1,
            "Up" or "PageUp" or "Right" => -1,
            _ => 0,
        };
        if (delta != 0)
        {
            int n = _loadMissionPilots.Count;
            LoadMissionSelectedIndex = ((LoadMissionSelectedIndex + delta) % n + n) % n;
        }
        return true;
    }

    private bool HandleOptionsInput(string action)
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

    private bool HandleOptionsPointerClick(int x, int y)
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

    private static int MainMenuItemAt(int x, int y)
    {
        if (x < 90 || x >= 235) return -1;
        for (int i = 0; i < 6; i++)
        {
            int top = 87 + i * 14;
            if (y >= top && y < top + 14) return i;
        }
        return -1;
    }

    private static int DifficultyFieldAt(int x, int y)
    {
        // ASKDIFF_SWD window is at (85, 22). The five buttons have field ids
        // 1..5 and local button rects x=40, y=29/53/77/101/132, lx=98, ly=12.
        if (x < 125 || x >= 223) return 0;
        if (y >= 51 && y < 63) return 1;   // TRAINING MODE
        if (y >= 75 && y < 87) return 2;   // ROOKIE
        if (y >= 99 && y < 111) return 3;  // VETERAN
        if (y >= 123 && y < 135) return 4; // ELITE
        if (y >= 154 && y < 166) return 5; // ABORT MISSION
        return 0;
    }

    private static int HangarPositionAt(int x, int y)
    {
        if (InRect(x, y, 120, 140, 60, 45)) return 0; // MISSION
        if (InRect(x, y, 215, 60, 55, 50)) return 1;  // SUPPLIES
        if (InRect(x, y, 5, 112, 65, 50)) return 2;   // MAIN MENU
        if (InRect(x, y, 225, 150, 75, 45)) return 3; // QUICK SAVE
        return -1;
    }

    private bool HandleHangarInput(string action, int currentFrame)
    {
        if (_inAskBool) return HandleAskBoolInput(action);
        if (action == "F2" || action == "S")
        {
            OpenAskBoolSave();
            return true;
        }
        if (action == "Down" || action == "Right")
        {
            // In C: Down/Right → pos-- (SC_DOWN case in WINDOWS.C); wraps from 0 to 3.
            _hangarPos = (_hangarPos - 1 + 4) % 4;
            return false;
        }
        if (action == "Up" || action == "Left")
        {
            // In C: Up/Left/Tab → pos++.
            _hangarPos = (_hangarPos + 1) % 4;
            return false;
        }
        if (action == "Escape")
        {
            _hangarPos = 2;
            EnterMenu(currentFrame);
            return true;
        }
        if (action == "Return")
        {
            if (_hangarPos == 0)  // MISSION
            {
                // Leave HANGAR → UNKNOWN (sector select). Anchor stays (reAnchor=false).
                _inSectorSelect = true;
                EnterState(WinState.Unknown, currentFrame, reAnchor: false);
                return true;
            }
            if (_hangarPos == 1)  // SUPPLIES → STORE_Enter
            {
                Store = new StoreLogic();
                EnterState(WinState.Store, currentFrame, reAnchor: true);
                return true;
            }
            // MAINMENU, QSAVE: stub.
            return false;
        }
        return false;
    }

    // Reference to the live store state, null unless WinState == Store.
    internal StoreLogic? Store { get; private set; }

    private bool HandleStoreInput(string action, int currentFrame)
    {
        if (Store == null) return false;
        switch (action)
        {
            case "Right":
            case "Up":
            case "PageUp":
                Store.NextItem();
                return false;

            case "Left":
            case "Down":
            case "PageDown":
                Store.PrevItem();
                return false;

            case "Space":
                Store.ToggleMode();
                return false;

            case "Escape":
                Store = null;
                EnterState(WinState.Hangar, currentFrame, reAnchor: false);
                return true;
        }
        return false;
    }

    private void EnterGame(int currentFrame)
    {
        GameNum           = 0;  // cur_game=0 → MISSION_1
        GameEnteredFrame  = currentFrame;
        InGame            = true;
        _inSectorSelect   = false;
        // In-game: win string is derived from InGame+GameNum, not WinState enum.
        // We still need the emitter to know, so fire the event before state change.
        OnGameEnter?.Invoke(GameNum);
        // State stays UNKNOWN in the enum but InGame flag drives the parity string.
        OnStateChanged?.Invoke();
    }

    private void EnterState(WinState next, int frame, bool reAnchor)
    {
        State = next;
        if (reAnchor)
            StateEnteredFrame = frame;
        OnStateChanged?.Invoke();
    }
}
