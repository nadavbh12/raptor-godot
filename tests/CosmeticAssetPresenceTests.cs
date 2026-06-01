using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Un-mocked filesystem tracers asserting the cosmetic sprite families needed by
/// the View are actually extracted under assets/bullets. Task 6 (ES_LASER beam
/// column rendering) draws ELASER_BLK (column), ELASEPOW_BLK (gun power glow),
/// and DRAYHIT_BLK (impact, shared with the death-ray) — ESHOT.C:558-573.
/// </summary>
public class CosmeticAssetPresenceTests
{
    private static string BulletsDir()
    {
        var d = AppContext.BaseDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "raptor.csproj")))
            d = Directory.GetParent(d)?.FullName;
        Assert.NotNull(d);
        return Path.Combine(d!, "assets", "bullets");
    }

    private static string SpritesDir()
    {
        var d = AppContext.BaseDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "raptor.csproj")))
            d = Directory.GetParent(d)?.FullName;
        Assert.NotNull(d);
        return Path.Combine(d!, "assets", "sprites");
    }

    private static bool HasFamily(string family)
        => Directory.GetFiles(BulletsDir(), $"{family}_*.png").Any();

    [Fact]
    public void Es_laser_sprites_exist()
    {
        Assert.True(HasFamily("ELASER_BLK"));
        Assert.True(HasFamily("ELASEPOW_BLK"));
        Assert.True(HasFamily("DRAYHIT_BLK")); // lashit impact, shared with deathray
    }

    // Task 7a: SMSHIELD_PIC (GLB item 966, name "SMSHIELD_PIC//") is the
    // super-shield HUD counter icon — OBJECTS.C:663-672.
    [Fact]
    public void SuperShield_hud_icon_is_extracted()
        => Assert.True(Directory.GetFiles(SpritesDir(), "*_SMSHIELD_PIC.png").Any());
}
