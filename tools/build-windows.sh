#!/usr/bin/env bash
# Cross-build the Windows version from Linux (or macOS): self-contained win-x64 app with
# its capture program, zipped as dist/arno-pet-tracker-windows.zip.
set -euo pipefail
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/build/win-x64"
rm -rf -- "$out" "$root/dist"
dotnet publish "$root/src/App/App.csproj" -c Release -r win-x64 --self-contained true -o "$out"
dotnet publish "$root/src/Capture/Capture.csproj" -c Release -r win-x64 --self-contained true -o "$out/capture"
cp -- "$root/LICENSE" "$root/NOTICE" "$root/THIRD-PARTY-NOTICES.txt" "$root/LEGAL.md" "$root/assets/icon.png" "$root/tools/install.ps1" "$out/"
cp -R -- "$root/licenses" "$out/licenses"
mkdir -p "$root/dist"
# One self-contained exe: the capture program (itself single-file) and all assets packed
# inside, unpacked by .NET to a private cache folder on first start.
single="$root/build/single"
rm -rf -- "$single"
dotnet publish "$root/src/Capture/Capture.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$single/capture"
dotnet publish "$root/src/App/App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:EmbedCapture="$single/capture/arnoa2-capture.exe" -o "$single/app"
cp -- "$single/app/arno-pet-tracker.exe" "$root/dist/ArnoPetTracker-win-x64.exe"
(cd "$root/build" && python3 -I -c "import shutil; shutil.make_archive('$root/dist/arno-pet-tracker-windows', 'zip', '.', 'win-x64')")
printf '\nBuilt %s\nZip: %s\nSingle exe: %s\n' "$out" "$root/dist/arno-pet-tracker-windows.zip" "$root/dist/ArnoPetTracker-win-x64.exe"
