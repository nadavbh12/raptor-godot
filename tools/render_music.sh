#!/usr/bin/env bash
# Regenerate assets/music/*.ogg from the extracted MIDI (assets/music/*.mid)
# using the authentic Apogee Sound System OPL2 FM sound.
#
# The committed .ogg are derived assets; this script reproduces them. It is NOT
# run by the build or CI — the rendered .ogg are checked in (Godot can't play
# .mid natively, and rendering needs the OPL synth below).
#
# Why OPL2 and not a General MIDI soundfont: the 1994 DOS game used the Apogee
# Sound System's AdLib/OPL2 FM synthesis. (Note: the dosraptor *port* renders
# its MIDI with a GM soundfont — TimGM6mb.sf2 — so this Godot port's music
# deliberately sounds different from that reference. Music is not parity-checked.)
#
# Requirements:
#   - cmake, make, a C++ toolchain (to build libADLMIDI)
#   - ffmpeg (with the built-in experimental 'vorbis' encoder)
#
# Usage:  tools/render_music.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MUSIC="$ROOT/assets/music"
SRC="${ADLMIDI_SRC:-/tmp/libADLMIDI}"
BIN="$SRC/build/adlmidiplay"

# libADLMIDI: the standard OPL2/OPL3 MIDI synth. https://github.com/Wohlstand/libADLMIDI
if [[ ! -x "$BIN" ]]; then
    echo "==> building libADLMIDI in $SRC"
    [[ -d "$SRC" ]] || git clone --depth 1 https://github.com/Wohlstand/libADLMIDI "$SRC"
    cmake -S "$SRC" -B "$SRC/build" -DCMAKE_BUILD_TYPE=Release -DWITH_MIDIPLAY=ON
    cmake --build "$SRC/build" -j
fi

# Bank 67 = "TMB (Apogee Sound System Default bank)" — the Raptor patch set.
# -vm 4 = Apogee Sound System volume model.  --emu-nuked-opl2 = accurate OPL2.
# -nl = render one pass (don't loop forever).  -w writes <input>.wav.
APOGEE_BANK=67
for mid in "$MUSIC"/*.mid; do
    name="$(basename "$mid" .mid)"
    cp "$mid" "/tmp/$name.mid"
    "$BIN" "/tmp/$name.mid" -w -nl -vm 4 --emu-nuked-opl2 "$APOGEE_BANK" >/dev/null 2>&1
    # loudnorm brings every track to a consistent ~-16 LUFS so music sits at a
    # steady, audible level under the (much louder, unnormalized) SFX.
    ffmpeg -y -hide_banner -loglevel error \
        -i "/tmp/$name.mid.wav" -af loudnorm=I=-16:TP=-1.5:LRA=11 \
        -c:a vorbis -strict experimental -q:a 5 \
        "$MUSIC/$name.ogg"
    rm -f "/tmp/$name.mid" "/tmp/$name.mid.wav"
    echo "  rendered $name.ogg"
done
echo "[render_music] done — $(ls "$MUSIC"/*.ogg | wc -l | tr -d ' ') tracks"
