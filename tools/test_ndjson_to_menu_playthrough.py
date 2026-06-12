#!/usr/bin/env python3
"""Unit tests for ndjson_to_menu_playthrough.to_playthrough (no Godot needed).

Run: python3 tools/test_ndjson_to_menu_playthrough.py
The end-to-end round-trip (convert a committed golden -> replay in Godot -> diff)
is exercised by tests/replay_live_menu_recording.sh.
"""

from ndjson_to_menu_playthrough import to_playthrough


def _row(idx, scancode, kind="key", win="MENU"):
    return {"event_index": idx, "kind": kind, "scancode": scancode, "win": win}


def test_maps_scancodes_to_keys_with_30_frame_gaps():
    # Down(0x50), Return(0x1c), F1(0x3b), letter A(0x1e).
    rows = [_row(0, 0x50), _row(1, 0x1C), _row(2, 0x3B), _row(3, 0x1E)]
    script, warnings = to_playthrough(rows, "x")
    assert warnings == [], warnings
    keys = [ln.split()[1] for ln in script if ln.startswith("key ")]
    assert keys == ["Down", "Return", "F1", "A"], keys
    # Leading + per-event + tail gaps, and it terminates.
    assert script[3] == "wait 30"                 # initial settle
    assert script.count("wait 30") == 1 + len(rows)  # initial + one per key
    assert script[-2:] == ["wait 40", "quit"]


def test_unmapped_scancode_is_skipped_and_warned():
    rows = [_row(0, 0x50), _row(1, 0x9999), _row(2, 0x48)]
    script, warnings = to_playthrough(rows, "x")
    keys = [ln.split()[1] for ln in script if ln.startswith("key ")]
    assert keys == ["Down", "Up"], keys          # the bad one dropped
    assert len(warnings) == 1 and "unmapped scancode" in warnings[0]


def test_mouse_event_is_skipped_and_warned():
    rows = [_row(0, 0x50), _row(1, 0, kind="click")]
    script, warnings = to_playthrough(rows, "x")
    keys = [ln.split()[1] for ln in script if ln.startswith("key ")]
    assert keys == ["Down"], keys
    assert len(warnings) == 1 and "mouse" in warnings[0]


def test_empty_recording_flags_a_warning():
    script, warnings = to_playthrough([], "x")
    assert script[-2:] == ["wait 40", "quit"]
    assert any("no replayable key events" in w for w in warnings)


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    for t in tests:
        t()
        print(f"ok  {t.__name__}")
    print(f"\n{len(tests)} passed")
