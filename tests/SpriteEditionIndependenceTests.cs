using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Guards against re-coupling the View layer to one edition of the game data.
///
/// Extracted sprites are named NNNN_&lt;iname&gt;.png, where NNNN is the item's
/// position in the GLB table. That position is NOT stable across editions:
/// the shareware archive holds 1764 items and the registered one 1768, so
/// every prefix after the first gap shifts. Hardcoding a prefix meant 30 of
/// the 48 hardcoded paths failed to resolve on shareware data -- the main
/// menu, both splash screens, the hangar and the pilot portraits silently
/// rendered nothing, because Image.LoadFromFile returned null.
///
/// Sprites must therefore be addressed by item name only.
/// </summary>
public class SpriteEditionIndependenceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "raptor.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);

    /// A literal "0030_BACKGRND_PIC.png" anywhere in live code, or a computed
    /// prefix like $"{58 + i:D4}_LPLAYER_PIC.png", reintroduces the coupling.
    [Fact]
    public void No_source_file_hardcodes_a_sprite_index_prefix()
    {
        var literal = new Regex("\"\\d{4}_[A-Za-z0-9$_]+\\.png\"");
        var computed = new Regex(":D4\\}_");
        var offenders = new List<string>();

        foreach (string path in SourceFiles(RepoRoot()))
        {
            int lineNo = 0;
            foreach (string raw in File.ReadLines(path))
            {
                lineNo++;
                string line = raw.Trim();
                // Comments may cite a filename as an example of the layout.
                if (line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("/*"))
                    continue;
                if (literal.IsMatch(line) || computed.IsMatch(line))
                    offenders.Add($"{Path.GetFileName(path)}:{lineNo}: {line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Sprite paths must be resolved by item name, not by GLB index prefix "
            + "(the prefix differs between game editions). Offending lines:\n  "
            + string.Join("\n  ", offenders));
    }

    /// Every menu sprite must name a real item, with no numeric prefix.
    [Fact]
    public void Menu_chrome_specs_use_bare_item_names()
    {
        var prefixed = new Regex(@"^\d{4}_");
        var specs = new List<Raptor.View.MenuSpriteSpec>
        {
            Raptor.View.MenuChrome.Background, Raptor.View.MenuChrome.RaptorLogo,
            Raptor.View.MenuChrome.Copyright, Raptor.View.MenuChrome.Hangar,
            Raptor.View.MenuChrome.HangarPilot, Raptor.View.MenuChrome.ShipComputer,
            Raptor.View.MenuChrome.Register, Raptor.View.MenuChrome.HelpComputer,
            Raptor.View.MenuChrome.Pointer, Raptor.View.MenuChrome.Slider,
            Raptor.View.MenuChrome.Cursor, Raptor.View.MenuChrome.LightOn,
            Raptor.View.MenuChrome.LightOff, Raptor.View.MenuChrome.RegisterPortrait,
        };
        specs.AddRange(Raptor.View.MenuChrome.MainVisibleItems);

        foreach (var spec in specs)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.IName));
            Assert.False(prefixed.IsMatch(spec.IName),
                $"menu sprite '{spec.IName}' still carries a GLB index prefix");
            Assert.DoesNotContain(".png", spec.IName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// The names the View layer asks for must exist in a real extraction.
    /// Skipped when assets/ has not been generated.
    [Fact]
    public void Menu_chrome_names_resolve_against_extracted_assets()
    {
        string sprites = Path.Combine(RepoRoot(), "assets", "sprites");
        if (!Directory.Exists(sprites)) return;   // assets not extracted here

        var available = Directory.GetFiles(sprites, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n!.Contains('_'))
            .Select(n => n!.Substring(n.IndexOf('_') + 1))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var required = new List<string>
        {
            Raptor.View.MenuChrome.Background.IName,
            Raptor.View.MenuChrome.RaptorLogo.IName,
            Raptor.View.MenuChrome.Hangar.IName,
            Raptor.View.MenuChrome.ShipComputer.IName,
            "APOGEE_PIC", "CYGNUS_PIC", "LPLAYER_PIC", "N$_PIC",
        };
        required.AddRange(Raptor.View.MenuChrome.MainVisibleItems.Select(s => s.IName));
        for (int i = 0; i <= 9; i++) required.Add($"N{i}_PIC");

        var missing = required.Where(n => !available.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            "menu sprites absent from the extracted assets: " + string.Join(", ", missing));
    }
}
