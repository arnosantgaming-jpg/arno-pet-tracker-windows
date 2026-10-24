#!/usr/bin/env bash
# Live check: build the capture daemon, grant it cap_net_raw, run it next to
# the game for a while and print what the decoders see. Another tracker can keep running;
# this one uses its own socket.
set -euo pipefail
# The SDK from docs/LINUX.md step 3 lives in ~/.dotnet.
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/build/capture"
seconds="${1:-60}"
rm -rf -- "$out"
dotnet publish "$root/src/Capture/Capture.csproj" -c Release -r linux-x64 --self-contained true -o "$out" >/dev/null
dotnet build "$root/tools/Replay/Replay.csproj" -c Release >/dev/null
sudo setcap cap_net_raw=ep "$out/arnoa2-capture"
"$out/arnoa2-capture" --parent $$ &
daemon=$!
trap 'kill $daemon 2>/dev/null || true' EXIT
sleep 1
printf 'Listening for %s seconds. Play normally: move, fight, collect souls.\n' "$seconds"
dotnet "$root/tools/Replay/bin/Release/net10.0/replay.dll" --live "$seconds"
