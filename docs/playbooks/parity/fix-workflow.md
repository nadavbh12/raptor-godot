# Fix Workflow Playbook

Use TDD for every confirmed mismatch.

1. Reproduce the mismatch with a deterministic scenario or add a new one.
2. Add a focused failing test or parity assertion that captures the root cause.
3. Verify the test fails for the right reason.
4. Trace the C source before editing Godot behavior.
5. Implement the smallest fix.
6. Run the focused test, the relevant scenario audit, and the relevant L2 parity script.
7. Update `coverage.md` with the new evidence.
8. Commit code, tests, and coverage together.

Do not close a visual issue based only on a manually watched video.
