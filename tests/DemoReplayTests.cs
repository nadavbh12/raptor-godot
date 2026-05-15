using System;
using System.IO;
using Raptor.Test;
using Xunit;

namespace Raptor.Tests;

public class DemoReplayTests
{
    [Fact]
    public void LoadFile_reads_header_and_records_from_extracted_demo()
    {
        var path = Path.Combine(FindRepoRoot(), "assets", "demos", "DEMO1G1_REC.json");

        var demo = DemoReplay.LoadFile(path);

        Assert.Equal(1890, demo.Header.MaxPlay);
        Assert.Equal(0, demo.Header.DemoGame);
        Assert.Equal(2, demo.Header.DemoWave);
        Assert.Equal(1889, demo.Records.Count);
        Assert.Equal(new DemoReplay.Frame(0, 0, 0, 0, 142, 158, 4), demo.Records[0]);
        Assert.Equal(new DemoReplay.Frame(0, 1, 0, 0, 149, 141, 3), demo.Records[25]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "raptor.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find raptor-godot repo root");
    }
}
