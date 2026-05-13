commit and push as each of these is achieved (don't do everything in one mega commit)

### 🐛 Bug Fixes & Glitches

* ✅ **Radio Metadata:** Fix the classical station so it stops displaying electronic/rap/pop song titles. (Root cause is upstream — SRG SSR's Icecast mountpoint publishes wrong titles. Mitigation: new SkipMetadata flag on RadioStation + URL fallback keeps the now-playing LCD blank for that source.)
* **Radio Varispeed:** Fix the glitch that occurs on the varispeed dial between the "slowed down" and "going in reverse" settings. It glitches out.
* **Buddy List:**
* ✅ Fix the issue preventing the Buddy List window from closing.
* ✅ Remove the hardcoded yellow color from the Buddy List header, let the header be reskinned according to the Retro Theme selection


* **Window Interaction Glitch:** Fix the bug where clicking and holding a window (like a drag, but stopping the mouse movement while continuing to hold the click) causes the window to slide around weirdly.
* **Context Menus:**
* ✅ Fix the right-click menu on the Status feature—it currently says "clear status" instead of "edit status" when a status is active.
* ✅ Fix the Chess Puzzles window so right-clicking correctly brings up the retro theme switcher.


* ✅ **Help Popups Overflow:** Fix the text overflowing outside of Help popups.

### 🎨 UI/UX & Visual Polish

* ✅ **Window Controls:** Unify the line widths of the 'Minimize' and 'X' (Close) buttons so they match.
* **Status Feature UI:**
* ✅ Change text animations so they only activate when the user's cursor hovers over them (currently active by default).
* ✅ Update the semi-transparent input window to a retro-themed input box.
* ✅ Prevent single words from splitting across multiple lines.


* **Help Windows/Popups UI:**
* ✅ Remove the 'Minimize' button from all Help windows and popups.
* ✅ Remove the hard-to-read, ugly gray instructional text at the bottom (e.g., "click x").
* ✅ Ensure Help popups are not rendered thinner than the parent window that contains them (reference the Klotski help popup for the fix).


* **OS Navigation:** Implement better general UI organization, utilizing folders.
* **Fonts:** Create and integrate a custom Windows 98-style font (if current font clearance/licensing issues cannot be resolved).

### 🖱️ Mouse, Cursor & "Cheese" Mechanics

* ✅ **Z-Index:** Ensure the mouse pet is always on top of all other elements.
* ✅ **Theme Update (Amoeba):** add an "Amoeba dripped window effect" that can be activated, windows 98 sprite art style green slime "drips" (procedural pixel-art drips beneath radio, buddy list, and activity panels — toggle in Appearance menu)
* **"Cheese" Feature Rework:**
* Introduce other mice (in various colors-- original sprites recolored) that run into the screen who try to eat the cheese before the mouse pet does.
* Allow the user to select one of these different colored mice as the "pet."
* Fix the scaling: The mouse pet and new incoming mice should be scaled 1x to match the scaling of the cheese.
* Add 2x more types of cheese. I will provide the sprites.



### 🛠️ OS Features & Core Apps

* **Social & Buddy System:**
* ✅ Make the Buddy Code hidden by default (click to reveal).
* ✅ Add a button to quickly generate a new Buddy Code if the current one is compromised.
* ✅ Add a quick-copy button for the Buddy Code.
* ✅ Implement rate limiting for users sending out friend requests (per-code 10-second cooldown; UI shows a "slow down" hint when throttled)


* **Setup/Uninstallation Wizard:** Build a setup wizard where users can manage their installation. It should function primarily as an uninstaller ("configure their setup/installation") and display the file size of everything being removed/added and totals.
* **Audio Core:**
* Record and integrate custom system sounds.
* Create a Sound Source Manager.
* Create a minimal Audio Editor app.


* **Productivity & Utilities:**
* ✅ Create a Clipboard Manager. (polls OS clipboard once per second, deduped 50-entry history with click-to-restore, persists to clipboard.json)
* Create an E-reader / Reader app with Text-to-Speech capabilities.
* Create minimal "Office" writing tools (Notepads, Word document editor, WordStar clone). (Partial: minimal Notepad shipped — auto-saves notepad.txt, Save Copy creates timestamped snapshots. Word/WordStar still open.)
* ✅ Create a Spreadsheet reader. (read-only CSV viewer: lists files in spreadsheets/, parses quoted fields, zebra-striped scrollable grid, first-row-as-header toggle)


* **Atmosphere:**
* ✅ Add "screensavers" that are laid over the user's screen (specifically, a fish aquarium screensaver). I'd like the option of the screensaver having a black background or transparent background so that the elements directly appear on the user's screen (procedural fish — ellipse body + animated tail + eye dot — with Aquarium/Transparent background toggle)
* ✅ Add a Sleep Sounds generator mode, cozy scenes with sounds. (5-track ambient mixer — rain/fireplace/ocean/wind/night — with per-track faders, presets, and persistent mix)



### 🎮 Games & Entertainment

* ✅ **Radio:** Audit the two visual lines on the varispeed controls that are to the left of middle to ensure they are logically placed. (verified: ticks sit at -1× reverse and 0× stopped — both meaningful detents)
* **Chess Puzzles:**
* Add extensive configuration options (e.g., mate-in-X only, setting specific rating ranges). (Partial: Mate Only toggle in menu — Lichess endpoint has no server filter so we client-side-reject up to 8 non-mate puzzles per Next.)
* ✅ Add a "Training" mode with very easy puzzles to teach users how to play. (Training menu toggle caps Lichess puzzle rating at 1100; refetches up to 8 times per Next)
* ✅ Improve the UI to make it easier to advance to the next puzzle. (Enter / Space now advances after solve)


* ✅ **Desktop Destroyer:** Add the ability to click and drag to spawn lots of ants at once.
* ✅ **Go Figure Game:** Audit the game to ensure it is actually possible to win. Determine its core mechanics/basis, and add a feature to show solutions or hints. (deals are now solver-validated; Hint menu reveals a working expression)
* **Golf Game:** Redesign needed. Choose one of two paths:
* *Path A (Minigolf):* Keep the current "putting" mechanics, but re-theme it and add putting obstacles.
* *Path B (Full Golf):* Extend the golf bar range, make levels longer, and implement wind and real lift physics.


* **New Game Additions:**
* Add a passive fishing game/mechanic.
* ✅ Add a Drum Machine app / music maker. (4-track 16-step sequencer with synthesized kick/snare/hat/clap, tempo slider 60-200 BPM, demo pattern, persistent)
* Add DOS-style tools and games. Remake Wordstar writing program
* Research and replicate classic macOS apps.
* Replicate more games / applications from the "201 Learning Games" collection (/Users/david/Downloads/201 Learning Games) and copy their categorization/folder organization.
* Minimal writing apps, notepad plaintext program, wordpad clone with markdown support
