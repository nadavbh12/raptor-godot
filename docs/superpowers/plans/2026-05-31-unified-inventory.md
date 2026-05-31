# Unified Per-Pilot Inventory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the port's fragmented weapon/object state with one `Inventory` model mirroring C's `OBJ`/`p_objs`/`plr.sweapon`, so a loaded pilot gets their saved weapons/specials/counts/equipped special, with full C inventory behavior (consumption, store transactions, shield spill, random-loss, energy-grab, megabomb detonation).

**Architecture:** A pure-C# `Inventory` (per-type `{Num, InUse}` slots + `EquippedSpecial`) driven by a static `ObjLib` flag table, mirroring `OBJECTS.C`. One `ObjType` enum replaces `WeaponType` + StoreLogic's `ObjType`. All consumers (new-pilot seed, save/load, store, gameplay, pickups, HUD) read/write the single `Inventory`. `PlayerLogic.Shield` becomes a view over the `Energy` slot with SUPER_SHIELD spill.

**Tech Stack:** C# (net8.0), xUnit (headless `DOTNET_ROLL_FORWARD=Major dotnet test` after `dotnet build raptor.csproj`), parity via `bash tests/run_l2a.sh <scenario>`.

**Spec:** `docs/superpowers/specs/2026-05-31-unified-inventory-design.md`

**Build/test env (prepend to every shell step):**
```bash
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH" DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec DOTNET_ROLL_FORWARD=Major
```

**Verified facts (do not re-derive):**
- On-disk: `PLAYEROBJ`=88B; `OBJ`=40B, offsets `num@16, type@20, inuse@32`; `prev`/`next`/`lib` are 8B pointers (garbage on disk, write zeros, ignore on load). MAX_SHIELD=100; ENERGY start_cnt=25 (3× on new pilot = 75).
- `obj_lib` flags (C OBJS_Init, OBJECTS.C:227-623), by `OBJ_TYPE`:

| type | name | cost | start | max | forever | only | special | money | loseit | game1 |
|---|---|---|---|---|---|---|---|---|---|---|
|0|ForwardGuns|12000|1|1|T|F|F|F|F|T|
|1|PlasmaGuns|78800|1|1|T|F|F|F|T|T|
|2|MicroMissile|175600|1|1|T|F|F|F|T|T|
|3|DumbMissile|145200|1|1|T|F|**T**|F|T|T|
|4|MiniGun|250650|1|1|T|F|**T**|F|T|T|
|5|Turret|512850|1|1|T|F|**T**|F|T|F|
|6|MissilePods|204950|1|1|T|F|**T**|F|T|T|
|7|AirMissile|63500|1|1|T|F|**T**|F|T|T|
|8|GrdMissile|110000|1|1|T|F|**T**|F|T|T|
|9|Bomb|98200|1|1|T|F|**T**|F|T|F|
|10|EnergyGrab|300750|1|1|T|F|**T**|F|T|F|
|11|MegaBomb|32250|1|**5**|**F**|**T**|F|F|T|T|
|12|PulseCannon|725000|1|1|T|F|**T**|F|T|T|
|13|ForwardLaser|1750000|1|1|T|F|**T**|F|T|F|
|14|DeathRay|950000|1|1|T|F|**T**|F|T|F|
|15|SuperShield|78500|100|100|**F**|F|F|F|F|T|
|16|Energy|400|25|100|T|**T**|F|F|F|T|
|17|Detect|10000|1|1|**F**|**T**|F|F|F|T|
|18|ItemBuy1|93800|cost|cost|F|F|F|**T**|T|T|
|19|ItemBuy2|76000|cost|cost|F|F|F|**T**|T|T|
|20|ItemBuy3|55700|cost|cost|F|F|F|**T**|T|T|
|21|ItemBuy4|35200|cost|cost|F|F|F|**T**|T|T|
|22|ItemBuy5|122500|cost|cost|F|F|F|**T**|T|T|
|23|ItemBuy6|50|cost|cost|F|F|F|**T**|T|T|

**Key correction:** the ONLY weapon consumed on fire is MegaBomb (`forever=FALSE`). All other specials (3-14) are `forever=TRUE` — owned/equipped but never depleted by firing. "Consume-on-fire" scope = MegaBomb only.

---

## Phase 0 — Verification tracers (front-loaded; resolve spec's UNVERIFIED items)

These read C / inspect goldens and record findings as comments in the plan or a scratch note. No production code. Each must complete before the task that depends on it.

> **PHASE 0 RESOLVED (2026-05-31). Findings (corrected several mapping-agent errors):**
> - **0.1 (gates 4.2):** NO parity scenario picks up SUPER_SHIELD or ENERGY (zero shield jumps >+1 across all 12; max shields 75–100 are pure +1 recharge). ⇒ The Energy-slot view + spill reduces to current shield math in the suite; Phase 4 is parity-safe if math is preserved.
> - **0.2 (4.x/5.2):** `OBJS_GetCost` = `onlyflag ? cost*start_cnt : cost`. `OBJS_GetResale` = same base `>>1` (half), 0 if unowned. `OBJS_Buy`: super-shield cap `GetTotal(SuperShield)>=5 → SHIPFULL`; `if score>=cost { Add; if GotIt score-=cost }`. `OBJS_Sell`: 0 if unowned; `score+=resale`; DETECT→just clear; onlyflag→`num-=start_cnt`, if `<=0` and `!forever` Remove+Equip+(if sweapon)GetNext; else `Del`+return GetTotal.
> - **0.3 (1.4):** `OBJS_GetNext`: `pos = sweapon<3 ? 3 : sweapon+1`; loop FIRST_SPECIAL(3)..LAST_WEAPON(14), wrap `pos>14→3`; pick first `p_objs[pos] && num && specialw`; else EMPTY.
> - **0.4 (4.1):** `OBJS_SubEnergy(amt)`: godmode→0; `startendwave!=EMPTY`→0; `DIFF_0 && amt>1 → amt>>=1`; if super-shield owned: `num-=amt`, if `<0` Del super-shield (NO spill-back); else energy `num-=amt` clamp 0. `OBJS_AddEnergy(amt)`: if energy owned and `num<max`: if `num==0` return 0 (dead, no recharge), else `num+=amt` clamp max; ELSE (energy full) super-shield `num += amt>>2` clamp max.
> - **0.5 (5.3):** `OBJS_LoseObj` is **DETERMINISTIC, NOT random** (agent was wrong): if `sweapon==EMPTY`, walk `type` from `S_LAST_OBJECT-1` down, first owned `&& loseit` → `Del`, break; else `Del(sweapon)`. ⇒ Task 5.3 needs NO RNG; its test asserts the deterministic choice.
> - **0.6 (5.4):** MegaBomb detonation (SHOTS.C:1267, on reach of move.x2=160,y2=75): `ESHOT_Clear()` (clear ALL enemy bullets) + `TILE_DamageAll()` + `for each enemy: hits -= lib->hits` + `startfadeflag=TRUE` + `A_SUPER_SHIELD` anim + remove shot. Energy-grab (SHOTS.C:944): fires an upward bullet (move.x2=x, y2=0) that interacts with enemies via `A_ENERGY_GRAB` (SHOTS.C:1167) — full siphon semantics to be read in Task 5.4 before implementing; treat as lower priority and document-then-skip if not cleanly scoped.
> - **CORRECTION (all phases):** only MegaBomb (type 11) is consumed on fire; every weapon 0–14 except MegaBomb is `forever=TRUE`. SUPER_SHIELD is `onlyflag=FALSE` (C uses multiple entries up to 5); the port models it as one slot with `Num` = count (behaviorally equivalent for GetTotal-cap/AddEnergy/SubEnergy).

### Task 0.1: Confirm no parity scenario picks up SUPER_SHIELD (gates Phase 4)
**Files:** none (inspection). 
- [ ] Inspect the 12 goldens (`tests/parity/scripts/{death_wave1,2,4,5,6,7,8,9,mission_start,mission_long,full_demo}.parity.txt` — note menu_demo has no gameplay). For each, check whether `shield` ever increases by more than the +1 recharge step or jumps toward a max beyond the Energy cap (a super-shield pickup signature).
- [ ] Record the finding in this task's checkbox note. **If any scenario picks up super-shield**, Phase 4 must reproduce C's spill exactly and is higher-risk; **if none do**, Phase 4's shield-view change is provably parity-neutral for the suite.
- [ ] Expected: most/all are pure-Energy. Document which (if any) involve super-shield.

### Task 0.2: Capture OBJS_GetCost / OBJS_GetResale formulas
**Files:** none (read `dosraptor/SOURCE/OBJECTS.C` OBJS_GetCost, OBJS_GetResale, OBJS_Buy:956-983, OBJS_Sell:903-954).
- [ ] Record verbatim: buy cost (onlyflag scales by start_cnt), resale value formula, and exactly what Buy/Sell mutate (score delta, num delta, removal rule). Note SUPER_SHIELD hard cap of 5 (OBJECTS.C:969-971).

### Task 0.3: Capture OBJS_GetNext wrap boundaries
**Files:** none (read OBJS_GetNext, OBJECTS.C:829-865; find FIRST_SPECIAL / LAST_WEAPON defines).
- [ ] Record: start index, loop range, wrap, selection predicate (`p_objs[t] && num>0 && specialw`), EMPTY fallback.

### Task 0.4: Capture OBJS_AddEnergy / OBJS_SubEnergy spill
**Files:** none (read OBJECTS.C:1217-1308).
- [ ] Record exact order: SubEnergy drains SuperShield then Energy; AddEnergy fills Energy to max then spills to SuperShield. Note clamping and the `num<=0` removal of SuperShield.

### Task 0.5: Capture OBJS_LoseObj random-loss
**Files:** none (read OBJECTS.C:1310-1342).
- [ ] Record: when called, RNG draw used (must use the per-wave `RandomNumberGenerator`, NOT engine RNG — sim rule), selection among `loseit` items, interaction with `plr.sweapon`.

### Task 0.6: Capture energy-grab actf + megabomb detonation
**Files:** none (read S_ENERGY_GRAB `actf` and the megabomb reach effect in `dosraptor/SOURCE/SHOTS.C`).
- [ ] Record energy-grab's actual effect (siphon? what/how much). Record megabomb on-reach effect (clear enemy bullets? damage all enemies? how much). These are firing EFFECTS enabled by inventory ownership.

---

## Phase 1 — Core model (ObjType, ObjLib, Inventory)

### Task 1.1: `ObjType` enum (unify)
**Files:**
- Create: `src/Sim/ObjType.cs`
- Modify (follow-up rename in later tasks): `src/Sim/Shots/WeaponType.cs` (kept as `[Obsolete]` alias initially, removed in Task 6.1)

- [ ] **Step 1: Write the failing test** — `tests/InventoryTests.cs` (new):
```csharp
using Raptor.Sim;
using Xunit;
namespace Raptor.Tests;
public class ObjTypeTests {
    [Fact] public void ObjType_values_match_C_OBJ_TYPE() {
        Assert.Equal(0, (int)ObjType.ForwardGuns);
        Assert.Equal(11, (int)ObjType.MegaBomb);
        Assert.Equal(15, (int)ObjType.SuperShield);
        Assert.Equal(16, (int)ObjType.Energy);
        Assert.Equal(17, (int)ObjType.Detect);
        Assert.Equal(23, (int)ObjType.ItemBuy6);
    }
}
```
- [ ] **Step 2: Run, expect FAIL** (ObjType undefined): `cd tests && dotnet test --filter ObjTypeTests`
- [ ] **Step 3: Implement** `src/Sim/ObjType.cs` — enum 0..23 + `LastObject=25`, members per the flag table (ForwardGuns..DeathRay 0-14, SuperShield 15, Energy 16, Detect 17, ItemBuy1..6 18-23). XML-doc each with its C `S_*` name.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `git add src/Sim/ObjType.cs tests/InventoryTests.cs && git commit -m "Inventory: unified ObjType enum (mirrors C OBJ_TYPE)"`

### Task 1.2: `ObjLib` static metadata table
**Files:** Create `src/Sim/ObjLib.cs`; Test in `tests/InventoryTests.cs`.
- [ ] **Step 1: Failing test** — assert flags for a representative set:
```csharp
public class ObjLibTests {
    [Theory]
    [InlineData(ObjType.ForwardGuns, true, false, false, 12000, 1, 1)]
    [InlineData(ObjType.DumbMissile, true, false, true, 145200, 1, 1)]
    [InlineData(ObjType.MegaBomb, false, true, false, 32250, 1, 5)]
    [InlineData(ObjType.Energy, true, true, false, 400, 25, 100)]
    [InlineData(ObjType.SuperShield, false, false, false, 78500, 100, 100)]
    public void Flags_match_C_obj_lib(ObjType t, bool forever, bool only, bool special, int cost, int start, int max) {
        var e = ObjLib.Of(t);
        Assert.Equal(forever, e.Forever); Assert.Equal(only, e.OnlyFlag);
        Assert.Equal(special, e.SpecialW); Assert.Equal(cost, e.Cost);
        Assert.Equal(start, e.StartCnt); Assert.Equal(max, e.MaxCnt);
    }
}
```
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** `ObjLib` with `readonly record struct Entry(int Cost,int StartCnt,int MaxCnt,bool Forever,bool OnlyFlag,bool SpecialW,bool MoneyFlag,bool LoseIt,bool Game1Flag)` and a `static Entry Of(ObjType)` backed by an array indexed by `(int)type`, populated EXACTLY from the flag table above (MAX_SHIELD=100). Include FIRST_SPECIAL=DumbMissile(3), LAST_WEAPON=DeathRay(14) consts.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `git commit -am "Inventory: ObjLib metadata table from C OBJS_Init"`

### Task 1.3: `Inventory` core — Add / Equip / IsEquip / GetAmt / GetTotal / Clear
**Files:** Create `src/Sim/Inventory.cs`; test in `tests/InventoryTests.cs`.
**C ref:** OBJS_Add:734-806, OBJS_Equip:688-706, OBJS_GetAmt:1007, OBJS_GetTotal:1023, OBJS_IsEquip:1202, OBJS_Clear:77-99.
- [ ] **Step 1: Failing tests:**
```csharp
public class InventoryCoreTests {
    [Fact] public void Add_new_weapon_auto_equips_and_is_owned() {
        var inv = new Inventory();
        Assert.Equal(BuyStuff.GotIt, inv.Add(ObjType.MiniGun));
        Assert.True(inv.IsEquip(ObjType.MiniGun));
        Assert.Equal(1, inv.GetAmt(ObjType.MiniGun));
    }
    [Fact] public void Add_onlyflag_stacks_capped_at_max() {
        var inv = new Inventory();
        for (int i=0;i<10;i++) inv.Add(ObjType.MegaBomb);   // max_cnt=5
        Assert.Equal(5, inv.GetAmt(ObjType.MegaBomb));
    }
    [Fact] public void Add_moneyflag_returns_gotit_without_slot() {
        var inv = new Inventory();
        inv.Add(ObjType.ItemBuy1);
        Assert.False(inv.IsEquip(ObjType.ItemBuy1));   // money bonus: no slot
    }
    [Fact] public void Clear_empties_all() {
        var inv = new Inventory(); inv.Add(ObjType.MiniGun); inv.Clear();
        Assert.False(inv.IsEquip(ObjType.MiniGun));
    }
}
```
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** `Inventory`: `Dictionary<ObjType,ObjSlot> _slots` (`ObjSlot{int Num;bool InUse;}`), `ObjType? EquippedSpecial`. `enum BuyStuff{GotIt,NoMoney,ShipFull,Error}`. Implement Add (moneyflag→no slot return GotIt; onlyflag→stack capped at MaxCnt; weapon→slot Num=StartCnt; auto-equip when first of type i.e. slot created; auto-set EquippedSpecial if SpecialW && EquippedSpecial==null), Equip, IsEquip (slot exists), GetAmt (slot.Num or 0), GetTotal (== GetAmt under one-slot model), Clear. Mirror C exactly per the cited refs.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `git commit -am "Inventory: core Add/Equip/query/Clear (mirrors OBJS_*)"`

### Task 1.4: `Inventory.GetNext` (cycle special)
**Files:** `src/Sim/Inventory.cs`; test in `tests/InventoryTests.cs`. **Use the formula captured in Task 0.3.**
- [ ] **Step 1: Failing test:**
```csharp
[Fact] public void GetNext_cycles_owned_specials_and_wraps() {
    var inv = new Inventory();
    inv.Add(ObjType.DumbMissile); inv.Add(ObjType.DeathRay);  // both specialw
    inv.EquippedSpecial = ObjType.DumbMissile;
    inv.GetNext();
    Assert.Equal(ObjType.DeathRay, inv.EquippedSpecial);
    inv.GetNext();                                            // wrap
    Assert.Equal(ObjType.DumbMissile, inv.EquippedSpecial);
}
[Fact] public void GetNext_sets_empty_when_no_specials_owned() {
    var inv = new Inventory(); inv.Add(ObjType.ForwardGuns);  // not specialw
    inv.GetNext(); Assert.Null(inv.EquippedSpecial);
}
```
- [ ] **Step 2: Run, expect FAIL.** **Step 3: Implement** per Task 0.3 finding (range FIRST_SPECIAL..LAST_WEAPON, predicate owned && SpecialW, wrap, EMPTY fallback). **Step 4: PASS. Step 5: Commit.**

---

## Phase 2 — Save / load round-trip

### Task 2.1: Extend the save fixture helper to write OBJ records
**Files:** Modify `tests/PilotSaveStoreTests.cs` (`WriteFakePilot`).
- [ ] **Step 1:** Add an overload `WriteFakePilot(..., (ObjType type,int num,bool inuse)[] objs)` that writes `numobjs` at header offset 60, then appends `objs.Length` 40-byte records (`num@16, type@20, inuse@32`, other bytes 0), CASTLE-XOR-encrypting **each 40-byte record separately** (reuse the existing `Encrypt` walk per record), after the encrypted header. (Existing no-inventory callers keep working via a default empty array.)
- [ ] **Step 2:** Add a self-test asserting the file length == 88 + 40*objs.Length. Run, expect PASS.
- [ ] **Step 3: Commit** `git commit -am "tests: WriteFakePilot can append encrypted OBJ records"`

### Task 2.2: Parse the OBJ array on load → `Inventory`
**Files:** Modify `src/Sim/PilotSaveStore.cs`; add `Inventory` to the loaded result (extend `PilotSaveSummary` or add a parallel `LoadInventory`). Test in `tests/PilotSaveStoreTests.cs`.
**C ref:** RAP_LoadPlayer LOADSAVE.C:260-282, OBJS_Load:708-732.
- [ ] **Step 1: Failing round-trip test** (this realizes the **Round-trip identity** property):
```csharp
[Fact]
public void Loaded_inventory_round_trips_objs() {
    using var tmp = new TempDir();
    WriteFakePilot(tmp.Path, slot:0, name:"A", callsign:"B", idPic:0, score:5000u,
        sweapon:(int)ObjType.MiniGun, curGame:0, gameWave:new[]{0,0,0}, diff:new[]{1,1,1,1},
        trainFlag:false, finTrain:false,
        objs:new[]{ (ObjType.ForwardGuns,1,true), (ObjType.MiniGun,1,true), (ObjType.MegaBomb,3,false), (ObjType.Energy,75,false) });
    var inv = PilotSaveStore.LoadInventory(Path.Combine(tmp.Path,"CHAR0000.FIL"));
    Assert.True(inv.IsEquip(ObjType.ForwardGuns));
    Assert.Equal(3, inv.GetAmt(ObjType.MegaBomb));
    Assert.Equal(75, inv.GetAmt(ObjType.Energy));
    Assert.Equal(ObjType.MiniGun, inv.EquippedSpecial);   // sweapon applied + valid
}
```
Property test (round-trip identity, `@given`-equivalent via xUnit `[Theory]` + a small generated set since C# lacks Hypothesis; use `tests/InventoryGen.cs` shared generator producing random valid `(type,num,inuse)[]`):
```csharp
[Theory] [MemberData(nameof(InventoryGen.RandomInventories), 50)]
public void Save_then_load_is_identity(IReadOnlyList<(ObjType,int,bool)> objs, int sweapon) {
    using var tmp = new TempDir();
    WriteFakePilot(tmp.Path,0,"A","B",0,0u,sweapon,0,new[]{0,0,0},new[]{1,1,1,1},false,false, objs.ToArray());
    var inv = PilotSaveStore.LoadInventory(Path.Combine(tmp.Path,"CHAR0000.FIL"));
    foreach (var (t,n,_) in objs) Assert.Equal(n, inv.GetAmt(t));
}
```
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement** in `PilotSaveStore`: after the 88-byte header decrypt, read `numobjs` (offset 60), then loop `numobjs` × {read 40 bytes, CASTLE-XOR decrypt that record, extract `num@16`(int32 LE), `type@20`(int32 LE), `inuse@32`(int32 LE != 0)}, bound-check `type` (skip if out of `ObjType` range), `inv.Load(type,num,inuse)`. After the loop, if `sweapon` not owned → `inv.GetNext()`. Add `Inventory.Load(ObjType,int,bool)` (mirrors OBJS_Load: create slot, set p_objs/EquippedSpecial if inuse). Handle truncation gracefully (stop on short read).
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `git commit -am "PilotSaveStore: parse OBJ inventory array on load"`

### Task 2.3: Write the OBJ array on save
**Files:** Modify the save path (`PilotSaveStore` save / wherever RAP_SavePlayer is mirrored). **C ref:** RAP_SavePlayer LOADSAVE.C:333-347.
- [ ] **Step 1: Failing test** — save an `Inventory`, reload, assert identity (reuse the round-trip test against the real save path, not just the fixture).
- [ ] **Step 2: Run, expect FAIL. Step 3: Implement** write: set header `numobjs`=slot count; per slot write a 40-byte record (`num@16,type@20,inuse@32`, rest 0), CASTLE-XOR-encrypt each record, append after encrypted header. **Step 4: PASS. Step 5: Commit.**

---

## Phase 3 — Wire consumers to the single Inventory

### Task 3.1: New-pilot seed
**Files:** Modify `src/Sim/WaveController.cs` (`OnPilotCreated`). **C ref:** WINDOWS.C:988-1007.
- [ ] **Step 1: Failing test** (`tests/WaveControllerTests.cs` via a pure helper if WaveController can't be constructed headless — extract `Inventory.SeedNewPilot()`):
```csharp
[Fact] public void New_pilot_seed_matches_C() {
    var inv = new Inventory(); inv.SeedNewPilot();
    Assert.True(inv.IsEquip(ObjType.ForwardGuns));
    Assert.Equal(75, inv.GetAmt(ObjType.Energy));
    Assert.Null(inv.EquippedSpecial);   // GetNext with no specials → none
}
```
This realizes the **New-pilot seed determinism** property (add a `[Fact]` asserting exact equality of the whole slot set + score handled by caller).
- [ ] **Step 2: FAIL. Step 3: Implement** `Inventory.SeedNewPilot()` = Add(ForwardGuns) + 3×Add(Energy) + GetNext(); call it from `OnPilotCreated` (score=10000 as today). **Step 4: PASS. Step 5: Commit.**

### Task 3.2: Apply loaded inventory on pilot load
**Files:** Modify `src/Sim/MenuController.cs:33` / the `OnPilotLoaded` flow + `PilotSaveStore` to surface the `Inventory`.
- [ ] **Step 1: Failing test** — loading a pilot sets the WaveController/PlayerShooter inventory to the saved one (test via the controller seam used by `MenuController`).
- [ ] **Step 2: FAIL. Step 3: Implement** `OnPilotLoaded` → set the live `Inventory` from `PilotSaveStore.LoadInventory`, apply score (existing). **Step 4: PASS. Step 5: Commit.**

### Task 3.3: Migrate PlayerShooter to query Inventory
**Files:** Modify `src/Sim/Shots/PlayerShooter.cs`. Replace `_ownedSpecials`/`HasPlasmaGuns`/`HasMicroMissile`/`MegaBombCount`/`SpecialWeapon` with delegations to an injected `Inventory` (owned = `IsEquip`, equipped = `EquippedSpecial`, megabomb count = `GetAmt(MegaBomb)`). Keep cooldown timers local.
- [ ] **Step 1:** Update `PlayerShooterTests` to construct with an `Inventory` and assert owned/equipped/fire-gating read from it. **Step 2: FAIL. Step 3: Implement** the delegation; `GrantWeapon`→`Inventory.Add`, `SelectSpecial`→`MakeSpecial` (sets EquippedSpecial if owned+SpecialW), `CycleSpecial`→`GetNext`. **Step 4: PASS (all PlayerShooterTests). Step 5: Commit.**

### Task 3.4: Migrate BonusEffectDispatcher pickups
**Files:** Modify `src/Sim/Bonus/BonusEffectDispatcher.cs`.
- [ ] **Step 1:** Update `BonusTests`/dispatcher tests: a weapon/special pickup calls `Inventory.Add` (auto-equip + auto-special); money pickups (18-23) add score; energy/super-shield route to AddEnergy (Phase 4). **Step 2: FAIL. Step 3: Implement** `Apply` to use `Inventory.Add` for types 0-14,17 and `AddEnergy` for 15-16 (stub to PlayerLogic until Phase 4), money for 18-23. **Step 4: PASS. Step 5: Commit.**

### Task 3.5: Migrate StoreLogic OwnedCount + remove StarterInventory
**Files:** Modify `src/Sim/StoreLogic.cs`. Replace static `StarterInventory`/`OwnedCount` with `Inventory.GetAmt`. (Buy/Sell mutations land in Phase 5.2.)
- [ ] **Step 1:** Update store tests: `OwnedCount` reflects the live `Inventory`. **Step 2: FAIL. Step 3: Implement** delegation; delete `StarterInventory`. **Step 4: PASS. Step 5: Commit.**

### Task 3.6: HUD reads Inventory
**Files:** Modify `src/View/HudWeaponIcon.cs` + any special/count HUD consumer to read `Inventory` (equipped special icon, megabomb count).
- [ ] **Step 1:** HUD test (or `HudWeaponIconTests`) asserts icon/count from `Inventory`. **Step 2: FAIL. Step 3: Implement. Step 4: PASS. Step 5: Commit.**

---

## Phase 4 — Shield via inventory (GATED on parity; needs Task 0.1 + 0.4)

### Task 4.1: SUPER_SHIELD/ENERGY spill in Inventory
**Files:** `src/Sim/Inventory.cs`. **Use Task 0.4 finding.**
- [ ] **Step 1: Failing tests** (realizes **Energy bounds** property):
```csharp
[Fact] public void SubEnergy_drains_supershield_before_energy() {
    var inv=new Inventory(); inv.Load(ObjType.Energy,100,true); inv.Load(ObjType.SuperShield,20,false);
    inv.SubEnergy(15); Assert.Equal(5,inv.GetAmt(ObjType.SuperShield)); Assert.Equal(100,inv.GetAmt(ObjType.Energy));
}
[Fact] public void AddEnergy_spills_to_supershield_when_energy_full() {
    var inv=new Inventory(); inv.Load(ObjType.Energy,95,true);
    inv.AddEnergy(10); Assert.Equal(100,inv.GetAmt(ObjType.Energy)); Assert.Equal(5,inv.GetAmt(ObjType.SuperShield));
}
```
Property `@Theory` over random amounts asserting `Energy.Num∈[0,100]` always. 
- [ ] **Step 2: FAIL. Step 3: Implement** AddEnergy/SubEnergy per Task 0.4 (spill order, clamps, remove SuperShield at 0). **Step 4: PASS. Step 5: Commit.**

### Task 4.2: PlayerLogic.Shield as Energy view + route damage/heal/recharge
**Files:** Modify `src/Sim/Player/PlayerLogic.cs`, `src/Sim/WaveController.cs` (damage/heal/recharge call sites).
- [ ] **Step 1: Failing test** — `PlayerLogic.Shield` reads `Inventory.GetAmt(Energy)`; `TakeDamage`→`SubEnergy`; `Heal`/recharge→`AddEnergy`.
- [ ] **Step 2: FAIL. Step 3: Implement** the view + reroute. Keep `MaxShield`=100. Recharge (`ShieldRechargeStep`→`AddEnergy(1)`).
- [ ] **Step 4: Run full unit suite, expect PASS.**
- [ ] **Step 5: PARITY GATE — run all 12 scenarios; ALL must stay green:**
```bash
for s in death_wave1 death_wave2 death_wave4 death_wave5 death_wave6 death_wave7 death_wave8 death_wave9 mission_start mission_long full_demo menu_demo; do bash tests/run_l2a.sh $s 2>&1 | grep -E "passed:|FAIL"; done
```
Expected: 8 death_waves in-game exact, mission_start 100%, mission_long 99.1%, full_demo 100%, menu_demo 100%. **If any regress, STOP — diagnose against Task 0.1 finding before proceeding.**
- [ ] **Step 6: Commit** only if the gate is green.

---

## Phase 5 — Live behaviors (each: read C ref, failing test, implement, commit)

### Task 5.1: MegaBomb consume-on-fire + auto-cycle
**Files:** `src/Sim/Inventory.cs` (`Use`), `src/Sim/Shots/PlayerShooter.cs` (call `Use` when megabomb fires). **C ref:** OBJS_Use:868-901.
- [ ] **Step 1: Failing test** (realizes **Consumption** property): firing megabomb decrements `GetAmt(MegaBomb)`; at 0 the slot is removed and `EquippedSpecial` cycles; `forever` weapons never decrement.
- [ ] **Step 2: FAIL. Step 3: Implement** `Inventory.Use(type)`: if `!Forever` decrement Num; if `<=0` remove slot + (if was EquippedSpecial) `GetNext`. Wire megabomb fire → `Use`. **Step 4: PASS. Step 5: Commit.**

### Task 5.2: Store Buy/Sell money + counts
**Files:** `src/Sim/StoreLogic.cs`, `src/Sim/Inventory.cs` (`Buy`/`Sell`). **Use Task 0.2 finding.**
- [ ] **Step 1: Failing test** (realizes **Money conservation** property): Buy deducts exact cost + adds inventory; Sell refunds resale + removes; insufficient funds → no change.
- [ ] **Step 2: FAIL. Step 3: Implement** `Inventory.Buy/Sell` per Task 0.2 (cost, onlyflag×start_cnt, resale, SUPER_SHIELD cap 5); wire StoreLogic buy/sell keys to them with the score. **Step 4: PASS. Step 5: Commit.**

### Task 5.3: LoseObj random-loss on low shield
**Files:** `src/Sim/Inventory.cs` (`LoseObj`), `src/Sim/WaveController.cs` (call on low-shield, replacing `LoseCurrentSpecialForShieldLow`). **Use Task 0.5 finding. RNG = per-wave `RandomNumberGenerator` (sim rule — no engine RNG).**
- [ ] **Step 1: Failing test** (realizes **Loseit domain** property): only `LoseIt` types can be lost; pass a seeded RNG and assert deterministic choice; non-loseit items never chosen.
- [ ] **Step 2: FAIL. Step 3: Implement** per Task 0.5 (sweapon-first then random loseit). **Step 4: PASS + parity gate (shield-low path appears in death_waves) — re-run 8 death_waves. Step 5: Commit.**

### Task 5.4: Energy-grab + megabomb detonation effects
**Files:** `src/Sim/WaveController.cs` / `src/Sim/Shots/*`. **Use Task 0.6 finding.** Only implement if Task 0.6 confirms the effect exists and is in scope; otherwise record as a documented follow-up and skip.
- [ ] **Step 1: Failing test** per the captured C effect (e.g. megabomb-on-reach clears enemy bullets + damages enemies). **Step 2: FAIL. Step 3: Implement mirroring C. Step 4: PASS + parity gate. Step 5: Commit.**

---

## Phase 6 — Cleanup + final gate

### Task 6.1: Remove obsolete fragmented state + WeaponType alias
**Files:** Delete `WeaponType.cs` (or fold into ObjType); remove the dead `_ownedSpecials`/`MegaBombCount`/`StarterInventory`/`HasPlasmaGuns`/`HasMicroMissile` once all consumers use `Inventory`.
- [ ] **Step 1:** Compile + full unit suite. **Step 2:** Fix references. **Step 3: Commit.**

### Task 6.2: Final verification
- [ ] Full unit suite green (`cd tests && dotnet test`).
- [ ] All 12 parity scenarios at their baselines (Phase 4 Step 5 command).
- [ ] `lint_sim.sh` passes (no engine RNG / delta / wall-clock introduced in `src/Sim`).
- [ ] Update `tests/parity/scenarios/coverage.md` (LOADSAVE.C row → inventory load implemented) + state.md.
- [ ] **Commit.**

---

## Self-review

**Spec coverage:** ObjType unify (1.1), ObjLib (1.2), Inventory model+API (1.3-1.4, 4.1, 5.1-5.3), save/load (2.1-2.3), seed (3.1), load-apply (3.2), consumers (3.3-3.6), shield-via-inventory (4.x), behaviors (5.x), enum cleanup (6.1). All spec sections mapped.

**Properties → tests:** Round-trip identity → 2.2; Ownership⇔slot → 1.3; Energy bounds → 4.1; New-pilot determinism → 3.1; Consumption → 5.1; Money conservation → 5.2; Loseit domain → 5.3. All 7 covered (C# uses `[Theory]`+shared `InventoryGen` generator in lieu of Hypothesis, max 50 cases).

**External-dependency tracer:** the save-file format (CASTLE-XOR + 40-byte records) is exercised un-mocked by the real-file round-trip tests in 2.2/2.3 (write real bytes to a TempDir, read them back through production `PilotSaveStore`) — no mocking of the byte/crypto boundary. The C-runtime is not callable from C# tests; parity scenarios (Phase 4/5 gates) are the cross-check against C behavior.

**Open items:** Phase-0 tasks resolve every UNVERIFIED spec item before the task that needs it (0.1→4.2, 0.2→5.2, 0.3→1.4, 0.4→4.1, 0.5→5.3, 0.6→5.4).

**Placeholders/type-consistency:** method names consistent (`Add/Load/GetAmt/IsEquip/GetNext/Use/Buy/Sell/AddEnergy/SubEnergy/LoseObj/SeedNewPilot`); `ObjSlot{Num,InUse}`, `EquippedSpecial`, `BuyStuff` used consistently across tasks.
