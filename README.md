# Arno's Pet Tracker

A Linux-first companion for **AION 2** pet farming: your pet ranks and soul totals,
what you are farming right now, and where to go next on a live map.

> **Tested only on CachyOS** (KDE Plasma 6, Wayland session, AION 2 through
> Steam/Proton, NVIDIA GPU). Other distributions, desktops and Wine setups are untested.

Unofficial and not affiliated with NCSOFT. Using third-party tools can put your game
account at risk — read [LEGAL.md](LEGAL.md).

## What it does

- **Collection:** every pet's rank and souls toward the next rank, exact while the
  game stream is continuous, clearly marked "last known" otherwise.
- **Tracker overlay:** pets you just earned souls for (Recent) or whose creatures are
  around you (Nearby), pinned pets on top, farming goals and session stats.
- **Black Cloud Merchant:** offers worth buying for pets you have not maxed.
- **Live map:** a mini-map (circle or rectangle) and a full map (Verteron, Altgard)
  with your position, pet spawns and the best farming spots for what you still need.
  Your position comes from the in-game minimap when it is visible (about 2 m), exact
  server positions after skills and hits, or a steady estimate from nearby creatures.
- **No relog needed:** started mid-session it recognises your character from game
  traffic; one login with the tracker running syncs exact soul totals.

## Install (CachyOS / Arch)

```sh
sudo pacman -S --needed git libpcap libcap fontconfig libx11
# .NET SDK 10.0.4xx into your home folder (build tool only):
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --version 10.0.401 --install-dir ~/.dotnet
git clone https://github.com/arnosantgaming-jpg/arno-pet-tracker.git
cd arno-pet-tracker
bash tools/build.sh
bash tools/install.sh        # asks for your password once; add --no-autostart to skip login start
```

Then start **Arno's Pet Tracker** from the menu before logging into your character.
On KDE, **Settings → Keep tracker and mini-map above the game** keeps the overlays
above a fullscreen game. Update with `git pull && bash tools/build.sh && bash tools/install.sh`.
On first start it imports the original Pet Tracker's saved collection, goals, kills and
settings from `~/.local/share/PetTracker` (read only). Its own data lives in
`~/.local/share/arno-pet-tracker`.

## How it works

| Part | Role | Privileges |
| --- | --- | --- |
| [`src/Capture`](src/Capture) `arnoa2-capture` | Finds the game connection through `/proc`, captures its incoming TCP stream with libpcap, puts it back in order and forwards the bytes over an owner-only Unix socket. No decoding. | `cap_net_raw` (file capability), dropped on the capture thread once the handle is open |
| [`src/Protocol`](src/Protocol) | Framing, LZ4 bundles, one typed decoder per game message | none |
| [`src/Tracker`](src/Tracker) | Pet ledger, kills, Recent/Nearby, pins, goals, session, merchant, storage | none |
| [`src/App`](src/App) `arno-pet-tracker` | Tracker overlay, mini-map, full map, pet finder with session stats, settings; reads the daemon's socket and the in-game minimap | none |

It only reads the game's incoming network traffic (and, for the map, the pixels of
the in-game minimap). It never reads or changes game memory and never sends anything
to the game. The client's own outgoing traffic is encrypted and not used.

**Soul ledger.** Each pet has a rank, a soul balance and a certainty. At login the
game sends the whole collection; after that, balance = login value + gains − spends,
in whatever order the messages arrive, and a rank-2 pet reaching 75 becomes max by
itself. Any capture gap turns balances into "last known" (shown with ≈) until the
next login.

## Build and check

Needs the .NET SDK 10.0.4xx (see `global.json`) and `libpcap`.

```sh
dotnet run --project tests/ProtocolTests
dotnet run --project tests/TrackerTests
dotnet run --project tests/CaptureTests
dotnet run --project tests/AppTests -- test-results/app   # renders every window
# With a capture of your own game session (keep it private):
dotnet run --project tools/Replay -c Release -- --tracker ~/path/to/recording.pcap
# Live, next to the running game (asks for your password once for cap_net_raw):
bash tools/try-live-capture.sh 60
```

Status of the checks when the core was written (2026-10-24), against the original
Pet Tracker's decoders and engine on two real recordings (~17,000 frames): identical
frames and messages, and the same final state for all 305 pets (ranks, trusted
balances, kills, session souls). A live 60-second run beside the game: 3,425 frames,
0 faults, 0 gaps, login collection captured.

## Credits

The game message formats were worked out by others; this code was written with their
work as reference:

- **[0Fox2Give/PetTracker](https://github.com/0Fox2Give/PetTracker)** (GPL-3.0) —
  collection, soul, rank, NPC, hit and merchant message layouts; the pet catalog and
  creature, merchant and acquisition data.
- **[SkeeveAN/Aion-DPS-Meter](https://github.com/SkeeveAN/Aion-DPS-Meter)** (MIT) —
  position, zone and movement layouts, map tiles, spawn data and map calibration.
- **[taengu/A2Tools-DPS-Meter](https://github.com/taengu/A2Tools-DPS-Meter)** (GPL-3.0)
  — the KDE window-rule approach for overlays above fullscreen games.

Details: [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
Game names and art belong to NCSOFT.

## License

GPL-3.0-only for the code — see [LICENSE](LICENSE). Game material and third-party
data are outside that grant.
