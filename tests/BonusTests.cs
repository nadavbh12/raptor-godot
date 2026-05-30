using Raptor.Sim.Bonus;
using Xunit;

namespace Raptor.Tests;

public class BonusTests
{
    [Fact]
    public void Bonus_drifts_down_one_pixel_per_tick()
    {
        // BONUS.C:187 — cur->y++ each iter (independent of the gcnt phase).
        var b = new BonusLogic(objType: 1 /* S_PLASMA_GUNS */, x: 100, y: 50);
        b.Tick(advance: false);
        Assert.Equal(50 + 1, b.Y);
        Assert.Equal(100, b.X);
        for (int i = 0; i < 10; i++) b.Tick(advance: false);
        Assert.Equal(61, b.Y);
        Assert.True(b.Alive);
    }

    [Fact]
    public void Bonus_wobble_and_sprite_frame_advance_only_on_advance_phase()
    {
        // BONUS.C:224 — pos/curframe advance only when (gcnt & 1). gcnt is a
        // GLOBAL counter incremented once per BONUS_Think, so the advance phase
        // is driven externally (by WaveController) and shared across all bonuses,
        // independent of when each bonus spawned.
        var b = new BonusLogic(objType: 4 /* S_MINI_GUN */, x: 100, y: 50, initialPos: 15);

        b.Tick(advance: false);
        Assert.Equal(15, b.Pos);
        Assert.Equal(0, b.Frame);

        b.Tick(advance: false);
        Assert.Equal(15, b.Pos);   // still no advance — phase, not tick parity
        Assert.Equal(0, b.Frame);

        b.Tick(advance: true);
        Assert.Equal(0, b.Pos);
        Assert.Equal(1, b.Frame);
    }

    [Fact]
    public void Bonus_glow_frame_advances_every_tick_like_c()
    {
        // BONUS.C:237 — curglow advances every iter regardless of the gcnt phase.
        var b = new BonusLogic(objType: 23 /* S_ITEMBUY6 */, x: 100, y: 50);

        b.Tick(advance: false);
        Assert.Equal(1, b.GlowFrame);

        b.Tick(advance: false);
        Assert.Equal(2, b.GlowFrame);

        b.Tick(advance: true);
        Assert.Equal(3, b.GlowFrame);

        b.Tick(advance: false);
        Assert.Equal(0, b.GlowFrame);
    }

    [Fact]
    public void Bonus_despawns_using_glow_center_gy_not_raw_y()
    {
        // BONUS.C:220+278 — the off-bottom cull uses gy, not raw y:
        //   gy = y - (glow_ly>>1) + ypos[pos]
        // with glow_ly = ICNGLW_BLK height = 32 (>>1 = 16) and the wobble table
        // ypos = { -3,-3,-3,-2,-1,0,1,2,3,3,3,2,1,0,-1,-2 }. gy is taken from the
        // pre-increment y/pos, so the despawn Y depends on the wobble phase.

        // Pos=5 → ypos=0: gy = y - 16. Alive at y=216 (gy=200), dead at y=217.
        var flat = new BonusLogic(objType: 16, x: 100, y: 216, initialPos: 5);
        flat.Tick(advance: false);
        Assert.True(flat.Alive);
        var flatDead = new BonusLogic(objType: 16, x: 100, y: 217, initialPos: 5);
        flatDead.Tick(advance: false);
        Assert.False(flatDead.Alive);

        // Pos=0 → ypos=-3: gy = y - 19. Survives longer — dead only at y=220.
        var low = new BonusLogic(objType: 16, x: 100, y: 219, initialPos: 0);
        low.Tick(advance: false);
        Assert.True(low.Alive);
        var lowDead = new BonusLogic(objType: 16, x: 100, y: 220, initialPos: 0);
        lowDead.Tick(advance: false);
        Assert.False(lowDead.Alive);

        // Pos=8 → ypos=+3: gy = y - 13. Dies earlier — dead at y=214.
        var high = new BonusLogic(objType: 16, x: 100, y: 213, initialPos: 8);
        high.Tick(advance: false);
        Assert.True(high.Alive);
        var highDead = new BonusLogic(objType: 16, x: 100, y: 214, initialPos: 8);
        highDead.Tick(advance: false);
        Assert.False(highDead.Alive);
    }

    [Fact]
    public void Killed_bonus_does_not_advance_position()
    {
        var b = new BonusLogic(objType: 1, x: 100, y: 50);
        b.Kill();
        b.Tick(advance: true);
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
            b.Tick(advance: false);

        Assert.True(b.Alive);
        Assert.True(b.DisplayAsPickedUpMoney);

        b.Tick(advance: false);
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

        b.Tick(advance: false);
        Assert.Equal(51, b.Y);
        Assert.Equal(15, b.Pos);
        Assert.Equal(49, b.PickedUpMoneyCountdown);

        b.Tick(advance: true);
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
