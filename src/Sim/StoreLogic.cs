using System.Collections.Generic;
using System.Linq;

namespace Raptor.Sim;

// Mirror of the supply-room state owned by dosraptor/SOURCE/STORE.C —
// cur_item index, BUY/SELL mode, player money + callsign, the BuyItems /
// SellItems lists produced by MakeBuyItems / MakeSellItems (sorted by cost
// ascending), and the item catalog populated by OBJECTS.C OBJS_Init.
//
// Scope: enough to render the supply screen and step through items.
// Transactions (OBJS_Buy / OBJS_Sell, money math, ship-inventory updates)
// are out of scope here — call sites that depend on them stay stubbed.
internal sealed class StoreLogic
{
    public enum Mode { Buy, Sell }

    public Mode CurrentMode { get; private set; } = Mode.Buy;
    public int  CurItem     { get; private set; } = 0;
    public int  Money       { get; private set; } = 10000;   // matches the
                                                              // demo pilot's
                                                              // starting score
    public string Callsign  { get; private set; } = "T1";

    // STORE_Enter calls Harrold(HAR1_TXT) before the main loop. C's
    // IMS_WaitTimed(10) holds the greeting for ~10 timer ticks (DOS 18.2 Hz);
    // any keypress dismisses it. Track that explicitly so the renderer
    // shows HAR1_TXT initially and switches to the current item's text
    // after the first navigation.
    public bool ShowingGreeting { get; private set; } = true;

    // OBJS_Init-equivalent: object type → cost + flags. Only the fields we
    // need for browse rendering. Types not listed have cost=0 (unbuyable).
    // Values mirror SOURCE/OBJECTS.C OBJS_Init.
    public readonly record struct Entry(
        int Cost, bool Game1, bool OnlyFlag, int StartCnt);

    public static readonly IReadOnlyDictionary<ObjType, Entry> Catalog
        = new Dictionary<ObjType, Entry>
        {
            [ObjType.ForwardGuns]   = new(12000,   true,  false, 1),
            [ObjType.PlasmaGuns]    = new(78800,   true,  false, 1),
            [ObjType.MicroMissile]  = new(175600,  true,  false, 1),
            [ObjType.DumbMissile]   = new(145200,  true,  false, 1),
            [ObjType.MiniGun]       = new(250650,  true,  false, 1),
            [ObjType.Turret]        = new(512850,  false, false, 1),
            [ObjType.MissilePods]   = new(204950,  true,  false, 1),
            [ObjType.AirMissile]    = new(63500,   true,  false, 1),
            [ObjType.GrdMissile]    = new(110000,  true,  false, 1),
            [ObjType.Bomb]          = new(98200,   false, false, 1),
            [ObjType.EnergyGrab]    = new(300750,  false, false, 1),
            [ObjType.MegaBomb]      = new(32250,   true,  true,  1),
            [ObjType.PulseCannon]   = new(725000,  true,  false, 1),
            [ObjType.ForwardLaser]  = new(1750000, false, false, 1),
            [ObjType.DeathRay]      = new(950000,  false, false, 1),
            // start_cnt for SuperShield/Energy comes from MAX_SHIELD (100)
            // in SOURCE/RAPTOR.H: SuperShield=MAX_SHIELD=100, Energy=MAX_SHIELD/4=25.
            [ObjType.SuperShield]   = new(78500,   true,  false, 100),
            [ObjType.Energy]        = new(400,     true,  true,  25),
            [ObjType.Detect]        = new(10000,   true,  true,  1),
        };

    // Default demo / new-pilot inventory + per-type starter counts. C's
    // OBJS_Reset on a fresh pilot equips ForwardGuns and seeds the
    // shareware loadout; the energy count of 75 matches what the C build
    // shows on first store entry (3 OBJS_Add calls of S_ENERGY at 25 each,
    // see RAP.C:1143-1145). Quantities for the other starter items default
    // to 1.
    private static readonly Dictionary<ObjType, int> StarterInventory = new()
    {
        [ObjType.ForwardGuns] = 1,
        [ObjType.MicroMissile] = 1,
        [ObjType.MegaBomb]    = 1,
        [ObjType.MiniGun]     = 1,
        [ObjType.AirMissile]  = 1,
        [ObjType.Turret]      = 1,
        [ObjType.DeathRay]    = 1,
        [ObjType.Detect]      = 1,
        [ObjType.Energy]      = 75,
    };

    public IReadOnlyList<ObjType> BuyItems  { get; private set; }
    public IReadOnlyList<ObjType> SellItems { get; private set; }

    public StoreLogic()
    {
        BuyItems  = MakeBuyItems();
        SellItems = MakeSellItems();
    }

    public ObjType? CurrentObject =>
        CurrentList is { Count: > 0 } list
            ? list[System.Math.Clamp(CurItem, 0, list.Count - 1)]
            : (ObjType?)null;

    public IReadOnlyList<ObjType> CurrentList =>
        CurrentMode == Mode.Buy ? BuyItems : SellItems;

    // Mirror of OBJS_GetCost: onlyflag items scale by start_cnt (e.g.
    // S_ENERGY at lib->cost=400 × start_cnt=25 = 10000).
    public int CurrentCost
    {
        get
        {
            var obj = CurrentObject;
            if (obj == null) return 0;
            if (!Catalog.TryGetValue(obj.Value, out var e)) return 0;
            return e.OnlyFlag ? e.Cost * e.StartCnt : e.Cost;
        }
    }

    public int OwnedCount
    {
        get
        {
            var obj = CurrentObject;
            if (obj == null) return 0;
            return StarterInventory.TryGetValue(obj.Value, out var n) ? n : 0;
        }
    }

    // First navigation press only dismisses the Harrold greeting — C's
    // Harrold() calls KBD_Clear at entry, drops keystrokes during the
    // IMS_WaitTimed(10) wait, then mainloop fires its initial display at
    // cur_item=0 before reading the next key. Mirror that by holding the
    // index until the greeting is gone.
    public void NextItem()
    {
        if (ShowingGreeting) { ShowingGreeting = false; return; }
        if (CurrentList.Count == 0) return;
        CurItem = (CurItem + 1) % CurrentList.Count;
    }

    public void PrevItem()
    {
        if (ShowingGreeting) { ShowingGreeting = false; return; }
        if (CurrentList.Count == 0) return;
        CurItem = (CurItem - 1 + CurrentList.Count) % CurrentList.Count;
    }

    public void ToggleMode()
    {
        if (ShowingGreeting) { ShowingGreeting = false; return; }
        CurrentMode = CurrentMode == Mode.Buy ? Mode.Sell : Mode.Buy;
        CurItem = 0;
    }

    private static int EffectiveCost(Entry e) =>
        e.OnlyFlag ? e.Cost * e.StartCnt : e.Cost;

    // STORE.C MakeBuyItems — collect every type that OBJS_CanBuy passes,
    // then bubble-sort by OBJS_GetCost (cost * start_cnt for onlyflag).
    // Shareware filter (reg_flag=false → game1flag required) is baked in.
    // C iterates types in enum order, so types with equal effective cost
    // keep their enum-order tie-break (stable sort).
    // C's MakeBuyItems iterates `for (loop = 0; loop < S_ITEMBUY1; loop++)`
    // — we walk the same 0..17 numeric range explicitly. Enum.GetValues<>()
    // order isn't guaranteed across .NET versions, and ties on effective
    // cost have to break on the numeric type id (Energy=16 before
    // Detect=17 — both at effective cost 10000).
    private const int LastBuyableType = 17;  // S_ITEMBUY1 = 18, so 0..17.

    private static List<ObjType> MakeBuyItems()
    {
        var list = new List<(ObjType type, int cost)>();
        for (int t = 0; t <= LastBuyableType; t++)
        {
            var type = (ObjType)t;
            if (!Catalog.TryGetValue(type, out var entry)) continue;
            if (entry.Cost == 0) continue;
            if (!entry.Game1) continue;  // shareware
            if (type == ObjType.ForwardGuns) continue;  // already equipped
            list.Add((type, EffectiveCost(entry)));
        }
        // Stable sort by effective cost. .NET's List<T>.Sort isn't stable in
        // general, but with a deterministic secondary key (the numeric type
        // id, which preserves insertion order here) it becomes effectively
        // stable for our purposes.
        list.Sort((a, b) =>
        {
            int c = a.cost.CompareTo(b.cost);
            return c != 0 ? c : ((int)a.type).CompareTo((int)b.type);
        });
        return list.ConvertAll(p => p.type);
    }

    private static List<ObjType> MakeSellItems()
    {
        var list = new List<(ObjType type, int cost)>();
        for (int t = 0; t <= LastBuyableType; t++)
        {
            var type = (ObjType)t;
            if (!StarterInventory.ContainsKey(type)) continue;
            if (!Catalog.TryGetValue(type, out var entry)) continue;
            if (entry.Cost == 0) continue;
            list.Add((type, EffectiveCost(entry)));
        }
        list.Sort((a, b) =>
        {
            int c = a.cost.CompareTo(b.cost);
            return c != 0 ? c : ((int)a.type).CompareTo((int)b.type);
        });
        return list.ConvertAll(p => p.type);
    }
}

