namespace Raptor.Sim;

/// <summary>
/// Owns the help-window paging state (current page index + resolved item name)
/// and the C-style modular page-cycle logic from HELP.C. Extracted from
/// <see cref="MenuStateMachine"/> (Phase 4 tranche A). MenuStateMachine remains
/// the parity frame-anchor: it owns <c>EnterState</c>/<c>OnStateChanged</c> and
/// decides WHEN to enter <see cref="WinState.Help"/>; this collaborator only
/// resolves and advances the page. The View reads <see cref="MenuStateMachine.HelpPageIndex"/>
/// and <see cref="MenuStateMachine.HelpTextName"/>, which forward here.
/// </summary>
internal sealed class HelpSystemController
{
    /// <summary>
    /// Ordered table of Help item names mirroring C HELP.C's modular page
    /// cycle. Derived from <c>SOURCE/file0000.inc</c>: items 0x12 (HELP1_TXT,
    /// the first STARTHELP entry after the +=2 unregistered offset and post-
    /// increment) through 0x38 (VEND00_TXT, last item before ENDHELP=0x39).
    /// 39 entries; mirrors <c>maxpages = enditem - startitem - 1 = 0x27</c>
    /// at HELP.C:33.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> PageOrder = new[]
    {
        "HELP1_TXT",    "STORY1_TXT",   "OVERVW01_TXT", "OVERVW02_TXT",
        "TRAIN01_TXT",  "TRAIN02_TXT",  "OVERVW03_TXT", "OVERVW04_TXT",
        "OVERVW05_TXT", "OVERVW06_TXT", "OVERVW07_TXT", "OVERVW08_TXT",
        "OVERVW09_TXT", "GAMEHLP1_TXT", "GAMEHLP2_TXT", "GAMEHLP3_TXT",
        "GAMEHLP4_TXT", "GAMEHLP5_TXT", "HINTS01_TXT",  "HINTS02_TXT",
        "NEWPLAY1_TXT", "NEWPLAY2_TXT", "LOADPLY1_TXT", "LOADPLY2_TXT",
        "HANGHLP1_TXT", "HANGHLP2_TXT", "COMPHLP1_TXT", "COMPHLP2_TXT",
        "STORHLP1_TXT", "STORHLP2_TXT", "RAP1_TXT",     "RAP2_TXT",
        "WEAP01_TXT",   "WEAP02_TXT",   "WEAP03_TXT",   "RAP3_TXT",
        "RAP4_TXT",     "RAP5_TXT",     "VEND00_TXT",
    };

    private int _pageIndex = 0;
    private string _textName = "HELP1_TXT";

    public int PageIndex => _pageIndex;
    public string TextName => _textName;

    /// <summary>
    /// EnterMenu reset. Mirrors the original MenuStateMachine.EnterMenu, which
    /// set <c>_helpTextName = "HELP1_TXT"</c> and left <c>_helpPageIndex</c>
    /// untouched (the index is always re-resolved by <see cref="SelectPageByName"/>
    /// before the Help window is shown).
    /// </summary>
    public void ResetTextName()
    {
        _textName = "HELP1_TXT";
    }

    /// <summary>
    /// Resolve the page indexed by <paramref name="itemName"/> in
    /// <see cref="PageOrder"/> and make it current. Unknown names fall back to
    /// page 0 (HELP1_TXT) — matches C HELP_Win's <c>EXIT_Error("Invalid Page")</c>
    /// path being unreachable in practice. Used by MenuStateMachine.EnterHelp,
    /// which then performs the actual WinState.Help transition.
    /// </summary>
    public void SelectPageByName(string itemName)
    {
        int idx = -1;
        for (int i = 0; i < PageOrder.Count; i++)
        {
            if (PageOrder[i] == itemName) { idx = i; break; }
        }
        if (idx < 0) { idx = 0; itemName = PageOrder[0]; }
        _pageIndex = idx;
        _textName = itemName;
    }

    /// <summary>
    /// Set the current Help page with C-style modular wrap (HELP.C:75-78).
    /// Updates both <see cref="PageIndex"/> and <see cref="TextName"/>. The
    /// caller (MenuStateMachine) fires OnStateChanged after this returns.
    /// </summary>
    public void SetPage(int newPage)
    {
        int n = PageOrder.Count;
        // C: `if (curpage >= 0) curpage %= maxpages; else curpage = maxpages + curpage`.
        // Handles -1 → n-1, n → 0 cleanly.
        if (newPage >= 0)
            newPage = newPage % n;
        else
            newPage = n + (newPage % n);
        if (newPage == n) newPage = 0;
        _pageIndex = newPage;
        _textName = PageOrder[_pageIndex];
    }
}
