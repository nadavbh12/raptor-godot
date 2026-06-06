# Failure Diagnosis Playbook

## Rule 0 — LOOK AT THE PIXELS FIRST (hard-won)

Before trusting ANY text trace or sim dump, **render both C and Godot for the scenario
and watch them.** Pixels are ground truth; a trace is a path-specific proxy that can lie.

A real failure (the bridge tile-money bug): a `RAPTOR_TILE_TRACE` showed "zero tile hits",
so the conclusion was "Godot never hits the bridge." Wrong on two counts — the trace's env
was read with `System.Environment.GetEnvironmentVariable` (not Godot's `OS.GetEnvironment`,
so it never activated) AND the headless probe runs were too short to even reach the bridge.
Hours were spent theorizing about routing/geometry. The moment the Godot render was watched,
the truth was obvious in seconds: the bridge IS hit and explodes — it just awards no points
(explosion-chain-destroyed tiles weren't paying bounty). Capture Godot gameplay with a headed
run (`RAPTOR_RENDER_MENUS=1 RAPTOR_SHOT_DIR=<dir> RAPTOR_SHOT_EVERY_FC=1`, NO `--headless`),
encode the frames, and diff side-by-side with the C `video.mkv`.

When you DO use a trace/probe, sanity-check it before believing a negative result:
1. Did the trace even ACTIVATE? (env read path consistent with the working dumpers — use
   `OS.GetEnvironment`; print a "trace active" line on open.)
2. Did the run REACH the iter/frame of interest? (headless cadence ~6.5 fc/iter here; a small
   `--quit-after` may stop hundreds of iters short. Log the max iter reached.)
3. Does the trace cover ALL the code paths that could produce the effect? (a tile can be
   destroyed by a direct hit, an explosion chain, OR a delayed blast — a probe on one path
   reports "zero" while another path is doing the work.)
"Trace shows nothing" ≠ "it isn't happening." Verify against pixels.

---

Use this after a visual audit report flags a mismatch.

Classify the finding first:

- `sim-missing`: C object exists; Godot object is absent.
- `render-missing`: both objects exist; Godot pixels are absent.
- `extra-object`: Godot object/pixels exist without C counterpart.
- `position-drift`: both sides have the object but positions differ.
- `timing-drift`: same event appears at different anchors.
- `clipping`: pixels appear in gutters or outside allowed regions.
- `asset-animation`: object exists but frame/sequence/art differs.
- `hud-menu`: mismatch belongs to UI, HUD, or menu state.
- `unknown`: not enough dump evidence yet.

Then trace in this order:

1. C source behavior.
2. C dump/object rows.
3. Godot sim dump/object rows.
4. Godot render path.
5. Visual crop and full-frame diff.

If the classification needs dump data that does not exist, update `coverage.md` before fixing code.
