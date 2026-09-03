using System.IO;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class PilotFixtureTests
{
    // Property: fixture round-trips (oracle). A CHAR####.FIL written by
    // PilotSaveStore.Save loads back via LoadAll with the same name/callsign/
    // score/idPic. Representative cases stand in for a property sweep (the
    // project has no FsCheck/Hypothesis; xUnit [Theory] expresses the invariant).
    [Theory]
    [InlineData("ALPHA", "AL", 0, 1000u)]
    [InlineData("BRAVO", "BR", 1, 25000u)]
    [InlineData("CHARLIE", "CH", 3, 148500u)]
    public void Pilot_save_round_trips_header_fields(string name, string callsign, int idPic, uint score)
    {
        string dir = Path.Combine(Path.GetTempPath(), "raptor_fix_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            int slot = PilotSaveStore.Save(dir, name, callsign, idPic, score);
            Assert.Equal(0, slot);
            var all = PilotSaveStore.LoadAll(dir);
            Assert.Single(all);
            Assert.Equal(name, all[0].Name);
            Assert.Equal(callsign, all[0].Callsign);
            Assert.Equal(idPic, all[0].IdPic);
            Assert.Equal(score, all[0].Score);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // The committed fixture that tools/capture_menu_goldens.sh stages for the
    // load_navigate_and_load scenario must stay loadable and correct. It used
    // to be produced by a permanently-skipped [Fact] in this file, which
    // asserted nothing; regeneration now lives in tools/PilotFixture, and this
    // verifies the committed bytes instead. If PilotSaveStore's format changes
    // without the fixture being regenerated, this fails rather than the C
    // reference silently reading garbage during a golden capture.
    [Fact]
    public void Committed_load_fixture_holds_the_three_expected_pilots()
    {
        string dir = Path.Combine(RepoRoot(), "tests", "parity", "menu_fixtures",
                                  "load_navigate_and_load");
        Assert.True(Directory.Exists(dir), $"missing pilot fixture: {dir}");

        var all = PilotSaveStore.LoadAll(dir);
        Assert.Equal(3, all.Count);

        (string Name, string Callsign, int IdPic, uint Score)[] expected =
        {
            ("ALPHA",   "AL", 0, 1000u),
            ("BRAVO",   "BR", 1, 25000u),
            ("CHARLIE", "CH", 3, 148500u),
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Name, all[i].Name);
            Assert.Equal(expected[i].Callsign, all[i].Callsign);
            Assert.Equal(expected[i].IdPic, all[i].IdPic);
            Assert.Equal(expected[i].Score, all[i].Score);
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null && !File.Exists(Path.Combine(d.FullName, "raptor.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root (raptor.csproj) not found");
    }
}
