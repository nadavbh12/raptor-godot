# Failure Diagnosis Playbook

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
