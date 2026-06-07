using System.Collections.Generic;
using System.Linq;

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
/// Per-pilot inventory. Mirrors the C OBJ linked list (first_objs..last_objs) and
/// the p_objs[] pointer array. Each owned unit is a real <see cref="ObjNode"/> in
/// insertion order: onlyflag items (Energy, MegaBomb) stack into ONE node whose Num
/// holds the count, while non-onlyflag items (SuperShield, OBJECTS.C:489) get a
/// FRESH node per buy. <c>p_objs[type]</c> is modelled as the first InUse node of a
/// type (<see cref="Equipped"/>). Insertion order is load-bearing for obj_hash parity.
///
/// No engine RNG / delta / _Process / wall-clock — sim-safe.
/// </summary>
public sealed class Inventory
{
    /// <summary>
    /// One OBJ node — mirrors the fields of C's OBJ struct that matter for
    /// ownership/quantity tracking: type, num (count) and inuse (equipped flag).
    /// Nested + private: nothing outside Inventory touches a node directly.
    /// </summary>
    private sealed class ObjNode
    {
        public ObjType Type;
        public int     Num;
        public bool    InUse;
    }

    // first_objs..last_objs: every owned unit is a node, in C insertion order.
    private readonly List<ObjNode> _objs = new();

    /// <summary>p_objs[type] equivalent: the first equipped (InUse) node of a type, or null.</summary>
    private ObjNode? Equipped(ObjType type)
    {
        foreach (var n in _objs)
            if (n.Type == type && n.InUse) return n;
        return null;
    }

    /// <summary>The first node of a type regardless of InUse (onlyflag stacking target / Equip target).</summary>
    private ObjNode? FirstOfType(ObjType type)
    {
        foreach (var n in _objs)
            if (n.Type == type) return n;
        return null;
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

        // C:755-759  moneyflag: credit score elsewhere; no node created.
        if (lib.MoneyFlag)
            return BuyStuff.GotIt;

        // C:761-765  !reg_flag && !game1flag → silently return GotIt.
        // We always run as "registered", so this branch is skipped (matches
        // the full-game code path, consistent with existing test data).

        // C:767-783  onlyflag: stack into the first existing node of this type; cap at max_cnt.
        if (lib.OnlyFlag)
        {
            var existing = FirstOfType(type);
            if (existing != null)
            {
                // C:773-774  if already at cap, return ShipFull.
                if (existing.Num >= lib.MaxCnt)
                    return BuyStuff.ShipFull;

                existing.Num += lib.StartCnt;
                if (existing.Num > lib.MaxCnt)
                    existing.Num = lib.MaxCnt;

                return BuyStuff.GotIt;
            }
            // No existing node: fall through to create a new one below.
        }

        // C:785-803  OBJS_Get → a fresh node appended at the tail (non-onlyflag types
        // like SuperShield therefore get one node per buy).
        var node = new ObjNode { Type = type, Num = lib.StartCnt, InUse = false };
        _objs.Add(node);

        // C:793-803  Equip if no node of this type is equipped yet (p_objs[type] == NUL).
        if (Equipped(type) == null)
        {
            node.InUse = true;

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
    /// Equips the first un-equipped node of a type, but only if no node of that type
    /// is currently equipped. Mirrors OBJS_Equip (the loop runs while p_objs[type]==NUL).
    /// Does NOT create a node. Returns true if a node was newly equipped.
    /// </summary>
    public bool Equip(ObjType type)
    {
        // C:695-703  if p_objs[type]==NUL, equip the first node of type.
        if (Equipped(type) != null) return false;
        var n = FirstOfType(type);
        if (n != null)
        {
            n.InUse = true;
            return true;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    // OBJS_IsEquip — OBJECTS.C:1202-1214
    // -----------------------------------------------------------------------
    /// <summary>
    /// Returns true if the type has an equipped node (p_objs[type] != NULL).
    /// </summary>
    public bool IsEquip(ObjType type) => Equipped(type) != null;

    // -----------------------------------------------------------------------
    // OBJS_GetAmt — OBJECTS.C:1007-1021
    // -----------------------------------------------------------------------
    /// <summary>
    /// Returns the Num of the EQUIPPED node for <paramref name="type"/> (C reads
    /// p_objs[type]->num). DEVIATION (preserved from the single-slot model): if the
    /// type is owned but un-equipped (e.g. a save loaded with inuse=false), report
    /// the first node's Num so save/load round-trips read the saved quantity. Returns
    /// 0 only when no node of the type exists.
    /// </summary>
    public int GetAmt(ObjType type)
    {
        var eq = Equipped(type);
        if (eq != null) return eq.Num;
        var first = FirstOfType(type);
        return first?.Num ?? 0;
    }

    // -----------------------------------------------------------------------
    // OBJS_GetTotal — OBJECTS.C:1062-1080
    // -----------------------------------------------------------------------
    /// <summary>
    /// Returns the NUMBER OF NODES of a type across the list (C OBJS_GetTotal:
    /// `for cur: if cur->type==type total++`). For onlyflag items (one stacked node)
    /// this is 1; for SuperShield it is the number of discrete shields. The
    /// SuperShield Buy cap (`GetTotal(SuperShield) >= 5`) counts shields, as in C.
    /// </summary>
    public int GetTotal(ObjType type) => _objs.Count(n => n.Type == type);

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
            if (Equipped(type) is { Num: > 0 } && ObjLib.Of(type).SpecialW)
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
    /// Rebuilds one inventory node from a saved OBJ record by APPENDING it at the
    /// tail (mirrors OBJS_Load → OBJS_Get). Replaying a save's node list therefore
    /// reconstructs multi-node types (e.g. two SuperShields) exactly. Sets Num and
    /// InUse from the record.
    ///
    /// Does NOT touch EquippedSpecial (sweapon). In C, RAP_LoadPlayer restores
    /// sweapon after the OBJS_Load loop via <c>if (!OBJS_IsEquip(plr.sweapon)) OBJS_GetNext()</c>.
    /// The caller (<see cref="PilotSaveStore.LoadInventory"/>) is responsible for that step.
    /// </summary>
    public void Load(ObjType type, int num, bool inuse)
    {
        _objs.Add(new ObjNode { Type = type, Num = num, InUse = inuse });
        // Note: EquippedSpecial deliberately not set here — mirrors C OBJS_Load.
    }

    // -----------------------------------------------------------------------
    // SetSingle — create-or-overwrite the single node of a type
    // -----------------------------------------------------------------------
    /// <summary>
    /// Creates or overwrites the SINGLE node of a type to (num, inuse) — used to
    /// "set the energy bar" (PlayerLogic.Reset / SetShield) without stacking nodes.
    /// Distinct from <see cref="Load"/> (which APPENDS per C OBJS_Load, for save
    /// replay where each saved record is a node): SetSingle replaces the existing
    /// node so a per-wave shield reset does not accumulate duplicate Energy nodes.
    /// Only valid for onlyflag/single-node types (Energy); never used for SuperShield.
    /// </summary>
    public void SetSingle(ObjType type, int num, bool inuse)
    {
        var existing = FirstOfType(type);
        if (existing != null)
        {
            existing.Num = num;
            existing.InUse = inuse;
        }
        else
        {
            _objs.Add(new ObjNode { Type = type, Num = num, InUse = inuse });
        }
    }

    // -----------------------------------------------------------------------
    // Read-only node enumerator — used by PilotSaveStore.Save to write OBJ records.
    // Keeps ObjNode private; exposes the minimal triple (type, num, inuse).
    // -----------------------------------------------------------------------
    /// <summary>
    /// Enumerates all owned nodes as (type, num, inuse) triples in list order.
    /// Used by <see cref="Raptor.Sim.PilotSaveStore"/> to persist the inventory.
    /// Multi-node types yield one triple per node.
    /// </summary>
    public IEnumerable<(ObjType type, int num, bool inuse)> Slots()
    {
        foreach (var n in _objs)
            yield return (n.Type, n.Num, n.InUse);
    }

    // -----------------------------------------------------------------------
    // obj_hash — mirrors C compute_obj_hash (port/platform/parity.c:160)
    // -----------------------------------------------------------------------
    /// <summary>
    /// FNV-1a 64-bit hash over the owned nodes in C linked-list order, folding
    /// (type &amp; 0xff) then (num &amp; 0xff) per node. Matches the C parity emitter's
    /// obj_hash field exactly (empty inventory → 0xcbf29ce484222325). Each node folds
    /// separately, so two SuperShields fold two (15, num) entries.
    /// </summary>
    public ulong ComputeObjHash() => ObjHash.Compute(HashObjs());

    private IEnumerable<(int type, int num)> HashObjs()
    {
        foreach (var n in _objs)
            yield return ((int)n.Type, n.Num);
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
    /// Removes all nodes and resets the equipped special weapon.
    /// Mirrors OBJS_Clear (memset of objs[] and p_objs[]).
    /// </summary>
    public void Clear()
    {
        _objs.Clear();
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
    /// gates. "Owned" == IsEquip (p_objs[type] != NULL in C). Operates on the
    /// equipped Energy / SuperShield nodes.
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
        var energy = Equipped(ObjType.Energy);
        if (energy == null)
            return 0;

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
        var shield = Equipped(ObjType.SuperShield);
        if (shield == null)
            return 0;

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
    /// If a super-shield is equipped, drain that node; when it goes negative the node
    /// is removed via <see cref="Del"/> which OBJS_Equip-promotes the NEXT SuperShield
    /// node (so a second shield takes over) — NO spill-back to energy. Otherwise drain
    /// the equipped energy node, clamping at 0. Returns the resulting Num (0 once the
    /// drained super-shield node is removed, or on the no-slot early-return; C's
    /// `return cur->num` after the delete is use-after-free, so 0 is the safe value).
    ///
    /// DELIBERATELY OMITTED (out of scope, see Task 4.1): the C game-state gates
    /// `if (godmode) return 0`, `if (startendwave != EMPTY) return 0`, and the
    /// `curplr_diff == DIFF_0 && amt &gt; 1 → amt &gt;&gt;= 1` difficulty halving.
    /// These depend on game state the pure Inventory model has no access to and live in
    /// WaveController.GateSubEnergyDamage on the damage path.
    /// </summary>
    public int SubEnergy(int amt)
    {
        // C:1238  if (cur)  — super-shield owned → drain the equipped node.
        var shield = Equipped(ObjType.SuperShield);
        if (shield != null)
        {
            shield.Num -= amt;

            // C:1248-1249  if (num < 0) OBJS_Del(S_SUPER_SHIELD) — remove this node,
            // OBJS_Equip promotes the next SuperShield node if one exists.
            if (shield.Num < 0)
            {
                Del(ObjType.SuperShield);
                return 0;
            }

            return shield.Num;
        }

        // C:1255-1256  else: no super-shield. if (!cur) return 0 — no energy → no-op.
        var energy = Equipped(ObjType.Energy);
        if (energy == null)
            return 0;

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
    /// OBJS_Use. For a non-Forever weapon, decrements the equipped node's Num; when it
    /// reaches 0 the node is removed (OBJS_Remove + p_objs[type]=NUL), the next node of
    /// the same type is OBJS_Equip-promoted, and if THIS type was the equipped special
    /// (plr.sweapon == type) and none remains, it cycles via <see cref="GetNext"/>.
    /// Forever weapons are never consumed.
    ///
    /// DELIBERATELY OMITTED (out of scope):
    ///   • objuse_flag = TRUE; think_cnt = 0 — game-state side effects (shield-recharge
    ///     timing), NOT inventory, and not in today's path. Adding them would change timing.
    ///   • lib->actf(type) — the firing/detonation EFFECT is Task 5.4. Here we decrement
    ///     unconditionally on use (the actf-gating of the decrement is deferred to 5.4).
    ///
    /// PARITY NOTE: MegaBomb (the only shipped non-Forever weapon) has SpecialW=false, so
    /// EquippedSpecial is never MegaBomb and the cycle-on-zero branch never fires for it —
    /// making Use(MegaBomb) a plain decrement-and-remove.
    /// </summary>
    public void Use(ObjType type)
    {
        var node = Equipped(type);
        if (node == null) return;                         // mirror !cur (p_objs[type]==NULL)
        var lib = ObjLib.Of(type);
        if (!lib.Forever)
            node.Num--;                                   // (actf gating is Task 5.4; here we decrement on use)
        if (node.Num <= 0 && !lib.Forever)
            Del(type);                                    // OBJS_Remove + OBJS_Equip next + cycle if special gone
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
        // GetTotal now counts NODES, so this is the C-faithful 5-shield cap.
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
    /// before any node mutation) to the live player <paramref name="score"/>.
    ///
    /// Branches mirror C exactly:
    ///   • not owned (!IsEquip)        → return 0, no score change.
    ///   • Detect                      → un-equip the node (p_objs[type]=NUL) but DO
    ///                                    NOT remove it — the node STAYS in the list
    ///                                    and still folds into obj_hash. return 0.
    ///   • onlyflag                    → num -= start_cnt; on &lt;= 0 clamp to 0 and,
    ///                                    if !forever, remove node + cycle the equipped
    ///                                    special (OBJS_GetNext) when it was this type.
    ///                                    (Energy is onlyflag+forever: never removed.)
    ///   • non-onlyflag                → OBJS_Del (remove the equipped node, promote the
    ///                                    next node of the type), return GetTotal.
    /// </summary>
    public int Sell(ObjType type, ref uint score)
    {
        // C:915-916  if (!cur) return 0 — not owned.
        var node = Equipped(type);
        if (node == null)
            return 0;

        var lib = ObjLib.Of(type);
        int rval = 0;

        // C:918  resale added from PRE-sell state (node still equipped here).
        score += (uint)GetResale(type);

        // C:920-924  Detect: un-equip (p_objs[type]=NUL) but keep the node. return 0.
        if (type == ObjType.Detect)
        {
            node.InUse = false;
            return 0;
        }

        if (lib.OnlyFlag)
        {
            // C:928  cur->num -= lib->start_cnt.
            node.Num -= lib.StartCnt;

            if (node.Num <= 0)
            {
                // C:930-942  clamp to 0; if !forever, remove + re-cycle special.
                node.Num = 0;
                rval = 0;
                if (!lib.Forever)
                    Del(type);
            }
            else
            {
                rval = node.Num;
            }
        }
        else
        {
            // C:948-950  OBJS_Del removes the equipped node (promoting the next node
            // of the type via OBJS_Equip), then return GetTotal (remaining nodes).
            Del(type);
            rval = GetTotal(type);
        }

        return rval;
    }

    // -----------------------------------------------------------------------
    // OBJS_Del — OBJECTS.C:811-826
    // -----------------------------------------------------------------------
    /// <summary>
    /// Removes the equipped node for <paramref name="type"/>, mirroring OBJS_Del:
    /// OBJS_Remove(p_objs[type]) + p_objs[type]=NUL + OBJS_Equip(type) — the latter
    /// promotes the next node of the SAME type if one remains (so a second SuperShield
    /// takes over). If the type was the equipped special and no node of it remains,
    /// cycle the special weapon via OBJS_GetNext. No-op when the type is not equipped.
    /// </summary>
    private void Del(ObjType type)
    {
        var eq = Equipped(type);
        if (eq == null) return;                // C: if (cur == NUL) return
        _objs.Remove(eq);                      // OBJS_Remove
        Equip(type);                           // OBJS_Equip(type): promote next node of this type
        // The cycle-to-next-special is a port concern: only when this special type is
        // now fully gone (no remaining node) and it was the equipped special.
        if (EquippedSpecial == type && Equipped(type) == null)
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
    /// Otherwise walk type LastObject-1 (23) down to 0 and Del the FIRST node that is
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
                    Del(type);                                       // remove + promote/cycle
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
    /// Clears this inventory first, then deep-copies every node (Type, Num, InUse) in
    /// order and EquippedSpecial from <paramref name="other"/>.
    ///
    /// Preserves reference identity: callers holding a reference to this
    /// instance see the updated data without the field needing to be reassigned.
    /// Used by the pilot-load flow:
    ///   <c>WaveController.Inventory.CopyFrom(PilotSaveStore.LoadInventory(path));</c>
    /// </summary>
    public void CopyFrom(Inventory other)
    {
        System.ArgumentNullException.ThrowIfNull(other);
        _objs.Clear();
        foreach (var n in other._objs)
            _objs.Add(new ObjNode { Type = n.Type, Num = n.Num, InUse = n.InUse });
        EquippedSpecial = other.EquippedSpecial;
    }
}
