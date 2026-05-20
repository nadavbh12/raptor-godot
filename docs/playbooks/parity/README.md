# Visual Parity Automation Entry Point

Start here when a future session needs to reduce manual video review or diagnose a new C/Godot visual mismatch.

## Purpose

The goal is to turn visual parity work into a repeatable loop:

1. Discover uncovered visual scenarios from the DOS C source.
2. Record those scenarios in the coverage matrix and manifest.
3. Generate aligned C/Godot frame artifacts from one command.
4. Use diffs, crops, dumps, and labels to diagnose the actual mismatch.
5. Fix with a focused failing test or parity assertion before changing production code.

Do not pick scenarios only from recent bug memory. The C source is the authority.

## Current Entry Points

- Coverage matrix: `tests/parity/scenarios/coverage.md`
- Scenario manifest: `tests/parity/scenarios/scenarios.json`
- Artifact runner: `tests/run_visual_audit.sh`
- Interactive C seed capture: `tests/capture_c_seed_run.sh`
- Dense visual pairing: `tests/build_visual_aligned_pairs.py`
- Framecount pairing: `tests/build_framecount_aligned_pairs.py`
- Iteration pairing: `tests/build_iter_aligned_pairs.py`
- Single-frame alignment search: `tests/find_visual_alignment.py`
- Visual report generator: `tests/visual_audit.py`
- Manifest/report tests: `tests/VisualParityScenarioTests.cs`
- Design spec: `docs/superpowers/specs/2026-05-19-visual-parity-scenario-coverage-design.md`

## Fast Path

Generate the current full-demo visual audit:

```bash
tests/run_visual_audit.sh full_demo
```

When diagnosing gameplay visuals, prefer dense C plus visual-aligned pairing:

```bash
VISUAL_ALIGN=1 tests/run_visual_audit.sh full_demo
```

This still captures C densely with `RAPTOR_DUMP_EVERY=1`, then pairs each
Godot frame to the closest C frame and writes:

```text
dumps/visual_audit/full_demo/<timestamp>/pairs/visual_alignment_pairs.json
```

Use that first to rule out temporal offset before interpreting crop diffs.
Label-aligned reports are useful for timeline sanity; visual-aligned reports
are better for finding the exact C frame a Godot screenshot should match.

When checking whether a mismatch is just a stable temporal offset, use
normalized framecount pairing. Current full-demo evidence shows the best C
frame baseline is about 160 C framecounts earlier than the Godot screenshot:

```bash
FRAMECOUNT_ALIGN=1 FRAMECOUNT_OFFSET=-160 tests/run_visual_audit.sh full_demo
```

This writes:

```text
dumps/visual_audit/full_demo/<timestamp>/pairs/framecount_alignment_pairs.json
```

Use framecount-aligned reports to validate timing hypotheses. Use visual-aligned
reports to classify remaining visual diffs once the timing offset is known.

When object dumps match but visuals still disagree, isolate renderer parity by
matching the same game-loop iteration:

```bash
ITER_ALIGN=1 tests/run_visual_audit.sh full_demo
```

This writes:

```text
dumps/visual_audit/full_demo/<timestamp>/pairs/iter_alignment_pairs.json
```

Use iteration-aligned reports to diagnose tile rendering, sprite composition,
palette, and effect-frame issues without cadence drift contaminating the pair.

Open the generated report from the printed `index.html` path under:

```text
dumps/visual_audit/full_demo/<timestamp>/index.html
```

The report is intentionally generated under ignored `dumps/` output. Do not commit generated frames, crops, or HTML reports unless a task explicitly asks for a frozen artifact.

Seed the corpus with a human-played C run:

```bash
tests/capture_c_seed_run.sh manual_c_<short-label>
```

That launches the C port interactively and writes frames, parity, position,
bullet/enemy, bonus, log, manifest, and optional MP4 artifacts under:

```text
dumps/c_seed_runs/<run-id>/
```

Useful overrides:

```bash
DUMP_EVERY=1 tests/capture_c_seed_run.sh manual_c_dense
ENCODE_MP4=0 tests/capture_c_seed_run.sh manual_c_no_video
KEEP_BMPS=1 tests/capture_c_seed_run.sh manual_c_keep_bmps
```

## Workflow Map

Use these playbooks in order:

1. `scenario-discovery.md` - scan C source modules and update the coverage matrix.
2. `scenario-authoring.md` - create deterministic playthrough/probe coverage for a missing row.
3. `artifact-generation.md` - generate paired C/Godot frames and visual reports.
4. `failure-diagnosis.md` - classify the mismatch and decide whether it is sim, render, timing, clipping, asset, HUD, or menu behavior.
5. `fix-workflow.md` - write the failing test/assertion, implement the smallest fix, rerun focused checks and the visual audit.

## Verification

For changes to this automation layer, run:

```bash
/opt/homebrew/opt/dotnet@8/bin/dotnet build tests/RaptorTests.csproj --nologo --verbosity minimal
/opt/homebrew/opt/dotnet@8/bin/dotnet test tests/RaptorTests.csproj --no-build --filter "FullyQualifiedName~VisualParityScenarioTests" --logger "console;verbosity=minimal"
```

Before treating a visual parity fix as complete, also run the relevant scenario audit and the normal acceptance path:

```bash
tests/run_visual_audit.sh full_demo
ci/full.sh
```

## Extension Rule

When adding a new scenario, update all of these together:

- `tests/parity/scenarios/coverage.md`
- `tests/parity/scenarios/scenarios.json`
- a deterministic playthrough or probe under the existing test/script conventions
- focused tests or parity assertions that fail before the production fix

If the new scenario cannot yet be automated, keep it in the coverage matrix as `missing-probe` with a concrete note. Silent gaps are worse than visible debt.
