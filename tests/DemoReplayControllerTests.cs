using System;
using System.IO;
using Raptor.Sim;
using Raptor.Test;
using Xunit;

namespace Raptor.Tests;

// Covers the demo-playback cursor/lifecycle DemoReplayController owns after the
// E8 extraction. Uses a real extracted demo (no in-memory DemoReplay ctor
// exists). The end-of-records transition is the behavior whose shape changed in
// the extraction, so it is exercised explicitly.
public class DemoReplayControllerTests
{
    private static DemoReplay LoadDemo() =>
        DemoReplay.LoadFile(Path.Combine(FindRepoRoot(), "assets", "demos", "DEMO1G1_REC.json"));

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Schedule_then_ShouldBegin_gates_on_start_frame()
    {
        var c = new DemoReplayController();
        Assert.False(c.Active);
        Assert.False(c.PendingScheduled);

        c.Schedule(LoadDemo(), startFrame: 100);
        Assert.True(c.PendingScheduled);
        Assert.False(c.Active);
        Assert.False(c.ShouldBegin(99));
        Assert.True(c.ShouldBegin(100));
        Assert.True(c.ShouldBegin(101));
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Begin_activates_resets_cursor_and_returns_replay()
    {
        var replay = LoadDemo();
        var c = new DemoReplayController();
        c.Schedule(replay, startFrame: 50);

        var begun = c.Begin();

        Assert.Same(replay, begun);
        Assert.True(c.Active);
        Assert.False(c.PendingScheduled);
        Assert.Equal(0, c.RecordIndex);
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void TryNextFrame_yields_records_in_order_then_ends()
    {
        var replay = LoadDemo();
        var c = new DemoReplayController();
        c.Schedule(replay, startFrame: 0);
        c.Begin();

        for (int i = 0; i < replay.Records.Count; i++)
        {
            Assert.True(c.TryNextFrame(out var frame));
            Assert.Equal(replay.Records[i], frame);
            Assert.Equal(i + 1, c.RecordIndex);
        }

        // Records exhausted → no frame, replay ends (Active flips to false).
        Assert.True(c.Active);
        Assert.False(c.TryNextFrame(out _));
        Assert.False(c.Active);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "raptor.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find raptor-godot repo root");
    }
}
