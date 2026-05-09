# CLAUDE.md — Raptor Godot reimplementation

## Project rules (non-negotiable)

1. **No `delta`, no `_Process`, no engine RNG, no wall-clock time in `src/Sim/`.** All sim logic ticks via `_PhysicsProcess` and reads `SimClock.Frame`. Engine RNG (`GD.Randi`, `Mathf.Randf`) is banned in sim files; use the per-wave `RandomNumberGenerator` instance owned by `WaveController`. Enforced by `scripts/lint_sim.sh` in CI.
2. **C# only.** No GDScript. Rationale: per godogen's analysis (https://github.com/htdt/godogen/blob/master/docs/gdscript-vs-csharp.md), C# avoids GDScript's silent type-inference failures that LLMs commonly produce.
3. **Pattern B (idiomatic Godot Node2D entities), with the discipline above.** Don't refactor toward "headless sim core" unless the spec is amended.
4. **Per-tick phase order is owned by `WaveController`**, not scene-tree traversal. Order: Input → Spawn → Movement → Collision collection → Collision resolution → Cleanup → HUD → Checkpoint emission.
5. **Tests must run on `godot --headless`**; never depend on the editor or MCP servers being available in CI.
6. **DO NOT READ:** `tests/scripts/holdout/` directory. The held-out scripts are sealed; reading them invalidates the test signal they provide.

## Test layers (see spec for full details)

- L1 (replay determinism), L2a (script parity), L2b (demo parity), L3 (invariants), L4 (seed sweep), L7 (AI visual diff).
- Fast mode: `RAPTOR_TEST_FAST=1` runs at ~50× wall clock for tests.
- Parity output schema: `tests/parity/schema.json`. NDJSON per checkpoint.

## Where things live

- `src/Sim/` — game logic (parity-affecting). Strict rules apply.
- `src/View/` — rendering, particles, HUD animations. May use `delta`. Cannot mutate sim state.
- `src/UI/` — menus, briefing, hangar, store.
- `src/Save/` — save format. JSON via System.Text.Json.
- `src/Test/` — checkpoint emitter, headless runners.
- `assets/` — extracted from dosraptor's `FILE0001.GLB`. Read-only.
- `tests/parity/` — committed goldens from the C version.

## Spec

The authoritative design lives at this repo's companion: `dosraptor/docs/superpowers/specs/2026-05-09-godot-csharp-reimpl-design.md`. If a decision in this CLAUDE.md conflicts with the spec, the spec wins.
