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
    // Cutscene movie lengths live in CutsceneTimings (shared with the View frame builder).
    // Normal menu has 7 items (indices 0-6): NEW, LOAD, OPTS, ORDER, CREDITS, QUIT, RETURN.
    public const int ItemCount = 7;
    public const int CreditsItemIndex = 4;
    public const int OrderItemIndex   = 3;
    public const int OptionsItemIndex = 2;
    public const int LoadItemIndex    = 1;
    public const int NewItemIndex     = 0;
    public const int QuitItemIndex    = 5;
    public const int ReturnItemIndex  = 6;
    // C LOADSAVE.C LOAD_LOAD = 0x0005; raptor_parity_menu_event emits field-1.
    // The LOAD window's keyboard cursor stays on LOAD_LOAD the whole time (Down/Up
    // cycle the shown pilot, they don't move the button cursor), so the menu-event
    // selected_item is this constant throughout the window — NOT the pilot index.
    public const int LoadWindowSelectedItem = 4;

    /// <summary>
    /// Number of menu items selectable in the current context. C only shows
    /// MAIN_RETURN when ingameflag is set (after pausing out of a wave). At
    /// cold launch and after a clean menu return, navigation wraps over
    /// 6 items (NEW..QUIT) and skips RETURN entirely.
    /// </summary>
    public int VisibleItemCount => InGame ? ItemCount : ItemCount - 1;

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
    // The multi-step dialog (name → callsign → difficulty → confirm), its
    // text-entry buffers (PilotName/Callsign), and the difficulty field cursor
    // live in PilotCreationFlow. MenuStateMachine owns the F1→HELP open, the
    // OnPilotCreated emission and the WinState.Hangar transition it drives.
    private readonly PilotCreationFlow _pilotCreate = new();

    // ── Hangar sub-state ─────────────────────────────────────────────────────
    // Hangar cursor position + navigation/hit-test live in HangarController.
    private readonly HangarController _hangar = new();

    // ── Sector-select sub-state ──────────────────────────────────────────────
    // After HANGAR exit → UNKNOWN (sector select dialog). One Return → game enter.
    private bool _inSectorSelect = false;
    // LOAD-mission pilot list + cursor nav live in LoadMissionPanel. The
    // PilotSaveStore.LoadAll I/O, the "No Pilots to Load" WIN_Msg and the
    // OnPilotLoaded / Hangar transition stay in MenuStateMachine.
    private readonly LoadMissionPanel _loadMission = new();
    private bool _inAskBool = false;
    private string _askBoolQuestion = "";
    private bool _askBoolYes = true;
    private Action? _askBoolOnYes;
    private bool _inWinMsg = false;
    private string _winMsgText = "";
    // OPTIONS dialog state + handlers live in OptionsPanel.
    private readonly OptionsPanel _options = new();
    // Help paging state + page-cycle logic live in HelpSystemController.
    private readonly HelpSystemController _help = new();

    // Live player inventory the supply-room store reads for ownership (Task 3.5).
    // Defaults to a fresh empty Inventory so headless / no-WaveController paths
    // are safe; MenuController._Ready replaces this with wave.Inventory (the
    // canonical instance gameplay and pilot load mutate).
    public Inventory Inventory { get; set; } = new();

    // Live player-score accessors for store transactions. MenuController._Ready
    // wires these to WaveController.Score / SetScore. Left null on headless /
    // no-WaveController paths, where the store falls back to a private score
    // field (seeded to the new-pilot 10000) so Buy/Sell still function in tests.
    public System.Func<uint>? GetScore { get; set; }
    public System.Action<uint>? SetScore { get; set; }
    private uint _fallbackScore = 10000;

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

    public int PilotCreateStep => _pilotCreate.Step;
    public string PilotName => _pilotCreate.PilotName;
    public string Callsign => _pilotCreate.Callsign;
    /// <summary>Portrait variant being chosen on the registration screen
    /// (0=WMALE, 1=BMALE, 2=WFEMALE, 3=BFEMALE). The renderer draws this.</summary>
    public int RegisterIdPic => _pilotCreate.CurId;
    /// <summary>Portrait variant (0=WMALE, 1=BMALE, 2=WFEMALE, 3=BFEMALE).</summary>
    public int IdPic { get; private set; } = 0;
    /// <summary>Fires when a saved pilot is loaded via the LOAD dialog. Receives the
    /// full summary so subscribers (e.g. WaveController) can apply Score, CurGame,
    /// diff, etc. to active game state.</summary>
    public event System.Action<PilotSaveSummary>? OnPilotLoaded;
    public int DifficultyFieldId => _pilotCreate.DifficultyFieldId;

    /// <summary>The DIFF (0..3) chosen at the last accepted difficulty dialog
    /// (captured before the field resets). Read by OnPilotCreated.</summary>
    public int AcceptedPilotDiff => _pilotCreate.AcceptedDiff;
    public int HangarPosition => _hangar.Position;
    public bool InSectorSelect => _inSectorSelect;
    public bool InOptions => _options.Active;
    public bool InLoadMission => _loadMission.Active;
    public bool InAskBool => _inAskBool;
    public string AskBoolQuestion => _askBoolQuestion;
    public bool AskBoolYesSelected => _askBoolYes;
    public bool InWinMsg => _inWinMsg;
    public string WinMsgText => _winMsgText;
    public int OptionsField => _options.Field;
    public bool OptionDetailHigh => _options.DetailHigh;
    public int OptionMusicVolume => _options.MusicVolume;
    public int OptionFxVolume => _options.FxVolume;
    public string HelpTextName => _help.TextName;
    public int HelpPageIndex => _help.PageIndex;

    /// <summary>
    /// Ordered table of Help item names mirroring C HELP.C's modular page
    /// cycle. Owned by <see cref="HelpSystemController"/>; forwarded here for the
    /// View / tests. See <see cref="HelpSystemController.PageOrder"/> for the
    /// derivation from <c>SOURCE/file0000.inc</c>.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string> HelpPageOrder => HelpSystemController.PageOrder;
    public string? PilotSaveDirectory { get; init; }
    public System.Collections.Generic.IReadOnlyList<PilotSaveSummary> LoadMissionPilots => _loadMission.Pilots;
    public int LoadMissionSelectedIndex => _loadMission.SelectedIndex;
    public PilotSaveSummary? LoadMissionPilot => _loadMission.SelectedPilot;

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

    /// <summary>The highlighted index C's active_field-1 reports for the
    /// currently-active menu window. Per-screen so menu-event parity holds
    /// outside the main menu. Exact per-screen alignment to the C goldens is
    /// pinned by tests in a later task; this returns each screen's natural index.</summary>
    public int EffectiveSelectedItem()
    {
        if (_inWinMsg) return 0;              // popup: no cursor (C field=1 → 0)
        // AskBool is SWD-navigated: C reports active_field-1, and ASK_YES=6 /
        // ASK_NO=7 (ASK.INC) → 5 / 6.
        if (_inAskBool)          return _askBoolYes ? 5 : 6;
        if (_options.Active)     return _options.Field;
        if (_loadMission.Active) return LoadWindowSelectedItem;
        if (_pilotCreate.Active) return _pilotCreate.DifficultyFieldId;
        return State switch
        {
            WinState.Hangar  => _hangar.Position,
            WinState.Store   => Store?.CurItem ?? 0,
            WinState.Help    => _help.PageIndex,
            WinState.Credits => 0,            // popup, no cursor (same sentinel as WinMsg)
            _                => CurrentItem,  // Menu / Unknown(sector)
        };
    }

    /// <summary>Which menu "screen" is currently taking input. Distinguishes the
    /// flag-based sub-dialogs (Options/AskBool/Load/PilotCreate) that all sit on
    /// WinState.Menu, so the menu-event emitter can tell whether a key stayed in
    /// one screen (report post-nav highlight) or crossed into another (report the
    /// source screen's highlight — C records the key in the loop that owned it).</summary>
    public enum Screen { Main, AskBool, Options, LoadMission, PilotCreate, Hangar, Store, Help, Credits, Sector, WinMsg }

    public Screen EffectiveScreen()
    {
        if (_inWinMsg)           return Screen.WinMsg;
        if (_inAskBool)          return Screen.AskBool;
        if (_options.Active)     return Screen.Options;
        if (_loadMission.Active) return Screen.LoadMission;
        if (_pilotCreate.Active) return Screen.PilotCreate;
        return State switch
        {
            WinState.Hangar  => Screen.Hangar,
            WinState.Store   => Screen.Store,
            WinState.Help    => Screen.Help,
            WinState.Credits => Screen.Credits,
            WinState.Unknown => _inSectorSelect ? Screen.Sector : Screen.Main,
            _                => Screen.Main,
        };
    }

    public void CompleteMission(int currentFrame)
    {
        InGame = false;
        _inSectorSelect = false;
        _options.Close();
        _pilotCreate.ResetStep();
        // C plays INTRO_Landing (ship lands on base) after each cleared wave
        // (WINDOWS.C:1863) before the next hangar. The hangar entry is deferred to
        // CompleteCutsceneIfDone once the landing movie finishes.
        EnterState(WinState.Landing, currentFrame, reAnchor: true);
    }

    public void PlayerDied(int currentFrame)
    {
        InGame = false;
        _inSectorSelect = false;
        _options.Close();
        _pilotCreate.ResetStep();
        EnterState(WinState.Death, currentFrame, reAnchor: true);
    }

    /// <summary>
    /// Start the startup/attract intro (INTRO_Credits + INTRO_PlayMain). Mirrors the
    /// once-at-launch attract in RAP.C; gated to interactive runs by the caller.
    /// </summary>
    public void StartIntro(int currentFrame)
    {
        EnterState(WinState.Intro, currentFrame, reAnchor: true);
    }

    /// <summary>
    /// Advance a finished cutscene to its successor: Death/Intro → main menu, Landing →
    /// hangar (the next wave's pre-game hangar). Returns true on the frame the transition
    /// fires. No-op for non-cutscene states.
    /// </summary>
    public bool CompleteCutsceneIfDone(int currentFrame)
    {
        int elapsed = currentFrame - StateEnteredFrame;
        int total = State switch
        {
            WinState.Death   => CutsceneTimings.DeathTotal,
            WinState.Landing => CutsceneTimings.LandingTotal,
            WinState.Intro   => CutsceneTimings.IntroTotal,
            _                => -1,
        };
        if (total < 0 || elapsed < total) return false;
        EnterCutsceneSuccessor(currentFrame);
        return true;
    }

    /// <summary>
    /// Skip the active cutscene to its successor immediately (faithful K_SKIPALL — any
    /// key dismisses an AGX movie). Returns true if a cutscene was skipped.
    /// </summary>
    public bool SkipCutscene(int currentFrame)
    {
        if (State is not (WinState.Death or WinState.Landing or WinState.Intro)) return false;
        EnterCutsceneSuccessor(currentFrame);
        return true;
    }

    private void EnterCutsceneSuccessor(int currentFrame)
    {
        switch (State)
        {
            case WinState.Landing:
                _hangar.Position = 1;
                EnterState(WinState.Hangar, currentFrame, reAnchor: true);
                break;
            default:   // Death, Intro → main menu
                EnterMenu(currentFrame);
                break;
        }
    }

    /// <summary>
    /// Notify the machine that the menu is now visible and ready for input.
    /// Mirrors raptor_parity_set_win_state(1) called right after ShowAllWindows.
    /// </summary>
    public void EnterMenu(int currentFrame)
    {
        CurrentItem = 0;
        _pilotCreate.Reset();
        _hangar.Position = 1;
        _inSectorSelect = false;
        _options.Reset();
        _loadMission.Reset();
        _inAskBool = false;
        _askBoolQuestion = "";
        _askBoolYes = true;
        _askBoolOnYes = null;
        _inWinMsg = false;
        _winMsgText = "";
        _help.ResetTextName();
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
                // Pagination (HELP.C:91-121 keypress switch + modular wrap).
                if (action == "Down" || action == "Right" || action == "PageDown")
                {
                    SetHelpPage(_help.PageIndex + 1);
                    return true;
                }
                if (action == "Up" || action == "Left" || action == "PageUp")
                {
                    SetHelpPage(_help.PageIndex - 1);
                    return true;
                }
                if (action == "Home")
                {
                    SetHelpPage(0);
                    return true;
                }
                if (action == "End")
                {
                    SetHelpPage(HelpPageOrder.Count - 1);
                    return true;
                }
                if (action == "F1")
                {
                    // HELP.C:98-101 — SC_F1 sets curpage = 1, not 0.
                    SetHelpPage(1);
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
                if (_inSectorSelect && action == "F1")
                {
                    // C WINDOWS.C:1449 — SC_F1 in sector_select → HELP_Win("COMPHLP1_TXT").
                    EnterHelp("COMPHLP1_TXT", currentFrame);
                    return true;
                }
                // Unknown && !InSectorSelect is the post-Credits/Help limbo
                // state: parity-wise it must stay "UNKNOWN" (credits.parity.txt
                // emits win=UNKNOWN at fc 140/210), but visually we render the
                // main menu (DebugRenderer's `Unknown && !InSectorSelect`
                // fallback). C achieves the same by returning into
                // WIN_MainMenu's input loop after WIN_Credits/HELP_Win, so
                // route keys to the main-menu handler here to keep parity
                // labels intact while restoring live interactivity.
                if (!_inSectorSelect && !_pilotCreate.Active)
                    return HandleMenuInput(action, currentFrame);
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

        if (_pilotCreate.Step == 1 || _pilotCreate.Step == 2)
        {
            if (InRect(x, y, 183, 128, 110, 12))
            {
                _pilotCreate.SetStep(1);
                return true;
            }
            if (InRect(x, y, 183, 144, 110, 12))
            {
                _pilotCreate.SetStep(2);
                return true;
            }
            // C REGISTER_SWD REG_VIEWID (x=1,y=102,lx=87,ly=48): clicking the
            // portrait cycles the ID picture, same as SC_ALT/SC_CTRL.
            if (InRect(x, y, 1, 102, 87, 48))
            {
                _pilotCreate.CycleId();
                return true;
            }
            return false;
        }

        if (_pilotCreate.Step == 3)
        {
            int field = DifficultyFieldAt(x, y);
            if (field == 0) return false;
            _pilotCreate.SetDifficultyField(field);
            return HandleInput("Return", currentFrame);
        }

        if (State == WinState.Menu)
        {
            if (_options.Active)
                return _options.HandlePointerClick(x, y);

            int item = MainMenuItemAt(x, y);
            if (item < 0) return false;
            CurrentItem = item;
            return HandleInput("Return", currentFrame);
        }

        if (State == WinState.Hangar)
        {
            int pos = HangarController.PositionAt(x, y);
            if (pos < 0) return false;
            _hangar.Position = pos;
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
        if (_inWinMsg)
        {
            // C WIN_Msg: any key dismisses, and the main-menu SWD cursor
            // re-initialises to the first field (NEW) on return — match it.
            _inWinMsg = false;
            _winMsgText = "";
            CurrentItem = NewItemIndex;
            return true;
        }
        if (_inAskBool)
            return HandleAskBoolInput(action);
        if (_options.Active)
            return _options.HandleInput(action);
        if (_loadMission.Active)
            return HandleLoadMissionInput(action);

        // Pilot-creation sub-flow: the step machine + text entry live in
        // PilotCreationFlow; MenuStateMachine drives the F1→HELP open and the
        // step-4 confirm (OnPilotCreated + HANGAR transition) off its result.
        if (_pilotCreate.Active)
        {
            switch (_pilotCreate.HandleInput(action))
            {
                case PilotCreationFlow.Result.OpenHelp:
                    // C WINDOWS.C:800 — SC_F1 in registration → HELP_Win("NEWPLAY1_TXT").
                    EnterHelp("NEWPLAY1_TXT", currentFrame);
                    return true;
                case PilotCreationFlow.Result.Confirm:
                    // Difficulty accepted → enter HANGAR (with fade delay).
                    // C: hangto defaults to HANGTOSTORE → pos=1 (SUPPLIES) on first entry.
                    _hangar.Position = 1;  // HANGTOSTORE → pos=1=SUPPLIES
                    // Persist the portrait chosen in the registration screen.
                    IdPic = _pilotCreate.CurId;
                    // Notify that a new pilot was created (triggers stat initialization).
                    OnPilotCreated?.Invoke();
                    // Delay anchor by HangarFadeFrames to simulate fade transitions.
                    EnterState(WinState.Hangar, currentFrame + HangarFadeFrames, reAnchor: true);
                    return true;
                default:
                    return true;
            }
        }

        // Normal menu navigation. Wrap over the visible item set so the
        // hidden MAIN_RETURN slot isn't reachable when not in-game.
        int n = VisibleItemCount;
        if (action == "Down")
        {
            CurrentItem = (CurrentItem + 1) % n;
            return false;
        }
        if (action == "Up")
        {
            CurrentItem = (CurrentItem - 1 + n) % n;
            return false;
        }
        if (action == "F1")
        {
            EnterHelp("HELP1_TXT", currentFrame);
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
                EnterHelp("RAP1_TXT", currentFrame);
                return true;
            }
            if (CurrentItem == OptionsItemIndex)
            {
                _options.Open();
                return true;
            }
            if (CurrentItem == NewItemIndex)
            {
                // Enter pilot-creation sub-flow: step 1 = name dialog.
                _pilotCreate.Begin();
                // No win-state change; still MENU during character creation.
                return true;
            }
            if (CurrentItem == LoadItemIndex)
            {
                var pilots = PilotSaveStore.LoadAll(PilotSaveDirectory);
                if (pilots.Count == 0)
                {
                    // C WINDOWS.C:2104-2106: RAP_LoadWin returns -1 → WIN_Msg.
                    _winMsgText = "No Pilots to Load";
                    _inWinMsg = true;
                    return true;
                }
                _loadMission.Open(pilots);
                return true;
            }
            if (CurrentItem == QuitItemIndex)
            {
                // C WINDOWS.C:608-611: case MAIN_QUIT → WIN_AskExit → WIN_AskBool("EXIT TO DOS")
                OpenAskBoolQuit();
                return true;
            }
            // RETURN: stub (only reachable when InGame).
            return false;
        }
        if (action == "Escape")
        {
            _hangar.Position = 2;
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

    /// <summary>
    /// Apply a loaded pilot's PLAYEROBJ header to active menu state, fire
    /// OnPilotLoaded so subscribers can pick up gameplay-state fields (score,
    /// cur_game, diff), and transition to Hangar. Mirrors the C path
    /// LOADSAVE.C:608-612 → WINDOWS.C:2112-2114 (ingameflag=FALSE, exit menu).
    /// </summary>
    public void ApplyLoadedPilot(PilotSaveSummary pilot, int currentFrame)
    {
        _pilotCreate.SetIdentity(pilot.Name, pilot.Callsign);
        IdPic = pilot.IdPic;
        OnPilotLoaded?.Invoke(pilot);
        EnterState(WinState.Hangar, currentFrame + HangarFadeFrames, reAnchor: true);
        _hangar.Position = 1;  // HANGTOSTORE → SUPPLIES
    }

    private void OpenAskBoolSave()
    {
        _askBoolQuestion = $"Save {PilotName} - {Callsign} ?";
        _askBoolYes = true;
        string saveDir = PilotSaveDirectory ?? System.IO.Directory.GetCurrentDirectory();
        string name = PilotName;
        string callsign = Callsign;
        int idPic = IdPic;
        uint score = GetScore?.Invoke() ?? 0;   // live run score (was hardcoded 0)
        _askBoolOnYes = () => PilotSaveStore.Save(saveDir, name, callsign, idPic: idPic, score: score);
        _inAskBool = true;
    }

    private void OpenAskBoolQuit()
    {
        _askBoolQuestion = "EXIT TO DOS";
        _askBoolYes = true;
        _askBoolOnYes = () => { QuitRequested = true; OnQuit?.Invoke(); };
        _inAskBool = true;
    }

    /// <summary>
    /// True once the user confirmed YES on the EXIT TO DOS AskBool. The UI
    /// layer should observe this each frame and call <c>GetTree().Quit()</c>.
    /// </summary>
    public bool QuitRequested { get; private set; }

    /// <summary>Fired when the user confirmed EXIT TO DOS. UI may bind a Quit handler.</summary>
    public event System.Action? OnQuit;

    private bool HandleLoadMissionInput(string action)
    {
        switch (_loadMission.HandleInput(action))
        {
            case LoadMissionPanel.Result.Confirm:
                // C LOADSAVE.C:608-612: Return on LOAD_LOAD calls RAP_LoadPlayer
                // which copies the saved PLAYEROBJ into active game state, then
                // returns to the hangar (ingameflag=FALSE in WIN_MainMenu exits
                // the menu loop). Apply the selected pilot's data here, fire
                // OnPilotLoaded so WaveController can pick up Score etc., then
                // transition to Hangar.
                var picked = _loadMission.SelectedPilot;
                if (picked != null)
                    ApplyLoadedPilot(picked, StateEnteredFrame);
                return true;
            default:
                // Closed (Escape) and Handled (nav / no-op) need no transition.
                return true;
        }
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

    private bool HandleHangarInput(string action, int currentFrame)
    {
        if (_inAskBool) return HandleAskBoolInput(action);
        if (action == "F2" || action == "S" || action == "s")
        {
            // C WINDOWS.C:1166 SC_S/SC_F2 → save. Interactive letter keys arrive
            // lowercase ("s"), so accept both cases.
            OpenAskBoolSave();
            return true;
        }
        if (action == "F1")
        {
            // C WINDOWS.C:1137 — SC_F1 in hangar → HELP_Win("HANGHLP1_TXT").
            EnterHelp("HANGHLP1_TXT", currentFrame);
            return true;
        }
        // Cursor-only navigation (Down/Right → pos--, Up/Left → pos++); the
        // controller owns the wrap arithmetic. Returns false like the original.
        if (_hangar.TryNavigate(action))
            return false;
        if (action == "Escape")
        {
            _hangar.Position = 2;
            EnterMenu(currentFrame);
            return true;
        }
        if (action == "Return")
        {
            if (_hangar.Position == 0)  // MISSION
            {
                // Leave HANGAR → UNKNOWN (sector select). Anchor stays (reAnchor=false).
                _inSectorSelect = true;
                EnterState(WinState.Unknown, currentFrame, reAnchor: false);
                return true;
            }
            if (_hangar.Position == 1)  // SUPPLIES → STORE_Enter
            {
                Store = new StoreLogic(
                    Inventory,
                    GetScore ?? (() => _fallbackScore),
                    SetScore ?? (v => _fallbackScore = v));
                EnterState(WinState.Store, currentFrame, reAnchor: true);
                return true;
            }
            if (_hangar.Position == 2)  // MAIN MENU → exit hangar to the menu
            {
                // C HANG_MAIN_MENU (WINDOWS.C:1268) → opt=-99 → return to menu.
                EnterMenu(currentFrame);
                return true;
            }
            if (_hangar.Position == 3)  // QSAVE → save prompt
            {
                // C HANG_QSAVE (WINDOWS.C:1285) → WIN_AskBool → RAP_SavePlayer.
                OpenAskBoolSave();
                return true;
            }
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

            case "Return":
                // C STORE.C:559 STOR_BUYIT — Return triggers the buy/sell action
                // for the current item ("Return" is menu_accept; see
                // InteractiveInputController). Stay in the store, re-render.
                if (Store.CurrentMode == StoreLogic.Mode.Buy) Store.Buy();
                else Store.Sell();
                return false;

            case "F1":
                // C STORE.C:475 — SC_F1 in supply room → HELP_Win("STORHLP1_TXT").
                EnterHelp("STORHLP1_TXT", currentFrame);
                return true;

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

    /// <summary>
    /// Enter WinState.Help at the page indexed by <paramref name="itemName"/>
    /// in <see cref="HelpPageOrder"/>. Page resolution (with the page-0 fallback)
    /// lives in <see cref="HelpSystemController.SelectPageByName"/>; this method
    /// keeps ownership of the actual WinState.Help transition and frame anchor.
    /// </summary>
    private void EnterHelp(string itemName, int currentFrame)
    {
        _help.SelectPageByName(itemName);
        EnterState(WinState.Help, currentFrame + HelpFadeFrames, reAnchor: true);
    }

    /// <summary>
    /// Set the current Help page with C-style modular wrap (HELP.C:75-78).
    /// Updates both <see cref="HelpPageIndex"/> and <see cref="HelpTextName"/>
    /// (via the controller) and fires OnStateChanged, matching the original.
    /// </summary>
    private void SetHelpPage(int newPage)
    {
        _help.SetPage(newPage);
        OnStateChanged?.Invoke();
    }
}
