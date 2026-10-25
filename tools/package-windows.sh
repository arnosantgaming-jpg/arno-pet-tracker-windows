#!/usr/bin/env bash
# Build the one-click Windows installer (dist/installer/...-Setup.exe) with Velopack.
# Needs Velopack's packaging tool once:  dotnet tool install -g vpk
#   bash tools/package-windows.sh [version]
set -euo pipefail
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
version="${1:-1.0.2}"
command -v vpk >/dev/null || { echo "Velopack's vpk tool is missing. Install it with: dotnet tool install -g vpk" >&2; exit 1; }
bash "$root/tools/build-windows.sh"
rm -rf -- "$root/dist/installer"
# The setup installs per user, adds Start menu and desktop shortcuts and an uninstaller.
vpk "[win]" pack \
    --packId ArnoPetTracker \
    --runtime win-x64 \
    --packVersion "$version" \
    --packTitle "Arno's Pet Tracker" \
    --packAuthors "arnosantgaming-jpg" \
    --packDir "$root/build/win-x64" \
    --mainExe arno-pet-tracker.exe \
    --icon "$root/assets/icon.ico" \
    --outputDir "$root/dist/installer"
ls -la "$root/dist/installer"
