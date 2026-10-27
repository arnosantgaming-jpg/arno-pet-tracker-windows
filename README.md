# Arno's Pet Tracker — Windows

The Windows port of [Arno's Pet Tracker](https://github.com/arnosantgaming-jpg/arno-pet-tracker),
a companion for **AION 2** pet farming: your pet ranks and soul totals, what you are
farming right now, and where to go next on a live map.

> **Status:** confirmed working on Windows by a community tester. Feedback and bug
> reports are welcome — see [TESTING.md](TESTING.md) for what to include. Linux
> players: use the [Linux repo](https://github.com/arnosantgaming-jpg/arno-pet-tracker).

Unofficial and not affiliated with NCSOFT. Using third-party tools can put your game
account at risk — read [LEGAL.md](LEGAL.md).

## What it does

- **Collection:** every pet's rank and souls toward the next rank, exact while the
  game stream is continuous, marked "last known" (≈) otherwise.
- **Tracker overlay:** pets you just earned souls for (Recent) or whose creatures are
  around you (Nearby), pinned pets on top, farming goals and session stats.
- **Black Cloud Merchant:** offers worth buying for pets you have not maxed.
- **Live map:** a mini-map (circle or rectangle) and a full map (Verteron, Altgard)
  with your position, pet spawns and the best farming spots for what you still need.
  Your marker is an arrow pointing your camera, read from the view cone on the in-game
  minimap; the mini-map can turn with it (hover → N / ↻) or stay north up.
- **No relog needed:** started mid-session it recognises your character; one login
  with the tracker running syncs exact soul totals.

## Install

1. Download **`ArnoPetTracker-win-Setup.exe`** from the
   [latest release](https://github.com/arnosantgaming-jpg/arno-pet-tracker-windows/releases)
   and double-click it. It installs for your user only (no administrator rights) and
   opens the tracker. Windows may say the publisher is unknown, because the program is
   not signed: choose **More info → Run anyway**.
2. The first time, the tracker asks you to install **Npcap**, a free helper it needs to
   read the game's network messages. Click **Download Npcap**, run that installer with
   its default options (leave *"Restrict Npcap driver's access to Administrators only"*
   unticked), then click **Check again**.
3. Start **Arno's Pet Tracker** from the Start menu before logging into your character.
   To have it start by itself, open **Settings → Start with Windows**.

Uninstall it like any app: **Settings → Apps → Arno's Pet Tracker → Uninstall**.
Prefer not to install? Download **`ArnoPetTracker-win-x64.exe`** instead: one
self-contained program, nothing to install, just double-click it (it unpacks itself to a
private cache folder on first start, so that start takes a few seconds). Npcap is still
needed.

Run AION 2 in **borderless** or **windowed** mode: Windows keeps "always on top"
windows above those, and the minimap reader can see the game. In exclusive fullscreen
the overlays may hide behind the game.

VPNs and game boosters such as ExitLag should work: the tracker listens on every adapter,
including Npcap's loopback adapter, where a booster's local relay hands the game its
traffic. Keep Npcap's default *"Support loopback traffic"* option ticked.

On first start the tracker imports the original Pet Tracker's saved collection, goals,
kills and settings from `%LOCALAPPDATA%\PetTracker` (read only). Its own data lives in
`%LOCALAPPDATA%\arno-pet-tracker`.

## How it works on Windows

| Part | Windows |
| --- | --- |
| Capture (`capture\arnoa2-capture.exe`) | Npcap on every non-loopback adapter; finds the game's connections in Windows' TCP table (owning process `AION2` / `AION2-Win64-Shipping`); forwards the incoming game stream, in order, to the app over a private local socket. No decoding. |
| Decoding and tracking | Shared with the Linux version, unchanged |
| Minimap reading | Copies the game's minimap corner from the screen (GDI); needs the game visible in borderless or windowed mode, and our own mini-map not placed over the game's minimap |

It only reads the game's incoming network traffic and, for the map, screen pixels. It
never reads or changes game memory and never sends anything to the game.

## Build from source

Needs the .NET SDK 10.0.4xx (`global.json`).

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1     # on Windows
```

```sh
bash tools/build-windows.sh                                   # cross-build from Linux
```

Both produce `build\win-x64` and `dist\arno-pet-tracker-windows.zip`. The one-click
installer is made with [Velopack](https://velopack.io) (`dotnet tool install -g vpk`, then
`bash tools/package-windows.sh 1.0.3`). Checks
(`dotnet run --project tests/ProtocolTests`, `TrackerTests`, `CaptureTests`, `AppTests`)
run on Windows or Linux; the code base still builds for both.

## Credits

The game message formats were worked out by others; this code was written with their
work as reference:

- **[0Fox2Give/PetTracker](https://github.com/0Fox2Give/PetTracker)** (GPL-3.0) —
  collection, soul, rank, NPC, hit and merchant message layouts; the pet catalog and
  creature, merchant and acquisition data.
- **[SkeeveAN/Aion-DPS-Meter](https://github.com/SkeeveAN/Aion-DPS-Meter)** (MIT) —
  position, zone and movement layouts, map tiles, spawn data and map calibration.

Details: [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
Npcap is not bundled; it is installed separately under its own license. Game names and
art belong to NCSOFT.

## License

GPL-3.0-only for the code — see [LICENSE](LICENSE).
