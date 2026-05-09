# Raptor — Godot 4 + C#

Reimplementation of Raptor: Call Of The Shadows in Godot 4 with C#. Phase 1 goal is single-player faithful gameplay; phase 2 (deferred) adds 2-player and extensions.

## Status

Stage 0: bootstrap (in progress).

## Build

Requires:
- Godot 4.3+ .NET edition
- .NET 8 SDK

```
dotnet build raptor.csproj
godot --path .
```

## Test

```
bash scripts/lint_sim.sh           # sim-layer discipline
dotnet test tests/RaptorTests.csproj
```

## Game data

You must supply your own copy of `FILE0001.GLB` from a legitimate Raptor distribution. Either:
- Use the freely-redistributable shareware (Internet Archive, search "Raptor Call of the Shadows shareware").
- Use the GOG/Steam 2010 Edition; the original `.GLB` files are bundled in its install directory.

Run the asset extractor (in the `dosraptor` repo, this project's parity reference) to produce the assets/ directory expected by Godot.

## Companion repo

The original C codebase lives at https://github.com/nadavbh12/dosraptor. It serves as the parity ground-truth generator and the asset extractor.

## License

GPL v2 or later (inherited from upstream Raptor source).
