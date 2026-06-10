using System.IO;
using Raptor.Sim;
using Raptor.Test;
using Xunit;

namespace Raptor.Tests;

public class ParityEmitterTests
{
    [Fact]
    public void Emits_NDJSON_on_70_frame_boundaries_only()
    {
        var path = Path.GetTempFileName();
        try {
            SimClock.ResetForTest();
            using var worker = new ParityEmitWorker();
            worker.Open(path);

            for (int i = 0; i < 140; i++) {
                SimClock.Tick();
                worker.Tick();
            }

            var lines = File.ReadAllLines(path);
            // Checkpoints emitted at fc=70 and fc=140.
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"fc\":70", lines[0]);
            Assert.Contains("\"fc\":140", lines[1]);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void No_output_when_path_is_null()
    {
        SimClock.ResetForTest();
        using var worker = new ParityEmitWorker();
        worker.Open(null);   // should be a no-op
        for (int i = 0; i < 200; i++) {
            SimClock.Tick();
            worker.Tick();
        }
        // No assertion needed beyond "does not throw".
    }

    [Fact]
    public void Output_validates_against_schema()
    {
        var path = Path.GetTempFileName();
        try {
            SimClock.ResetForTest();
            using var worker = new ParityEmitWorker();
            worker.Open(path);
            for (int i = 0; i < 70; i++) { SimClock.Tick(); worker.Tick(); }

            var line = File.ReadAllText(path).Trim();

            // Spot-check the NDJSON shape. Full schema validation runs in Python CI.
            Assert.StartsWith("{\"fc\":70,", line);
            Assert.Contains("\"win\":\"UNKNOWN\"", line);
            Assert.Contains("\"player_x\":144", line);
            Assert.Contains("\"player_y\":160", line);
            Assert.Contains("\"obj_hash\":\"cbf29ce484222325\"", line);
            Assert.EndsWith("}", line);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Emits_real_obj_hash_when_provider_is_wired()
    {
        var path = Path.GetTempFileName();
        try {
            SimClock.ResetForTest();
            using var worker = new ParityEmitWorker();
            worker.GetObjHash = () => 0x0123456789abcdefUL;
            worker.Open(path);
            for (int i = 0; i < 70; i++) { SimClock.Tick(); worker.Tick(); }

            var line = File.ReadAllText(path).Trim();
            // 16-hex lowercase, exactly as C formats obj_hash ("%016llx").
            Assert.Contains("\"obj_hash\":\"0123456789abcdef\"", line);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void QuitAfterDeath_suppresses_the_post_death_menu_tail()
    {
        // Death scenarios freeze the wave snapshot and re-emit it as MENU rows
        // until the script's trailing `wait` expires — a frozen tail whose length
        // depends on run length (the #27c death-movie-tail artifact). With
        // QuitAfterDeath set (parity death-wave runs), the emitter must stop once a
        // death has occurred, so the output is gameplay-only and matches a C golden
        // trimmed to the same point. Without the flag the menu tail still emits.
        var path = Path.GetTempFileName();
        try {
            SimClock.ResetForTest();
            var menu = new MenuStateMachine();
            menu.EnterMenu(0);
            using var worker = new ParityEmitWorker { Menu = menu, QuitAfterDeath = true };
            worker.Open(path);

            // Player dies → Death state; tick through it so the worker observes it
            // (emission already suppressed during Death by #26).
            menu.PlayerDied(SimClock.Frame);
            for (int i = 0; i < 5; i++) { SimClock.Tick(); worker.Tick(); }
            // Death movie completes → back to MENU (the frozen-tail context).
            menu.CompleteCutsceneIfDone(SimClock.Frame + CutsceneTimings.DeathTotal);
            Assert.Equal(WinState.Menu, menu.State);

            // Tick well past several 70-frame emit boundaries: nothing must emit.
            for (int i = 0; i < 210; i++) { SimClock.Tick(); worker.Tick(); }

            Assert.Equal("", File.ReadAllText(path).Trim());
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Landing_cutscene_state_is_suppressed_from_parity_stream()
    {
        // C emits no parity rows during INTRO_Landing (it's a MOVIE_Play, not a Do_Game
        // tick), so the post-wave HANGAR rows in the bench goldens follow MISSION_N with
        // no LANDING block. Godot mirrors this by suppressing WinState.Landing — otherwise
        // the inserted landing phase would add rows C never has and break the exact-diff.
        var path = Path.GetTempFileName();
        try {
            SimClock.ResetForTest();
            var menu = new MenuStateMachine();
            menu.EnterMenu(0);
            menu.CompleteMission(SimClock.Frame);   // → WinState.Landing
            Assert.Equal(WinState.Landing, menu.State);

            using var worker = new ParityEmitWorker { Menu = menu };
            worker.Open(path);

            // Several 70-frame emit boundaries elapse while in Landing: nothing must emit.
            for (int i = 0; i < 210; i++) { SimClock.Tick(); worker.Tick(); }

            Assert.Equal("", File.ReadAllText(path).Trim());
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Demo_playback_uses_mission_iter_buckets_not_menu_fc_schedule()
    {
        var path = Path.GetTempFileName();
        try {
            SimClock.ResetForTest();
            var menu = new MenuStateMachine();
            menu.EnterMenu(0);

            int gameIter = 0;
            using var worker = new ParityEmitWorker {
                Menu = menu,
                GetDemoGameNum = () => 0,
                GetGameAnchorFrame = () => 100,
                GetGameIter = () => gameIter,
            };
            worker.Open(path);

            for (int i = 0; i < 40; i++) {
                gameIter = i + 1;
                SimClock.Tick();
                worker.Tick();
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Contains("\"iter\":0", lines[0]);
            Assert.Contains("\"win\":\"MISSION_1\"", lines[0]);
            Assert.Contains("\"iter\":18", lines[1]);
            Assert.Contains("\"win\":\"MISSION_1\"", lines[1]);
            Assert.Contains("\"iter\":36", lines[2]);
        } finally {
            File.Delete(path);
        }
    }
}
