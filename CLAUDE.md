# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build              # Debug build
dotnet run                # Run the app
dotnet build -c Release   # Release build (trimmed, single-file)
dotnet publish -c Release # Publish platform binaries to publish/
```

Target: .NET 10.0, self-contained. Dependencies: Raylib-cs 6.1.1 (graphics), ENet-CSharp 2.4.8 (networking). No test suite or linter.

## Architecture

**MouseHouse** is a desktop pet app — a pixel-art mouse that lives on a fullscreen transparent overlay window. The assembly name is MouseHouse despite the repo name CrabbyCS.

### Core loop

`Program.cs` → `App.Run()` → creates the transparent overlay window via Raylib, then runs a 60 FPS loop: `InputManager.Update()` → `DesktopPetScene.Update()` → `Draw()`. The window uses `ConfigFlags.TransparentWindow | TopmostWindow | MousePassthroughWindow` so the pet floats above all other windows.

### Key subsystems

- **PetStateMachine** — Enum-based state machine (Idle, Walking, Sleeping, Dragging, Thrown, Jumping). Each state has an `Enter*()` method and update logic dispatched by the current state. Handles animation frame advancement, gravity physics, and drag-throw mechanics. Sprites are 76×76 pixel frames in horizontal strip spritesheets.

- **Activities** — Mini-games (Fishing, Solitaire, Paint, Dance, etc.) implementing `IActivity` (PanelSize, Load, Update, Draw, Close, IsFinished). Only one active at a time; rendered as a centered opaque panel over the transparent overlay. Opened via the right-click context menu.

- **Events** — Ambient creatures/weather (seagulls, butterflies, rain) spawned by `EventManager` from a weighted table. Each extends `EventBase` with movement patterns. Max one active at a time.

- **Color modes** — Three sprite sets ("2color", "1color", "fullcolor") with separate asset files per animation. Stored as a `Dictionary<string, SpriteSheetSet>` and hot-swappable via the context menu.

- **WindowHelper** — Platform-specific click-through toggle. macOS uses Objective-C runtime P/Invoke (NSWindow.setIgnoresMouseEvents). Windows uses GLFW WS_EX_LAYERED/WS_EX_TRANSPARENT. Linux is stubbed. The passthrough is toggled per-frame based on whether the mouse is over the pet or UI.

- **Multiplayer** — `MultiplayerManager` wraps `INetworkTransport` (ENetTransport for online, OfflineTransport as fallback). Messages are JSON-serialized `NetMessage` types (position, chat, activity state).

- **Persistence** — `SaveManager` writes JSON to platform-specific app data dirs. `PetSettings` stores color mode, scale, mute state.

### Sprite conventions

Mouse sprites face left by default. `FlipH = _facingRight` is the correct flip convention — do not invert it.

### Adding a new activity

1. Create a class in `Scenes/Activities/` implementing `IActivity`
2. Add a menu item in `DesktopPetScene.ShowContextMenu()` with a unique ID
3. Add the handler in `OnMenuItemSelected()` calling `OpenActivity(new YourActivity(...))`

### Adding a new event type

1. Add a class extending `EventBase` in `Scenes/DesktopPet/Events/EventTypes.cs`
2. Register it in `EventManager`'s spawn table with a weight
