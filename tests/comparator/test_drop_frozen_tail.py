"""Unit test for parity_diff.drop_frozen_tail. Run: python3 tests/comparator/test_drop_frozen_tail.py"""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
from parity_diff import drop_frozen_tail


def _r(it, score, enemies=1, win="MISSION_1"):
    return {"iter": it, "fc": it, "win": win, "player_x": 144, "player_y": 160,
            "score": score, "shield": 50, "enemies": enemies, "pbullets": 0,
            "ebullets": 0, "obj_hash": f"{score}-{enemies}"}


def test_trims_runaway_iter_tail_to_the_last_distinct_state():
    # 3 distinct states, then a long frozen tail (state == last distinct) with a
    # runaway iter counter — exactly the live-recording loop-spin. The tail
    # collapses into the last distinct row.
    rows = [_r(0, 100), _r(18, 200), _r(36, 300)] + [_r(36 + 18 * k, 300) for k in range(1, 500)]
    out = drop_frozen_tail(rows)
    assert len(out) == 3, len(out)            # 100, 200, 300 — frozen 300-tail dropped
    assert out[-1]["score"] == 300 and out[-1]["iter"] == 36


def test_keeps_a_distinct_final_state_before_its_frozen_tail():
    rows = [_r(0, 100), _r(18, 300), _r(36, 350)] + [_r(36 + 18 * k, 350) for k in range(1, 200)]
    out = drop_frozen_tail(rows)
    assert len(out) == 3 and out[-1]["score"] == 350


def test_no_frozen_tail_is_unchanged():
    rows = [_r(0, 100), _r(18, 200), _r(36, 300)]
    assert drop_frozen_tail(rows) == rows


def test_empty_is_empty():
    assert drop_frozen_tail([]) == []


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    for t in tests:
        t(); print(f"ok  {t.__name__}")
    print(f"\n{len(tests)} passed")
