# Testing on Windows

A first Windows test takes about ten minutes. Note what you see at each step.

1. **Install** Npcap (default options) and the tracker (see README). Start it from the
   Start menu. The tracker window should open; the status line should say
   *Waiting for AION 2…* — not an Npcap or permission error.
2. **Log in** to your character with the tracker running. Within a few seconds the
   status should show your character and **live**, and your pets should have exact
   soul totals (no ≈).
3. **Souls:** kill a pet creature. Its pet should appear under *Recent* with the new
   total, and the session line should count it.
4. **Mini-map:** open it. Your dot should be in the middle; the map should match where
   you are in Verteron or Altgard. Hover it and try opacity, zoom, and the ◯/▢ shape
   button. Walk around: the dot should follow.
5. **Mini-map reading:** open **Map**; under the checkboxes it should say
   *Minimap lock (0.4–0.6)* while the game's minimap is visible. If it says
   *not matched*, note your resolution and UI scale.
6. **On top:** with the game in borderless mode, the tracker and mini-map should stay
   above it.
7. **Restart** the tracker while in game: it should recognise your character again
   within about 30 seconds without relogging.

If something fails, the most useful details are: the status line text, your Windows
version, the game's display mode and resolution, and whether Npcap was installed with
default options. Do not post packet captures, login data or your collection files.
