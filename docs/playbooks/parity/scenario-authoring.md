# Scenario Authoring Playbook

Use this when a coverage row needs a deterministic probe.

1. Start from an existing script in `/Users/nadavb/dev/dosraptor/tests/scripts/` when possible.
2. Keep the probe short. Long demos are broad sweeps; short probes are debugging tools.
3. Add `dump <label>` commands around the behavior so frame pairing can use labels.
4. Prefer deterministic setup through existing script commands and deterministic RNG.
5. Run the script through C and Godot before adding it to `scenarios.json`.
6. Record the C source references and expected dump categories in the manifest.

Accepted probes are checked-in scripts. Exploratory input sequences stay out of the manifest until reproducible.
