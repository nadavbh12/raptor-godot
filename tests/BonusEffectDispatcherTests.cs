using Raptor.Sim;
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
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType: 1 /* PlasmaGuns */, ps, inv, MaxShield);
        Assert.True(r.GrantedWeapon);
        Assert.Equal(0, r.HealAmount);
        Assert.Equal(0u, r.ScoreAdd);
        Assert.False(r.DetectorActivated);
        Assert.True(ps.HasPlasmaGuns);
    }

    [Fact]
    public void Special_weapon_pickup_returns_GrantedWeapon_and_sets_active()
    {
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType: 3 /* DumbMissile */, ps, inv, MaxShield);
        Assert.True(r.GrantedWeapon);
        Assert.Equal(ObjType.DumbMissile, ps.SpecialWeapon);
    }

    [Fact]
    public void Special_weapon_pickup_lands_as_equipped_slot_in_shared_inventory()
    {
        // The weapon path must route through shooter.GrantWeapon → the SAME
        // Inventory instance the dispatcher is handed. A first special pickup
        // creates an equipped slot AND auto-sets EquippedSpecial.
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType: 3 /* DumbMissile */, ps, inv, MaxShield);
        Assert.True(r.GrantedWeapon);
        Assert.True(inv.IsEquip(ObjType.DumbMissile));
        Assert.Equal(ObjType.DumbMissile, inv.EquippedSpecial);
    }

    [Fact]
    public void Super_shield_heals_to_full()
    {
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType: 15 /* SuperShield */, ps, inv, MaxShield);
        Assert.False(r.GrantedWeapon);
        Assert.Equal(MaxShield, r.HealAmount);
        Assert.Equal(0u, r.ScoreAdd);
    }

    [Fact]
    public void Energy_heals_by_quarter_max()
    {
        // BONUS.C:214 — MAX_SHIELD/4. MaxShield=100 → +25.
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType: 16 /* Energy */, ps, inv, MaxShield);
        Assert.Equal(25, r.HealAmount);
    }

    [Fact]
    public void Detect_activates_detector_no_score()
    {
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType: 17 /* DETECT */, ps, inv, MaxShield);
        Assert.True(r.DetectorActivated);
        Assert.Equal(0u, r.ScoreAdd);   // moneyflag=FALSE for S_DETECT
        Assert.Equal(0, r.HealAmount);
    }

    [Fact]
    public void Detect_creates_equipped_inventory_slot_and_keeps_detector_flag()
    {
        // Task 3.4: detector is a real obj slot in C (p_objs[S_DETECT]).
        // Pickup routes through Inventory.Add AND still flags DetectorActivated.
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType: 17 /* DETECT */, ps, inv, MaxShield);
        Assert.True(r.DetectorActivated);
        Assert.True(inv.IsEquip(ObjType.Detect));
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
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType, ps, inv, MaxShield);
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
        var inv = new Inventory();
        var ps = new PlayerShooter(inv);
        var r = BonusEffectDispatcher.Apply(objType: 999, ps, inv, MaxShield);
        Assert.False(r.GrantedWeapon);
        Assert.Equal(0, r.HealAmount);
        Assert.Equal(0u, r.ScoreAdd);
        Assert.False(r.DetectorActivated);
    }
}
