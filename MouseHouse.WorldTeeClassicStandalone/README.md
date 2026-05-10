# Ohio Golf — standalone build

A standalone, normal-windowed version of the World Tee Classic / Ohio
Golf game for distribution on itch.io. It shares source with the main
CrabbyCS pet app: the same `WorldTeeClassicActivity`, globe picker, bear
swarms, wildlife, planner / heightfield / physics, and retro chrome are
pulled in via `<Compile Include="..\..\..">` globs in the csproj. No
files are copied — improvements to the game in the main app flow into
this binary on the next build and vice versa.

## What's different from CrabbyCS

| | CrabbyCS pet | Ohio Golf standalone |
|---|---|---|
| Window | Transparent always-on-top overlay | Normal decorated window |
| Hosts | Pet + every activity | Just the golf game |
| Save dir (Win) | `%APPDATA%\MouseHouse\` | `%APPDATA%\WorldTeeClassic\` |
| Save dir (mac) | `~/Library/Application Support/MouseHouse/` | `~/Library/Application Support/WorldTeeClassic/` |
| Save dir (Linux) | `~/.local/share/MouseHouse/` | `~/.local/share/WorldTeeClassic/` |
| Closing the window | Closes the activity panel | Quits the app |
| Moon-unlock state | Tracked in pet's golf save | Tracked locally — unlocking it here doesn't unlock it in CrabbyCS, and vice versa |

The savedir split is enforced by `SaveManager.AppFolderName = "WorldTeeClassic"`
at the very top of `Program.cs`, before any I/O. That puts
`world_tee_classic.json`, the user-edited `courses/` folder, and the
Moon-unlock flag in their own data root.

## Window title vs assembly name

Two product names are floating around for this game right now:

- **`WorldTeeClassic`** — the executable / assembly name.
- **`Ohio Golf`** — the user-facing brand (window title, in-game chrome).

Both are pulled from a single source of truth — the activity's
`AppTitle` const for the window title, the csproj's `<AssemblyName>`
for the executable. Picking one and consolidating is a two-line edit.

## Runtime requirements

None beyond a modern desktop OS. All assets ship inside the published
folder (sound effects, splash card, world map mask, fonts).

## Local build

```bash
# Debug
dotnet build

# Release
dotnet build -c Release
```

Output: `bin/Debug/net10.0/WorldTeeClassic` (or the equivalent under Release).

## Publishing platform binaries for itch.io

itch.io accepts a zipped folder per platform. Run from this directory:

```bash
# macOS Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/osx-arm64

# macOS Intel
dotnet publish -c Release -r osx-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/osx-x64

# Windows x64
dotnet publish -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/win-x64

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/linux-x64
```

Each `publish/<rid>/` folder contains the executable plus the `assets/`
tree (golf sounds, splash, tree sprite, world mask, chrome fonts) and
the Raylib native library. Zip the folder and upload to the matching
itch.io channel:

```bash
cd publish && zip -r OhioGolf-osx-arm64.zip osx-arm64/
```

> **Note for itch.io macOS builds:** the published binary is unsigned.
> Users will need to right-click → Open the first time, or run
> `xattr -dr com.apple.quarantine WorldTeeClassic` after extracting.
> Document this on the itch.io page or sign with a Developer ID before
> shipping.

## Renaming the product

- **Window title / in-game chrome:** edit `AppTitle` in
  `Scenes/Activities/WorldTeeClassicActivity.cs`. The standalone reads
  the same const so the rename flows everywhere automatically.
- **Executable name:** edit `<AssemblyName>` in
  `MouseHouse.WorldTeeClassicStandalone.csproj`.
- **Save folder:** edit the `SaveManager.AppFolderName` string in
  `Program.cs`. (Existing user data won't migrate — bump only if you
  also want fresh installs.)

## What about the main pet?

The main pet app is unaffected by this folder. `Crabby.csproj` excludes
`MouseHouse.WorldTeeClassicStandalone/**` from its auto-include glob
(same trick already used for `MouseHouse.Activities/**` and
`MouseHouse.RadioStandalone/**`), so `dotnet build` from the repo root
produces only the pet binary. Building the standalone is a separate,
opt-in step.
