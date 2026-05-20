# Artifact Generation Playbook

Use this to produce visual parity evidence.

1. Run:

   ```bash
   tests/run_visual_audit.sh full_demo
   ```

2. Inspect the printed output directory under `dumps/visual_audit/<scenario>/<timestamp>/`.
3. Open `index.html` for ranked findings and crop panels.
4. Use `findings.json` for machine-readable frame ids, crop paths, and classifications.
5. If object dumps are needed, enable the relevant dump category in the manifest and runner before diagnosing.

## Alignment Modes

Use label alignment for a quick timeline sanity check:

```bash
tests/run_visual_audit.sh full_demo
```

Use visual alignment to find the closest C frame for each Godot screenshot:

```bash
VISUAL_ALIGN=1 tests/run_visual_audit.sh full_demo
```

Use framecount alignment to test temporal-offset hypotheses. For the current
full demo, the best observed baseline is C at Godot-relative-framecount minus
160:

```bash
FRAMECOUNT_ALIGN=1 FRAMECOUNT_OFFSET=-160 tests/run_visual_audit.sh full_demo
```

Use iteration alignment after object dumps indicate the simulation state
matches and the remaining question is renderer parity:

```bash
ITER_ALIGN=1 tests/run_visual_audit.sh full_demo
```

Generated artifacts are intentionally ignored by Git.
