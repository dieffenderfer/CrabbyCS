commit and push as each of these is achieved (don't do everything in one mega commit)

### 🐛 Bug Fixes & Glitches

* **Radio Metadata:** Fix the classical station so it stops displaying electronic/rap/pop song titles. (This might not be something that we can control but it happens consistently, might be something wrong with the source...  it's only this station that does this... just maybe take a look at least)
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
* Change text animations so they only activate when the user's cursor hovers over them (currently active by default).
* Update the semi-transparent input window to a retro-themed input box.
* ✅ Prevent single words from splitting across multiple lines.


* **Help Windows/Popups UI:**
* ✅ Remove the 'Minimize' button from all Help windows and popups.
* ✅ Remove the hard-to-read, ugly gray instructional text at the bottom (e.g., "click x").
* ✅ Ensure Help popups are not rendered thinner than the parent window that contains them (reference the Klotski help popup for the fix).


* **OS Navigation:** Implement better general UI organization, utilizing folders.
* **Fonts:** Create and integrate a custom Windows 98-style font (if current font clearance/licensing issues cannot be resolved).

### 🖱️ Mouse, Cursor & "Cheese" Mechanics

* ✅ **Z-Index:** Ensure the mouse pet is always on top of all other elements.
* **Theme Update (Amoeba):** add an "Amoeba dripped window effect" that can be activated, windows 98 sprite art style green slime "drips"
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
* Implement rate limiting for users sending out friend requests (if this makes sense-- it might not make sense per the way it's set up, i'm not up to speed on it)


* **Setup/Uninstallation Wizard:** Build a setup wizard where users can manage their installation. It should function primarily as an uninstaller ("configure their setup/installation") and display the file size of everything being removed/added and totals.
* **Audio Core:**
* Record and integrate custom system sounds.
* Create a Sound Source Manager.
* Create a minimal Audio Editor app.


* **Productivity & Utilities:**
* Create a Clipboard Manager.
* Create an E-reader / Reader app with Text-to-Speech capabilities.
* Create minimal "Office" writing tools (Notepads, Word document editor, WordStar clone).
* Create a Spreadsheet reader.


* **Atmosphere:**
* Add "screensavers" that are laid over the user's screen (specifically, a fish aquarium screensaver). I'd like the option of the screensaver having a black background or transparent background so that the elements directly appear on the user's screen
* Add a Sleep Sounds generator mode, cozy scenes with sounds.



### 🎮 Games & Entertainment

* **Radio:** Audit the two visual lines on the varispeed controls that are to the left of middle to ensure they are logically placed.
* **Chess Puzzles:**
* Add extensive configuration options (e.g., mate-in-X only, setting specific rating ranges).
* Add a "Training" mode with very easy puzzles to teach users how to play.
* Improve the UI to make it easier to advance to the next puzzle.


* ✅ **Desktop Destroyer:** Add the ability to click and drag to spawn lots of ants at once.
* **Go Figure Game:** Audit the game to ensure it is actually possible to win. Determine its core mechanics/basis, and add a feature to show solutions or hints.
* **Golf Game:** Redesign needed. Choose one of two paths:
* *Path A (Minigolf):* Keep the current "putting" mechanics, but re-theme it and add putting obstacles.
* *Path B (Full Golf):* Extend the golf bar range, make levels longer, and implement wind and real lift physics.


* **New Game Additions:**
* Add a passive fishing game/mechanic.
* Add a Drum Machine app / music maker.
* Add DOS-style tools and games. Remake Wordstar writing program
* Research and replicate classic macOS apps.
* Replicate more games / applications from the "201 Learning Games" collection (/Users/david/Downloads/201 Learning Games) and copy their categorization/folder organization.
* Minimal writing apps, notepad plaintext program, wordpad clone with markdown support
