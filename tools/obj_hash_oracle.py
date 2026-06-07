#!/usr/bin/env python3
"""C-faithful obj_hash oracle for inventory parity tests.

Ports C's compute_obj_hash (port/platform/parity.c:160): an FNV-1a 64-bit hash
over the player object linked list (first_objs..last_objs), folding each node's
(type & 0xff) then (num & 0xff) in C list order.

Verified against the two committed C goldens:
    empty inventory                      -> cbf29ce484222325
    fresh pilot [FwdGuns(0,1),Energy(16,75)] -> 4445527f98b766af

Used to derive expected obj_hash values for the SuperShield multi-node scenario
(finding #20) for which no live C recording exists yet. Because the port
reproduces both known C goldens exactly, its output for any node list is what
C's compute_obj_hash would produce.

Usage:
    python3 tools/obj_hash_oracle.py            # prints the parity-test goldens
"""

FNV_OFFSET = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3
MASK64 = (1 << 64) - 1


def obj_hash(nodes):
    """nodes: iterable of (type, num) in C linked-list order."""
    h = FNV_OFFSET
    for t, n in nodes:
        h ^= t & 0xFF
        h = (h * FNV_PRIME) & MASK64
        h ^= n & 0xFF
        h = (h * FNV_PRIME) & MASK64
    return h


# ObjType / start_cnt (src/Sim/ObjType.cs, src/Sim/ObjLib.cs; verified vs C obj_lib):
#   ForwardGuns=0 (num 1), SuperShield=15 (num 100), Energy=16 (num 75=3*25), Detect=17 (num 1)
FRESH_PILOT = [(0, 1), (16, 75)]

SCENARIOS = {
    "empty": [],
    "fresh_pilot": FRESH_PILOT,
    "fresh_plus_1_supershield": FRESH_PILOT + [(15, 100)],
    "fresh_plus_2_supershields": FRESH_PILOT + [(15, 100), (15, 100)],
    "fresh_plus_detect": FRESH_PILOT + [(17, 1)],
}

if __name__ == "__main__":
    assert obj_hash([]) == 0xCBF29CE484222325, "empty golden mismatch"
    assert obj_hash(FRESH_PILOT) == 0x4445527F98B766AF, "fresh-pilot golden mismatch"
    for name, nodes in SCENARIOS.items():
        print(f"{name:28} {obj_hash(nodes):016x}  {nodes}")
