using System.Collections.Generic;

namespace Raptor.Sim;

/// <summary>
/// Mirrors the C BUYSTUFF enum (OBJECTS.H).
/// </summary>
public enum BuyStuff
{
    GotIt    = 0,   // OBJ_GOTIT
    NoMoney  = 1,   // OBJ_NOMONEY
    ShipFull = 2,   // OBJ_SHIPFULL
    Error    = 3,   // OBJ_ERROR
}

/// <summary>
/// Per-pilot inventory. Mirrors the C p_objs[] pointer array and the linked-list
/// of OBJ nodes. Because each type can appear at most once in our single-slot
/// model, Dictionary&lt;ObjType, ObjSlot&gt; replaces both.
///
/// No engine RNG / delta / _Process / wall-clock — sim-safe.
/// </summary>
public sealed class Inventory
{
    /// <summary>
    /// Single inventory slot — mirrors the fields of C's OBJ struct that matter for
    /// ownership/quantity tracking: num (count) and inuse (equipped flag).
    /// Nested + private: nothing outside Inventory should touch a slot directly.
    /// Fields stay mutable — Inventory mutates Num/InUse in later tasks.
    /// </summary>
    private sealed class ObjSlot
    {
        public int  Num;
        public bool InUse;
    }

    // p_objs[] equivalent: slot present + InUse == equipped.
    private readonly Dictionary<ObjType, ObjSlot> _slots = new();

    /// <summary>
    /// Mirrors plr.sweapon. Null == EMPTY (no special weapon equipped).
    /// </summary>
    public ObjType? EquippedSpecial { get; set; }

    // -----------------------------------------------------------------------
    // OBJS_Add — OBJECTS.C:734-806
    // -----------------------------------------------------------------------
    /// <summary>
    /// Adds an object of the given type to the inventory.
    /// Mirrors OBJS_Add. C deviations from the task description are noted inline.
    /// </summary>
    public BuyStuff Add(ObjType type)
    {
        // C:747-748  type >= S_LAST_OBJECT → error
        if ((int)type >= (int)ObjType.LastObject)
            return BuyStuff.Error;

        var lib = ObjLib.Of(type);

        // C:755-759  moneyflag: credit score elsewhere; no slot created.
        if (lib.MoneyFlag)
            return BuyStuff.GotIt;

        // C:761-765  !reg_flag && !game1flag → silently return GotIt.
        // We always run as "registered", so this branch is skipped (matches
        // the full-game code path, consistent with existing test data).

        // C:767-783  onlyflag: stack into existing slot; cap at max_cnt.
        if (lib.OnlyFlag)
        {
            if (_slots.TryGetValue(type, out var existing))
            {
                // C:773-774  if already at cap, return ShipFull.
                // DEVIATION FROM TASK DESCRIPTION: the task says "clamp"; C
                // actually returns ShipFull when num >= max_cnt before adding.
                if (existing.Num >= lib.MaxCnt)
                    return BuyStuff.ShipFull;

                existing.Num += lib.StartCnt;
                if (existing.Num > lib.MaxCnt)
                    existing.Num = lib.MaxCnt;

                return BuyStuff.GotIt;
            }
            // No existing slot: fall through to create a new one below.
        }

        // C:785-803  Get a free OBJ slot and initialise it.
        if (!_slots.TryGetValue(type, out var slot))
        {
            slot = new ObjSlot { Num = lib.StartCnt };
            _slots[type] = slot;
        }
        // Weapons: if a slot already existed, C leaves num as-is (owned once).

        // C:793-803  Equip if not already equipped (p_objs[type] == NUL).
        if (!slot.InUse)
        {
            slot.InUse = true;

            // C:798-802  auto-set sweapon when first special weapon is added.
            if (EquippedSpecial == null && lib.SpecialW)
                EquippedSpecial = type;
        }

        return BuyStuff.GotIt;
    }

    // -----------------------------------------------------------------------
    // OBJS_Equip — OBJECTS.C:688-706
    // -----------------------------------------------------------------------
    /// <summary>
    /// Equips an existing slot that is not yet equipped. Mirrors OBJS_Equip.
    /// Only sets InUse if the slot exists and is currently un-equipped (InUse=false).
    /// Does NOT create a new slot (C iterates the existing linked list).
    /// </summary>
    public bool Equip(ObjType type)
    {
        // C:695-703  finds first matching OBJ where p_objs[type]==NUL, sets inuse.
        if (_slots.TryGetValue(type, out var slot) && !slot.InUse)
        {
            slot.InUse = true;
            return true;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // OBJS_IsEquip — OBJECTS.C:1202-1214
    // -----------------------------------------------------------------------
    /// <summary>
    /// Returns true if the type has an equipped slot (p_objs[type] != NULL).
    /// Mirrors C: the pointer is only set when InUse=true, so we check both.
    /// </summary>
    public bool IsEquip(ObjType type)
    {
        return _slots.TryGetValue(type, out var slot) && slot.InUse;
    }

    // -----------------------------------------------------------------------
    // OBJS_GetAmt — OBJECTS.C:1007-1021
    // -----------------------------------------------------------------------
    /// <summary>
    /// Returns the Num of the slot for <paramref name="type"/>, or 0 if no slot exists.
    ///
    /// DEVIATION FROM C OBJS_GetAmt: C's version reads p_objs[type] which is only set
    /// when inuse=true, so it returns 0 for un-equipped items. We return Num regardless
    /// of InUse so that save/load round-trips are correct: OBJS_Load can restore a slot
    /// with inuse=false (representing a purchased-but-not-equipped item) and GetAmt
    /// must still report the saved quantity. No existing test relied on GetAmt returning
    /// 0 for an un-equipped-but-owned slot; InUse=false means IsEquip returns false,
    /// which is the meaningful gameplay predicate.
    /// </summary>
    public int GetAmt(ObjType type)
    {
        if (_slots.TryGetValue(type, out var slot))
            return slot.Num;
        return 0;
    }

    // -----------------------------------------------------------------------
    // OBJS_GetTotal — OBJECTS.C:1023-1043
    // -----------------------------------------------------------------------
    /// <summary>
    /// In C, GetTotal counts the NUMBER OF OBJ ENTRIES for a type across the linked
    /// list (each unit is a separate OBJ node). In THIS port a type occupies exactly
    /// ONE slot whose Num field holds the count, so the node-count is folded into Num
    /// and GetTotal == GetAmt under the one-slot model.
    ///
    /// This equivalence is load-bearing: the SuperShield purchase cap (Task 5.2,
    /// Phase-0 finding 0.2) is `GetTotal(SuperShield) >= 5 → ShipFull`. In C that
    /// counts 5 separate nodes; here it must read the single slot's Num. Returning a
    /// slot-count (0/1) would make `>= 5` unreachable and silently break the cap.
    /// </summary>
    public int GetTotal(ObjType type)
    {
        return GetAmt(type);
    }

    // -----------------------------------------------------------------------
    // OBJS_GetNext — OBJECTS.C:829-865
    // -----------------------------------------------------------------------
    /// <summary>
    /// Advances EquippedSpecial to the next owned special weapon, wrapping around.
    /// Mirrors OBJS_GetNext exactly: walks the range FirstSpecial..LastWeapon
    /// (at most 12 steps), wrapping pos back to FirstSpecial when it exceeds
    /// LastWeapon. If no owned special is found, sets EquippedSpecial to null
    /// (C's EMPTY).
    /// </summary>
    public void GetNext()
    {
        int firstSpecial = (int)ObjLib.FirstSpecial;   // 3
        int lastWeapon   = (int)ObjLib.LastWeapon;     // 14

        int cur = (int)(EquippedSpecial ?? (ObjType)(-1));  // null → -1 → < 3 → start at 3
        int pos = cur < firstSpecial ? firstSpecial : cur + 1;

        ObjType? setval = null;   // EMPTY

        // Loop over the full range (at most lastWeapon - firstSpecial + 1 = 12 iterations).
        for (int loop = firstSpecial; loop <= lastWeapon; loop++)
        {
            if (pos > lastWeapon)
                pos = firstSpecial;

            var type = (ObjType)pos;
            if (_slots.TryGetValue(type, out var slot) && slot.InUse && slot.Num > 0 && ObjLib.Of(type).SpecialW)
            {
                setval = type;
                break;
            }

            pos++;
        }

        EquippedSpecial = setval;
    }

    // -----------------------------------------------------------------------
    // OBJS_Load — OBJECTS.C:708-732
    // -----------------------------------------------------------------------
    /// <summary>
    /// Rebuilds one inventory slot from a saved OBJ record.
    /// Mirrors C OBJS_Load: allocates/overwrites the slot, sets Num and InUse,
    /// and — if inuse — registers the type as equipped (p_objs[type] = cur).
    ///
    /// Does NOT touch EquippedSpecial (sweapon). In C, RAP_LoadPlayer restores
    /// sweapon after the OBJS_Load loop via <c>if (!OBJS_IsEquip(plr.sweapon)) OBJS_GetNext()</c>.
    /// The caller (<see cref="PilotSaveStore.LoadInventory"/>) is responsible for that step.
    /// </summary>
    public void Load(ObjType type, int num, bool inuse)
    {
        // Overwrite any existing slot for this type (mirrors OBJS_Get() + fresh assignment).
        var slot = new ObjSlot { Num = num, InUse = inuse };
        _slots[type] = slot;
        // Note: EquippedSpecial deliberately not set here — mirrors C OBJS_Load.
    }

    // -----------------------------------------------------------------------
    // Read-only slot enumerator — used by PilotSaveStore.Save to write OBJ records.
    // Keeps ObjSlot private; exposes the minimal triple (type, num, inuse).
    // -----------------------------------------------------------------------
    /// <summary>
    /// Enumerates all owned slots as (type, num, inuse) triples.
    /// Used by <see cref="Raptor.Sim.PilotSaveStore"/> to persist the inventory.
    /// </summary>
    public IEnumerable<(ObjType type, int num, bool inuse)> Slots()
    {
        foreach (var kvp in _slots)
            yield return (kvp.Key, kvp.Value.Num, kvp.Value.InUse);
    }

    // -----------------------------------------------------------------------
    // New-pilot seed — mirrors WINDOWS.C:989-1007
    // -----------------------------------------------------------------------
    /// <summary>
    /// Seeds the inventory for a freshly created pilot, mirroring the C
    /// new-pilot creation sequence in WINDOWS.C:989-1007:
    ///   OBJS_Add(S_FORWARD_GUNS)
    ///   OBJS_Add(S_ENERGY) × 3   → 75 energy (start_cnt=25, onlyflag; max=100)
    ///   OBJS_GetNext()            → no specials owned → EquippedSpecial stays null
    ///
    /// Score (10000) is NOT set here; the caller retains that responsibility.
    /// </summary>
    public void SeedNewPilot()
    {
        Add(ObjType.ForwardGuns);
        Add(ObjType.Energy);
        Add(ObjType.Energy);
        Add(ObjType.Energy);
        GetNext();
    }

    // -----------------------------------------------------------------------
    // OBJS_Clear — OBJECTS.C:77-99
    // -----------------------------------------------------------------------
    /// <summary>
    /// Removes all slots and resets the equipped special weapon.
    /// Mirrors OBJS_Clear (memset of objs[] and p_objs[]).
    /// </summary>
    public void Clear()
    {
        _slots.Clear();
        EquippedSpecial = null;
    }
}
