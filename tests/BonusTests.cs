using Raptor.Sim.Bonus;
using Xunit;

namespace Raptor.Tests;

public class BonusTests
{
    [Fact]
    public void Bonus_drifts_down_one_pixel_per_tick()
    {
        // BONUS.C:187 — cur->y++ each iter.
        var b = new BonusLogic(objType: 1 /* S_PLASMA_GUNS */, x: 100, y: 50);
        b.Tick();
        Assert.Equal(50 + 1, b.Y);
        Assert.Equal(100, b.X);
        for (int i = 0; i < 10; i++) b.Tick();
        Assert.Equal(61, b.Y);
        Assert.True(b.Alive);
    }

    [Fact]
    public void Bonus_wobble_and_sprite_frame_advance_every_other_tick()
    {
        var b = new BonusLogic(objType: 4 /* S_MINI_GUN */, x: 100, y: 50, initialPos: 15);

        b.Tick();
        Assert.Equal(15, b.Pos);
        Assert.Equal(0, b.Frame);

        b.Tick();
        Assert.Equal(0, b.Pos);
        Assert.Equal(1, b.Frame);
    }

    [Fact]
    public void Bonus_glow_frame_advances_every_tick_like_c()
    {
        var b = new BonusLogic(objType: 23 /* S_ITEMBUY6 */, x: 100, y: 50);

        b.Tick();
        Assert.Equal(1, b.GlowFrame);

        b.Tick();
        Assert.Equal(2, b.GlowFrame);

        b.Tick();
        Assert.Equal(3, b.GlowFrame);

        b.Tick();
        Assert.Equal(0, b.GlowFrame);
    }

    [Fact]
    public void Bonus_dies_when_falling_below_screen()
    {
        // BONUS.C:241 — `if (cur->gy > 200) remove`. Our equivalent: y > 200.
        var b = new BonusLogic(objType: 16 /* S_ENERGY */, x: 100, y: 199);
        b.Tick();   // y → 200, still alive.
        Assert.True(b.Alive);
        b.Tick();   // y → 201, despawn.
        Assert.False(b.Alive);
    }

    [Fact]
    public void Killed_bonus_does_not_advance_position()
    {
        var b = new BonusLogic(objType: 1, x: 100, y: 50);
        b.Kill();
        b.Tick();
        Assert.Equal(50, b.Y);
        Assert.False(b.Alive);
    }

    [Fact]
    public void Money_bonus_survives_pickup_as_dollar_countdown()
    {
        var b = new BonusLogic(objType: 23 /* S_ITEMBUY6 */, x: 100, y: 50);

        b.MarkPickedUpMoney();

        Assert.True(b.Alive);
        Assert.True(b.DisplayAsPickedUpMoney);
        Assert.Equal(50, b.PickedUpMoneyCountdown);

        for (int i = 0; i < 49; i++)
            b.Tick();

        Assert.True(b.Alive);
        Assert.True(b.DisplayAsPickedUpMoney);

        b.Tick();
        Assert.False(b.Alive);
    }

    [Fact]
    public void Picked_up_money_keeps_c_drift_and_wobble_while_counting_down()
    {
        // BONUS.C computes the display position, then still advances y/pos even
        // while dflag is set. The rendered pickup should be a moving '$', not a
        // frozen marker.
        var b = new BonusLogic(objType: 23 /* S_ITEMBUY6 */, x: 100, y: 50, initialPos: 15);
        b.MarkPickedUpMoney();

        b.Tick();
        Assert.Equal(51, b.Y);
        Assert.Equal(15, b.Pos);
        Assert.Equal(49, b.PickedUpMoneyCountdown);

        b.Tick();
        Assert.Equal(52, b.Y);
        Assert.Equal(0, b.Pos);
        Assert.Equal(48, b.PickedUpMoneyCountdown);
    }

    [Fact]
    public void Picked_up_money_cannot_be_collected_again_while_showing_dollar()
    {
        // BONUS.C pickup path is guarded by !cur->dflag.
        var b = new BonusLogic(objType: 23 /* S_ITEMBUY6 */, x: 150, y: 166);

        Assert.True(b.CanBePickedUpBy(playerX: 144, playerY: 160));

        b.MarkPickedUpMoney();

        Assert.False(b.CanBePickedUpBy(playerX: 144, playerY: 160));
    }

    [Fact]
    public void ObjType_is_preserved_for_dispatch()
    {
        // The raw OBJ_TYPE value is what ApplyBonusEffect dispatches on; verify
        // it survives construction without coercion.
        var b = new BonusLogic(objType: 11 /* S_MEGA_BOMB */, x: 0, y: 0);
        Assert.Equal(11, b.ObjType);
    }
}
