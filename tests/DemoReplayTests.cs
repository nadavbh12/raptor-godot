using System;
using System.IO;
using Raptor.Test;
using Xunit;

namespace Raptor.Tests;

public class DemoReplayTests
{
    [Fact]
    [Trait("RequiresGameData", "true")]
    public void LoadFile_reads_header_and_records_from_extracted_demo()
    {
        var path = Path.Combine(FindRepoRoot(), "assets", "demos", "DEMO1G1_REC.json");

        var demo = DemoReplay.LoadFile(path);

        Assert.Equal(1890, demo.Header.MaxPlay);
        Assert.Equal(0, demo.Header.DemoGame);
        Assert.Equal(2, demo.Header.DemoWave);
        Assert.Equal(1889, demo.Records.Count);
        Assert.Equal(0, demo.Header.V);   // legacy demo: no version field
        // v1 demos carry no dirs/gax/gay → those default to 0.
        Assert.Equal(new DemoReplay.Frame(0, 0, 0, 0, 0, 0, 0, 142, 158, 4), demo.Records[0]);
        Assert.Equal(new DemoReplay.Frame(0, 1, 0, 0, 0, 0, 0, 149, 141, 3), demo.Records[25]);
    }

    [Fact]
    public void LoadFile_parses_v2_dirs_gax_and_loadout()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
            {"header":{"max_play":3,"demo_game":0,"demo_wave":1,"v":2,
                       "loadout":{"score":12500,"sweapon":11,"diff":1,
                                  "objs":[[0,1,1],[16,60,1],[11,4,1]]}},
             "records":[
               {"frame":0,"b1":1,"b2":0,"b3":0,"b4":0,"dirs":1,"gax":-3,"gay":0,"px":100,"py":160,"playerpic":18},
               {"frame":1,"b1":0,"b2":0,"b3":0,"b4":0,"dirs":8,"gax":0,"gay":2,"px":100,"py":162,"playerpic":16}]}
            """);

            var demo = DemoReplay.LoadFile(path);

            Assert.Equal(2, demo.Header.V);
            Assert.NotNull(demo.Header.Loadout);
            Assert.Equal(12500u, demo.Header.Loadout!.Score);
            Assert.Equal(11, demo.Header.Loadout.Sweapon);
            Assert.Equal(1, demo.Header.Loadout.Diff);
            Assert.Equal(3, demo.Header.Loadout.Objs.Count);
            Assert.Equal(new[] { 16, 60, 1 }, demo.Header.Loadout.Objs[1]);
            Assert.Equal(new DemoReplay.Frame(1, 0, 0, 0, 1, -3, 0, 100, 160, 18), demo.Records[0]);
            Assert.Equal(new DemoReplay.Frame(0, 0, 0, 0, 8, 0, 2, 100, 162, 16), demo.Records[1]);
        }
        finally { File.Delete(path); }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "raptor.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not find raptor-godot repo root");
    }
}
