#!/usr/bin/env bash
# Ensure the C# SDK package matches the Godot Mono runtime on PATH.
# A stale Godot.NET.Sdk can build successfully but crash the runtime loader
# with ".NET: Assemblies not found" when C# scripts are loaded.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if ! command -v godot >/dev/null 2>&1; then
    echo "[check_godot_sdk_version] missing required command: godot" >&2
    exit 2
fi

sdk_version="$(sed -nE 's/^<Project Sdk="Godot\.NET\.Sdk\/([^"]+)">$/\1/p' "$ROOT/raptor.csproj")"
if [[ -z "$sdk_version" ]]; then
    echo "[check_godot_sdk_version] could not read Godot.NET.Sdk version from raptor.csproj" >&2
    exit 2
fi

godot_version="$(godot --version | sed -nE 's/^([0-9]+\.[0-9]+\.[0-9]+)\..*$/\1/p')"
if [[ -z "$godot_version" ]]; then
    echo "[check_godot_sdk_version] could not parse godot --version" >&2
    exit 2
fi

if [[ "$sdk_version" != "$godot_version" ]]; then
    echo "[check_godot_sdk_version] Godot.NET.Sdk/$sdk_version does not match Godot runtime $godot_version" >&2
    exit 1
fi

echo "[check_godot_sdk_version] OK: Godot.NET.Sdk/$sdk_version matches Godot $godot_version"
