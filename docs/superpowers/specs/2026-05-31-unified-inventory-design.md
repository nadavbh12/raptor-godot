# Unified Per-Pilot Inventory — Design Spec

_Date: 2026-05-31. Status: approved design, pending implementation plan._

## Goal

Replace the Godot port's fragmented weapon/object state with a single per-pilot
**`Inventory`** model that mirrors C's `OBJ` / `p_objs[]` / `plr.sweapon` system,
shared by every consumer (new-pilot seeding, save/load, store, gameplay firing,
bonus pickups, HUD). The user-facing driver: a **loaded pilot must get their
saved weapons, specials, counts, and equipped special** (today they start with
defaults). Scope chosen by the user: **full C inventory parity** — including the
live behaviors (consumption-on-fire, store transactions, SUPER_SHIELD spill,
random-loss, energy-grab, megabomb detonation), each verified against C.

## Background — why

The port currently scatters inventory across:
- `PlayerShooter`: `HasPlasmaGuns`/`HasMicroMissile` bools, `_ownedSpecials`
  HashSet, `MegaBombCount`, `SpecialWeapon`.
- `StoreLogic`: a **static** `StarterInventory` dict + `OwnedCount` (display-only;
  buy/sell are cosmetic no-ops).
- `PlayerLogic.Shield`: a plain int; SUPER_SHIELD modelled as instant heal.
- Save/load: only the 88-byte header is parsed; the OBJ array is skipped, and
  new pilots are never seeded.

C, by contrast, has one source of truth: a linked list of `OBJ {num, type,
inuse, lib}` with a `p_objs[type]` fast-lookup array and `plr.sweapon` for the
equipped special. Store, gameplay, pickups, and save/load all operate on it.

## Validated dependencies

- **On-disk record sizes** — VERIFIED via compiled clang probe (64-bit, default
  packing, matching the dosraptor build): `PLAYEROBJ = 88` bytes;
  `OBJ = 40` bytes, field offsets `num=16, type=20, inuse=32`. `prev`(0–7),
  `next`(8–15), `lib`(24–31) are 8-byte pointers — garbage on disk, ignored on
  load (C's `OBJS_Load` rebuilds the list and re-links `lib`).
- **CASTLE-XOR encrypt/decrypt** — already implemented and unit-tested in
  `PilotSaveStore` (`Decrypt`) and `PilotSaveStoreTests` (`Encrypt`/`WriteFakePilot`).
  Key `"CASTLE"`, seed `0x19 % 6 = 1` ('A'), state carried via prev encrypted byte.
- **C `OBJS_*` semantics** — cited from `OBJECTS.C` / `OBJECTS.H` / `WINDOWS.C` /
  `LOADSAVE.C` / `PUBLIC.H`. The data-model + lifecycle (Add/Equip/Load/Del/
  GetNext/MakeSpecial/Buy/Sell/Clear) is well-cited. The peripheral combat
  behaviors are marked UNVERIFIED below and MUST be re-read from C before
  implementation (the mapping agent's claims there are unconfirmed).
- **UNVERIFIED — energy-grab (`S_ENERGY_GRAB`) behavior:** re-read its `actf` in C.
- **UNVERIFIED — megabomb mass-detonation:** re-read SHOTS.C (~1232) for the
  clear-enemy-bullets + damage-all-enemies effect.
- **UNVERIFIED — whether any committed parity scenario (death_wave1/2/4–9,
  mission_start/long, full_demo) involves a SUPER_SHIELD pickup.** Gates the
  shield-model change; check the goldens (does shield ever jump to max mid-run or
  exceed the ENERGY cap?) before routing shield through the inventory.

## Architecture / components

### 1. `ObjType` enum (unify three identifiers into one)
One enum mirroring C `OBJ_TYPE` 0–25 (weapons 0–14, `SuperShield`=15, `Energy`=16,
`Detect`=17, `ItemBuy1..6`=18–23, `LastObject`=25). Replaces `WeaponType` (0–14)
and StoreLogic's internal `ObjType` (0–17). Values already align, so migration is
rename+extend across PlayerShooter, BulletLogic, HUD, StoreLogic,
BonusEffectDispatcher. Removes fragile manual int-casting.

### 2. `ObjLib` static metadata table
Static table (indexed by `ObjType`) mirroring C `obj_lib[]`:
`{ Cost, StartCnt, MaxCnt, Forever, OnlyFlag, SpecialW, MoneyFlag, LoseIt,
Game1Flag }`. Sourced verbatim from `OBJS_Init` (OBJECTS.C:212–624). Subsumes
StoreLogic's partial `Catalog`. Drives all behavior gating (consumable?,
stackable?, selectable special?, buyable in shareware?, randomly-losable?).

### 3. `Inventory` class (single source of truth)
Mirrors C's list + `p_objs` + `plr.sweapon`:
- `Dictionary<ObjType, ObjSlot> _slots`, `ObjSlot { int Num; bool InUse; }`.
  Owned ⇔ slot present (mirrors `p_objs[type] != NULL`). One slot per type with
  `Num` = count — faithful for `onlyflag` stackables (Energy/SuperShield/MegaBomb)
  and for owned/equipped weapons (Num 0/1). C's rare duplicate-weapon list entries
  are a simplification to verify-harmless (no consumer relies on `OBJS_GetTotal`
  for non-stackable weapons).
- `ObjType? EquippedSpecial` (= `plr.sweapon`).
- API mirroring C: `Add, Equip, Load, Del, GetNext, Use, SubAmt, GetAmt,
  GetTotal, IsEquip, IsOnly, Buy, Sell, AddEnergy, SubEnergy, LoseObj, Reset,
  Clear`. Each method's behavior is taken from the cited C function.
- Pure C# (no Godot Node) so it is fully unit-testable. Owned by `WaveController`
  / player state; `PlayerShooter` and `StoreLogic` query it.

### 4. Shield integration (gated on parity)
`PlayerLogic.Shield` becomes a view over the `Energy` slot's `Num`. Damage/heal/
recharge route through `Inventory.SubEnergy`/`AddEnergy` with the SUPER_SHIELD
spill (SubEnergy drains SuperShield first then Energy; AddEnergy fills Energy to
`MaxCnt`=100 then spills to SuperShield). **Parity-critical** — death_waves +
mission_long + full_demo all track shield exactly. Gate: re-run the full parity
suite (8 death_waves + 4 CI L2a); all must stay green. Verify the super-shield
pickup question (above) first.

### 5. Consumers rewired
- **New-pilot seed** (`WaveController.OnPilotCreated`): `Add(ForwardGuns)`,
  3×`Add(Energy)`, score 10000, then `GetNext()` (WINDOWS.C:988–1007). Replaces
  the score/shield-only reset.
- **Save** (`PilotSaveStore`/`RAP_SavePlayer`): header `numobjs` = slot count;
  then one 40-byte CASTLE-XOR record per slot (`num@16, type@20, inuse@32`,
  pointer fields zeroed).
- **Load** (`RAP_LoadPlayer`): after the 88-byte header, read `numobjs` × 40-byte
  records, decrypt each, `Inventory.Load`; then validate `sweapon` (`GetNext` if
  the equipped special isn't owned).
- **Store** (`StoreLogic`): `Buy`/`Sell` call `Inventory.Buy`/`Sell` (move score +
  mutate slots); `OwnedCount` reads `GetAmt`.
- **Gameplay** (`PlayerShooter`): owned/equipped/count queries → `Inventory`;
  `ApplyButton1` fires owned weapons; consumable specials → `Inventory.Use`
  (decrement, remove + `GetNext` at 0).
- **Pickups** (`BonusEffectDispatcher`): → `Inventory.Add` (auto-equip if first of
  type; auto-set `EquippedSpecial` if special and none equipped).
- **HUD**: weapon icon + counts read `Inventory`.

## Data flow (load round-trip)
disk bytes → CASTLE-XOR decrypt → 88B header (`PLAYEROBJ`) + `numobjs`×40B `OBJ`
→ `Inventory.Load` per record → validate `sweapon` → `Inventory` is live for
store/gameplay/HUD. Save reverses it.

## Properties / Invariants

- **Round-trip identity**: `load(save(inv)) == inv` for all `(type, Num, InUse)`
  slots and `EquippedSpecial`. *Domain:* PilotSaveStore ↔ Inventory.
- **Ownership ⇔ slot**: a type is owned iff a slot exists; `EquippedSpecial != null`
  ⇒ that type is owned and `ObjLib[type].SpecialW`. *Domain:* Inventory.
- **Energy bounds**: `Energy.Num ∈ [0, 100]`; AddEnergy spills to SuperShield only
  when Energy is at `MaxCnt`; SubEnergy drains SuperShield before Energy.
  *Domain:* Inventory.AddEnergy/SubEnergy.
- **New-pilot seed determinism**: a fresh pilot always has exactly
  `{ForwardGuns: Num 1, InUse}` + `{Energy: Num 75}`, score 10000, equipped
  special = none (or first owned special). *Domain:* OnPilotCreated.
- **Consumption**: firing a non-`Forever` special decrements `Num` by 1; at `Num==0`
  the slot is removed and `EquippedSpecial` advances via `GetNext`. `Forever`
  weapons never decrement. *Domain:* Inventory.Use.
- **Money conservation**: `Buy` costs exactly `ObjLib.Cost` (×`StartCnt` for
  onlyflag) and adds inventory; `Sell` refunds the C resale value and removes it.
  *Domain:* Inventory.Buy/Sell.
- **Loseit domain**: only `ObjLib[type].LoseIt` types may be randomly lost by
  `LoseObj`. *Domain:* Inventory.LoseObj.

## Error handling
- Truncated/odd-length save file (bytes after header not a multiple of 40, or
  fewer than `numobjs`×40): load as many whole records as present, stop, log; do
  not throw (mirror C's `OBJS_Load` break-on-failure loop).
- Unknown/out-of-range `type` in a record: skip that record (defensive; C indexes
  `obj_lib[type]` directly, so the port must bound-check).
- `MaxCnt` caps enforced on Add/Buy.

## Testing
- **Encrypted-fixture round-trip**: extend `WriteFakePilot` to append OBJ records;
  write a pilot with a known inventory → load → assert `Inventory` matches.
- **Per-`OBJS_*` unit tests** vs cited C semantics (Add auto-equip, onlyflag
  stacking + caps, GetNext cycle/wrap, Use depletion → cycle, Buy/Sell money,
  AddEnergy/SubEnergy spill, LoseObj domain).
- **Seed test**: new pilot → exact starter inventory + score.
- **Behavior tests** (each after re-reading its C ref): consume-on-fire,
  store buy/sell, shield spill, loseit, energy-grab, megabomb detonation.
- **Full parity regression gate**: 8 death_waves + 4 CI L2a (`run_l2a.sh`) must
  stay green — the hard gate for the shield-model change. No parity scenario
  exercises the loaded-pilot path, so save/load correctness rides on the
  fixture tests, not parity.

## Out of scope / explicit non-goals
- No new UI/menus. No campaign assets. No changes to the parity harness.
- Duplicate-weapon multi-entry lists (C edge) — modelled as one slot/type unless
  a consumer is found to need `GetTotal` semantics for non-stackables.

## Open verification items (resolve during implementation, before the dependent task)
1. Super-shield pickup in any parity scenario? (gates shield-model change)
2. Energy-grab `actf` behavior in C.
3. Megabomb mass-detonation in SHOTS.C.
4. Exact C resale formula (`OBJS_GetResale` / sell value).
5. C `OBJS_GetNext` start/wrap boundaries (FIRST_SPECIAL..LAST_WEAPON).
