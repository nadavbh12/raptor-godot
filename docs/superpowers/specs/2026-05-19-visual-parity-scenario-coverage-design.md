# Visual Parity Scenario Coverage Design

## Goal

Replace ad hoc full-video review with a reproducible workflow that discovers parity scenarios from the DOS C source, generates deterministic C/Godot artifacts, and cross-links object dumps with visual diffs.

## Architecture

The workflow has four durable layers:

1. A C-derived coverage matrix in `tests/parity/scenarios/coverage.md`.
2. A machine-readable scenario manifest in `tests/parity/scenarios/scenarios.json`.
3. Operational playbooks in `docs/playbooks/parity/`.
4. Generated reports under `dumps/visual_audit/`.

Existing capture tools remain the base: `tests/build_compare_video.sh` captures C/Godot frames and `tests/pair_frames.py` aligns them. The visual audit wrapper calls those tools and then runs `tests/visual_audit.py` to generate ranked findings, crop panels, `findings.json`, and `index.html`.

## Workflow

Start with C source scanning, not recent manual complaints. For each C behavior that affects visible output, add a coverage row. If the behavior is already covered, record the evidence. If not, design the shortest deterministic playthrough that exercises it and add it to the manifest only when it is runnable.

Every visual audit scenario should produce synchronized artifacts:

- C frames.
- Godot frames.
- Paired frame sequence.
- Existing object dumps where available.
- Pixel diffs and enlarged high-disagreement crops.
- A JSON and HTML report that future agents can inspect without watching a full video.

## Properties

**C-derived coverage**: New scenario candidates come from C source behavior inventory before manual issue memory. *Domain:* `coverage.md`.

**Deterministic probes**: Manifest scenarios must use deterministic playthrough scripts. *Domain:* `scenarios.json`.

**Aligned visual evidence**: Reports compare paired C/Godot frames produced by existing label or frame alignment tooling. *Domain:* visual audit artifacts.

**Actionable findings**: Each finding must include frame id, image paths, a crop, and a classification string. *Domain:* `findings.json`.

**Generated artifacts stay untracked**: Reports, videos, crops, and frame dumps live under ignored `dumps/`. *Domain:* repository hygiene.

## Validated Dependencies

- Ran and inspected existing repo tools: `tests/build_compare_video.sh`, `tests/pair_frames.py`, and `tests/menu_pixel_parity.py`. They already provide frame capture, pairing, and diff primitives.
- Verified C dump hooks exist in `/Users/nadavb/dev/dosraptor/port/platform/parity.c`: `RAPTOR_POS_DUMP` and `RAPTOR_BULLET_DUMP`.
- `python3` is used for report generation; `Pillow` is already a repo dependency in `tests/menu_pixel_parity.py`.

## Acceptance

V1 is accepted when:

- The coverage matrix and manifest are checked in.
- Manifest validation runs in the xUnit suite.
- `tests/run_visual_audit.sh full_demo` generates a report under `dumps/visual_audit/`.
- The report includes `findings.json`, `index.html`, and crop images.
- `ci/full.sh` remains green.
