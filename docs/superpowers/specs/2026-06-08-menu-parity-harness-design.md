# Menu-Parity Harness — Design

_2026-06-08. Record menu interaction in the C build, replay the same input in Godot,
and verify the menus match — logical state and rendered screen. Companion to the
gameplay parity harness (`benchmarks/bench_20260606_141540`)._

## Goal

The reimplementation's menus (main menu, pilot creation, hangar, store, options,
sector select) have diverged from the C original in ways we've been catching by eye —
text positions, the callsign offset, the ID-picture click. Build a harness that catches
these **systematically**: record a session of menu interaction in the C build
(dosraptor), replay the same input in Godot, and assert the menus match — both the
**logical state** and the **rendered screen**.

Non-goal: gameplay (covered by the existing per-iter NDJSON harness). Menus only.

## Key decision: event-based, not frame-based

Gameplay parity compares state every game-loop iter because gameplay advances
continuously and deterministically. Menus are **event-driven** — nothing changes until
the user clicks or presses a key. So the unit of record/compare is an **input event**,
not a frame:

- Record `(event, post-event menu-state snapshot)` pairs.
- Replay each event in Godot, snapshot the menu state immediately after, compare.

This eliminates frame-count synchronization between C and Godot (the `fc+20` cadence
offset that plagued the gameplay harness). There are no frames to align — only
"after event N, both engines are in this menu state."

## Architecture / components

```
C build (dosraptor)                       Godot build (raptor-godot)
  menu loop (WIN_*)                          MenuStateMachine
    └ SWD_Dialog(&dlg)  ◀ input chokepoint     └ HandleInput / HandlePointerClick
        │                                          │
   [C recorder] hook                          [Godot replay] feeds events
        │ logs event + emits snapshot              │ + emits snapshot
        ▼                                          ▼
   menu_rec.json (events)                     godot_menu_states.ndjson
   c_menu_states.ndjson (golden)                    │
        └──────────────► [comparator] ◀─────────────┘
                     event-indexed state diff
        + [visual layer] static-screen pixel diff (C frame vs Godot screenshot per event)
```

Components — each its own spec→plan, built in this order:

1. **Menu-state schema** — the logical snapshot both sides emit per event. Foundational;
   nothing else can be built without it.
2. **C recorder** — instrument the `SWD_Dialog` chokepoint to (a) log each input event —
   mouse `(x, y, button)` or key/scancode — and (b) emit a menu-state snapshot after each
   event. New dosraptor code (the existing `DEMO_Think` recorder is gameplay-only).
3. **Godot replay + emit** — feed the recorded events (clicks → `HandlePointerClick(x,y)`,
   keys → `HandleInput(name)`) via the PlaythroughDriver, and emit the matching snapshot
   after each event.
4. **Comparator** — event-indexed diff of the two snapshot streams (a `parity_diff.py`
   sibling; exact match required).
5. **Coverage expansion** — pilot-create (mouse + text entry + idpic click), hangar
   (mouse; logical state is RNG-independent), store, options, sector select.
6. **Visual layer** — capture the rendered screen after each event on both sides and
   pixel-diff. Menus are static (no scroll), so frames are directly comparable — unlike
   gameplay. Must account for the hangar's `random(3)` cosmetic pilot animation.

## Menu-state schema (the snapshot)

One row per event (NDJSON):

```json
{ "event": 3, "win": 0, "screen": "REGISTER", "field": 2,
  "pilot_name": "ASD", "callsign": "AS", "idpic": 2, "difficulty": 3,
  "store_cursor": 0, "money": 10000, "opts": { "music": 88, "sfx": 100 } }
```

Only the fields meaningful to the current screen are asserted; others may be null/absent.
RNG-independent — the cosmetic pilot-animation frame is NOT in the snapshot (it lives in
the visual layer). Both engines already hold this state: C in the SWD window/fields +
the pilot struct (`tp`); Godot in `MenuStateMachine` + collaborators (`PilotCreationFlow`,
`OptionsController`, `StoreLogic`, `HangarController`). The schema is the contract.

## Recording format (`menu_rec`)

Per event: `{ "event": n, "kind": "key"|"click", "key": "<SC name>"|null, "x": int, "y": int, "button": int }`,
plus a header naming the screen the session starts on. The C recorder writes both
`menu_rec` (events) and `c_menu_states` (golden snapshots) in one pass. The Godot replay
reads `menu_rec` and dispatches each event.

## Tracer bullet (first deliverable)

**Main-menu navigation.** In C, record Down/Down/Up + Return on the main menu and emit the
snapshot after each (selected item + win transition). Replay in Godot; compare. Keyboard
only, no mouse, no RNG, trivial state — proves record → replay → compare end-to-end with the
least C instrumentation. Everything else (mouse, text entry, idpic click) is an expansion of
the same pipeline.

## Properties / invariants

- **Event-determinism**: given the same event sequence, the post-event logical snapshot is
  identical run-to-run (RNG-independent). *Domain:* each engine's menu state machine.
- **Replay round-trip**: a C-recorded event sequence replayed in Godot reproduces C's
  per-event snapshots exactly. *Domain:* harness end-to-end.
- **Click→field equivalence**: a click at (x, y) maps to the same field/item id in C and
  Godot. *Domain:* the click dispatch (`SWD_Dialog` hit-test vs `HandlePointerClick`).
- **State-snapshot completeness**: the snapshot captures every piece of logical state an
  input event can change; no hidden state drives a later divergence. *Domain:* the schema.
- **RNG isolation**: the cosmetic menu RNG (hangar `random(3)`) never alters the logical
  snapshot. *Domain:* schema vs visual layer.

## Validated dependencies

- **C input chokepoint** — `SWD_Dialog(&dlg)` returns the hit field in `dlg.sfield`; mouse
  via `PTR_X/PTR_Y/PTR_B1`, keys via `SC_*` (verified reading `SOURCE/WINDOWS.C:237,315-341`
  + the per-field case handlers).
- **C menu RNG** — `random(3)` at `WINDOWS.C:1107` (`WIN_Hangar`), cosmetic pilot animation
  only (verified).
- **C win-state hook** — `raptor_parity_set_win_state(n)` already emitted at screen
  transitions (`WINDOWS.C:471/513/1097/1297`) — reusable for the `win` field (verified).
- **Godot** — `MenuStateMachine.HandleInput(name, frame)`, `HandlePointerClick(x,y,frame)`,
  accessors `State/CurrentItem/PilotName/Callsign/IdPic`; `PlaythroughDriver` supports keys
  + `dump` (verified).
- **UNVERIFIED** — the exact dosraptor edit point to emit the event log + snapshot from the
  `SWD_Dialog` chokepoint (resolve in the C-recorder sub-project via a tracer-bullet that
  records one real main-menu event). The menu screen-capture timing for the visual layer
  (resolve in the visual sub-project).

## Risks / open questions

- **C instrumentation is the bulk of the new work** — the recorder is from-scratch
  dosraptor code. The `SWD_Dialog` chokepoint makes it tractable (one hook covers all
  screens), but it's still the riskiest piece. The tracer bullet de-risks it first.
- **Mouse-position vs click-only** — v1 records discrete events (clicks + keys). Continuous
  hover (field highlight) is visual-layer only; the logical snapshot only changes on
  click/key, so event-based recording is sufficient for state parity.
- **Hangar RNG** — ignore for state parity; for visual parity, seed-match the menu RNG or
  mask the pilot-animation region.
- **Text entry** — name/callsign typing is a sequence of letter key-events; the snapshot
  after each captures the growing string. Caps + 12-char limits must match
  (`PilotCreationFlow` already mirrors this).

## Testing

- **Unit**: each engine's `event → snapshot` is unit-testable (feed an event sequence,
  assert snapshots) without the full game loop, mirroring the existing
  `MenuStateMachine`/`PilotCreationFlow` tests.
- **Integration**: the tracer bullet IS the first integration test (record main-menu in C
  → replay → comparator → 100% match).
- The comparator gates exact state match per event; the visual layer gates a pixel-diff
  budget per static screen.

## Build order (decomposition)

1. Schema + comparator skeleton, with the **main-menu tracer bullet** end-to-end. ← first plan
2. C recorder generalized over all screens (mouse + keys).
3. Godot replay generalized; pilot-create coverage (mouse + text + idpic).
4. Hangar + store + options + sector-select coverage.
5. Visual layer (static-screen pixel diff; hangar-RNG handling).
