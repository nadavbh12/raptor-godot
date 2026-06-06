using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

// FNV-1a 64-bit obj_hash, mirroring C compute_obj_hash
// (dosraptor port/platform/parity.c:160). Each owned object contributes
// (type & 0xff) then (num & 0xff) to the hash, in linked-list insertion order.
// Goldens below are independently computed and cross-checked against real C
// recordings in benchmarks/.
public class ObjHashTests
{
    [Fact]
    public void Empty_list_hashes_to_FNV_offset_basis()
    {
        // C menu rows (empty inventory) record this exact value.
        Assert.Equal(0xcbf29ce484222325UL, ObjHash.Compute(System.Array.Empty<(int, int)>()));
    }

    [Theory]
    [InlineData(11, 4, 0x08512207b505732aUL)]   // MegaBomb x4
    [InlineData(2, 1, 0x08395307b4f1348cUL)]    // MicroMissile x1
    [InlineData(0, 1, 0x08328707b4eb6e3aUL)]    // ForwardGuns x1
    public void Single_object_matches_FNV1a_golden(int type, int num, ulong expected)
    {
        Assert.Equal(expected, ObjHash.Compute(new[] { (type, num) }));
    }

    [Fact]
    public void Multiple_objects_hash_in_list_order()
    {
        Assert.Equal(0x41e5310bc59074bcUL,
            ObjHash.Compute(new[] { (2, 1), (11, 4), (4, 1) }));
    }

    [Fact]
    public void Order_changes_the_hash()
    {
        Assert.NotEqual(
            ObjHash.Compute(new[] { (0, 1), (16, 75) }),
            ObjHash.Compute(new[] { (16, 75), (0, 1) }));
    }
}

// Inventory.ComputeObjHash() must iterate slots in C linked-list insertion order
// and produce hashes matching real C recordings.
public class InventoryObjHashTests
{
    [Fact]
    public void Empty_inventory_matches_C_menu_hash()
    {
        Assert.Equal(0xcbf29ce484222325UL, new Inventory().ComputeObjHash());
    }

    [Fact]
    public void Fresh_pilot_matches_C_recorded_ingame_hash()
    {
        // Golden: benchmarks/rookie_lvl1-2/parity.ndjson first MISSION_1 row.
        // Fresh pilot = ForwardGuns(0,1) then Energy(16,75), in that order.
        var inv = new Inventory();
        inv.SeedNewPilot();
        Assert.Equal(0x4445527f98b766afUL, inv.ComputeObjHash());
    }

    [Fact]
    public void Removed_then_readded_object_appends_at_C_list_tail()
    {
        // C OBJS_Del unlinks the node; a later OBJS_Add links a fresh node at the
        // tail (OBJECTS.C:119-121). So remove + re-add changes inventory order and
        // therefore the hash — a plain unordered map cannot reproduce this.
        uint score = 1_000_000;
        var inv = new Inventory();
        inv.Add(ObjType.MegaBomb);              // [MegaBomb]
        inv.Add(ObjType.MiniGun);               // [MegaBomb, MiniGun]
        inv.Sell(ObjType.MegaBomb, ref score);  // removes MegaBomb → [MiniGun]
        inv.Add(ObjType.MegaBomb);              // re-append at tail → [MiniGun, MegaBomb]

        ulong expected = ObjHash.Compute(new[]
        {
            ((int)ObjType.MiniGun,  inv.GetAmt(ObjType.MiniGun)),
            ((int)ObjType.MegaBomb, inv.GetAmt(ObjType.MegaBomb)),
        });
        Assert.Equal(expected, inv.ComputeObjHash());
    }
}
