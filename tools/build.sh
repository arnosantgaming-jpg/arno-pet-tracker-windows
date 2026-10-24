#!/usr/bin/env bash
# Build the self-contained app with its capture daemon into build/linux-x64.
set -euo pipefail
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/build/linux-x64"
rm -rf -- "$out"
dotnet publish "$root/src/App/App.csproj" -c Release -r linux-x64 --self-contained true -o "$out"
dotnet publish "$root/src/Capture/Capture.csproj" -c Release -r linux-x64 --self-contained true -o "$out/capture"
cp -- "$root/LICENSE" "$root/NOTICE" "$root/THIRD-PARTY-NOTICES.txt" "$root/LEGAL.md" "$root/assets/icon.png" "$out/"
cp -R -- "$root/licenses" "$out/licenses"
printf '\nBuilt %s\nInstall with: bash tools/install.sh\n' "$out"
