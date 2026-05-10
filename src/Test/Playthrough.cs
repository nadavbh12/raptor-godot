using System;
using System.Collections.Generic;
using System.IO;

namespace Raptor.Test;

/// <summary>
/// Pure C# script parser and tick-based event dispatcher.
/// Mirrors dosraptor's port/platform/playthrough.c semantics.
///
/// Script grammar (only commands used by credits.txt are fully implemented;
/// others are stubbed to no-op with a log callback):
///   wait N         — pause N frames before the next command
///   key NAME       — one-frame key press (press + schedule release next tick)
///   down NAME      — hold key (no auto-release)
///   up NAME        — release held key
///   dump LABEL     — screenshot label (Stage 4: no-op)
///   mouse X,Y      — set mouse position (Stage 4: no-op)
///   click          — one-frame mouse button (Stage 4: no-op)
///   checkpoint LBL — label annotation (Stage 4: no-op)
///   quit           — terminate
///   # ...          — comment (ignored)
///
/// Wired callbacks:
///   OnKeyPress(name)  — key was pressed (caller dispatches to input pipeline)
///   OnKeyDown(name)   — key was held
///   OnKeyUp(name)     — key was released
///   OnDump(label)     — screenshot requested
///   OnLog(msg)        — informational log message
///   OnQuit()          — script finished; caller should quit the process
/// </summary>
public sealed class Playthrough
{
    private readonly List<(string cmd, string arg)> _script = new();
    private int _ip = 0;
    private int _waitUntilFrame = 0;
    private bool _menuReady = false;

    public bool Done { get; private set; }

    // Callbacks — wire these before first Tick call.
    public Action<string>? OnKeyPress { get; set; }
    public Action<string>? OnKeyDown { get; set; }
    public Action<string>? OnKeyUp { get; set; }
    public Action<string>? OnDump { get; set; }
    public Action<string>? OnLog { get; set; }
    public Action? OnQuit { get; set; }

    /// <summary>
    /// Parses a script file. Lines are trimmed; blank lines and comment lines
    /// (starting with #) are ignored.
    /// </summary>
    public static Playthrough LoadFile(string path)
    {
        var p = new Playthrough();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var split = line.Split(' ', 2);
            p._script.Add((split[0], split.Length > 1 ? split[1].Trim() : ""));
        }
        return p;
    }

    /// <summary>
    /// Called by MenuController when the menu is fully painted and ready for
    /// input. Mirrors raptor_playthrough_menu_ready() — resets the wait clock
    /// to the menu's current frame so "wait N" means "N frames after menu
    /// paints", not "N frames after the binary started".
    /// </summary>
    public void NotifyMenuReady(int currentFrame)
    {
        if (_menuReady) return;
        _menuReady = true;
        _waitUntilFrame = currentFrame;
        OnLog?.Invoke($"playthrough: menu ready at fc={currentFrame}, dispatching");
    }

    /// <summary>
    /// Called every physics tick. Dispatches commands that are due.
    /// </summary>
    public void Tick(int currentFrame)
    {
        if (Done) return;
        if (!_menuReady) return;
        if (currentFrame < _waitUntilFrame) return;

        while (_ip < _script.Count)
        {
            var (cmd, arg) = _script[_ip++];
            if (Dispatch(cmd, arg, currentFrame))
                return;  // command consumed the tick; pause until next tick
        }

        // End of script.
        if (!Done)
        {
            Done = true;
            OnLog?.Invoke("playthrough: end-of-script");
            OnQuit?.Invoke();
        }
    }

    // Returns true if the command consumed the tick (caller should stop
    // processing for this tick), false if the line was inert and we should
    // keep reading.
    private bool Dispatch(string cmd, string arg, int currentFrame)
    {
        switch (cmd)
        {
            case "wait":
                if (int.TryParse(arg, out int n))
                {
                    _waitUntilFrame = currentFrame + n;
                    OnLog?.Invoke($"playthrough: wait {n} (until fc={_waitUntilFrame})");
                }
                return true;

            case "key":
                OnLog?.Invoke($"playthrough: key {arg}");
                OnKeyPress?.Invoke(arg);
                return true;

            case "down":
                OnLog?.Invoke($"playthrough: down {arg}");
                OnKeyDown?.Invoke(arg);
                return true;

            case "up":
                OnLog?.Invoke($"playthrough: up {arg}");
                OnKeyUp?.Invoke(arg);
                return true;

            case "dump":
                OnLog?.Invoke($"playthrough: dump {arg}");
                OnDump?.Invoke(arg);
                return true;  // dump is its own tick (consistent with C dispatch)

            case "quit":
                OnLog?.Invoke("playthrough: quit");
                Done = true;
                OnQuit?.Invoke();
                return true;

            // Stubbed commands — log and continue to next command (inert).
            case "checkpoint":
            case "mouse":
            case "click":
                OnLog?.Invoke($"playthrough: stub {cmd} {arg}");
                return false;

            default:
                OnLog?.Invoke($"playthrough: unknown command '{cmd}'");
                return false;
        }
    }
}
