# Visual Parity Scenario Coverage

Status values: `inventory`, `covered`, `missing-probe`, `manifested`, `accepted`.

| C source | Behavior / visual phenomenon | Existing coverage | Missing artifacts | Proposed probe | Status | Notes |
|---|---|---|---|---|---|---|
| `SHOTS.C` | Player weapon projectile creation, movement, collision, hit sparks, tile impacts | Unit tests, bullet dump parity, `full_demo` | Broader weapon-matrix probes | weapon matrix probe | missing-probe | Existing fixes cover several weapons, but coverage should be source-inventoried weapon by weapon. |
| `ESHOT.C` | Enemy bullet types, mines, aimed shots, clipping at map gutters | Unit tests, bullet dump parity, visual audit via `full_demo` | Enemy-shot focused probes | enemy bullet probe | missing-probe | Known dump stream exists through `RAPTOR_BULLET_DUMP`. |
| `ENEMY.C` | Enemy spawn, movement, death, boss lifecycle, body crashes | L2 parity, unit tests | Boss/death visual probes | mission end probe | missing-probe | Use C source scan to enumerate special flight types. |
| `TILE.C` | Ground object hits, delayed tile explosions, destroyed-flat visual state | Unit tests, partial visual coverage | Tile/explosion object dumps | ground object probe | missing-probe | Recent tile-hit spark fix should be confirmed in visual audit. |
| `BONUS.C` | Bonus drops, wobble animation, pickup and money visuals | Unit tests, bonus traces, `full_demo` | Focused powerup probes | bonus lifecycle probe | missing-probe | Existing full demo is broad, not precise. |
| `OBJECTS.C` | Inventory, HUD object display, shield warning, current weapon, store object state | Unit tests, L2 parity | HUD-region visual detectors | HUD probe | missing-probe | HUD widgets need region-specific diffs. |
| `RAP.C` | Main gameplay loop order, player death prelude, mission end transition | Unit tests, AGX/death tests | Transition-focused visual probes | death and mission-end probes | missing-probe | Death prelude and AGX movie are now implemented but need scenario-level audit. |
| `WINDOWS.C` | Menu windows, registration, difficulty, hangar, sector select, options dialog | Menu pixel parity, `options` menu probe | Help pages, credits, order, load/save dialogs | menu scenario set | missing-probe | Core flow, store, and options now have pixel probes. Remaining menu gaps are full-page help, credits, order, load mission, and save/load. |
| `INTRO.C` | Intro/death movie sequencing and fades | AGX unit tests | Visual cutscene audit | death movie probe | missing-probe | AGX frames are generated; report should catch blank/regressed frames. |
| `tests/scripts/full_demo.txt` | Broad passive cross-system visual sweep | `ci/full.sh`, side-by-side videos, dense visual alignment, framecount-offset alignment, game-iteration alignment | Correlated tile/render diagnosis for remaining visual diffs | `full_demo` | manifested | `FRAMECOUNT_ALIGN=1 FRAMECOUNT_OFFSET=-160` is the current timing baseline; `ITER_ALIGN=1` isolates renderer parity after object dumps match. Remaining crop diffs now point at tile render selection/palette and effect composition, not broad projectile sim drift. |
