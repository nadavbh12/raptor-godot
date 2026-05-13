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
    public void ObjType_is_preserved_for_dispatch()
    {
        // The raw OBJ_TYPE value is what ApplyBonusEffect dispatches on; verify
        // it survives construction without coercion.
        var b = new BonusLogic(objType: 11 /* S_MEGA_BOMB */, x: 0, y: 0);
        Assert.Equal(11, b.ObjType);
    }
}
