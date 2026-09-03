using System;
using System.IO;
using Raptor.Sim;

namespace Raptor.PilotFixture;

/// <summary>
/// Writes the pilot-save fixture used by the load_navigate_and_load menu-parity
/// scenario. The C reference binary reads CHAR####.FIL from its working
/// directory, so the LOAD dialog needs real files on disk before it starts;
/// this produces them through the game's own save code.
///
///   dotnet build raptor.csproj
///   dotnet run --project tools/PilotFixture
///
/// Deterministic (no RNG, no timestamps), so re-running is idempotent.
/// </summary>
internal static class Program
{
    /// The three pilots the scenario navigates between. Slot order is the
    /// write order: ALPHA lands in CHAR0000, BRAVO CHAR0001, CHARLIE CHAR0002.
    internal static readonly (string Name, string Callsign, int IdPic, uint Score)[] Pilots =
    {
        ("ALPHA",   "AL", 0, 1000u),
        ("BRAVO",   "BR", 1, 25000u),
        ("CHARLIE", "CH", 3, 148500u),
    };

    private static int Main(string[] args)
    {
        string dir;
        if (args.Length > 0)
        {
            dir = args[0];
        }
        else
        {
            string? root = FindRepoRoot();
            if (root == null)
            {
                Console.Error.WriteLine(
                    "PilotFixture: could not locate the repo root (no raptor.csproj found "
                    + "above the working directory). Pass an output directory explicitly.");
                return 2;
            }
            dir = Path.Combine(root, "tests", "parity", "menu_fixtures", "load_navigate_and_load");
        }

        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        foreach (var (name, callsign, idPic, score) in Pilots)
        {
            int slot = PilotSaveStore.Save(dir, name, callsign, idPic, score);
            Console.WriteLine($"  CHAR{slot:D4}.FIL  {name} ({callsign}) score={score}");
        }

        Console.WriteLine($"[PilotFixture] wrote {Pilots.Length} pilots to {dir}");
        return 0;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "raptor.csproj")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
