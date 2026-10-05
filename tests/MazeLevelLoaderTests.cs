using System;
using System.IO;
using Raptor.Sim.MazeLevel;
using Xunit;

namespace Raptor.Tests;

public class MazeLevelLoaderTests
{
    // Path from tests/bin/Debug/net8.0/ up to the repo root, then into assets/levels/.
    private static string WavePath(int waveNum)
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(
            Path.Combine(baseDir, "..", "..", "..", "..", "assets", "levels", $"MAP{waveNum}G1_MAP.json"));
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Loads_Map1_Wave1()
    {
        var level = MazeLevelLoader.LoadFromFile(WavePath(1));
        Assert.NotNull(level);
        Assert.NotNull(level.Sprites);
        Assert.NotEmpty(level.Sprites);
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Sprite_count_matches_numsprites_field()
    {
        var level = MazeLevelLoader.LoadFromFile(WavePath(1));
        Assert.Equal(level.NumSprites, level.Sprites!.Count);
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Map1_has_299_sprites()
    {
        var level = MazeLevelLoader.LoadFromFile(WavePath(1));
        Assert.Equal(299, level.Sprites!.Count);
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Spawn_Fields_Are_Populated()
    {
        var level = MazeLevelLoader.LoadFromFile(WavePath(1));
        var first = level.Sprites![0];
        // Per JSON: {"link": 0, "slib": 13, "x": 4, "y": 139, "game": 0, "level": 4}
        Assert.True(first.Slib >= 0, "Slib must be a valid sprite library index");
        Assert.True(first.X >= 0 && first.X < level.Cols, "X must be within tile grid columns");
        Assert.True(first.Y >= 0 && first.Y < level.Rows, "Y must be within tile grid rows");
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void First_sprite_has_expected_values()
    {
        var level = MazeLevelLoader.LoadFromFile(WavePath(1));
        var s = level.Sprites![0];
        Assert.Equal(0, s.Link);
        Assert.Equal(13, s.Slib);
        Assert.Equal(4, s.X);
        Assert.Equal(139, s.Y);
        Assert.Equal(0, s.Game);
        Assert.Equal(4, s.Level);
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Map_name_is_correct()
    {
        var level = MazeLevelLoader.LoadFromFile(WavePath(1));
        Assert.Equal("MAP1G1_MAP", level.Name);
    }

    [Fact]
    [Trait("RequiresGameData", "true")]
    public void Map_grid_dimensions_are_correct()
    {
        var level = MazeLevelLoader.LoadFromFile(WavePath(1));
        Assert.Equal(150, level.Rows);
        Assert.Equal(9, level.Cols);
    }

    [Fact]
    public void TileXToPixel_centers_within_tile()
    {
        // tile 0 center = 18, tile 1 center = 54
        Assert.Equal(18,  MazeLevelLoader.TileXToPixel(0));
        Assert.Equal(54,  MazeLevelLoader.TileXToPixel(1));
        Assert.Equal(306, MazeLevelLoader.TileXToPixel(8));
    }
}
