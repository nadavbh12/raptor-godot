# Cutscenes — design (2026-06-10)

Implement the missing AGX cinematics, faithful to the shareware (episode-1) C build,
mirroring the already-working death-scene template. View-only + parity-inert.

## Verified asset/data reality (do not re-litigate)

GLB item-handle prefix = file index. This repo ships only `FILE0000.GLB` + `FILE0001.GLB`
(shareware ep1). `FILE0002+.GLB` (registered ep2/3) are **absent**.

- Present AGX (re-extract per-frame via `dosraptor/build/tools/extract_assets/extract_assets
  FILE0000.GLB FILE0001.GLB <out>`): `LANDING(33) CHASE(30) EXPLO(22) PILOT(21)
  SHIPSD1/2(20) DOWN(30) SDEATH(6) GAME1END(5)`.
- Present PICs: `APOGEE_PIC` `CYGNUS_PIC` (`assets/sprites/0039_…`, `0040_…`).
- **Absent** (need FILE0002+.GLB): `BASE_AGX`, `GAME2END_AGX`, `GAME3END_AGX`.

`INTRO_Base()`/`INTRO_BaseLanding()` early-out `if (!GAME2)` (`GAME2 = gameflag[1]`,
false in shareware). So the shareware original **already** shows only the ship landing,
not the sunset-base frame. Building landing-only loses nothing vs the real game.

## C authority (`dosraptor/SOURCE/INTRO.C`) — scene parameters

Each AGX scene = N frames of one family, held `framerate` ticks each, looped `loops`
times (the `MOVIE_Play(frm, loops, …)` arg), with optional trailing palette fade.

| Scene | family | N | framerate | loops | trailing |
|---|---|---|---|---|---|
| City (`INTRO_City`) | CHASE_AGX | 30 | 8 | 1 | fade-in 128 on frame 0 |
| Side1 (`INTRO_Side1`) | SHIPSD1_AGX | 20 | 18 | 2 | — |
| Pilot (`INTRO_Pilot`) | PILOT_AGX | 21 | 10 | 1 | — |
| Side2 (`INTRO_Side2`) | SHIPSD1_AGX then SHIPSD2_AGX | 20+20 | 18 | 1 each | — |
| Explosion (`INTRO_Explosion`) | EXPLO_AGX | 22 | 12 | 1 | fadeout 60 |
| Landing (`INTRO_Landing`) | LANDING_AGX | 33 | 10 | 1 | fadeout 64 |
| Game1End (`INTRO_Game1End`) | GAME1END_AGX | 5 | 4 | 8 | fadeout 120 |
| Death1/2 (existing) | DOWN_AGX / SDEATH_AGX | 30/6 | 11/3 | 1/8 | fadeout 100 |

Attract intro `INTRO_PlayMain` = City + Side1 + Pilot + Side2 + Explosion.
Logos `INTRO_Credits` = APOGEE_PIC held (~30×4 ticks, APOGEE_MUS) then CYGNUS_PIC held
(~65×3 ticks). Single PICs, not AGX → 1-frame "movies" with fades.
Episode-1 victory `INTRO_EndGame(0)` = Game1End + Landing + `WIN_WinGame` text + `WIN_Order`.

## Triggers (faithful)

- **Landing** after every cleared non-final wave: `WIN_PlayGame` else-branch, WINDOWS.C:1863.
  Godot seam: `WaveController.cs:1714 → MenuStateMachine.CompleteMission` (today goes straight
  to Hangar). Insert `WinState.Landing` before Hangar.
- **Episode-1 victory** on final wave (`game_wave==dwrap`, dwrap≥8): WINDOWS.C:1847.
  Needs episode-end detection in `EndWaveSequencer`/`WaveController` (today CompleteMission is
  unconditional).
- **Logos + attract intro** once at startup: RAP.C:1689 (Credits) then 1699 (PlayMain).
  Godot seam: `MenuController._Ready` before `EnterMenu`.
- **Idle attract loop** `WIN_MainAuto` cycles intro/credits/demos when the menu idles
  (WINDOWS.C:1872, 2088). Godot: idle timer on `WinState.Menu`.

## Architecture (generalize the death template)

Death today: `AgxMovieSequence` (hardcoded death builder) → `MenuRenderer.DrawDeathMovieOverlay`
→ `WinState.Death` → `MenuStateMachine.CompleteDeathMovieIfDone(frame, DeathMovieFrames)` →
driven by `MenuController._Process`. Generalize:

1. **`AgxMovie`** (new, `src/View` — pure data): ordered `AgxMovieFrame{Path,Duration}` list +
   `FadeOutFrames` + `TotalFrames` + `TrySelectFrame(elapsed, out frame, out alpha)`. A
   `CutsceneLibrary` builds each movie from the table above. Death rebuilt on this (same frames,
   same 574 total) — regression-locked by `AgxMovieSequenceTests`.
2. **`CutsceneSequencer`** (new, `src/Sim` — deterministic, frame-based): an ordered list of
   segments `{WinState, lengthFrames}`; given `(currentFrame, enteredFrame)` reports the active
   segment and whether the whole playlist is done. Generalizes `CompleteDeathMovieIfDone`.
   No delta/RNG/wall-clock (rule #1). Drives Landing (1 segment→Hangar), Intro (logos+playmain
   segments→Menu), Victory (game1end+landing segments→Hangar/Order).
3. **Renderer**: `MenuRenderer` picks the movie for the current cutscene state and draws frame
   `SimClock.Frame - StateEnteredFrame` (death path unchanged, just routed through `AgxMovie`).
4. **WinState**: reuse `Death(20)/Landing(21)/Intro(22)` (already in enum). Victory reuses the
   sequence (Game1End under `Intro`/a victory marker → Landing → Order). Logos under `Intro`.
5. **Parity**: extend `ParityEmitter` suppression (already drops `Death`) to also drop
   `Landing`/`Intro` (comment at ParityEmitter.cs:210 already anticipates this). Cutscene
   win-states never reach the NDJSON stream → C parity unchanged.
6. **Music**: `MusicController.DesiredTrack` — `Intro→RINTRO_MUS` (exists); add Landing
   (continue prior / silent), logos APOGEE_MUS. Secondary; faithful where cheap.

## Asset prep

Re-extract per-frame AGX into `assets/agx/` for LANDING/CHASE/PILOT/EXPLO/SHIPSD1/SHIPSD2/
GAME1END (DOWN/SDEATH already per-frame). Extract to temp, copy only the needed `*_AGX_NN.png`.
Delete stale single-sheet `LANDING_AGX.png`/`GAME1END_AGX.png`. Logos already present.

## Phasing (each phase = TDD + parity sweep + lint + commit)

0. Asset prep (extract per-frame AGX).
1. Foundation (`AgxMovie`+`CutsceneLibrary`+`CutsceneSequencer`) + **Landing** after waves.
2. Startup **logos + attract intro**.
3. **Episode-1 victory** (final-wave detection + Game1End+Landing+WinGame+Order).
4. **Idle attract loop**.

## Properties / invariants

- **Movie totality**: `TrySelectFrame(e)` returns a frame iff `0 ≤ e < TotalFrames`.
  *Domain:* `AgxMovie`.
- **Total accounting**: `TotalFrames == Σ frame.Duration + FadeOutFrames`. *Domain:* `AgxMovie`.
- **Death invariance**: the regenerated death movie's frame sequence and total (574) are byte-for-byte
  the prior values. *Domain:* regression vs `AgxMovieSequenceTests`.
- **Auto-advance exactness**: a segment entered at frame F yields its successor at exactly
  `F + lengthFrames`. *Domain:* `CutsceneSequencer`.
- **Parity inertness**: enabling all cutscenes adds zero rows to the parity NDJSON vs C; the
  bench wave1/wave2 exact-diffs and menu sweep are unchanged. *Domain:* full parity sweep.
- **Sim purity**: no cutscene code under `src/Sim` reads delta/`_Process`/engine RNG/wall-clock;
  `lint_sim` stays clean. *Domain:* `src/Sim`.

## Validated dependencies

- Ran `extract_assets FILE0000.GLB FILE0001.GLB /tmp/agx_verify`. Produces per-frame
  `agx/NAME_AGX_NN.png`; AGX families present listed above; **no BASE/GAME2END/GAME3END**.
- Read `INTRO.C`, `WINDOWS.C` (triggers 1788/1794/1847/1863/1872), `RAP.C:1689/1699`,
  `INPUT.C:319/323`, `PUBLIC.H:57` (`GAME2=gameflag[1]`) — scene params + triggers above.
- `ParityEmitter.cs:204-212` already suppresses `Death`; comment names `Landing`/`Intro` as the
  same C-absent class — extension is the documented path.
