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
/// The playthrough script presses Down x4 from item 0 to land on item 4
/// (CREDITS), then Return to enter Credits, then Return to exit back to Menu.
///
/// Anchor semantics mirror parity.c's raptor_parity_set_win_state:
///   - Entering any non-Unknown state: re-anchor (reset StateEnteredFrame).
///   - Entering Unknown: keep the previous anchor so relative fc continues
///     from where it left off (C parity.c only re-anchors when win != 0).
/// </summary>
public sealed class MenuStateMachine
{
    // Normal menu has 7 items (indices 0-6): NEW, LOAD, OPTS, ORDER, CREDITS, QUIT, RETURN.
    // RETURN (index 6) is greyed out unless ingameflag is set. For navigation
    // purposes we still wrap through it; the difference only matters on Return press.
    public const int ItemCount = 7;
    public const int CreditsItemIndex = 4;

    /// <summary>
    /// Simulated animation delay (in frames at 70 Hz) before CREDITS state is anchored.
    /// In the C version, WIN_Credits runs GFX_FadeOut(16) + ShowWindow + GFX_FadeIn(16)
    /// before calling raptor_parity_set_win_state(2). Each GFX_DisplayScreen call in
    /// deterministic mode advances framecount by 1 (via pump_events). Two 16-step fades
    /// contribute 34 frames minimum; with SDL event overhead the total is empirically
    /// ~70 frames. This constant makes the CREDITS anchor land in the right range so the
    /// parity golden (fc=140 UNKNOWN, fc=210 UNKNOWN) is reproduced correctly.
    ///
    /// Value derived from: CREDITS_anchor ∈ (6584642, 6584676] in a reference C run
    /// where key fired at fc=6584581. 6584651 - 6584581 = 70.
    /// </summary>
    public const int CreditsFadeFrames = 70;

    public WinState State { get; private set; } = WinState.Unknown;

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
    /// Fired when a state transition (into or out of a named win-state) occurs.
    /// Caller (PlaythroughDriver / MenuController) uses this to notify ParityEmitter
    /// that it should reset its per-context emit counter.
    /// </summary>
    public event Action? OnStateChanged;

    /// <summary>
    /// Notify the machine that the menu is now visible and ready for input.
    /// Mirrors raptor_parity_set_win_state(1) called right after ShowAllWindows.
    /// </summary>
    public void EnterMenu(int currentFrame)
    {
        CurrentItem = 0;
        EnterState(WinState.Menu, currentFrame, reAnchor: true);
    }

    /// <summary>
    /// Handle one input action. Returns true if a win-state transition occurred.
    /// Only meaningful keys: "Down", "Up", "Return", "Escape".
    /// </summary>
    public bool HandleInput(string action, int currentFrame)
    {
        switch (State)
        {
            case WinState.Menu:
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
                if (action == "Return")
                {
                    if (CurrentItem == CreditsItemIndex)
                    {
                        // Delay the anchor by CreditsFadeFrames to simulate the
                        // GFX_FadeOut + FadeIn animation that runs in WIN_Credits
                        // before raptor_parity_set_win_state(2) is called.
                        EnterState(WinState.Credits, currentFrame + CreditsFadeFrames, reAnchor: true);
                        return true;
                    }
                    // Other items: stub — no transition for Stage 4.
                    return false;
                }
                break;

            case WinState.Credits:
                if (action == "Return" || action == "Escape")
                {
                    // Exit credits → back to unknown (C: raptor_parity_set_win_state(0)
                    // after IMS_WaitTimed returns, then the menu loop continues but
                    // does NOT re-call set_win_state(1) because the menu is still open).
                    EnterState(WinState.Unknown, currentFrame, reAnchor: false);
                    // Cursor returns to where it was (Credits item stays highlighted).
                    return true;
                }
                break;
        }
        return false;
    }

    private void EnterState(WinState next, int frame, bool reAnchor)
    {
        State = next;
        if (reAnchor)
        {
            StateEnteredFrame = frame;
        }
        OnStateChanged?.Invoke();
    }
}
