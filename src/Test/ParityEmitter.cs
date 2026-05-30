using System;
using System.Globalization;
using System.IO;
using Godot;
using Raptor.Sim;

namespace Raptor.Test;

/// <summary>
/// Pure C# checkpoint writer. Holds no Godot objects; safe to instantiate
/// in xUnit tests without the Godot native runtime.
///
/// When an output path is provided, emits one NDJSON line per simulated
/// second (every 70 frames). Schema lives at tests/parity/schema.json.
///
/// Emission uses boundary-crossing detection (same algorithm as parity.c)
/// rather than strict modulo equality. Each win-state context has its own
/// anchor frame (StateEnteredFrame / GameEnteredFrame) and its own
/// "last emitted second" counter (_lastEmitSec), reset on anchor change.
/// </summary>
internal class ParityEmitWorker : IDisposable
{
    private StreamWriter? _out;
    private int _lastEmitSec = -1;
    private int _lastAnchor  = -1;  // tracks anchor changes to auto-reset _lastEmitSec

    // Win-state context from MenuStateMachine.
    public Sim.MenuStateMachine? Menu { get; set; }

    // Live game state providers — wired by WaveController when in-game.
    // These delegate to WaveController's properties via lambdas.
    public Func<int>?  GetPlayerX  { get; set; }
    public Func<int>?  GetPlayerY  { get; set; }
    public Func<uint>? GetScore    { get; set; }
    public Func<int>?  GetShield   { get; set; }
    public Func<int>?  GetEnemies  { get; set; }
    public Func<int>?  GetPbullets { get; set; }
    public Func<int>?  GetEbullets { get; set; }
    /// <summary>Game-loop iters completed since game-enter. Wired by
    /// WaveController; used as the primary alignment key for in-game
    /// checkpoint emission (iter-bucket boundaries, not fc-bucket).</summary>
    public Func<int>?  GetGameIter { get; set; }
    public Func<int>?  GetGameAnchorFrame { get; set; }
    public Func<int>?  GetDemoGameNum { get; set; }
    public Func<int>?  GetMenuDemoEmitSequence { get; set; }

    // Iter-bucket size for in-game emission. Must match parity.c's ITER_BUCKET.
    // 18 iters ≈ 70 fc in C-time at C's variable ~3.89 fc/iter cadence.
    private const int IterBucket = 18;
    private const int DemoEmitDisabled = int.MinValue;
    private int _lastMenuDemoEmitSequence = DemoEmitDisabled;
    private int _menuDemoMilestoneIndex = 0;
    private static readonly int[] MenuDemoSeqTargets = { 1, 2, 24, 37, 71, 94 };
    private static readonly int[] MenuDemoReportFcs = { 140, 280, 350, 420, 490, 560 };

    // Stub fallback fields — used when Menu is null or before in-game starts.
    public string WinState  { get; set; } = "UNKNOWN";
    public int    PlayerX   { get; set; } = 144;
    public int    PlayerY   { get; set; } = 160;
    public uint   Score     { get; set; } = 0;
    public int    Shield    { get; set; } = 0;
    public int    Enemies   { get; set; } = 0;
    public int    Pbullets  { get; set; } = 0;
    public int    Ebullets  { get; set; } = 0;

    /// <summary>Opens the output file at the given path. No-op if path is null or empty.</summary>
    public void Open(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try {
            _out = new StreamWriter(path, append: false) { AutoFlush = true };
        } catch (Exception e) {
            GD.PrintErr($"ParityEmitter: failed to open {path}: {e.Message}");
        }
    }

    /// <summary>
    /// Resets the per-context emit counter. Retained for compatibility.
    /// The canonical reset is anchor-change detection in Tick().
    /// </summary>
    public void OnStateChanged() { /* anchor-change detection in Tick() handles reset */ }

    /// <summary>
    /// Called every physics tick. Emits a checkpoint line whenever we cross
    /// a bucket boundary:
    ///   In-game: every IterBucket game-loop iters since game enter. Iter
    ///            count is read from WaveController via GetGameIter so the
    ///            alignment is robust to fc-per-iter cadence drift between
    ///            the C dosraptor and this port.
    ///   Menu:    every 70 fc since the win-state was entered. Menus are
    ///            event-driven, no iter loop, so fc remains the alignment key.
    /// </summary>
    public void Tick()
    {
        if (_out == null) return;

        int anchor;
        string win;
        bool isInGame = false;

        if (Menu != null)
        {
            // In-game: anchor = GameEnteredFrame, win = "MISSION_N".
            // In menu: anchor = StateEnteredFrame, win = state parity string.
            int demoGameNum = GetDemoGameNum?.Invoke() ?? -1;
            if (Menu.InGame || demoGameNum >= 0)
            {
                isInGame = true;
                anchor   = Menu.InGame
                    ? Menu.GameEnteredFrame
                    : GetGameAnchorFrame?.Invoke() ?? Menu.StateEnteredFrame;
                int gameNum = Menu.InGame ? Menu.GameNum : demoGameNum;
                win      = gameNum switch
                {
                    0 => "MISSION_1",
                    1 => "MISSION_2",
                    2 => "MISSION_3",
                    _ => "UNKNOWN",
                };
            }
            else
            {
                anchor = Menu.StateEnteredFrame;
                win    = Menu.State.ToParityString();
            }

            // Reset emit counter on anchor change (matches parity.c's
            // g_last_emit_sec=-1 reset on raptor_parity_set_win_state(win!=0)
            // and raptor_parity_game_enter).
            if (anchor != _lastAnchor)
            {
                _lastEmitSec = -1;
                _lastAnchor  = anchor;
                _lastMenuDemoEmitSequence = DemoEmitDisabled;
                _menuDemoMilestoneIndex = 0;
            }
        }
        else
        {
            // Legacy stub mode (used by xUnit tests): emit at absolute frame
            // multiples of 70 with iter=-1 since there's no real game loop.
            if (Sim.SimClock.Frame % 70 != 0) return;
            Emit(Sim.SimClock.Frame, -1, WinState);
            return;
        }

        int curSec, reportFc, reportIter;
        if (isInGame)
        {
            // WaveController.GameLoopIter increments AFTER scheduler.Tick, so
            // it reads as "1 + iters_completed". C's g_game_iter is read
            // BEFORE increment so it's "iters_completed". Match C's semantics
            // by subtracting 1 — both then represent "after N iters of work".
            int iter = (GetGameIter?.Invoke() ?? 1) - 1;
            if (iter < 0) return;  // game just entered, no iter yet
            curSec = iter / IterBucket;
            if (curSec <= _lastEmitSec) return;
            _lastEmitSec = curSec;
            reportIter = curSec * IterBucket;
            reportFc   = Sim.SimClock.Frame - anchor;
        }
        else
        {
            // C's parity emitter (parity.c win_state_name) has NO "DEATH" state:
            // the player-death cinematic runs without the emit hook, so C goes
            // straight from MISSION_N (death anim plays in-game) to MENU. Godot
            // models the death movie as a distinct WinState.Death phase; emitting
            // it here injects DEATH checkpoints C never has, which (a) mismatch
            // C's MENU rows by index and (b) shift the whole tail. Suppress them
            // to mirror C. (Landing/Intro are likewise C-absent transitional
            // states, but only Death occurs in the death scenarios.)
            if (Menu.State == Sim.WinState.Death) return;

            int demoSeq = GetMenuDemoEmitSequence?.Invoke() ?? DemoEmitDisabled;
            if (demoSeq != DemoEmitDisabled)
            {
                if (demoSeq < 0 || demoSeq == _lastMenuDemoEmitSequence) return;
                int target = MenuDemoTargetFor(_menuDemoMilestoneIndex);
                if (demoSeq < target) return;

                int demoReportFc = MenuDemoReportFcFor(_menuDemoMilestoneIndex);
                _lastMenuDemoEmitSequence = demoSeq;
                _menuDemoMilestoneIndex++;
                Emit(demoReportFc, -1, win);
                return;
            }

            int relFc = Sim.SimClock.Frame - anchor;
            curSec = relFc / 70;
            if (curSec <= _lastEmitSec) return;
            _lastEmitSec = curSec;
            reportIter = -1;
            reportFc   = curSec * 70;
        }
        Emit(reportFc, reportIter, win);
    }

    private static int MenuDemoTargetFor(int index)
    {
        if (index < MenuDemoSeqTargets.Length)
            return MenuDemoSeqTargets[index];
        return MenuDemoSeqTargets[^1] + (index - MenuDemoSeqTargets.Length + 1) * 23 + 1;
    }

    private static int MenuDemoReportFcFor(int index)
    {
        if (index < MenuDemoReportFcs.Length)
            return MenuDemoReportFcs[index];
        return MenuDemoReportFcs[^1] + (index - MenuDemoReportFcs.Length + 1) * 70;
    }

    private void Emit(int fc, int iter, string win)
    {
        // Read live values if delegates are wired, else fall back to stub fields.
        int    px  = GetPlayerX  != null ? GetPlayerX()  : PlayerX;
        int    py  = GetPlayerY  != null ? GetPlayerY()  : PlayerY;
        uint   sc  = GetScore    != null ? GetScore()    : Score;
        int    sh  = GetShield   != null ? GetShield()   : Shield;
        int    en  = GetEnemies  != null ? GetEnemies()  : Enemies;
        int    pb  = GetPbullets != null ? GetPbullets() : Pbullets;
        int    eb  = GetEbullets != null ? GetEbullets() : Ebullets;

        // Clamp to schema bounds (mirrors parity.c clamp_* helpers).
        px = Math.Clamp(px, 0, 319);
        py = Math.Clamp(py, 0, 199);
        en = Math.Clamp(en, 0, 64);
        pb = Math.Clamp(pb, 0, 64);
        eb = Math.Clamp(eb, 0, 64);
        sh = Math.Clamp(sh, 0, 100);

        // obj_hash: emit FNV offset basis (empty list) — advisory field only.
        const string ObjHash = "cbf29ce484222325";

        // InvariantCulture: keeps negative ints (e.g. iter=-1 for menus) from
        // getting a U+200E LRM inserted on RTL-aware locales, which would
        // otherwise break the JSON parser in the comparator.
        var line = string.Create(CultureInfo.InvariantCulture, $"{{\"fc\":{fc},\"iter\":{iter},\"win\":\"{win}\",\"player_x\":{px},\"player_y\":{py},\"score\":{sc},\"shield\":{sh},\"enemies\":{en},\"pbullets\":{pb},\"ebullets\":{eb},\"obj_hash\":\"{ObjHash}\"}}");
        _out!.WriteLine(line);
    }

    public void Dispose()
    {
        _out?.Dispose();
        _out = null;
    }
}

/// <summary>
/// Godot Node wrapper around ParityEmitWorker. Bridges the Godot scene-tree
/// lifecycle (_Ready, _PhysicsProcess, _ExitTree) to the pure-C# worker.
///
/// Activate by setting the RAPTOR_PARITY_OUT environment variable to an output
/// file path before the scene loads.
/// </summary>
public partial class ParityEmitter : Node
{
    private readonly ParityEmitWorker _worker = new();

    // Forwarded properties for scene-level wiring.
    public Sim.MenuStateMachine? Menu { get => _worker.Menu; set => _worker.Menu = value; }

    // Live-state delegate hooks — wired by WaveController.
    public Func<int>?  GetPlayerX  { get => _worker.GetPlayerX;  set => _worker.GetPlayerX  = value; }
    public Func<int>?  GetPlayerY  { get => _worker.GetPlayerY;  set => _worker.GetPlayerY  = value; }
    public Func<uint>? GetScore    { get => _worker.GetScore;     set => _worker.GetScore    = value; }
    public Func<int>?  GetShield   { get => _worker.GetShield;    set => _worker.GetShield   = value; }
    public Func<int>?  GetEnemies  { get => _worker.GetEnemies;   set => _worker.GetEnemies  = value; }
    public Func<int>?  GetPbullets { get => _worker.GetPbullets;  set => _worker.GetPbullets = value; }
    public Func<int>?  GetEbullets { get => _worker.GetEbullets;  set => _worker.GetEbullets = value; }
    public Func<int>?  GetGameIter { get => _worker.GetGameIter;  set => _worker.GetGameIter = value; }
    public Func<int>?  GetGameAnchorFrame { get => _worker.GetGameAnchorFrame; set => _worker.GetGameAnchorFrame = value; }
    public Func<int>?  GetDemoGameNum { get => _worker.GetDemoGameNum; set => _worker.GetDemoGameNum = value; }
    public Func<int>?  GetMenuDemoEmitSequence { get => _worker.GetMenuDemoEmitSequence; set => _worker.GetMenuDemoEmitSequence = value; }

    // Legacy stub fields (used when Menu is null).
    public string WinState { get => _worker.WinState; set => _worker.WinState = value; }
    public int    PlayerX  { get => _worker.PlayerX;  set => _worker.PlayerX  = value; }
    public int    PlayerY  { get => _worker.PlayerY;  set => _worker.PlayerY  = value; }
    public uint   Score    { get => _worker.Score;    set => _worker.Score    = value; }
    public int    Shield   { get => _worker.Shield;   set => _worker.Shield   = value; }
    public int    Enemies  { get => _worker.Enemies;  set => _worker.Enemies  = value; }
    public int    Pbullets { get => _worker.Pbullets; set => _worker.Pbullets = value; }
    public int    Ebullets { get => _worker.Ebullets; set => _worker.Ebullets = value; }

    /// <summary>Notify the emitter that the menu context has changed.</summary>
    public void OnStateChanged() => _worker.OnStateChanged();

    /// <summary>
    /// Signal that the playthrough has ended. Closes the output immediately
    /// so no further checkpoints are emitted (mirrors the C version's
    /// exit(0) which terminates mid-game-loop before additional ticks run).
    /// </summary>
    public void SignalQuit() => _worker.Dispose();

    public override void _Ready()
    {
        _worker.Open(OS.GetEnvironment("RAPTOR_PARITY_OUT"));
    }

    public override void _PhysicsProcess(double _) { _worker.Tick(); }

    public override void _ExitTree()
    {
        _worker.Dispose();
    }
}
