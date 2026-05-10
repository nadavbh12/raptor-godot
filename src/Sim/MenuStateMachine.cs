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
    // Normal menu has 7 items (indices 0-6): NEW, LOAD, OPTS, ORDER, CREDITS, QUIT, RETURN.
    public const int ItemCount = 7;
    public const int CreditsItemIndex = 4;
    public const int OrderItemIndex   = 3;
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

    // ── Hangar sub-state ─────────────────────────────────────────────────────
    // Hangar has 4 positions: 0=MISSION, 1=SUPPLIES, 2=MAINMENU, 3=QSAVE.
    // C default hangto=HANGTOSTORE(0) → case HANGTOSTORE → pos=1 (SUPPLIES).
    // From golden: 02_hangar_supplies = initial dump, then Down to 03_hangar_mission.
    // Down in C: pos-- (so 1→0=MISSION). Then Return on MISSION → hangar_exit → UNKNOWN.
    private int _hangarPos = 1;  // default: SUPPLIES (hangto=HANGTOSTORE)

    // ── Sector-select sub-state ──────────────────────────────────────────────
    // After HANGAR exit → UNKNOWN (sector select dialog). One Return → game enter.
    private bool _inSectorSelect = false;

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

    /// <summary>
    /// Notify the machine that the menu is now visible and ready for input.
    /// Mirrors raptor_parity_set_win_state(1) called right after ShowAllWindows.
    /// </summary>
    public void EnterMenu(int currentFrame)
    {
        CurrentItem = 0;
        _pilotCreateStep = 0;
        _hangarPos = 1;
        _inSectorSelect = false;
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

            case WinState.Unknown:
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

    // ── Private helpers ──────────────────────────────────────────────────────

    private bool HandleMenuInput(string action, int currentFrame)
    {
        // Pilot-creation sub-flow: absorb inputs until we've consumed enough Returns.
        if (_pilotCreateStep > 0)
        {
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
                    // Difficulty accepted → enter HANGAR (with fade delay).
                    // C: hangto defaults to HANGTOSTORE → pos=1 (SUPPLIES) on first entry.
                    _pilotCreateStep = 0;
                    _hangarPos = 1;  // HANGTOSTORE → pos=1=SUPPLIES
                    // Notify that a new pilot was created (triggers stat initialization).
                    OnPilotCreated?.Invoke();
                    // Delay anchor by HangarFadeFrames to simulate fade transitions.
                    EnterState(WinState.Hangar, currentFrame + HangarFadeFrames, reAnchor: true);
                    return true;
                }
            }
            // Any non-Return key (text input) is absorbed silently.
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
                EnterState(WinState.Help, currentFrame + HelpFadeFrames, reAnchor: true);
                return true;
            }
            if (CurrentItem == NewItemIndex)
            {
                // Enter pilot-creation sub-flow: step 1 = name dialog.
                _pilotCreateStep = 1;
                // No win-state change; still MENU during character creation.
                return true;
            }
            // LOAD, OPTS, QUIT, RETURN: stub.
            return false;
        }
        return false;
    }

    private bool HandleHangarInput(string action, int currentFrame)
    {
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
        if (action == "Return")
        {
            if (_hangarPos == 0)  // MISSION
            {
                // Leave HANGAR → UNKNOWN (sector select). Anchor stays (reAnchor=false).
                _inSectorSelect = true;
                EnterState(WinState.Unknown, currentFrame, reAnchor: false);
                return true;
            }
            // Other positions: stub (SUPPLIES, MAINMENU, QSAVE).
            return false;
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
