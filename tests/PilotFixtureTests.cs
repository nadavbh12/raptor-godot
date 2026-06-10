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

    // Manual fixture author. Run explicitly to (re)generate the committed
    // load_navigate_and_load fixture. Skipped in normal CI — it writes into the
    // repo tree. Deterministic (no RNG/timestamp), so re-running is idempotent.
    [Fact(Skip = "manual: regenerates the committed load fixture; run with --filter")]
    public void Generate_load_navigate_fixture()
    {
        string dir = Path.Combine(RepoRoot(), "tests", "parity", "menu_fixtures", "load_navigate_and_load");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        PilotSaveStore.Save(dir, "ALPHA",   "AL", 0, 1000u);   // CHAR0000
        PilotSaveStore.Save(dir, "BRAVO",   "BR", 1, 25000u);  // CHAR0001
        PilotSaveStore.Save(dir, "CHARLIE", "CH", 3, 148500u); // CHAR0002
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null && !File.Exists(Path.Combine(d.FullName, "raptor.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root (raptor.csproj) not found");
    }
}
