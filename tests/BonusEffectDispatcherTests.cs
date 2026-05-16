using Raptor.Sim.Bonus;
using Raptor.Sim.Shots;
using Xunit;

namespace Raptor.Tests;

/// <summary>
/// Unit tests for the bonus-pickup effect dispatch ported as
/// BonusEffectDispatcher. Mirrors C OBJS_Add / OBJS_AddEnergy
/// (OBJECTS.C:691-720, BONUS.C:208-216).
/// </summary>
public class BonusEffectDispatcherTests
{
    private const int MaxShield = 100;

    [Fact]
    public void Weapon_pickup_returns_GrantedWeapon_no_other_effects()
    {
        var ps = new PlayerShooter();
        var r = BonusEffectDispatcher.Apply(objType: 1 /* PlasmaGuns */, ps, MaxShield);
        Assert.True(r.GrantedWeapon);
        Assert.Equal(0, r.HealAmount);
        Assert.Equal(0u, r.ScoreAdd);
        Assert.False(r.DetectorActivated);
        Assert.True(ps.HasPlasmaGuns);
    }

    [Fact]
    public void Special_weapon_pickup_returns_GrantedWeapon_and_sets_active()
    {
        var ps = new PlayerShooter();
        var r = BonusEffectDispatcher.Apply(objType: 3 /* DumbMissile */, ps, MaxShield);
        Assert.True(r.GrantedWeapon);
        Assert.Equal(WeaponType.DumbMissile, ps.SpecialWeapon);
    }

    [Fact]
    public void Super_shield_heals_to_full()
    {
        var ps = new PlayerShooter();
        var r = BonusEffectDispatcher.Apply(objType: 15 /* SuperShield */, ps, MaxShield);
        Assert.False(r.GrantedWeapon);
        Assert.Equal(MaxShield, r.HealAmount);
        Assert.Equal(0u, r.ScoreAdd);
    }

    [Fact]
    public void Energy_heals_by_quarter_max()
    {
        // BONUS.C:214 — MAX_SHIELD/4. MaxShield=100 → +25.
        var ps = new PlayerShooter();
        var r = BonusEffectDispatcher.Apply(objType: 16 /* Energy */, ps, MaxShield);
        Assert.Equal(25, r.HealAmount);
    }

    [Fact]
    public void Detect_activates_detector_no_score()
    {
        var ps = new PlayerShooter();
        var r = BonusEffectDispatcher.Apply(objType: 17 /* DETECT */, ps, MaxShield);
        Assert.True(r.DetectorActivated);
        Assert.Equal(0u, r.ScoreAdd);   // moneyflag=FALSE for S_DETECT
        Assert.Equal(0, r.HealAmount);
    }

    [Theory]
    [InlineData(18, 93800u)]
    [InlineData(19, 76000u)]
    [InlineData(20, 55700u)]
    [InlineData(21, 35200u)]
    [InlineData(22, 122500u)]
    [InlineData(23, 50u)]
    [InlineData(24, 50u)]
    public void ItemBuy_adds_cost_to_score(int objType, uint expectedCost)
    {
        var ps = new PlayerShooter();
        var r = BonusEffectDispatcher.Apply(objType, ps, MaxShield);
        Assert.Equal(expectedCost, r.ScoreAdd);
        Assert.Equal(0, r.HealAmount);
        Assert.False(r.DetectorActivated);
        Assert.False(r.GrantedWeapon);
    }

    [Fact]
    public void ItemBuyCost_table_matches_C_obj_lib()
    {
        // OBJECTS.C:481-572 lib->cost. Hardcoded reference values; if these
        // ever drift from C, this test fires alongside the per-slot Theory.
        Assert.Equal(new[] { 93800, 76000, 55700, 35200, 122500, 50 },
                     BonusEffectDispatcher.ItemBuyCost);
    }

    [Fact]
    public void Unknown_objType_is_safe_noop()
    {
        var ps = new PlayerShooter();
        var r = BonusEffectDispatcher.Apply(objType: 999, ps, MaxShield);
        Assert.False(r.GrantedWeapon);
        Assert.Equal(0, r.HealAmount);
        Assert.Equal(0u, r.ScoreAdd);
        Assert.False(r.DetectorActivated);
    }
}
