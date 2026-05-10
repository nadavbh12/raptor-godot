using System;
using System.Collections.Generic;
using System.IO;
using Raptor.Test;
using Xunit;

namespace Raptor.Tests;

public class PlaythroughTests
{
    private static Playthrough LoadScript(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        try { return Playthrough.LoadFile(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Does_nothing_before_menu_ready()
    {
        var pt = LoadScript("quit\n");
        var quitFired = false;
        pt.OnQuit = () => quitFired = true;
        pt.Tick(0);
        Assert.False(quitFired);  // not ready yet
    }

    [Fact]
    public void Dispatches_quit_after_menu_ready()
    {
        var pt = LoadScript("quit\n");
        var quitFired = false;
        pt.OnQuit = () => quitFired = true;
        pt.NotifyMenuReady(0);
        pt.Tick(0);
        Assert.True(quitFired);
        Assert.True(pt.Done);
    }

    [Fact]
    public void Wait_delays_subsequent_commands()
    {
        var pt = LoadScript("wait 5\nquit\n");
        var quitFired = false;
        pt.OnQuit = () => quitFired = true;
        pt.NotifyMenuReady(0);

        // Tick at fc=0: processes "wait 5" → _waitUntilFrame = 5; stops.
        pt.Tick(0);
        Assert.False(quitFired);

        // Ticks fc=1..4: still waiting.
        for (int fc = 1; fc < 5; fc++) { pt.Tick(fc); Assert.False(quitFired); }

        // Tick at fc=5: wait complete, processes "quit".
        pt.Tick(5);
        Assert.True(quitFired);
    }

    [Fact]
    public void Key_fires_OnKeyPress_callback()
    {
        var pt = LoadScript("key Return\nquit\n");
        var keys = new List<string>();
        pt.OnKeyPress = k => keys.Add(k);
        pt.NotifyMenuReady(0);

        pt.Tick(0);  // processes "key Return" and stops for this tick
        Assert.Single(keys);
        Assert.Equal("Return", keys[0]);

        pt.Tick(1);  // processes "quit"
        Assert.True(pt.Done);
    }

    [Fact]
    public void Comments_and_blank_lines_are_skipped()
    {
        var pt = LoadScript("# this is a comment\n\nquit\n");
        var quitFired = false;
        pt.OnQuit = () => quitFired = true;
        pt.NotifyMenuReady(0);
        pt.Tick(0);
        Assert.True(quitFired);
    }

    [Fact]
    public void Dump_fires_OnDump_callback_and_consumes_tick()
    {
        var pt = LoadScript("dump label1\nquit\n");
        var dumps = new List<string>();
        pt.OnDump = l => dumps.Add(l);
        bool quitFired = false;
        pt.OnQuit = () => quitFired = true;
        pt.NotifyMenuReady(0);

        pt.Tick(0);  // processes "dump label1" — consumes tick
        Assert.Single(dumps);
        Assert.Equal("label1", dumps[0]);
        Assert.False(quitFired);

        pt.Tick(1);  // processes "quit"
        Assert.True(quitFired);
    }

    [Fact]
    public void Stub_commands_do_not_consume_tick()
    {
        // "checkpoint" is a stub — should be skipped and "key" processed same tick.
        var pt = LoadScript("checkpoint cp1\nquit\n");
        bool quitFired = false;
        pt.OnQuit = () => quitFired = true;
        pt.NotifyMenuReady(0);
        pt.Tick(0);
        Assert.True(quitFired);
    }

    [Fact]
    public void NotifyMenuReady_resets_wait_clock_to_current_frame()
    {
        // Script: wait 3, quit. Menu ready at fc=100. quit should fire at fc=103.
        var pt = LoadScript("wait 3\nquit\n");
        bool quitFired = false;
        pt.OnQuit = () => quitFired = true;
        pt.NotifyMenuReady(100);

        pt.Tick(100);  // processes "wait 3" → _waitUntilFrame = 103
        Assert.False(quitFired);

        pt.Tick(102);
        Assert.False(quitFired);

        pt.Tick(103);  // crosses threshold → processes "quit"
        Assert.True(quitFired);
    }

    [Fact]
    public void Full_credits_script_drives_correct_key_sequence()
    {
        // Simulate the credits.txt flow: Down x4, Return, Return.
        const string script = @"
wait 60
dump 01_menu_initial
key Down
wait 5
key Down
wait 5
key Down
wait 5
key Down
wait 5
dump 02_credits_highlighted
key Return
wait 200
dump 03_credits_screen
key Return
wait 100
dump 04_back_to_menu
quit
";
        var pt = LoadScript(script);
        var keys = new List<string>();
        pt.OnKeyPress = k => keys.Add(k);
        pt.NotifyMenuReady(0);

        // Run enough ticks to exhaust the script.
        for (int fc = 0; fc <= 500; fc++) pt.Tick(fc);

        Assert.True(pt.Done);
        // Expect 6 key presses: Down, Down, Down, Down, Return, Return.
        Assert.Equal(6, keys.Count);
        Assert.Equal(new[] { "Down", "Down", "Down", "Down", "Return", "Return" }, keys);
    }
}
