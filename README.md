# Raptor — Godot 4 + C#

Reimplementation of Raptor: Call Of The Shadows in Godot 4 with C#. Phase 1 goal is single-player faithful gameplay; phase 2 (deferred) adds 2-player and extensions.

## Status

Phase 1 parity implementation is in progress. Current local gates are the unit suite,
script parity, demo parity, and targeted menu/visual parity checks. The original seed
sweep gate has been dropped because the captured seed script was not meaningfully
RNG-sensitive.

## Build

Requires:
- Godot 4.3+ .NET edition
- .NET 8 SDK

```
dotnet build raptor.csproj
godot --path .
```

The game will not run until you have generated `assets/` — see
[Game data](#game-data) below.

## Test

```
dotnet build raptor.csproj         # tests reference the built game assembly
dotnet test tests/RaptorTests.csproj
ci/full.sh                         # full local acceptance runner
```

`dotnet test` does not rebuild the game assembly, so build it first after
changing anything under `src/`. The test project refuses to run against a
stale one rather than reporting a misleading pass.

`ci/full.sh` disables audio for both Godot and the C reference. Menu pixel parity is
included when `MENU_C_CAPTURE_ROOT=/path/to/reusable/c/captures` is provided.

## Game data

**This repository contains no game content.** Raptor's art, audio, maps and text
remain the property of their rights holders and are not redistributed here. The
`assets/` directory is git-ignored; you generate it from your own copy of the
game.

You need `FILE0000.GLB` and `FILE0001.GLB` from a legitimate Raptor
distribution. Either:
- The freely-redistributable shareware (Internet Archive, search
  "Raptor Call of the Shadows shareware").
- The GOG/Steam 2010 Edition; the original `.GLB` files are bundled in its
  install directory.

Then:

```
tools/extract_assets.py /path/to/dir/containing/GLBs
```

That unpacks both archives into `assets/` (~1800 files, a few seconds) and
renders the music. Extraction is pure Python standard library — no other
repository, no C toolchain, no third-party packages. The music render is the
one exception: authentic Apogee OPL2 FM sound comes from libADLMIDI, so that
step needs `cmake`, a C++ compiler and `ffmpeg`, and builds the synth on first
run. Pass `--skip-music` to skip it (the game runs, silently).

The extractor is a port of the one in `dosraptor`, and its output is verified
against it: every image pixel-identical, everything else byte-identical. It
also fixes a tempo bug in the original — MUS is converted at DMX's default
rate of 140 rather than the 70 Hz Raptor actually uses
(`SOURCE/FX.C:1076`), which makes every track play at double speed.

## Companion repo

The original C codebase lives at https://github.com/nadavbh12/dosraptor. It serves as the parity ground-truth generator and the asset extractor.

## License

GPL v2 or later (inherited from the upstream Raptor source), see
[LICENSE](LICENSE).

This covers the source code in this repository only. The original game data
you supply is not covered by it and is not redistributed here.
