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
dotnet test tests/RaptorTests.csproj
ci/full.sh                         # full local acceptance runner
```

`ci/full.sh` disables audio for both Godot and the C reference. Menu pixel parity is
included when `MENU_C_CAPTURE_ROOT=/path/to/reusable/c/captures` is provided.

## Game data

**This repository contains no game content.** Raptor's art, audio, maps and text
remain the property of their rights holders and are not redistributed here. The
`assets/` directory is git-ignored; you generate it locally from your own copy of
the game.

You need `FILE0000.GLB` and `FILE0001.GLB` from a legitimate Raptor
distribution. Either:
- The freely-redistributable shareware (Internet Archive, search
  "Raptor Call of the Shadows shareware").
- The GOG/Steam 2010 Edition; the original `.GLB` files are bundled in its
  install directory.

Then, with the [`dosraptor`](https://github.com/nadavbh12/dosraptor) repo checked
out alongside this one:

```
tools/extract_assets.sh /path/to/dir/containing/GLBs
```

That builds dosraptor's extractor, unpacks both archives into `assets/`, and
renders `assets/music/*.ogg`. Pass `--skip-music` to skip the (slower) music
render; set `$DOSRAPTOR` if the repo lives somewhere other than `../dosraptor`.

Requires `cmake`, a C toolchain and `libpng`; music additionally needs `ffmpeg`
and a C++ toolchain (it builds libADLMIDI for authentic OPL2 FM synthesis).

Two details the script handles that a bare extractor run does not: the extractor
converts MUS with `mus2mid(rate=140)` — the DMX library default rather than the
70 Hz Raptor actually uses — so every track would otherwise play at double
speed; and it writes `FLATSG1_ITM.json` to the output root rather than
`assets/flats/`. See `tools/render_music.sh` for the music pipeline.

## Companion repo

The original C codebase lives at https://github.com/nadavbh12/dosraptor. It serves as the parity ground-truth generator and the asset extractor.

## License

GPL v2 or later (inherited from the upstream Raptor source), see
[LICENSE](LICENSE).

This covers the source code in this repository only. The original game data
you supply is not covered by it and is not redistributed here.
