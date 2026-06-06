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

    // p_objs[] equivalent: slot present + InUse == equipped. Insertion-ordered so
    // obj_hash can walk it in C linked-list order (see SlotMap / ComputeObjHash).
    private readonly SlotMap _slots = new();

    /// <summary>
    /// Insertion-ordered ObjType→ObjSlot map mirroring C's OBJ linked list
    /// (OBJECTS.C): a new key links at the tail; Remove unlinks; a re-added key
    /// links a fresh node at the tail. Exposes exactly the surface Inventory uses
    /// (indexer, TryGetValue, Remove, Clear, ordered enumeration) so every existing
    /// call site is unchanged. Ordering is load-bearing for obj_hash parity.
    /// </summary>
    private sealed class SlotMap : IEnumerable<KeyValuePair<ObjType, ObjSlot>>
    {
        private readonly Dictionary<ObjType, ObjSlot> _map = new();
        private readonly List<ObjType> _order = new();

        public ObjSlot this[ObjType key]
        {
            get => _map[key];
            set
            {
                if (!_map.ContainsKey(key))
                    _order.Add(key);
                _map[key] = value;
            }
        }

        public bool TryGetValue(ObjType key,
            [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ObjSlot value)
            => _map.TryGetValue(key, out value);

        public bool Remove(ObjType key)
        {
            if (!_map.Remove(key)) return false;
            _order.Remove(key);
            return true;
        }

        public void Clear()
        {
            _map.Clear();
            _order.Clear();
        }

        public IEnumerator<KeyValuePair<ObjType, ObjSlot>> GetEnumerator()
        {
            foreach (var key in _order)
                yield return new KeyValuePair<ObjType, ObjSlot>(key, _map[key]);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

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
    // obj_hash — mirrors C compute_obj_hash (port/platform/parity.c:160)
    // -----------------------------------------------------------------------
    /// <summary>
    /// FNV-1a 64-bit hash over the owned objects in C linked-list order, folding
    /// (type &amp; 0xff) then (num &amp; 0xff) per object. Matches the C parity
    /// emitter's obj_hash field exactly (empty inventory → 0xcbf29ce484222325).
    /// </summary>
    public ulong ComputeObjHash() => ObjHash.Compute(HashObjs());

    private IEnumerable<(int type, int num)> HashObjs()
    {
        foreach (var kvp in _slots)
            yield return ((int)kvp.Key, kvp.Value.Num);
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

    // -----------------------------------------------------------------------
    // OBJS_MakeSpecial — OBJECTS.C:1315-1326
    // -----------------------------------------------------------------------
    /// <summary>
    /// Sets <see cref="EquippedSpecial"/> to <paramref name="type"/> iff the type
    /// is owned (IsEquip) AND flagged as a special weapon (SpecialW).
    /// Mirrors C OBJS_MakeSpecial: `if (p_objs[type] == NULL) return FALSE`.
    /// Returns true on success, false if the type is not owned or not a special.
    /// </summary>
    public bool MakeSpecial(ObjType type)
    {
        var lib = ObjLib.Of(type);
        if (!lib.SpecialW) return false;
        if (!IsEquip(type)) return false;
        EquippedSpecial = type;
        return true;
    }

    // -----------------------------------------------------------------------
    // OBJS_AddEnergy — OBJECTS.C:1272-1308
    // -----------------------------------------------------------------------
    /// <summary>
    /// Adds energy, mirroring OBJS_AddEnergy. PURE inventory math; no game-state
    /// gates. "Owned" == IsEquip (p_objs[type] != NULL in C).
    ///
    /// If energy is owned and below max: add the FULL <paramref name="amt"/>, clamp
    /// to MaxCnt. A dead (Num==0) energy slot is NOT revived. If energy is already at
    /// max, spill a QUARTER (amt &gt;&gt; 2) into an EXISTING super-shield (never creates
    /// one, never revives a 0-num super-shield), clamped to MaxCnt.
    /// Returns the resulting Num of the slot it touched, or 0 on the C early-returns.
    /// </summary>
    public int AddEnergy(int amt)
    {
        // C:1282-1283  if (!cur) return 0  — no energy slot owned → no-op.
        if (!IsEquip(ObjType.Energy))
            return 0;

        var energy = _slots[ObjType.Energy];
        int energyMax = ObjLib.Of(ObjType.Energy).MaxCnt;

        // C:1285  energy NOT full.
        if (energy.Num < energyMax)
        {
            // C:1289-1290  if (num == 0) return 0 — DEAD player is not revived.
            if (energy.Num == 0)
                return 0;

            // C:1292  add the FULL amt, clamp to max.
            energy.Num += amt;
            if (energy.Num > energyMax)
                energy.Num = energyMax;

            return energy.Num;
        }

        // C:1296-1305  energy AT max → spill a quarter into an existing super-shield.
        // C:1298-1299  if (!cur) return 0 — does NOT create a super-shield.
        if (!IsEquip(ObjType.SuperShield))
            return 0;

        var shield = _slots[ObjType.SuperShield];

        // C:1301-1302  if (num == 0) return 0 — does NOT revive a dead super-shield.
        if (shield.Num == 0)
            return 0;

        // C:1304  spill a QUARTER, clamp to super-shield's max.
        int shieldMax = ObjLib.Of(ObjType.SuperShield).MaxCnt;
        shield.Num += amt >> 2;
        if (shield.Num > shieldMax)
            shield.Num = shieldMax;

        return shield.Num;
    }

    // -----------------------------------------------------------------------
    // OBJS_SubEnergy — OBJECTS.C:1220-1266
    // -----------------------------------------------------------------------
    /// <summary>
    /// Subtracts energy, mirroring OBJS_SubEnergy's damage routing. PURE inventory
    /// math; "owned" == IsEquip (p_objs[type] != NULL in C).
    ///
    /// If a super-shield is owned, drain it; when it goes negative the slot is DELETED
    /// (via _slots.Remove) with NO spill-back to energy — exactly like
    /// C's OBJS_Del(S_SUPER_SHIELD). Otherwise drain energy, clamping at 0.
    /// Returns the resulting Num (0 once the super-shield slot is deleted, or on the
    /// no-slot early-return).
    ///
    /// DELIBERATELY OMITTED (out of scope, see Task 4.1): the C game-state gates
    /// `if (godmode) return 0`, `if (startendwave != EMPTY) return 0`, and the
    /// `curplr_diff == DIFF_0 && amt &gt; 1 → amt &gt;&gt;= 1` difficulty halving.
    /// These depend on game state the pure Inventory model has no access to and are
    /// not present in today's damage path; adding them would regress parity.
    /// </summary>
    public int SubEnergy(int amt)
    {
        // C:1238  if (cur)  — super-shield owned → drain it (no spill-back to energy).
        if (IsEquip(ObjType.SuperShield))
        {
            var shield = _slots[ObjType.SuperShield];
            shield.Num -= amt;

            // C:1248-1249  if (num < 0) OBJS_Del(S_SUPER_SHIELD).
            if (shield.Num < 0)
            {
                _slots.Remove(ObjType.SuperShield);
                return 0;
            }

            return shield.Num;
        }

        // C:1255-1256  else: no super-shield. if (!cur) return 0 — no energy → no-op.
        if (!IsEquip(ObjType.Energy))
            return 0;

        var energy = _slots[ObjType.Energy];
        energy.Num -= amt;

        // C:1263-1264  if (num < 0) num = 0 — clamp to 0.
        if (energy.Num < 0)
            energy.Num = 0;

        return energy.Num;
    }

    // -----------------------------------------------------------------------
    // OBJS_Use — OBJECTS.C:868-901 (INVENTORY part only)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Consumes one unit of <paramref name="type"/>, mirroring the inventory side of
    /// OBJS_Use. For a non-Forever weapon, decrements Num; when Num reaches 0 the slot
    /// is removed (OBJS_Remove + p_objs[type]=NUL) and, if THIS type was the equipped
    /// special (plr.sweapon == type), cycles to the next owned special via
    /// <see cref="GetNext"/>. Forever weapons are never consumed.
    ///
    /// DELIBERATELY OMITTED (out of scope):
    ///   • objuse_flag = TRUE; think_cnt = 0 — game-state side effects (shield-recharge
    ///     timing), NOT inventory, and not in today's path. Adding them would change timing.
    ///   • lib->actf(type) — the firing/detonation EFFECT is Task 5.4. Here we decrement
    ///     unconditionally on use (the actf-gating of the decrement is deferred to 5.4).
    ///   • OBJS_Equip(type) re-equip — a no-op in our one-slot model: we remove the whole
    ///     slot at 0 (no second node to re-equip), so it is correctly omitted.
    ///
    /// PARITY NOTE: MegaBomb (the only shipped non-Forever weapon) has SpecialW=false, so
    /// EquippedSpecial is never MegaBomb and the cycle-on-zero branch never fires for it —
    /// making Use(MegaBomb) a plain decrement-and-remove.
    /// </summary>
    public void Use(ObjType type)
    {
        if (!IsEquip(type)) return;                       // mirror !cur (p_objs[type]==NULL)
        var lib = ObjLib.Of(type);
        var slot = _slots[type];
        if (!lib.Forever)
            slot.Num--;                                   // (actf gating is Task 5.4; here we decrement on use)
        if (slot.Num <= 0 && !lib.Forever)
        {
            _slots.Remove(type);                          // OBJS_Remove + p_objs[type]=NUL
            if (EquippedSpecial == type)                  // if plr.sweapon == type
                GetNext();                                // cycle to next owned special
        }
    }

    // -----------------------------------------------------------------------
    // OBJS_GetCost — OBJECTS.C:1061-1078
    // -----------------------------------------------------------------------
    /// <summary>
    /// Returns the game cost of an object. Mirrors OBJS_GetCost:
    /// onlyflag items scale by start_cnt (cost * start_cnt); others use cost.
    ///
    /// Cost data lives in <see cref="ObjLib"/> (the model-side single source of
    /// truth — it already carries Cost/StartCnt/OnlyFlag), so Buy/Sell stay with
    /// the inventory model and no cost table is duplicated. (StoreLogic.Catalog is
    /// a pre-existing parallel table used only for browse rendering; it is left
    /// untouched and StoreLogic.Buy/Sell route cost math through here.)
    /// </summary>
    public int GetCost(ObjType type)
    {
        var lib = ObjLib.Of(type);
        return lib.OnlyFlag ? lib.Cost * lib.StartCnt : lib.Cost;
    }

    // -----------------------------------------------------------------------
    // OBJS_GetResale — OBJECTS.C:1084-1103
    // -----------------------------------------------------------------------
    /// <summary>
    /// Returns the resale value of an object. Mirrors OBJS_GetResale:
    /// 0 if not owned (p_objs[type]==NULL → !IsEquip); otherwise GetCost >> 1.
    /// </summary>
    public int GetResale(ObjType type)
    {
        if (!IsEquip(type)) return 0;     // C: if (!cur) return 0
        return GetCost(type) >> 1;
    }

    // -----------------------------------------------------------------------
    // OBJS_Buy — OBJECTS.C:959-983
    // -----------------------------------------------------------------------
    /// <summary>
    /// Buys an object, mirroring OBJS_Buy. Transacts against the live player
    /// <paramref name="score"/> (C's plr.score): the super-shield cap, the
    /// affordability guard, and the deduction-only-on-GotIt all mirror C exactly.
    ///
    /// Returns the BUYSTUFF result. <paramref name="score"/> is decremented by
    /// exactly GetCost(type) iff Add returned GotIt. The `score >= cost` guard
    /// runs before the deduction, so the uint subtraction can never underflow
    /// (costs fit in int and are non-negative).
    /// </summary>
    public BuyStuff Buy(ObjType type, ref uint score)
    {
        // C:967-972  super-shield cap: GetTotal(SuperShield) >= 5 → ShipFull.
        if (type == ObjType.SuperShield && GetTotal(ObjType.SuperShield) >= 5)
            return BuyStuff.ShipFull;

        // C:974  if (plr.score >= OBJS_GetCost(type)) ...; else default NoMoney.
        BuyStuff rval = BuyStuff.NoMoney;
        int cost = GetCost(type);
        if (score >= (uint)cost)
        {
            rval = Add(type);
            // C:978-979  only deduct when Add reported GotIt.
            if (rval == BuyStuff.GotIt)
                score -= (uint)cost;
        }
        return rval;
    }

    // -----------------------------------------------------------------------
    // OBJS_Sell — OBJECTS.C:906-954
    // -----------------------------------------------------------------------
    /// <summary>
    /// Sells an object, mirroring OBJS_Sell. Returns the amount left of the type.
    /// Adds GetResale (computed from the PRE-sell state, since resale is read
    /// before any slot mutation) to the live player <paramref name="score"/>.
    ///
    /// Branches mirror C exactly:
    ///   • not owned (!IsEquip)        → return 0, no score change.
    ///   • Detect                      → remove slot, return 0.
    ///   • onlyflag                    → num -= start_cnt; on &lt;= 0 clamp to 0 and,
    ///                                    if !forever, remove slot + cycle the equipped
    ///                                    special (OBJS_GetNext) when it was this type.
    ///                                    (Energy is onlyflag+forever: never removed.)
    ///   • non-onlyflag                → OBJS_Del (remove whole slot + GetNext if equipped),
    ///                                    return GetTotal (0 in the one-slot model).
    /// </summary>
    public int Sell(ObjType type, ref uint score)
    {
        // C:915-916  if (!cur) return 0 — not owned.
        if (!IsEquip(type))
            return 0;

        var lib = ObjLib.Of(type);
        int rval = 0;

        // C:918  resale added from PRE-sell state (slot still present here).
        score += (uint)GetResale(type);

        // C:920-924  Detect: drop the slot, return 0.
        if (type == ObjType.Detect)
        {
            _slots.Remove(type);
            return 0;
        }

        if (lib.OnlyFlag)
        {
            // C:928  cur->num -= lib->start_cnt.
            var slot = _slots[type];
            slot.Num -= lib.StartCnt;

            if (slot.Num <= 0)
            {
                // C:930-942  clamp to 0; if !forever, remove + re-cycle special.
                slot.Num = 0;
                rval = 0;
                if (!lib.Forever)
                {
                    _slots.Remove(type);                  // OBJS_Remove + p_objs[type]=NUL
                    // OBJS_Equip(type) here is a no-op in the one-slot model (slot gone).
                    if (EquippedSpecial == type)          // if (plr.sweapon == type)
                        GetNext();
                }
            }
            else
            {
                rval = slot.Num;
            }
        }
        else
        {
            // C:948-950  OBJS_Del removes the whole slot, re-cycles the equipped
            // special if it was this type, then return GetTotal (== 0 here).
            Del(type);
            rval = GetTotal(type);
        }

        return rval;
    }

    // -----------------------------------------------------------------------
    // OBJS_Del — OBJECTS.C:811-826
    // -----------------------------------------------------------------------
    /// <summary>
    /// Removes the slot for <paramref name="type"/> entirely, mirroring OBJS_Del:
    /// OBJS_Remove + p_objs[type]=NUL, then OBJS_Equip(type) (a no-op here — the
    /// slot is gone, so there is no second node to re-equip), then if this type
    /// was the equipped special (plr.sweapon == type), OBJS_GetNext to cycle.
    /// No-op when the type is not owned (C: cur == NUL).
    /// </summary>
    private void Del(ObjType type)
    {
        if (!IsEquip(type)) return;            // C: if (cur == NUL) return
        _slots.Remove(type);                   // OBJS_Remove + p_objs[type]=NUL
        // OBJS_Equip(type): no-op in one-slot model (no remaining node to equip).
        if (EquippedSpecial == type)           // if (type == plr.sweapon)
            GetNext();
    }

    // -----------------------------------------------------------------------
    // OBJS_LoseObj — OBJECTS.C:1311-1342 (Task 5.3)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Mirrors OBJS_LoseObj, the low-shield "system damage" object loss. DETERMINISTIC:
    /// no RNG (Phase-0 finding 0.5 corrected the old "RNG" task text).
    ///
    /// If a special weapon is equipped (plr.sweapon != EMPTY), <see cref="Del"/> it.
    /// Otherwise walk type LastObject-1 (23) down to 0 and Del the FIRST slot that is
    /// both owned (p_objs[type] != NULL → IsEquip) AND flagged loseit; then stop.
    ///
    /// C QUIRK (mirrored faithfully — do NOT "fix"): rval is initialised to TRUE and the
    /// no-special branch never resets it, so the method returns true even when the walk
    /// finds nothing to lose (nothing is removed). The caller treats the return as the
    /// "system damaged" HUD warning flag, not as "an object was lost".
    /// </summary>
    public bool LoseObj()
    {
        if (EquippedSpecial == null)
        {
            for (int t = (int)ObjType.LastObject - 1; t >= 0; t--)   // C: type = S_LAST_OBJECT-1 .. 0
            {
                var type = (ObjType)t;
                if (IsEquip(type) && ObjLib.Of(type).LoseIt)         // owned (p_objs!=NULL) && loseit
                {
                    Del(type);                                       // remove + cycle equipped special if it was equipped
                    return true;
                }
            }
            return true;   // C: rval stays TRUE even when nothing was found/lost (mirror faithfully)
        }

        Del(EquippedSpecial.Value);                                  // C else: OBJS_Del(plr.sweapon)
        return true;
    }

    // -----------------------------------------------------------------------
    // CopyFrom — Task 3.2
    // -----------------------------------------------------------------------
    /// <summary>
    /// Replaces all state in this instance with the state of <paramref name="other"/>.
    /// Clears this inventory first, then copies every slot (Num, InUse) and
    /// EquippedSpecial from <paramref name="other"/>.
    ///
    /// Preserves reference identity: callers holding a reference to this
    /// instance see the updated data without the field needing to be reassigned.
    /// Used by the pilot-load flow:
    ///   <c>WaveController.Inventory.CopyFrom(PilotSaveStore.LoadInventory(path));</c>
    /// </summary>
    public void CopyFrom(Inventory other)
    {
        System.ArgumentNullException.ThrowIfNull(other);
        _slots.Clear();
        foreach (var kvp in other._slots)
            _slots[kvp.Key] = new ObjSlot { Num = kvp.Value.Num, InUse = kvp.Value.InUse };
        EquippedSpecial = other.EquippedSpecial;
    }
}
