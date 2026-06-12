using System.Collections.Generic;
using System.Linq;

namespace Raptor.Sim;

// Mirror of the supply-room state owned by dosraptor/SOURCE/STORE.C —
// cur_item index, BUY/SELL mode, player money + callsign, the BuyItems /
// SellItems lists produced by MakeBuyItems / MakeSellItems (sorted by cost
// ascending), and the item catalog populated by OBJECTS.C OBJS_Init.
//
// Transactions (OBJS_Buy / OBJS_Sell) are implemented here (Task 5.2): Buy() /
// Sell() transact against the live player score (the injected accessors) and the
// live Inventory, then recompute the active list and reposition the cursor —
// mirroring STORE.C's STOR_BUYIT handling. ToggleMode recomputes the now-active
// list (STORE.C:548-555), matching C's MakeBuyItems/MakeSellItems on mode switch.
internal sealed class StoreLogic
{
    public enum Mode { Buy, Sell }

    public Mode CurrentMode { get; private set; } = Mode.Buy;
    public int  CurItem     { get; private set; } = 0;
    // Live player score (C's plr.score) — read through the injected accessor so
    // Buy/Sell transact directly against the running game's WaveController.Score.
    public int  Money       => (int)_getScore();
    // C STORE.C:261 shows plr.callsign and :257 shows id_pics[plr.id_pic] — the
    // active pilot's callsign + portrait, passed in at STORE_Enter. (Were hardcoded.)
    public string Callsign  { get; private set; }
    public int    IdPic     { get; private set; }

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

    // Live player inventory — the same canonical instance gameplay and pilot
    // load mutate (WaveController.Inventory). Queried for browse/render via
    // GetAmt / IsEquip, and mutated by Buy() / Sell() (Inventory.Buy/Sell).
    private readonly Inventory _inventory;

    // Live player-score accessors — the canonical WaveController.Score (C's
    // plr.score). Buy/Sell read via _getScore and write the mutated value back
    // via _setScore so transactions hit the running game's score directly.
    private readonly System.Func<uint>   _getScore;
    private readonly System.Action<uint> _setScore;

    public IReadOnlyList<ObjType> BuyItems  { get; private set; }
    public IReadOnlyList<ObjType> SellItems { get; private set; }

    public StoreLogic(Inventory inventory, System.Func<uint> getScore, System.Action<uint> setScore,
        string callsign = "", int idPic = 0)
    {
        _inventory = inventory;
        _getScore  = getScore;
        _setScore  = setScore;
        Callsign   = callsign;
        IdPic      = idPic;
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

    // Mirror of C STORE.C's OBJS_GetAmt(cur_obj) display (STORE.C:342/355):
    // how many of the current item the player owns.
    public int OwnedCount
    {
        get
        {
            var obj = CurrentObject;
            if (obj == null) return 0;
            return _inventory.GetAmt(obj.Value);
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
        // STORE.C:548-555 recomputes the now-active list on a mode switch
        // (STOR_BUY → MakeBuyItems; STOR_SELL → MakeSellItems), then cur_item=0.
        // Without this the opposite-mode list stays a construction-time snapshot
        // and misses items bought/sold since (e.g. a just-bought weapon would be
        // absent from SellItems).
        if (CurrentMode == Mode.Sell)
            SellItems = MakeSellItems();
        else
            BuyItems = MakeBuyItems();
        CurItem = 0;
    }

    // STORE.C:559-608 STOR_BUYIT — buy/sell the current item, then recompute the
    // active list and re-find the same object so the cursor follows it (clamped
    // if it sold out of the list). Score math lives in Inventory.Buy/Sell against
    // the live player score (read/written via the injected accessors).
    public BuyStuff Buy()
    {
        // First press while the Harrold greeting is up only dismisses it — mirrors
        // the sibling NextItem/PrevItem/ToggleMode guards. C drops keystrokes during
        // STORE_Enter's IMS_WaitTimed greeting before the buy/sell button is live, so
        // we never transact through the greeting (avoids acting on CurItem=0 blindly).
        if (ShowingGreeting) { ShowingGreeting = false; return BuyStuff.Error; }

        var obj = CurrentObject;
        if (obj == null) return BuyStuff.Error;

        ObjType pos = obj.Value;
        uint score = _getScore();
        BuyStuff rval = _inventory.Buy(pos, ref score);
        _setScore(score);

        BuyItems = MakeBuyItems();
        RepositionOnto(pos);
        return rval;
    }

    public int Sell()
    {
        // First press while the greeting is up only dismisses it (see Buy()).
        if (ShowingGreeting) { ShowingGreeting = false; return 0; }

        var obj = CurrentObject;
        if (obj == null) return 0;

        ObjType pos = obj.Value;
        uint score = _getScore();
        int left = _inventory.Sell(pos, ref score);
        _setScore(score);

        SellItems = MakeSellItems();
        RepositionOnto(pos);
        return left;
    }

    // Mirror of STORE.C:584-591 / 599-606: after MakeBuyItems/MakeSellItems,
    // walk the recomputed list and set cur_item to the index of `pos`. If `pos`
    // is no longer present (e.g. sold out), C leaves cur_item unchanged; we clamp
    // it into the new list's range so CurrentObject stays valid.
    private void RepositionOnto(ObjType pos)
    {
        var list = CurrentList;
        int idx = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == pos) { idx = i; break; }
        }
        if (idx >= 0)
            CurItem = idx;
        else if (list.Count > 0)
            CurItem = System.Math.Clamp(CurItem, 0, list.Count - 1);
        else
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

    // STORE.C MakeSellItems — collect every type that OBJS_CanSell passes,
    // then bubble-sort by OBJS_GetCost. Reads the live inventory (no longer the
    // old hardcoded StarterInventory). C's sell loop runs `for (loop = 0;
    // loop < S_LAST_OBJECT; loop++)`, so we walk 0..(LastObject-1) — wider than
    // the buy range. CanSell already rejects the un-slotted items 18..23 (they
    // are never equipped), so the practical sellable set is still 0..17, but we
    // match C's bound exactly to keep the seam faithful.
    private List<ObjType> MakeSellItems()
    {
        var list = new List<(ObjType type, int cost)>();
        for (int t = 0; t < (int)ObjType.LastObject; t++)
        {
            var type = (ObjType)t;
            if (!CanSell(type)) continue;
            if (!Catalog.TryGetValue(type, out var entry)) continue;
            list.Add((type, EffectiveCost(entry)));
        }
        list.Sort((a, b) =>
        {
            int c = a.cost.CompareTo(b.cost);
            return c != 0 ? c : ((int)a.type).CompareTo((int)b.type);
        });
        return list.ConvertAll(p => p.type);
    }

    // Mirror of OBJS_CanSell (OBJECTS.C:1150) exactly:
    //   type >= S_LAST_OBJECT           → false
    //   p_objs[type] == NULL            → false  (not owned/equipped)
    //   onlyflag && type == S_ENERGY:
    //       num <= start_cnt            → false  (can't sell below starter energy)
    //   num < start_cnt                 → false  (need at least start_cnt to sell one)
    //   else                            → true
    private bool CanSell(ObjType type)
    {
        if ((int)type >= (int)ObjType.LastObject) return false;
        if (!_inventory.IsEquip(type)) return false;             // p_objs[type] == NULL
        if (!Catalog.TryGetValue(type, out var entry)) return false;

        int num = _inventory.GetAmt(type);
        if (entry.OnlyFlag && type == ObjType.Energy)
        {
            if (num <= entry.StartCnt) return false;
        }
        if (num < entry.StartCnt) return false;
        return true;
    }
}

