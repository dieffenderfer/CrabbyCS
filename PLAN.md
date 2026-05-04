# Microsoft Entertainment Pack — Clone Plan

Goal: ship faithful in-app clones of every game from the four Microsoft
Entertainment Packs (1990–1992), rendered in authentic Windows 3.1 / 95 / 98
chrome. The visual target is the same fidelity bar as **jspaint** is to MS
Paint — a loving recreation, not a re-skin.

Each game runs as an `IActivity` (see `Scenes/Activities/IActivity.cs`),
opened from the pet's right-click menu, drawn into a centered opaque panel
over the transparent overlay window.

---

## Visual / chrome target

Every game shares one retro-Windows look so the suite feels coherent.
Build it once in Stage 0; every game thereafter just consumes it.

- 3D-beveled window chrome: 1px white top/left, 1px dark-grey bottom/right,
  light-grey (`#C0C0C0`) face — the canonical Win9x raised/sunken pattern.
- Title bar: navy `#000080` gradient with white MS Sans Serif-style title,
  minimize/close buttons drawn as beveled square buttons with pixel glyphs.
- Menu bar: File / Game / Help, with underlined accelerator letters.
- Status bar at the bottom of the window with sunken inset.
- Buttons, group boxes, scroll bars, checkboxes, radios — all drawn as
  pixel-accurate Win9x widgets.
- Bitmap font that matches MS Sans Serif 8pt for body text, fixed pitch
  for scoreboards.
- Mouse cursor stays the OS cursor (the overlay is transparent); no custom
  cursor sprite.

The pet's existing window chrome is close but not pixel-accurate to Win9x —
Stage 0 replaces it with the shared widget kit.

---

## Architecture additions

New shared modules, all under `Scenes/Activities/Retro/`:

- `RetroSkin.cs` — color palette, bevel helpers (`DrawRaised`, `DrawSunken`),
  font metrics.
- `RetroWidgets.cs` — `Button`, `Checkbox`, `Radio`, `GroupBox`, `MenuBar`,
  `StatusBar`, `Dialog`, `ScrollBar`. Immediate-mode API matching the
  existing activity update/draw style.
- `CardKit.cs` — 52-card deck, shuffle, render, drag/cascade math, foundation
  / tableau / stock / waste primitives. Used by every solitaire variant.
- `TileGrid.cs` — generic NxM tile board with mouse picking, used by
  Minesweeper, TetraVex, Klotski, Pipe Dream, Life, Rodent's Revenge.
- `RetroDialogs.cs` — New Game, High Scores, About, Options modals.
- `ScoreStore.cs` — per-game high score persistence via existing
  `SaveManager` (one JSON blob keyed by game id).

Existing activities (Solitaire, Chess, etc.) get migrated onto the new
chrome opportunistically — not a blocker for new games.

---

## Game inventory (all four packs)

### Pack 1 (1990)
1. Cruel — single-deck patience
2. Golf — column-clearing patience
3. Minesweeper
4. Pegged — peg solitaire
5. Taipei — mahjong solitaire (turtle layout)
6. Tetris (Windows version)
7. TicTactics — 4×4 tic-tac-toe variant
8. IdleWild — screensaver collection (deferred; see notes)

### Pack 2 (1991)
9. FreeCell
10. Jigsawed — jigsaw puzzle
11. Pipe Dream — route flowing water through pipe tiles
12. Rattler Race — multiplayer-style snake
13. Rodent's Revenge — push blocks to trap cats
14. Stones — abstract strategy
15. Tut's Tomb — pyramid solitaire variant

### Pack 3 (1991)
16. Fuji Golf — top-down golf
17. Klotski — sliding-block puzzle
18. Life Genesis — Conway's Life with 2-player mode
19. SkiFree
20. TetraVex — edge-matching tile puzzle
21. TriPeaks — three-peak solitaire
22. WordZap — word-finding race

### Pack 4 (1992)
23. Chess
24. Chip's Challenge
25. Dr. Black Jack
26. Go Figure! — numeric puzzle
27. JezzBall — carve walls to trap bouncing balls
28. Maxwell's Maniac — pattern / chase
29. Tic Tac Drop — Connect-Four variant with custom boards

### Deferred / out of scope
- **IdleWild screensavers** (Packs 1 & 2): the host app is itself a
  desktop overlay, not a screensaver host. Revisit after Pack 4 — could
  ship as ambient `EventBase` types that briefly take over the screen.
- **WordZap wordlist**: needs a ~10k-word dictionary asset; license a
  permissive list (e.g. SCOWL) before implementation.
- **Chess** already exists as `ChessPuzzleActivity`; Pack 4 work re-skins
  it and adds a play-against-AI mode rather than starting fresh.

---

## Pack 1 staged plan (the immediate work)

Pack 1 is deliberately ordered to front-load the shared infrastructure so
later packs become mostly content work.

### Stage 0 — Retro chrome kit *(prerequisite for everything)*
Build `RetroSkin`, `RetroWidgets`, `RetroDialogs`, `ScoreStore`. Validate
by porting one existing activity (`SolitaireActivity`) to the new chrome
end-to-end. Acceptance: the ported activity is pixel-indistinguishable from
a Win98 screenshot at 1× scale.

### Stage 1 — Minesweeper
Smallest scope, highest recognizability, exercises `RetroWidgets` (smiley
button, 7-seg LCD counters, beveled cell grid). Three difficulty presets
+ custom dialog. Right-click flag, chord-click reveal, win/lose face
states, high-score persistence per difficulty.

### Stage 2 — CardKit + Cruel + Golf
Build `CardKit` once, then ship two solitaires off it. Cruel is the
strictest test of the deal/redeal API; Golf is the simplest tableau →
waste rule set. Both validate drag-and-drop and undo.

### Stage 3 — Taipei
Builds the layered tile picker (z-buffer for tile occlusion). Ship with
the canonical turtle layout in JSON; layout format is reused later by
Solitile-style variants. Tile art: monochrome mahjong glyphs to match
the 1990 EGA look.

### Stage 4 — Pegged
Trivial once `TileGrid` exists from Minesweeper. Triangular and English
cross board layouts, undo, "you left N pegs" end screen.

### Stage 5 — TicTactics
4×4 board, AI opponent (minimax with alpha-beta — branching factor is
small enough). Difficulty selector in the Game menu.

### Stage 6 — Tetris
Largest Pack 1 item. Standard 10×20 well, 7-bag randomizer, SRS-lite
rotation (the 1990 version predates SRS — use simple rotation to match),
soft/hard drop, line-clear flash, level-based gravity, next-piece preview,
high-score table with initials entry. Sound effects optional.

### Pack 1 exit criteria
- All seven games launchable from the right-click menu under a "Games ▶"
  submenu grouped by pack.
- Each game: New Game, Help → About, high-score persistence, close button
  returns cleanly to the pet scene.
- Visual diff against reference screenshots reviewed for each game.

---

## Pack 2–4 plan (sketch)

Detailed plans land when each pack starts; the order below is the
intended sequence.

**Pack 2** — FreeCell and Tut's Tomb reuse `CardKit`. Pipe Dream,
Rodent's Revenge, and Rattler Race share a fixed-tick `TileGrid`
simulation loop — build that loop once at the top of Pack 2. Stones and
Jigsawed are one-offs at the end.

**Pack 3** — Life Genesis first (smallest, validates the 2-player input
split). TriPeaks reuses `CardKit`. TetraVex and Klotski reuse `TileGrid`.
SkiFree and Fuji Golf each need a scrolling viewport — build that helper
during SkiFree. WordZap last, gated on the dictionary asset.

**Pack 4** — Chess re-skin first (cheapest, code mostly exists). JezzBall
and Maxwell's Maniac need real-time physics — share a `BouncingBall`
helper. Chip's Challenge is the single largest item in the whole project
(level format, ~30 tile behaviors, 149 official levels we can't ship —
plan to ship a small set of original levels plus a level-loader for
user-supplied files). Dr. Black Jack, Go Figure!, and Tic Tac Drop are
small finishers.

---

## Risks and open questions

- **Asset licensing.** Card faces, mahjong tiles, and Tetris tetromino
  colors should be redrawn from scratch to avoid IP issues. No bitmap
  rips from the original packs. Game *rules* are not copyrightable;
  *names* mostly are — final shipping names may need to differ
  ("MouseSweeper", "Mahjong Solitaire", etc.). Decide naming policy
  before Stage 1.
- **Window scaling.** The pet overlay is fullscreen at native resolution;
  Win9x widgets are designed for 96 DPI. Need a global integer scale
  factor in `RetroSkin` so games are legible on 4K displays.
- **Input model.** `IActivity.Update` already gets mouse state; keyboard
  input for Tetris / TicTactics needs a small addition to the interface
  (or read `Raylib.IsKeyPressed` directly inside the activity — current
  pattern in `ChessPuzzleActivity`).
- **Audio.** No sound system exists yet. Tetris and Minesweeper are
  playable silent; defer audio until after Pack 1.
- **Scope realism.** 28 games is a multi-month project. The plan is
  staged so each pack ships independently — Pack 1 alone is a
  satisfying release.
