using System;
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

    // When the game enters, the C version's Do_Game init (GFX_FadeIn) consumes
    // ~65+ frames before the second parity_tick fires. This causes the C golden
    // to skip fc=70 and jump from fc=0 directly to fc=140 (curSec=2 not 1).
    // We replicate this by pre-advancing _lastEmitSec by 1 on the first in-game
    // emission (effectively skipping the fc=70 bucket).
    private bool _inGameFirstEmitDone = false;

    /// <summary>
    /// Called every physics tick. Emits a checkpoint line whenever we cross
    /// into a new 70-frame "second" bucket relative to the current context anchor.
    /// Mirrors the boundary-crossing detection in parity.c:raptor_parity_tick.
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
            if (Menu.InGame)
            {
                isInGame = true;
                anchor   = Menu.GameEnteredFrame;
                win      = Menu.GameNum switch
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
                if (isInGame)
                    _inGameFirstEmitDone = false;
            }
        }
        else
        {
            // Legacy mode: emit at absolute frame multiples of 70.
            if (Sim.SimClock.Frame % 70 != 0) return;
            Emit(Sim.SimClock.Frame, WinState);
            return;
        }

        int relFc  = Sim.SimClock.Frame - anchor;
        int curSec = relFc / 70;
        if (curSec <= _lastEmitSec) return;
        _lastEmitSec = curSec;
        Emit(curSec * 70, win);

        // After the first in-game emission (fc=0), skip one bucket to simulate
        // the C version's GFX_FadeIn delay that causes the second parity_tick
        // to land at curSec=2 (fc=140) rather than curSec=1 (fc=70).
        if (isInGame && !_inGameFirstEmitDone)
        {
            _inGameFirstEmitDone = true;
            _lastEmitSec = 1;  // pre-fill curSec=1 → next emit is curSec=2 = fc=140
        }
    }

    private void Emit(int fc, string win)
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

        var line = $"{{\"fc\":{fc}," +
                   $"\"win\":\"{win}\"," +
                   $"\"player_x\":{px}," +
                   $"\"player_y\":{py}," +
                   $"\"score\":{sc}," +
                   $"\"shield\":{sh}," +
                   $"\"enemies\":{en}," +
                   $"\"pbullets\":{pb}," +
                   $"\"ebullets\":{eb}," +
                   $"\"obj_hash\":\"{ObjHash}\"}}";
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
