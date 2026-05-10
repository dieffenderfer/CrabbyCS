# Crabby Radio — standalone build

A standalone, normal-windowed version of the Crabby radio for distribution
on itch.io. It shares source with the main CrabbyCS pet app: the same
`RadioWidget`, `RadioPlayer`, station library / editor, and visualizers
are pulled in via `<Compile Include="..\..\..">` globs in the csproj. There
are no copied files — improvements to the radio in the main app flow
into this binary on the next build and vice versa.

## What's different from CrabbyCS

| | CrabbyCS pet | Crabby Radio standalone |
|---|---|---|
| Window | Transparent always-on-top overlay | Normal decorated window |
| Hosts | Pet + activities + radio | Just the radio |
| Save dir (Win) | `%APPDATA%\MouseHouse\` | `%APPDATA%\CrabbyRadio\` |
| Save dir (mac) | `~/Library/Application Support/MouseHouse/` | `~/Library/Application Support/CrabbyRadio/` |
| Save dir (Linux) | `~/.local/share/MouseHouse/` | `~/.local/share/CrabbyRadio/` |
| Closing the window | Closes the radio panel | Quits the app |

The savedir split is enforced by `SaveManager.AppFolderName = "CrabbyRadio"`
at the very top of `Program.cs`, before any I/O.

## Runtime requirements

Same as the pet's radio companion:
- **ffmpeg** on PATH for the OB-4-style scrub wheel + recording. Without
  it, playback falls back to ffplay/mpv (still works, but no tape /
  scrubbing / MP3 record).

## Local build

```bash
# Debug
dotnet build

# Release
dotnet build -c Release
```

Output: `bin/Debug/net10.0/CrabbyRadio` (or the equivalent under Release).

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
tree (fonts) and the Raylib native library. Zip the folder and upload
to the matching itch.io channel:

```bash
cd publish && zip -r CrabbyRadio-osx-arm64.zip osx-arm64/
```

> **Note for itch.io macOS builds:** the published binary is unsigned.
> Users will need to right-click → Open the first time, or run
> `xattr -dr com.apple.quarantine CrabbyRadio` after extracting. Document
> this on the itch.io page or sign with a Developer ID before shipping.

## Renaming the product

Change `<AssemblyName>` in `MouseHouse.RadioStandalone.csproj` and
`WindowTitle` in `Program.cs`. The save folder name is the string passed
to `SaveManager.AppFolderName` in `Program.cs` — change that too if
you want fresh user-data for the renamed product.

## What about the main pet?

The main pet app is unaffected by this folder. `Crabby.csproj` excludes
`MouseHouse.RadioStandalone/**` from its auto-include glob (same trick
already used for `MouseHouse.Activities/**`), so `dotnet build` from the
repo root produces only the pet binary. Building the standalone is a
separate, opt-in step.
