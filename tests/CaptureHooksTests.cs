using System.Linq;
using Raptor.Sim;
using Raptor.Sim.Enemy;
using Raptor.View;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Unit coverage for the env-gated cosmetic-capture hooks. These are parity-inert
/// (the envs are never set by the 12-scenario L2a gate); the tests pin the pure
/// helpers behind them so the behavior can't silently regress.
/// </summary>
public class CaptureHooksTests
{
    // ── RAPTOR_GRANT name parsing (WaveController.ParseGrant) ──────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense,foo")]
    public void ParseGrant_returns_empty_for_blank_or_unknown(string? env)
    {
        Assert.Empty(WaveController.ParseGrant(env));
    }

    [Fact]
    public void ParseGrant_maps_known_names_case_insensitively()
    {
        var got = WaveController.ParseGrant("Detect, SUPERSHIELD ; megabomb").ToList();
        Assert.Equal(
            new[] { ObjType.Detect, ObjType.SuperShield, ObjType.MegaBomb },
            got);
    }

    [Fact]
    public void ParseGrant_ignores_unknown_but_keeps_known()
    {
        var got = WaveController.ParseGrant("detect,turret,megabomb").ToList();
        Assert.Equal(new[] { ObjType.Detect, ObjType.MegaBomb }, got);
    }

    // ── RAPTOR_BOSS_LOWHP clamp (EnemyLogic.DebugClampHits) ────────────────────

    private static SpriteMeta MetaWithHits(int hits) => new SpriteMeta
    {
        Hits = hits,
        NumFlight = 1,
        FlightType = 1,
        FlightX = new[] { 0 },
        FlightY = new[] { 0 },
        NumGuns = 0,
    };

    [Fact]
    public void DebugClampHits_lowers_hits_below_smoke_threshold_but_keeps_maxhits()
    {
        var e = new EnemyLogic(MetaWithHits(350), 0, 0);
        Assert.Equal(350, e.Hits);
        e.DebugClampHits(40);
        Assert.Equal(40, e.Hits);     // < 50 → boss-smoke cosmetic can trigger
        Assert.Equal(350, e.MaxHits); // health-% denominator unchanged
        Assert.True(e.Alive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(400)]   // n >= current Hits → no-op
    public void DebugClampHits_is_noop_for_nonpositive_or_higher(int n)
    {
        var e = new EnemyLogic(MetaWithHits(350), 0, 0);
        e.DebugClampHits(n);
        Assert.Equal(350, e.Hits);
    }

    // ── secret-tier spawn mapping (WaveController.GetEbLevel) ──────────────────

    [Theory]
    [InlineData(0, 1)]   // E_SECRET_1 → EB_SECRET_1
    [InlineData(1, 2)]   // E_SECRET_2 → EB_SECRET_2
    [InlineData(2, 4)]   // E_SECRET_3 → EB_SECRET_3
    [InlineData(3, 8)]   // E_EASY_LEVEL
    [InlineData(4, 16)]  // E_MED_LEVEL
    [InlineData(5, 32)]  // E_HARD_LEVEL
    public void GetEbLevel_maps_secret_tiers_so_secret_enemies_can_spawn(int rawLevel, int expectedBit)
    {
        // Regression: secret levels 0/1/2 previously fell through to EB_NOT_USED,
        // so the wave-8 ES_LASER secret enemy could never spawn. Parity-inert: the
        // secret bits are absent from cur_diff in every scenario, so a secret-level
        // sprite still doesn't spawn unless RAPTOR_FORCE_SECRET adds them.
        Assert.Equal(expectedBit, WaveController.GetEbLevel(rawLevel));
        const int normalDiff = 8 | 16 | 32;   // EASY|MED|HARD, no secret bits
        Assert.Equal(rawLevel <= 2 ? 0 : expectedBit, WaveController.GetEbLevel(rawLevel) & normalDiff);
    }

    // ── shot_map.tsv InvariantCulture formatting (DebugRenderer.FormatShotMapRow) ─

    [Fact]
    public void FormatShotMapRow_negative_iter_has_no_bidi_mark_and_parses()
    {
        // Pre-game frames carry iter = -1; on a Hebrew/RTL locale this used to be
        // written as "‎-1", which crashed the alignment tools' int() parse.
        string row = DebugRenderer.FormatShotMapRow("fc00015_sec000.png",
            savedFc: 15, drawnFc: 15, drawnIter: -1,
            score: 0, shield: 0, enemies: 0, pbullets: 0, ebullets: 0);

        // The whole row must be ASCII — this catches the LRM (U+200E) and the
        // bidi-marked negative sign that he-IL's CurrentCulture would otherwise
        // emit, which broke the alignment tools' int() parse.
        Assert.All(row, c => Assert.True(c < 0x80,
            $"non-ASCII char U+{(int)c:X4} in shot_map row: {row}"));

        string[] parts = row.TrimEnd('\n').Split('\t');
        Assert.Equal(9, parts.Length);
        Assert.Equal("fc00015_sec000.png", parts[0]);
        // drawn_iter round-trips (parsed invariantly, as the audit tools do).
        Assert.Equal(-1, int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
    }
}
