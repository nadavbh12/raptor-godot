# Scenario Discovery Playbook

Use this before inventing new visual parity scenarios.

1. Pick one C source module, starting with the rows already listed in `tests/parity/scenarios/coverage.md`.
2. Search for rendering, animation, collision, state transition, and object lifecycle behavior.
3. For each behavior, ask: can current unit tests, L2 parity, object dumps, or visual reports prove this matches Godot?
4. Add or update a coverage row with:
   - C source reference.
   - Visible behavior.
   - Current coverage.
   - Missing artifacts.
   - Proposed deterministic probe.
   - Status.
5. Only add a scenario to `scenarios.json` once a runnable playthrough exists.

Do not choose scenarios from recent bug memory alone. Manual reports are useful signals, but the coverage matrix is sourced from C behavior.
