# Raptor — Godot 4 + C#

*Raptor: Call Of The Shadows* — the 1994 top-down shooter by Cygnus Studios and
Apogee — rebuilt in Godot 4 and C#.

## Build

> Rather not do this by hand? Point your coding agent at this file — this
> section and the next are everything it needs to get you from a fresh clone to
> a running game.

Requires:

- **Godot 4.6.3, .NET edition.** The version must match `Godot.NET.Sdk` in
  `raptor.csproj`. A mismatch still builds, then fails at runtime with
  `.NET: Assemblies not found`, so `ci/full.sh` checks it up front.
- **.NET 8 SDK or newer.** The projects target `net8.0` with
  `RollForward=Major`, so a newer runtime alone is fine.

```
dotnet build raptor.csproj
godot --path .
```

The game needs `assets/`, which you generate yourself — see [Game data](#game-data).

## Game data

**This repository contains no game content.** Raptor's art, audio, maps and text
remain the property of their rights holders and are not redistributed here.
`assets/` is git-ignored and built from your own copy of the game.

You need `FILE0000.GLB` and `FILE0001.GLB` from a legitimate Raptor
distribution, either:

- the freely-redistributable shareware release (on the Internet Archive), or
- the GOG/Steam *2010 Edition*, which bundles the original `.GLB` files in its
  install directory.

Then:

```
tools/extract_assets.py /path/to/dir/containing/GLBs
```

This unpacks both archives into `assets/` — around 1850 files in a few seconds —
and renders the soundtrack. Extraction is pure Python standard library: no
other repository, no C toolchain, no third-party packages. The music render is
the one exception, since authentic OPL2 FM sound comes from libADLMIDI, which it
builds on first run; that step needs `cmake`, a C++ compiler and `ffmpeg`. Pass
`--skip-music` to skip it and the game runs silently.

Either edition works. Sprites are addressed by their archive item name, not by
index, because the shareware and registered archives number their contents
differently.

## Test

```
dotnet build raptor.csproj         # tests reference the built game assembly
dotnet test tests/RaptorTests.csproj
```

`dotnet test` does not rebuild the game assembly, so build first after changing
anything under `src/`; the test project refuses to run against a stale one
rather than reporting a misleading pass.

The unit suite needs nothing beyond this repository and your generated
`assets/`.

21 tests parse real extracted game data and carry a `RequiresGameData` trait.
CI cannot run those — the data is not redistributable, so a runner has no
`assets/` — and filters them with `--filter "RequiresGameData!=true"`. Run
locally without the filter and you get the full 883.

## Parity harness

"Ported, not re-imagined" is checked rather than claimed: a harness replays the
same input through this port and the original binary, then diffs per-frame
object state. That is what keeps the flight paths and weapon timings honest.

Running it needs the original C source as a sibling checkout
([`dosraptor`](https://github.com/nadavbh12/dosraptor), or `$DOSRAPTOR`) so
`ci/full.sh` can build the reference binary. A few of its gates also read
recorded inputs and captured frames that are not published, so `ci/full.sh`
will not go fully green outside a maintainer setup — `dotnet test` is the gate
for contributions.

`tools/capture_menu_goldens.sh` records menu goldens from the C build, and
`dotnet run --project tools/PilotFixture` regenerates the pilot-save fixture
they stage.

## License

GPL v2 or later, inherited from the upstream Raptor source — see [LICENSE](LICENSE).

This covers the source code in this repository only. The game data you supply is
not covered by it and is not redistributed here.
