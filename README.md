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

The unit suite and `tools/extract_assets.py` need nothing beyond this repo.
(One parity scenario's playthrough script lives in `dosraptor` and is not
published; the manifest test checks it only when that sibling checkout is
present, so a plain clone still passes.)
`ci/full.sh` is different: it is the maintainer's acceptance runner and needs a
sibling checkout of [`dosraptor`](https://github.com/nadavbh12/dosraptor) (or
`$DOSRAPTOR`) to build the C reference binary it compares against. Several of
its gates cannot pass outside that setup — the `death_wave*` parity scenarios
and the menu-event sweep read input scripts that are not published, and menu
pixel parity additionally needs `MENU_C_CAPTURE_ROOT=/path/to/c/captures`. It
also runs `set -e`, so it stops at the first such failure rather than
reporting the rest. If you are contributing, `dotnet test` is the gate that
matters; `ci/full.sh` disables audio for both Godot and the C reference.

Maintainer tooling lives in `tools/`: `capture_menu_goldens.sh` records the C
reference's menu goldens, and `PilotFixture` regenerates the pilot-save fixture
those captures stage (`dotnet run --project tools/PilotFixture`).

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
