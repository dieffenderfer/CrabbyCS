using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Data;
using MouseHouse.Net;
using MouseHouse.Rendering;
using MouseHouse.Scenes.Activities;
using MouseHouse.Scenes.Activities.Retro;
using MouseHouse.Scenes.Zones;
using MouseHouse.Scenes.DesktopPet.Cheese;
using MouseHouse.Scenes.DesktopPet.Costumes;
using MouseHouse.Scenes.DesktopPet.Events;
using MouseHouse.Scenes.DesktopPet.Reactions;
using MouseHouse.Scenes.DesktopPet.Toys;
using MouseHouse.UI;

namespace MouseHouse.Scenes.DesktopPet;

/// <summary>
/// Main desktop pet scene. Renders the mouse on a fullscreen transparent overlay.
/// </summary>
public class DesktopPetScene
{
    private readonly AssetCache _assets;
    private readonly InputManager _input;
    private readonly AudioManager _audio;
    private readonly MultiplayerManager _mp;
    private readonly PetStateMachine _pet;
    private readonly PopupMenu _menu;
    private int _screenWidth;
    private int _screenHeight;
    private PetSettings _settings;
    private EventManager _events = null!;

    // Active activity (rendered as a draggable panel)
    private IActivity? _activeActivity;
    private Vector2 _activityOffset; // top-left corner of the activity panel
    private bool _draggingActivity;
    private Vector2 _activityDragOffset;
    private const int ActivityTitleBarHeight = 28;

    // Color mode spritesheets
    private readonly Dictionary<string, SpriteSheetSet> _colorModes = new();

    // Status bubble above pet
    private readonly StatusBubble _statusBubble = new();

    // Floating radio widget — lives in the pet's always-on-top window so it
    // floats above other apps just like the pet does.
    // Floating radio widget — lives in the pet's always-on-top window so it
    // floats above other apps just like the pet does.
    private readonly RadioPlayer _radioPlayer = new();
    /// <summary>Last mtime seen on theme_sync.txt — drives the per-frame
    /// poll that picks up theme changes made by sibling processes
    /// (e.g. World Tee Classic's title-bar theme picker).</summary>
    private DateTime _lastThemeMtime = DateTime.MinValue;
    private readonly RadioWidget _radio;

    // Buddy list (AIM-style friends panel). Owns the broker
    // connection + friends.json + identity.json; the widget is the
    // user-facing window. Lazy-init in Load() so the broker connect
    // happens after first paint (otherwise an unreachable broker
    // would stall startup).
    private MouseHouse.Net.Buddies.BuddyService? _buddies;
    private MouseHouse.UI.BuddyList.BuddyListWidget? _buddyWidget;

    // Standalone-debug instance of the radio: launches the same widget in
    // the MouseHouse.Activities companion process (id 9). Lets the user
    // sanity-check the out-of-process port without making it the default.
    // Hidden under Tools → "Radio (Standalone Debug)".
    private System.Diagnostics.Process? _radioProc;

    // Most-recently-launched World Tee Classic companion process. We track
    // it so the per-frame snapshot can persist its open-state to settings,
    // and the next launch can auto-restore it.
    private System.Diagnostics.Process? _worldTeeProc;

    // Desktop destroyer — paints damage on the always-on-top transparent
    // overlay, so effects appear over the user's actual desktop.
    private readonly DesktopDestroyerOverlay _destroyer = new();

    // Track whether mouse is over an interactive element
    private bool _mouseOverPet;
    private bool _mouseOverUI;

    // Time update throttle
    private float _timeUpdateTimer;

    // Cheese-feeding mode
    private readonly CheeseManager _cheese = new();
    /// <summary>Position the popup menu was opened at — used as the drop point
    /// for cheese-tray menu items.</summary>
    private Vector2 _menuOpenPos;

    // Placeable toys (bed, wheel, water bottle, ball). Persistent across
    // restarts via toys.json in the save dir.
    private readonly ToyManager _toys = new();
    private ToyInstance? _currentToy;     // toy the pet is currently using
    private readonly Random _rng = new();

    // Soft tamagotchi stats. Decay slowly, refilled by interactions, drive
    // need-priority toy routing.
    private readonly PetNeeds _needs = new();

    // Floating reactions (hearts, sneeze stars, music notes) + costume choice.
    private readonly Reactions.PetReactions _reactions = new();
    private Costumes.CostumeType _costume = Costumes.CostumeType.None;
    // Hover-pet timer: time the cursor has been continuously over the pet
    // without moving much. Crosses _hoverPetThreshold → spawn a heart and
    // bump Happy. Resets on click / move-away.
    // Needs system disabled per user request 2026-05-08; these two fields
    // are still referenced by the (now-commented) UpdatePetting body so
    // they stay declared. Suppress unused warnings to keep the build clean.
#pragma warning disable CS0169
    private float _hoverPetTimer;
    private float _hoverHeartCooldown;
#pragma warning restore CS0169
    private const float HoverPetThreshold = 0.7f;
    private const float HoverHeartInterval = 0.9f;
    private float _snootCooldown;
    private float _zoomiesTimer;

    // Cheese placement mode — user picked "Place Cheese" from the menu
    // and enters a place-loop: every left-click drops a piece at the
    // cursor, then re-rolls the variety so click-click-click drops a
    // sequence of different cheeses. Right-click on empty desktop or
    // Esc exits the loop; right-click on the pet exits the loop AND
    // flows through to the pet's normal context menu.
    private bool _placingCheese;
    private CheeseType _placingCheeseType;
    /// <summary>Cached layout of the on-screen Cancel chip the
    /// cursor tooltip renders. Set every frame in DrawCheeseGhost,
    /// read by the click handler so it knows whether the user
    /// clicked the X (vs. an empty-desktop area, which would drop
    /// a cheese instead).</summary>
    private Rectangle _placementCancelChip;

    // Floating "doing the wave" cheese-name text spawned above the pet
    // when a piece is eaten — same effect as the golf celebration text,
    // smaller, with a rise-and-fade. Lives ~2 s.
    private class CheeseWaveText
    {
        public string Text = "";
        public Vector2 Anchor;       // pet head at spawn time
        public float Time;
        public float Life = 2.0f;
    }
    private readonly List<CheeseWaveText> _cheeseWaves = new();

    // Debug: where the most recent left-click was registered, in screen
    // pixels. Drawn as a small purple cross every frame so visual drift
    // between the OS cursor and our click hit-testing is obvious.
    // Currently disabled — see commented blocks in Update() and Draw().
    #pragma warning disable CS0169
    private Vector2? _debugLastClick;
    #pragma warning restore CS0169

    // Held by the capture-hysteresis logic so passthrough stays off briefly
    // after the cursor leaves the pet/UI region — see Update().
    private float _captureHoldTimer;

    public DesktopPetScene(AssetCache assets, InputManager input, AudioManager audio, MultiplayerManager multiplayer, int screenWidth, int screenHeight)
    {
        _assets = assets;
        _input = input;
        _audio = audio;
        _mp = multiplayer;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
        _pet = new PetStateMachine();
        _menu = new PopupMenu();
        // Load the cheese PNG sprites once so Place Cheese / the cheese
        // manager / the placement ghost all have the textures available.
        MouseHouse.Scenes.DesktopPet.Cheese.CheeseImages.Load(_assets);
        _menu.OnItemSelected += OnMenuItemSelected;
        _menu.OnItemHover += OnMenuItemHover;
        _settings = PetSettings.Load();
        _radio = new RadioWidget(_radioPlayer);
        _radio.StateChanged = SaveRadioState;
        // Right-click on the radio's title bar picks a retro theme — feed
        // the choice into the same save+broadcast path the pet's own
        // context-menu theme submenu uses (PetSettings + ThemeSync).
        _radio.ThemeCommitted = OnRadioThemeCommitted;

        // Buddy list: instantiate the service so identity/friend code
        // exists by the time the menu builds, but defer the broker
        // connect until Load() so a missing network doesn't stall
        // ctor. Widget hides itself until the user opens it.
        _buddies = new MouseHouse.Net.Buddies.BuddyService();
        _buddyWidget = new MouseHouse.UI.BuddyList.BuddyListWidget(_buddies);
        // Netplay golf races run in-process inside the pet (the
        // sibling activity host has no BuddyService — it's a
        // separate process), so the buddy widget asks the scene to
        // open the activity once both sides have agreed on the seed.
        _buddies.OpenNetplayGolfRequested += session =>
        {
            var golf = new MouseHouse.Scenes.Activities.WorldTeeClassicActivity();
            golf.ConfigureNetplay(session);
            OpenActivity(golf);
        };
        // Chess races run in-process for the same reason golf does —
        // the BuddyService lives in the pet, the sibling activity
        // host doesn't have one. Both clients land here on the same
        // event so they open simultaneously.
        _buddies.OpenNetplayChessRequested += session =>
        {
            var chess = new MouseHouse.Scenes.Activities.RetroChessPuzzlesActivity();
            chess.ConfigureNetplay(session);
            OpenActivity(chess);
        };
        _buddies.OpenNetplayTetrisRequested += session =>
        {
            var tetris = new MouseHouse.Scenes.Activities.TetrisActivity();
            tetris.ConfigureNetplay(session);
            OpenActivity(tetris);
        };
        // Hearts is 4-way host-mediated; the activity runs in-process
        // on every participating client. The host generated the
        // seed + seating in the buddy widget picker; shadows learned
        // both from the start_match envelope.
        _buddies.OpenNetplayHeartsRequested += session =>
        {
            var hearts = new MouseHouse.Scenes.Activities.HeartsActivity();
            hearts.ConfigureNetplay(session);
            OpenActivity(hearts);
        };

        _toys.Load();
    }

    /// <summary>
    /// Theme picked from the radio widget's right-click title-bar menu.
    /// Same plumbing as the pet's own context-menu theme submenu — save
    /// to PetSettings (so the pet remembers across restarts) and broadcast
    /// via ThemeSync so the radio station editor (and any other sibling
    /// process) live-update. The radio widget already updated
    /// <c>RetroSkin.Current</c> before invoking the callback.
    /// </summary>
    private void OnRadioThemeCommitted(string themeName)
    {
        _settings.RetroThemeName = themeName;
        _settings.Save();
        MouseHouse.Core.ThemeSync.Write(themeName);
        // Sync the preview baseline so a stray OnItemHover(-1) on the
        // pet's own context menu can't snap us back to the old theme.
        _committedThemeName = themeName;
    }

    private void SaveRadioState()
    {
        _settings.RadioVisible = _radio.Visible;
        _settings.RadioX = _radio.Position.X;
        _settings.RadioY = _radio.Position.Y;
        _settings.RadioStationIdx = _radio.StationIndex;
        _settings.RadioVolume = _radio.Volume;
        _settings.RadioVizMode = _radio.VizMode;
        _settings.Save();
        // Closing the radio via its own X (or any other widget-driven
        // visibility change) needs to restore the pet's always-on-top
        // level; opening it needs to drop the window so other apps can
        // stack over the radio.
        UpdateTopmost();
    }

    public void Load()
    {
        _colorModes["2color"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_walk.png", 8),
            Idle = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_idle.png", 8),
            Sleep = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_jump.png", 8),
        };

        _colorModes["1color"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_1c_walk.png", 8),
            Idle = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_1c_idle.png", 8),
            Sleep = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_1c_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_1c_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_1c_jump.png", 8),
        };

        _colorModes["fullcolor"] = new SpriteSheetSet
        {
            Walk = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_fc_walk.png", 8),
            Idle = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_fc_idle.png", 8),
            Sleep = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_fc_sleep.png", 12),
            SleepLoop = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_fc_sleep_loop.png", 3),
            Jump = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/mouse_fc_jump.png", 8),
        };

        var idleActions = LoadIdleActionSheets();
        foreach (var set in _colorModes.Values)
            set.IdleActions = idleActions;

        ApplyColorMode(_settings.ColorMode);

        // Restore the persisted retro theme. Empty / unknown name → leave
        // the default (Win95Default) in place.
        if (!string.IsNullOrEmpty(_settings.RetroThemeName))
        {
            foreach (var t in RetroSkin.AllThemes)
                if (t.Name == _settings.RetroThemeName) { RetroSkin.Current = t; break; }
        }

        if (_settings.ScaleOverride > 0.01f)
            _pet.Scale = _settings.ScaleOverride;

        _audio.Muted = _settings.Muted;
        MouseHouse.UI.AmoebaTheme.Enabled = _settings.AmoebaDrips;

        if (Enum.TryParse<CostumeType>(_settings.Costume, out var savedCostume))
            _costume = savedCostume;

        _events = new EventManager(_assets, _screenWidth, _screenHeight);
        _events.SetColorMode(_settings.ColorMode);

        FontManager.Init(_assets.BasePath);
        if (Enum.TryParse<TextureFilter>(_settings.FontFilter, out var filter))
            FontManager.SetFilter(filter);
        FontManager.SetLoadSize(_settings.FontLoadSize);
        FontManager.SetFont(_settings.FontFile);
        if (_settings.MenuFontSize > 0) PopupMenu.FontSize = _settings.MenuFontSize;

        _radio.Visible = _settings.RadioVisible;
        _radio.Position = new Vector2(_settings.RadioX, _settings.RadioY);
        _radio.Restore(_settings.RadioStationIdx, _settings.RadioVolume, _settings.RadioVizMode);
        UpdateTopmost();

        // Re-spawn the companion-process activities that were open at the
        // last shutdown. Each one is its own OS process; the pet's main
        // window stays pinned topmost while these get normal z-level.
        if (_settings.WorldTeeClassicOpen) LaunchRetroGame(246);
        // Note: RadioOpen is the standalone-debug instance; we deliberately
        // do NOT auto-restore it on launch since it's a debugging tool, not
        // the daily-use radio.

        _pet.Init(_screenWidth, _screenHeight);
        TimeSystem.Update();

        // Buddy list: kick off broker connect now that the first
        // paint is past. Non-blocking — if the network is missing,
        // the widget shows "Offline (broker unreachable)" and the
        // user can still see their own friend code + their stored
        // friends list (presence will just be stale).
        _buddies?.StartNetwork();
    }

    /// <summary>
    /// Best-effort cleanup on app quit. Currently used to publish
    /// an Offline presence + disconnect the buddy broker so friends
    /// see us go offline immediately rather than waiting for the
    /// MQTT will-message timeout. Safe to call multiple times.
    /// </summary>
    public void Shutdown()
    {
        try { _buddies?.Dispose(); } catch { }
    }

    private void ApplyColorMode(string mode)
    {
        if (!_colorModes.TryGetValue(mode, out var sheets))
            sheets = _colorModes["2color"];

        _pet.WalkSheet = sheets.Walk;
        _pet.IdleSheet = sheets.Idle;
        _pet.SleepSheet = sheets.Sleep;
        _pet.SleepLoopSheet = sheets.SleepLoop;
        _pet.JumpSheet = sheets.Jump;
        _pet.IdleActionSheets = sheets.IdleActions;
        _pet.RefreshActiveSheet();
    }

    public void Update(float delta)
    {
        TweenSystem.Update(delta);

        // Refill the radio's owned audio buffer once per frame so playback
        // never starves regardless of widget visibility / focus.
        _radioPlayer.Pump();

        // Watch for sibling-process theme writes (e.g. the user picked a
        // theme from World Tee Classic's right-click title-bar menu in the
        // separate game window). Apply the new theme + persist so it
        // survives a pet restart.
        var newTheme = MouseHouse.Core.ThemeSync.Poll(ref _lastThemeMtime);
        if (newTheme != null && newTheme != RetroSkin.Current.Name)
        {
            RetroSkin.SetTheme(newTheme);
            if (_settings.RetroThemeName != newTheme)
            {
                _settings.RetroThemeName = newTheme;
                _settings.Save();
            }
        }

        // Detect when World Tee Classic was closed via its own X button: the
        // child process exits, we notice on the next frame, and persist
        // WorldTeeClassicOpen=false so it doesn't re-spawn next launch.
        if (_worldTeeProc != null && _worldTeeProc.HasExited)
        {
            _worldTeeProc.Dispose();
            _worldTeeProc = null;
            if (_settings.WorldTeeClassicOpen)
            {
                _settings.WorldTeeClassicOpen = false;
                _settings.Save();
            }
        }

        // Same lifecycle bookkeeping for the radio companion process.
        if (_radioProc != null && _radioProc.HasExited)
        {
            _radioProc.Dispose();
            _radioProc = null;
            if (_settings.RadioOpen)
            {
                _settings.RadioOpen = false;
                _settings.Save();
            }
        }

        _timeUpdateTimer += delta;
        if (_timeUpdateTimer >= 30f)
        {
            _timeUpdateTimer = 0;
            TimeSystem.Update();
        }

        // Always query the OS for the cursor — Raylib's cached position freezes
        // while mouse passthrough is on (the OS stops delivering mouseMoved /
        // WM_MOUSEMOVE to a window set to ignore mouse events). Using a stale
        // position to decide whether to disable passthrough creates a chicken-
        // and-egg loop where clicks on the pet/UI rarely register.
        var mousePos = WindowHelper.GetGlobalCursorPosition();

        // For click hit-testing, swap to the position the user *actually
        // clicked at* — sampled inside the high-rate poller at the moment
        // of the press transition. The frame loop ticks at 60 Hz, so by
        // the time a frame reads the cursor after the poller increments
        // the press counter the cursor may have drifted 5-30 px past the
        // target if the user is moving while clicking. That drift was the
        // long-standing "I clicked the button but nothing happened" bug;
        // hit-testing at the at-click position is what actually fixes it.
        if (_input.LeftPressed) mousePos = _input.LeftPressedPos;
        else if (_input.LeftReleased) mousePos = _input.LeftReleasedPos;
        else if (_input.RightPressed) mousePos = _input.RightPressedPos;
        else if (_input.RightReleased) mousePos = _input.RightReleasedPos;

        // if (_input.LeftPressed) _debugLastClick = mousePos;
        bool activityConsumed = false;

        // Handle activity panel if one is open
        if (_activeActivity != null)
        {
            var panelRect = new Rectangle(_activityOffset.X, _activityOffset.Y,
                _activeActivity.PanelSize.X, _activeActivity.PanelSize.Y);
            // For transparent activities (zones), only the chrome / interactive areas
            // consume input — empty space lets the pet through.
            bool mouseOverPanel = Raylib.CheckCollisionPointRec(mousePos, panelRect)
                && _activeActivity.ContainsPoint(mousePos - _activityOffset);

            // ESC closes the activity
            if (_input.IsKeyPressed(KeyboardKey.Escape))
            {
                CloseActivity();
                activityConsumed = true;
            }
            else
            {
                // Handle title bar dragging
                var titleBarRect = new Rectangle(_activityOffset.X, _activityOffset.Y,
                    _activeActivity.PanelSize.X - 40, ActivityTitleBarHeight);
                var closeRect = new Rectangle(
                    _activityOffset.X + _activeActivity.PanelSize.X - 40,
                    _activityOffset.Y, 40, ActivityTitleBarHeight);

                if (_draggingActivity)
                {
                    if (_input.LeftPressed || _input.LeftReleased)
                        Console.WriteLine($"[scene] CONSUMED by _draggingActivity LP={_input.LeftPressed} LR={_input.LeftReleased}");
                    _activityOffset = mousePos - _activityDragOffset;
                    activityConsumed = true;
                    if (_input.LeftReleased)
                        _draggingActivity = false;
                }
                else if (_input.LeftPressed && Raylib.CheckCollisionPointRec(mousePos, closeRect))
                {
                    Console.WriteLine($"[scene] CONSUMED by closeRect");
                    CloseActivity();
                    activityConsumed = true;
                }
                else if (_input.LeftPressed && Raylib.CheckCollisionPointRec(mousePos, titleBarRect)
                    && _activeActivity.OnTitleBarClick(mousePos - _activityOffset))
                {
                    // Activity handled the title-bar click (e.g. an in-bar button).
                    activityConsumed = true;
                }
                else if (_input.LeftPressed && Raylib.CheckCollisionPointRec(mousePos, titleBarRect))
                {
                    Console.WriteLine($"[scene] CONSUMED by titleBarRect (drag start) mousePos=({mousePos.X:F0},{mousePos.Y:F0}) titleBarRect=({titleBarRect.X:F0},{titleBarRect.Y:F0},{titleBarRect.Width:F0},{titleBarRect.Height:F0}) LR={_input.LeftReleased}");
                    _draggingActivity = true;
                    _activityDragOffset = mousePos - _activityOffset;
                    activityConsumed = true;
                    if (_input.LeftReleased)
                    {
                        // Same-frame press+release on the title bar. Could
                        // be a tap (no cursor movement) or a fast drag —
                        // apply any delta between at-press and at-release
                        // so a quick flick still moves the panel, then
                        // clear so we don't get stuck in drag mode.
                        var dragDelta = _input.LeftReleasedPos - _input.LeftPressedPos;
                        if (dragDelta.LengthSquared() > 0.1f)
                            _activityOffset += dragDelta;
                        _draggingActivity = false;
                    }
                }
                else if (_input.LeftPressed && mouseOverPanel
                    && _activeActivity is SolitaireActivity)
                {
                    var newRect = new Rectangle(
                        _activityOffset.X + _activeActivity.PanelSize.X - 100,
                        _activityOffset.Y, 60, ActivityTitleBarHeight);
                    if (Raylib.CheckCollisionPointRec(mousePos, newRect))
                    {
                        _activeActivity.Close();
                        OpenActivity(new SolitaireActivity(_assets));
                        activityConsumed = true;
                    }
                }

                if (!activityConsumed && _activeActivity != null)
                {
                    if (_input.LeftPressed || _input.LeftReleased)
                        Console.WriteLine($"[scene] -> activity LP={_input.LeftPressed} LR={_input.LeftReleased} mouseOverPanel={mouseOverPanel} mousePos=({mousePos.X:F0},{mousePos.Y:F0}) panelOffset=({_activityOffset.X:F0},{_activityOffset.Y:F0}) draggingActivity={_draggingActivity} activityConsumed={activityConsumed}");
                    _activeActivity.Update(delta, mousePos, _activityOffset,
                        mouseOverPanel && _input.LeftPressed,
                        // Release events must propagate even when the cursor
                        // is outside the panel — otherwise drag operations
                        // (e.g. Paint's selection rubber-band) get stuck if
                        // the user lets go beyond the canvas edge.
                        _input.LeftReleased,
                        mouseOverPanel && _input.RightPressed);

                    if (_activeActivity?.IsFinished == true)
                    {
                        _activeActivity = null;
                        UpdateTopmost();
                    }
                }

                if (mouseOverPanel || _draggingActivity)
                    activityConsumed = true;
            }
        }

        // Pet and desktop always update — activities don't block them.
        // Per-pixel alpha hit test against the current sprite frame: clicks
        // in transparent space around the pet (between the visible body and
        // the 76x76 frame edge) reach the desktop. This is also the gate
        // for transparent-overlay capture below — using the same tight test
        // for both means the capture region matches the visible sprite
        // exactly, no halo above the pet swallowing clicks.
        _mouseOverPet = !activityConsumed
            && _pet.ActiveSheet != null
            && _pet.ActiveSheet.HitTest(_pet.CurrentFrame, _pet.Position, _pet.Scale, _pet.FlipH, mousePos);

        _mouseOverUI = _menu.ContainsPoint(mousePos)
            || _statusBubble.ContainsPoint(mousePos)
            || _radio.ContainsPoint(mousePos)
            || (_buddyWidget != null && _buddyWidget.ContainsPoint(mousePos))
            || _destroyer.ContainsPoint(mousePos);

        bool radioConsumed = _radio.Update(delta, mousePos,
            !activityConsumed && _input.LeftPressed,
            _input.LeftReleased,
            !activityConsumed && _input.RightPressed);
        if (radioConsumed) activityConsumed = true;

        // Drain broker → main-thread events (presence updates,
        // incoming requests, challenges) and pump widget input.
        _buddies?.Update();
        if (_buddyWidget != null)
        {
            bool buddyConsumed = _buddyWidget.Update(delta, mousePos,
                !activityConsumed && _input.LeftPressed,
                _input.LeftReleased,
                !activityConsumed && _input.RightPressed);
            if (buddyConsumed) activityConsumed = true;
        }

        // Destroyer overlay swallows everything else when active so the user
        // can paint freely over their entire desktop. Routed AFTER radio /
        // popup-menu so those still work while destroying.
        bool destroyerConsumed = _destroyer.Update(delta, mousePos,
            !activityConsumed && _input.LeftPressed,
            _input.LeftReleased,
            !activityConsumed && _input.RightPressed);
        if (destroyerConsumed) activityConsumed = true;

        bool statusConsumed = _statusBubble.Update(delta, mousePos, !activityConsumed && _input.LeftPressed);
        bool menuConsumed = _menu.Update(mousePos,
            !activityConsumed && _input.LeftPressed,
            !activityConsumed && _input.RightPressed);

        // Approach margin around an open activity panel — passthrough must
        // already be OFF by the moment the cursor enters the panel, otherwise
        // a quick click in the same OS event burst is eaten before
        // setIgnoresMouseEvents has flipped. The panel is a stationary
        // rectangle so a generous margin around it is a cheap pre-disable
        // without creating meaningful dead zones for the desktop, and
        // 48 px keeps quick clicks landing reliably even when the cursor
        // travels in fast.
        bool nearActivityPanel = false;
        if (_activeActivity != null)
        {
            const int PanelApproachMargin = 48;
            var pr = new Rectangle(
                _activityOffset.X - PanelApproachMargin,
                _activityOffset.Y - PanelApproachMargin,
                _activeActivity.PanelSize.X + 2 * PanelApproachMargin,
                _activeActivity.PanelSize.Y + 2 * PanelApproachMargin);
            nearActivityPanel = Raylib.CheckCollisionPointRec(mousePos, pr);
        }

        bool wantCapture = activityConsumed || _draggingActivity || nearActivityPanel
            || _destroyer.ShouldCaptureMouse
            || _mouseOverPet || _mouseOverUI
            || _pet.State == PetState.Dragging
            || _menu.Visible || _statusBubble.IsEditing
            || _placingCheese;

        // Hysteresis: once we want capture, hold it for a generous window
        // even after the cursor moves out so any quick follow-up click
        // still finds passthrough off. Earlier we'd dropped this to
        // 60 ms but that left fast clicks landing during the macOS
        // setIgnoresMouseEvents propagation window, where the OS routed
        // the click underneath us — the recurring "I have to hold the
        // button down to make it register" complaint. 200 ms is plenty
        // for OS event-dispatch latency without re-creating the original
        // dead-zone-near-the-pet problem (the spatial halo, not the hold
        // duration, was what caused that).
        const float CaptureHoldSeconds = 0.20f;
        if (wantCapture) _captureHoldTimer = CaptureHoldSeconds;
        else _captureHoldTimer = MathF.Max(0, _captureHoldTimer - delta);

        // Click-bump for our UI ONLY — gated on wantCapture. Earlier
        // this fired on any global mouse event, which broke double-clicks
        // in other apps: clicking in Finder bumped our hold to 350 ms,
        // our window's passthrough flipped off for the gap, and Finder's
        // second click of the double landed on our overlay instead of
        // Finder. With the wantCapture gate the bump only triggers when
        // the cursor is over our actual UI (panel, pet, menu, radio,
        // etc.), so clicks anywhere else still pass through cleanly to
        // whatever app is underneath.
        if (wantCapture && (_input.LeftPressed || _input.LeftReleased
            || _input.RightPressed || _input.RightReleased
            || _input.LeftDown || _input.RightDown))
        {
            _captureHoldTimer = MathF.Max(_captureHoldTimer, 0.35f);
        }

        bool shouldCapture = wantCapture || _captureHoldTimer > 0f;
        WindowHelper.SetMousePassthrough(!shouldCapture);

        // Cheese placement mode — every left click drops a piece + re-
        // rolls the variety; Esc / right-click cancels. Right-click
        // on the pet itself also cancels but flows through so the
        // pet's context menu still opens (the user is clearly done
        // placing if they're going for the menu). Click on the
        // floating Cancel chip near the cursor exits cleanly for
        // mouse-only users who don't think to press Esc.
        bool placementConsumed = false;
        if (_placingCheese && !activityConsumed && !menuConsumed && !statusConsumed)
        {
            if (_input.IsKeyPressed(KeyboardKey.Escape))
            {
                _placingCheese = false;
                placementConsumed = true;
            }
            else if (_input.RightPressed)
            {
                _placingCheese = false;
                // If the right-click landed on the pet, fall through
                // so the existing right-click-on-pet path opens the
                // context menu. On empty desktop, consume so the
                // click doesn't double-pop something else.
                if (!_mouseOverPet) placementConsumed = true;
            }
            else if (_input.LeftPressed
                && _placementCancelChip.Width > 0
                && Raylib.CheckCollisionPointRec(mousePos, _placementCancelChip))
            {
                _placingCheese = false;
                placementConsumed = true;
            }
            else if (_input.LeftPressed)
            {
                _cheese.Drop(_placingCheeseType, mousePos);
                // Re-roll the variety so the next click drops a
                // different cheese. Filtering against the just-
                // dropped type makes consecutive clicks visibly
                // different even though the pool only has 11 items.
                var pool = CheeseImages.Available;
                if (pool.Length > 1)
                {
                    CheeseType pick;
                    do { pick = pool[_rng.Next(pool.Length)]; }
                    while (pick == _placingCheeseType);
                    _placingCheeseType = pick;
                }
                placementConsumed = true;
            }
            else
            {
                placementConsumed = true;
            }
        }

        if (!activityConsumed && !menuConsumed && !statusConsumed && !placementConsumed)
        {
            if (_mouseOverPet && _input.LeftPressed)
            {
                // Snoot boop: if the click landed on the pet's nose, sneeze
                // instead of starting a drag.
                if (_snootCooldown <= 0 && IsClickOnSnoot(mousePos))
                {
                    var (petPos, petSize) = _pet.GetBounds();
                    var snoot = petPos + petSize * 0.5f - new Vector2(0, petSize.Y * 0.05f);
                    _reactions.SpawnSneeze(snoot, _pet.FlipH);
                    _snootCooldown = 0.6f;
                    // Needs system disabled per user request 2026-05-08;
                    // uncomment to restore the Happy bump on snoot boops.
                    // _needs.Add(NeedKind.Happy, 4);
                }
                else
                {
                    _pet.StartDrag(mousePos);
                }
            }
            else if (_pet.State == PetState.Dragging && _input.LeftReleased)
            {
                // If the pet was dropped over a toy's bounds, snap it onto
                // the interaction point and let the toy AI take over —
                // skipping the throw arc.
                var hit = _toys.HitTest(mousePos);
                if (hit != null)
                {
                    var def = Toys.Toys.Get(hit.Type);
                    var center = hit.InteractPoint();
                    _pet.Position = center - new Vector2(PetStateMachine.FrameSize * _pet.Scale / 2f);
                    _pet.EndDrag();
                    _currentToy = hit;
                    hit.InUse = true;
                    hit.UseTimer = 0;
                    ArriveAtToy(center);
                }
                else
                {
                    _pet.EndDrag();
                }
            }

            if (_mouseOverPet && _input.RightPressed)
                ShowContextMenu(mousePos);
        }

        _pet.Frozen = _menu.Visible || _statusBubble.IsEditing;
        _pet.Update(delta, mousePos);
        _events.Update(delta);
        UpdateCheeseAI(delta);
        UpdateToyAI(delta);
        _cheese.Update(delta);
        _toys.Update(delta);
        _reactions.Update(delta);
        // Tick the cheese-name wave overlays; drop ones that have finished
        // their full bob-and-fade cycle.
        for (int i = _cheeseWaves.Count - 1; i >= 0; i--)
        {
            _cheeseWaves[i].Time += delta;
            if (_cheeseWaves[i].Time >= _cheeseWaves[i].Life)
                _cheeseWaves.RemoveAt(i);
        }
        // Needs system disabled per user request 2026-05-08; uncomment to restore.
        // The hover-to-pet heart was driven by UpdatePetting + _needs.Add(Happy);
        // both stay disabled together so hovering does nothing. Snoot-boop sneeze
        // is unrelated and remains active above.
        // UpdatePetting(delta, mousePos);
        _snootCooldown = MathF.Max(0, _snootCooldown - delta);

        // Needs system disabled per user request 2026-05-08; uncomment to restore.
        // Without Tick the four needs stay at their constructor defaults (80 each),
        // which makes LowestUnmet() return null forever — auto-routing-to-bed-when-
        // tired etc. naturally no-ops. Cheese AI is independent of needs and still works.
        // bool sleeping = _pet.State == PetState.Sleeping
        //              || (_pet.State == PetState.UsingToy && _currentToy?.Type == ToyType.Bed);
        // _needs.Tick(delta, sleeping);

        UpdateZoomies(delta);
    }

    // Needs system disabled per user request 2026-05-08; uncomment to restore.
    // The heart-on-hover feature was part of the needs system (it bumped
    // Happy). Method body wrapped in /* */ rather than the whole method
    // so the call site upstream can stay commented in lockstep without
    // a stale-reference compile error.
    /// <summary>
    /// Hover-to-pet: holding the cursor over the pet's body for ~0.7 s spawns
    /// a heart and bumps Happy. Continuing to hover keeps emitting hearts at
    /// a slower rate. Resets if the cursor leaves the sprite, an activity is
    /// open, or the pet is being dragged.
    /// </summary>
    private void UpdatePetting(float delta, Vector2 mousePos)
    {
        /* Needs-driven; disabled. Restore by uncommenting alongside the
           UpdatePetting call upstream and the _needs.Tick + _needs.Add lines.
        bool eligible = _mouseOverPet && _activeActivity == null
                     && _pet.State != PetState.Dragging
                     && !_menu.Visible;
        if (!eligible) { _hoverPetTimer = 0; _hoverHeartCooldown = 0; return; }

        _hoverPetTimer += delta;
        _hoverHeartCooldown -= delta;

        if (_hoverPetTimer >= HoverPetThreshold && _hoverHeartCooldown <= 0)
        {
            var (petPos, petSize) = _pet.GetBounds();
            var head = petPos + new Vector2(petSize.X * 0.3f, -8);
            _reactions.SpawnHeart(head);
            _needs.Add(NeedKind.Happy, 6);
            _hoverHeartCooldown = HoverHeartInterval;
        }
        */
    }

    /// <summary>
    /// Personality quirk — every now and then the pet does "zoomies": fast
    /// circuits across the screen for a few seconds, scaled walk speed.
    /// Triggered randomly when Happy is high (he's in a good mood) and the
    /// pet is in a free state.
    /// </summary>
    private void UpdateZoomies(float delta)
    {
        if (_zoomiesTimer > 0)
        {
            _zoomiesTimer -= delta;
            if (_zoomiesTimer <= 0) _pet.WalkSpeedMul = 1f;
            return;
        }
        if (_pet.State != PetState.Idle && _pet.State != PetState.Walking) return;
        if (_needs.Happy < 75) return;
        // Roughly once every couple of minutes when in a good mood.
        if (_rng.NextDouble() < 0.0008)
        {
            _zoomiesTimer = 3.5f + (float)_rng.NextDouble() * 2f;
            _pet.WalkSpeedMul = 2.4f;
        }
    }

    private bool IsClickOnSnoot(Vector2 mousePos)
    {
        // Snoot is in the front-bottom-quarter of the sprite, opposite the
        // facing direction (pet faces left by default; FlipH flips to right).
        var (petPos, petSize) = _pet.GetBounds();
        float frontEdgeX = _pet.FlipH
            ? petPos.X + petSize.X * 0.85f     // facing right → nose on right
            : petPos.X + petSize.X * 0.05f;    // facing left  → nose on left
        var snoot = new Rectangle(
            frontEdgeX - petSize.X * 0.10f, petPos.Y + petSize.Y * 0.40f,
            petSize.X * 0.20f, petSize.Y * 0.20f);
        return Raylib.CheckCollisionPointRec(mousePos, snoot);
    }

    private void UpdateToyAI(float delta)
    {
        if (_pet.State == PetState.Dragging || _activeActivity != null) return;

        // Cheese always wins — if there's any cheese on the floor, that AI
        // owns the pet's behavior. Toys only steer the pet during downtime.
        if (_cheese.Active.Count > 0) return;

        var center = _pet.Position + new Vector2(PetStateMachine.FrameSize * _pet.Scale / 2f);

        // From a free state, route to a toy. Need-priority routing
        // (auto-go-to-bed-when-tired etc.) was here; needs system disabled
        // per user request 2026-05-08, so the routing falls through to the
        // generic 'wander to a random toy occasionally' path. Restore by
        // uncommenting alongside the _needs.Tick + ApplyToyReward call below.
        if (_pet.State == PetState.Idle && _toys.Toys.Count > 0)
        {
            ToyInstance? target = null;
            // var unmet = _needs.LowestUnmet();
            // if (unmet.HasValue)
            // {
            //     var prefer = ToyForNeed(unmet.Value);
            //     if (prefer.HasValue) target = _toys.FindClosestUnused(center, prefer);
            // }
            if (target != null) StartSeekingToy(target);
            else
            {
                // No urgent need — wander to a toy occasionally.
                var t = _toys.FindClosestUnused(center);
                if (t != null && _rng.NextDouble() < 0.012)
                    StartSeekingToy(t);
            }
        }

        if (_pet.State == PetState.SeekingToy)
        {
            // If our target was removed (cleared / picked up), bail.
            if (_currentToy == null || !_toys.Toys.Contains(_currentToy))
            {
                _currentToy = null;
                _pet.HasToyTarget = false;
                _pet.EnterIdle();
                return;
            }
            // Update target each frame in case the toy moved (ball can drift).
            _pet.ToyTarget = _currentToy.InteractPoint();
            float dist = Vector2.Distance(center, _pet.ToyTarget);
            if (dist < 22f) ArriveAtToy(center);
        }

        if (_pet.State == PetState.UsingToy && _currentToy != null)
        {
            DriveToyInteraction(_currentToy, center, delta);
        }
        else if (_pet.State != PetState.UsingToy && _currentToy != null)
        {
            // Pet just left UsingToy this frame (timer ran out, or got
            // interrupted) — needs reward disabled per user request
            // 2026-05-08; restore by uncommenting ApplyToyReward.
            // ApplyToyReward(_currentToy.Type);
            _currentToy.InUse = false;
            _currentToy = null;
        }
    }

    private void StartSeekingToy(ToyInstance t)
    {
        _currentToy = t;
        var def = Toys.Toys.Get(t.Type);
        _pet.EnterSeekingToy(t.InteractPoint(), def.Preference);
    }

    private static ToyType? ToyForNeed(NeedKind need) => need switch
    {
        // Hunger is normally fed by cheese; toys can't satisfy it directly.
        NeedKind.Hunger => null,
        NeedKind.Energy => ToyType.Bed,
        NeedKind.Hygiene => ToyType.WaterBottle,
        NeedKind.Happy => ToyType.Wheel,    // running raises mood
        _ => null,
    };

    /// <summary>Per-toy reward applied as the pet finishes using it.</summary>
    private void ApplyToyReward(ToyType type)
    {
        switch (type)
        {
            case ToyType.Bed:         _needs.Add(NeedKind.Energy, 60); _needs.Add(NeedKind.Happy, 5); break;
            case ToyType.Wheel:       _needs.Add(NeedKind.Happy, 35); _needs.Add(NeedKind.Energy, -10); break;
            case ToyType.WaterBottle: _needs.Add(NeedKind.Hygiene, 50); break;
            case ToyType.Ball:        _needs.Add(NeedKind.Happy, 25); _needs.Add(NeedKind.Energy, -5); break;
        }
    }

    private void ArriveAtToy(Vector2 petCenter)
    {
        if (_currentToy == null) return;
        var def = Toys.Toys.Get(_currentToy.Type);
        _currentToy.InUse = true;
        _currentToy.UseTimer = 0;

        // Pick the right sprite sheet + framerate per toy type.
        switch (_currentToy.Type)
        {
            case ToyType.Bed:
                _pet.EnterUsingToy(def.UseSeconds, _pet.SleepLoopSheet, frameCount: 3,
                    frameSpeed: 0.35f, loops: true);
                break;
            case ToyType.Wheel:
                // Hammers the walk sheet faster than normal walking — looks like running.
                _pet.EnterUsingToy(def.UseSeconds, _pet.WalkSheet, frameCount: 8,
                    frameSpeed: 0.08f, loops: true);
                break;
            case ToyType.WaterBottle:
                _pet.EnterUsingToy(def.UseSeconds, _pet.IdleSheet, frameCount: 8,
                    frameSpeed: 0.18f, loops: true);
                break;
            case ToyType.Ball:
                // Pet "kicks" the ball on arrival, then chases it for a beat.
                var dir = petCenter.X < _currentToy.Position.X ? 1f : -1f;
                _toys.KickBall(_currentToy, new Vector2(dir, 0), 220f);
                _pet.EnterUsingToy(def.UseSeconds, _pet.WalkSheet, frameCount: 8,
                    frameSpeed: 0.12f, loops: true);
                break;
        }
    }

    private void DriveToyInteraction(ToyInstance t, Vector2 petCenter, float delta)
    {
        switch (t.Type)
        {
            case ToyType.Wheel:
                // The wheel's spin advance is handled in ToyManager.Update via t.InUse.
                break;
            case ToyType.Ball:
                // Pet keeps gentle nudges going while in UsingToy state — adds
                // a small impulse if the ball slowed down.
                if (t.BallVel.LengthSquared() < 4f)
                {
                    var dir = t.Position.X < petCenter.X ? -1f : 1f;
                    _toys.KickBall(t, new Vector2(dir, 0), 140f);
                }
                break;
        }
    }

    private void UpdateCheeseAI(float delta)
    {
        // While dragging or in an activity, don't override the pet's behavior.
        if (_pet.State == PetState.Dragging || _activeActivity != null) return;

        var center = _pet.Position + new Vector2(PetStateMachine.FrameSize * _pet.Scale / 2f);

        // Idle/walking/idle-action/content: try to lock onto the nearest cheese.
        if (_pet.State == PetState.Idle || _pet.State == PetState.Walking
            || _pet.State == PetState.IdleAction || _pet.State == PetState.Content
            || _pet.State == PetState.Jumping)
        {
            var c = _cheese.FindClosestUnclaimed(center);
            if (c != null)
            {
                var def = MouseHouse.Scenes.DesktopPet.Cheese.Cheeses.Get(c.Type);
                _pet.EnterSeekingCheese(c.Position, def.Preference);
                _pet.HasCheeseTarget = true;
            }
        }

        // Seeking: if the cheese was removed (cleared / eaten by something),
        // bail back to idle.
        if (_pet.State == PetState.SeekingCheese)
        {
            // Locate the closest cheese again — also handles the case where a
            // closer one got dropped mid-walk.
            var c = _cheese.FindClosestUnclaimed(center);
            if (c == null) { _pet.HasCheeseTarget = false; _pet.EnterIdle(); }
            else _pet.CheeseTarget = c.Position;
        }

        if (_pet.State == PetState.EatingCheese)
        {
            // Find the cheese we're parked on (may already be marked BeingEaten).
            CheeseInstance? c = null;
            float bestD = float.MaxValue;
            foreach (var ch in _cheese.Active)
            {
                float d = Vector2.DistanceSquared(_pet.CheeseTarget, ch.Position);
                if (d < bestD) { bestD = d; c = ch; }
            }
            if (c == null || bestD > 60 * 60) { _pet.EnterIdle(); return; }

            c.BeingEaten = true;
            // Shrink it over the eat duration. The full directional
            // dissolve is timed to one Size unit, but the second half
            // collapses instantly into FallingChunks below — so the
            // perceived eat time is roughly half of EatSeconds.
            float total = MouseHouse.Scenes.DesktopPet.Cheese.Cheeses.Get(c.Type).EatSeconds;
            if (total <= 0.01f) total = 1.4f;
            float dec = (1f / total) * delta;
            c.Size -= dec;
            _cheese.OnBiteTaken(c, center);

            // Half-time collapse: when the directional dissolve has eaten
            // through the contact-side half of the sprite, the rest of
            // the cheese visibly explodes into small falling chunks
            // (~4×4 sprite-pixel groups) that arc downward and fade.
            // Snap Size to 0 right after so the existing "fully eaten"
            // branch below removes the cheese instance and the pet idles.
            if (!c.Collapsed && c.Size <= 0.5f)
            {
                _cheese.CollapseRemaining(c, center, CheeseCellPx());
                c.Size = 0f;
            }

            // Match the pet's eat-state duration to the cheese variety so
            // brie really does linger longer than cheddar.
            _pet.CheeseEatTotal = total;

            if (c.Size <= 0.01f)
            {
                _cheese.Active.Remove(c);
                _cheese.RecordEat(c, center);
                // Needs system disabled per user request 2026-05-08; uncomment
                // to restore the per-eat Hunger/Happy bumps. Cheese pipeline
                // (RecordEat, the cheese HUD's eat-counter + favorite, the
                // FEAST! easter egg) keeps working unchanged.
                // _needs.Add(NeedKind.Hunger, 35f);
                // _needs.Add(NeedKind.Happy, 8f);
                _pet.HasCheeseTarget = false;
                _pet.EnterIdle();
                // Wave-text celebration — same letter-bob effect the golf
                // game uses for HOLE IN ONE, smaller, anchored at the
                // visible mouse-head position (not the sprite-bounds
                // top, which has ~18% empty padding above the silhouette
                // and made the celebration float well above the actual
                // mouse). Spells out the variety eaten ("Goat Cheese").
                // The flat +30 px shift drops the text into the cheek/
                // body row of the rendered mouse — the user found the
                // earlier "just above the head" placement still too high.
                var (petPos, petSize) = _pet.GetBounds();
                var head = new Vector2(
                    petPos.X + petSize.X / 2f,
                    petPos.Y + petSize.Y * 0.20f + 30f);
                _cheeseWaves.Add(new CheeseWaveText
                {
                    Text = Cheeses.Get(c.Type).Name,
                    Anchor = head,
                });
            }
        }
    }

    private void OpenActivity(IActivity activity)
    {
        _activeActivity = activity;
        _activeActivity.Load();
        _draggingActivity = false;
        _activityOffset = new Vector2(
            (_screenWidth - activity.PanelSize.X) / 2f,
            (_screenHeight - activity.PanelSize.Y) / 2f
        );
        // Activity windows behave like normal apps — let the user put other
        // windows over them. The pet alone keeps its always-on-top floating
        // level (restored in CloseActivity).
        WindowHelper.SetTopmost(false);
    }

    private void CloseActivity()
    {
        _activeActivity?.Close();
        _activeActivity = null;
        _draggingActivity = false;
        UpdateTopmost();
    }

    /// <summary>
    /// Pet alone is always-on-top. Anything app-like sharing the overlay
    /// window (an open activity, or the visible radio) drops the window
    /// to a normal z-level so other apps can stack over it.
    /// </summary>
    private void UpdateTopmost()
    {
        bool wantTopmost = _activeActivity == null && !_radio.Visible;
        WindowHelper.SetTopmost(wantTopmost);
    }

    private void LaunchRetroGame(int activityId)
    {
        var proc = ActivityLauncher.Launch(
            activityId,
            theme: RetroSkin.Current.Name,
            bodyFontSize: RetroSkin.BodyFontSize,
            titleFontSize: RetroSkin.TitleFontSize,
            statusFontSize: RetroWidgets.StatusFontSize,
            uiScale: UIScaling.Factor);
        // Track session-restore-eligible activities so we can persist their
        // open-state and re-launch them on next start. Only Golf (246) and
        // the Radio (9) opt in currently — everything else is fire-and-forget.
        if (activityId == 246 && proc != null)
        {
            _worldTeeProc = proc;
            _settings.WorldTeeClassicOpen = true;
            _settings.Save();
        }
        else if (activityId == 9 && proc != null)
        {
            _radioProc = proc;
            _settings.RadioOpen = true;
            _settings.Save();
        }
    }

    private bool IsRadioRunning() => _radioProc != null && !_radioProc.HasExited;

    private void ShowContextMenu(Vector2 position)
    {
        _menuOpenPos = position;
        var items = new List<MenuItem>();

        // ── Pet primary action ─────────────────────────────────────────
        if (_pet.State == PetState.Sleeping)
            items.Add(MenuItem.Item("Wake Up", 1));
        else
            items.Add(MenuItem.Item("Sleep", 0));

        // Ambient pet actions tucked into one submenu so they're not five
        // lines of equal weight at the top.
        items.Add(MenuItem.Submenu("Actions", new List<MenuItem>
        {
            MenuItem.Item("Jump", 15),
            MenuItem.Item("Walk Right", 17),
            MenuItem.Item("Walk Left", 18),
            MenuItem.Item(_statusBubble.Visible ? "Edit Status" : "Set Status", 70),
        }));

        // Cheese-feeding feature: top-level head, with the contextual
        // "Clear cheese (N)" only when there's something to clear.
        items.Add(MenuItem.Item("Place Cheese", 700));
        if (_cheese.Active.Count > 0)
            items.Add(MenuItem.Item($"Clear cheese ({_cheese.Active.Count})", 719));

        items.Add(MenuItem.Separator());

        // ── Headline features ──────────────────────────────────────────
        // Radio + the two flagship games + retro theme are what the user
        // opens most. Promoted out of being buried inside Games / Activities.
        items.Add(MenuItem.Item(_radio.Visible ? "Hide Radio" : "Show Radio", 290));
        // Buddy list: same toggle pattern as Radio. The widget owns
        // the broker connection lifetime, so opening / closing it
        // here doesn't disconnect.
        if (_buddyWidget != null)
            items.Add(MenuItem.Item(_buddyWidget.Visible ? "Hide Friends" : "Friends…", 295));
        items.Add(MenuItem.Item(MouseHouse.Scenes.Activities.WorldTeeClassicActivity.AppTitle, 246));
        items.Add(MenuItem.Item("Chess Puzzles", 260));
        items.Add(MenuItem.Item("Paint", 7));
        var topThemeItems = new List<MenuItem>();
        for (int ti = 0; ti < RetroSkin.AllThemes.Length; ti++)
        {
            var t = RetroSkin.AllThemes[ti];
            topThemeItems.Add(MenuItem.Item(t.Name, 220 + ti, RetroSkin.Current.Name != t.Name));
        }
        items.Add(MenuItem.Submenu("Retro Theme", topThemeItems));
        // Destructive gimmick — placed at the end of the headline cluster
        // because it's dramatic / one-click-of-regret material, not because
        // it's something the user opens daily. Earns top-level over Tools
        // ▶ for visibility (and so toggling 'stop' is one click away).
        items.Add(MenuItem.Item(_destroyer.Active ? "Stop Destroying Desktop" : "Destroy Desktop", 291));

        items.Add(MenuItem.Separator());

        // Toys / Wardrobe / Zones moved to the bottom 'Pet ▶' submenu so
        // the top level stays focused on the headline attractions. Build
        // the three submenus here so each runs through its current state
        // (counts, selected costume, etc.) and we add 'Pet ▶' below the
        // customization-tweakers row.
        var toyItems = new List<MenuItem>();
        for (int i = 0; i < Toys.Toys.All.Length; i++)
        {
            var def = Toys.Toys.All[i];
            toyItems.Add(MenuItem.Item($"Place {def.Name}", 730 + i));
        }
        if (_toys.Toys.Count > 0)
        {
            toyItems.Add(MenuItem.Separator());
            toyItems.Add(MenuItem.Item($"Clear toys ({_toys.Toys.Count})", 749));
        }

        var wardrobeItems = new List<MenuItem>();
        for (int i = 0; i < CostumeRenderer.All.Length; i++)
        {
            var (type, name) = CostumeRenderer.All[i];
            string label = _costume == type ? $"• {name}" : name;
            wardrobeItems.Add(MenuItem.Item(label, 800 + i));
        }

        var zoneItems = new List<MenuItem>
        {
            MenuItem.Item("Beach", 100),
            MenuItem.Item("Apartment", 101),
            MenuItem.Item("Bedroom", 102),
            MenuItem.Item("Camping", 103),
        };

        // Needs system disabled per user request 2026-05-08; uncomment to
        // restore the 'Show / Hide Needs HUD' menu toggle.
        // items.Add(MenuItem.Item(_needs.ShowHud ? "Hide Needs HUD" : "Show Needs HUD", 760));

        // ── More Games: secondary catalogue + Entertainment Pack ───────
        // World Tee Classic and Chess Puzzles are top-level; not
        // duplicated here so there's one canonical entry per game.
        // Sectioned with disabled-row dividers for readability.
        // "More Games" is split into nested submenus per section so the
        // outer list stays short enough to fit on screen — the flat
        // sectioned layout had grown past the bottom edge.
        items.Add(MenuItem.Submenu("More Games", new List<MenuItem>
        {
            MenuItem.Submenu("Activities", new List<MenuItem>
            {
                MenuItem.Item("Go Fishing", 6),
                MenuItem.Item("Cooking", 41),
                MenuItem.Item("Gardening", 43),
                MenuItem.Item("Dance", 44),
                MenuItem.Item("Kite Flying", 45),
                MenuItem.Item("Stargazing", 42),
                MenuItem.Item("Solitaire", 3),
                MenuItem.Item("Chess Puzzles (original)", 8),
                MenuItem.Item("Retro Chrome Demo", 200),
            }),
            MenuItem.Submenu("Pack 1", new List<MenuItem>
            {
                MenuItem.Item("Minesweeper", 201),
                MenuItem.Item("Golf", 202),
                MenuItem.Item("Cruel", 203),
                MenuItem.Item("Taipei", 204),
                MenuItem.Item("Pegged", 205),
                MenuItem.Item("TicTactics", 206),
                MenuItem.Item("Tetris", 207),
                MenuItem.Item("IdleWild", 208),
            }),
            MenuItem.Submenu("Pack 2", new List<MenuItem>
            {
                MenuItem.Item("FreeCell", 210),
                MenuItem.Item("Tut's Tomb", 211),
                MenuItem.Item("Jigsawed", 213),
                MenuItem.Item("Rattler Race", 214),
                MenuItem.Item("Pipe Dream", 215),
                MenuItem.Item("Rodent's Revenge", 216),
            }),
            MenuItem.Submenu("Pack 3", new List<MenuItem>
            {
                MenuItem.Item("TriPeaks", 240),
                MenuItem.Item("TetraVex", 241),
                MenuItem.Item("Klotski", 242),
                MenuItem.Item("Life Genesis", 243),
            }),
            MenuItem.Submenu("Pack 4", new List<MenuItem>
            {
                MenuItem.Item("Chess", 250),
                MenuItem.Item("Dr. Black Jack", 252),
                MenuItem.Item("Go Figure!", 253),
                MenuItem.Item("JezzBall", 254),
                MenuItem.Item("Tic Tac Drop", 256),
                MenuItem.Item("Hearts", 261),
            }),
            MenuItem.Submenu("Fun Games (201 ports)", new List<MenuItem>
            {
                MenuItem.Item("Sokoban", 299),
                MenuItem.Item("Link Four", 300),
                MenuItem.Item("WinRoach", 301),
                MenuItem.Item("Blackout", 302),
                MenuItem.Item("Medieval War", 303),
                MenuItem.Item("Pentominoes", 304),
            }),
            MenuItem.Submenu("Office", new List<MenuItem>
            {
                MenuItem.Item("Notepad", 280),
                MenuItem.Item("Clipboard Manager", 281),
                MenuItem.Item("Spreadsheet", 285),
                MenuItem.Item("E-reader", 286),
            }),
            MenuItem.Submenu("Atmosphere", new List<MenuItem>
            {
                MenuItem.Item("Sleep Sounds", 282),
                MenuItem.Item("Fish Aquarium", 283),
                MenuItem.Item("Neko", 297),
                MenuItem.Item("Spiroplot", 298),
            }),
            MenuItem.Submenu("Learning", new List<MenuItem>
            {
                MenuItem.Item("Math: Flashcards", 287),
                MenuItem.Item("Math: Holly's NumDrops", 289),
                MenuItem.Item("Clocks: Tell Time", 288),
            }),
            MenuItem.Submenu("Studio", new List<MenuItem>
            {
                MenuItem.Item("4-Track Recorder", 270),
                MenuItem.Item("Drum Machine", 284),
            }),
            MenuItem.Submenu("Beta", new List<MenuItem>
            {
                MenuItem.Item("Stones",           212),
                MenuItem.Item("Maxwell's Maniac", 255),
                MenuItem.Item("WordZap",          244),
                MenuItem.Item("SkiFree",          245),
                MenuItem.Item("Chip's Challenge", 251),
            }),
        }));

        items.Add(MenuItem.Separator());

        // ── Customization tweakers ─────────────────────────────────────
        items.Add(MenuItem.Submenu("Appearance", new List<MenuItem>
        {
            MenuItem.Item("2-Color Mode", 20, _settings.ColorMode != "2color"),
            MenuItem.Item("1-Color Mode", 21, _settings.ColorMode != "1color"),
            MenuItem.Item("Full Color Mode", 22, _settings.ColorMode != "fullcolor"),
            MenuItem.Separator(),
            MenuItem.Item("Scale 1x", 30, _pet.Scale != 1f),
            MenuItem.Item("Scale 1.5x", 33, _pet.Scale != 1.5f),
            MenuItem.Item("Scale 2x", 31, _pet.Scale != 2f),
            MenuItem.Item("Scale 3x", 32, _pet.Scale != 3f),
            MenuItem.Separator(),
            MenuItem.Item("UI Scale 1x", 34, UIScaling.Factor != 1f),
            MenuItem.Item("UI Scale 2x (Recommended)", 36, UIScaling.Factor != 2f),
            MenuItem.Separator(),
            MenuItem.Item("Preview Fonts", 80),
            MenuItem.Item("Font Size...", 87),
            MenuItem.Item("Menu Font Size...", 88),
            MenuItem.Separator(),
            MenuItem.Item("Filter: Point", 81, FontManager.CurrentFilter != TextureFilter.Point),
            MenuItem.Item("Filter: Bilinear", 82, FontManager.CurrentFilter != TextureFilter.Bilinear),
            MenuItem.Item("Filter: Trilinear", 83, FontManager.CurrentFilter != TextureFilter.Trilinear),
            MenuItem.Item("Filter: Anisotropic 4x", 84, FontManager.CurrentFilter != TextureFilter.Anisotropic4X),
            MenuItem.Item("Filter: Anisotropic 8x", 85, FontManager.CurrentFilter != TextureFilter.Anisotropic8X),
            MenuItem.Item("Filter: Anisotropic 16x", 86, FontManager.CurrentFilter != TextureFilter.Anisotropic16X),
            MenuItem.Separator(),
            MenuItem.Item(MouseHouse.UI.AmoebaTheme.Enabled
                ? "Amoeba Drips: On"
                : "Amoeba Drips: Off", 296),
        }));

        // ── Tools: gimmicks, debug, audio, multiplayer ─────────────────
        var toolsItems = new List<MenuItem>
        {
            MenuItem.Item(_audio.Muted ? "Unmute Audio" : "Mute Audio", 16),
            MenuItem.Separator(),
            MenuItem.Item(_events.Enabled ? "Disable Events" : "Enable Events", 51),
            MenuItem.Item("Spawn Event", 50, _events.Enabled),
            MenuItem.Separator(),
            // The radio's out-of-process port — buggy (wrong font, audio
            // pauses on window drag, etc). Kept for debugging the eventual
            // migration; the daily-use Radio above stays in-process.
            MenuItem.Item(IsRadioRunning()
                ? "Close Radio (Standalone Debug)"
                : "Radio (Standalone Debug)", 292),
        };
        if (_mp.Enabled)
        {
            toolsItems.Add(MenuItem.Separator());
            if (!_mp.IsConnected)
            {
                toolsItems.Add(MenuItem.Item("Host Game", 60));
                toolsItems.Add(MenuItem.Item("Join Game...", 61));
            }
            else
            {
                var who = _mp.RemoteName ?? "peer";
                toolsItems.Add(MenuItem.Item($"Connected: {who}", 62, false));
                toolsItems.Add(MenuItem.Item("Disconnect", 63));
                if (_mp.IsHost)
                    toolsItems.Add(MenuItem.Item("Kick Visitor", 64));
            }
        }
        items.Add(MenuItem.Submenu("Tools", toolsItems));

        // Pet — Toys / Wardrobe / Zones used to be top-level but they're
        // pet-themed customization, not headline attractions. Folded into
        // a single submenu next to Appearance / Tools.
        items.Add(MenuItem.Submenu("Pet", new List<MenuItem>
        {
            MenuItem.Submenu("Toys", toyItems),
            MenuItem.Submenu("Wardrobe", wardrobeItems),
            MenuItem.Submenu("Zones", zoneItems),
        }));

        items.Add(MenuItem.Separator());
        items.Add(MenuItem.Item("About / Credits", 95));
        items.Add(MenuItem.Item("Quit", 99));

        _menu.SetItems(items);
        _menu.Show(position);
    }

    /// <summary>
    /// Theme name we'll snap back to if the user hovers a different theme
    /// in the menu and then dismisses without clicking. Updated whenever
    /// a new theme is actually committed.
    /// </summary>
    private string? _committedThemeName;

    private void OnMenuItemHover(int id)
    {
        // Theme submenu: hovering an item live-previews that theme.
        // Hovering off (id == -1, or any non-theme item) reverts to
        // whatever was last committed.
        if (id >= 220 && id < 220 + RetroSkin.AllThemes.Length)
        {
            // Capture the committed baseline the first time we enter
            // the theme range during this menu's life.
            _committedThemeName ??= RetroSkin.Current.Name;
            RetroSkin.Current = RetroSkin.AllThemes[id - 220];
            MouseHouse.Core.ThemeSync.Write(RetroSkin.Current.Name);
        }
        else
        {
            if (_committedThemeName != null)
            {
                foreach (var t in RetroSkin.AllThemes)
                    if (t.Name == _committedThemeName) { RetroSkin.Current = t; break; }
                MouseHouse.Core.ThemeSync.Write(RetroSkin.Current.Name);
                if (id == -1) _committedThemeName = null;   // menu closed
            }
        }
    }

    private void OnMenuItemSelected(int id)
    {
        // "Place Cheese" — enter placement mode; the next left-click on the
        // desktop drops a cheese there and the pet goes to eat it. Pick
        // the variety up front so the placement-ghost previews the same
        // cheese that'll be dropped on click.
        if (id == 700)
        {
            var pool = CheeseImages.Available;
            _placingCheeseType = pool[_rng.Next(pool.Length)];
            _placingCheese = true;
            return;
        }
        if (id == 719)
        {
            _cheese.Active.Clear();
            return;
        }

        // Toy placement range — drops the chosen toy at the menu position.
        if (id >= 730 && id < 730 + Toys.Toys.All.Length)
        {
            _toys.Place(Toys.Toys.All[id - 730].Type, _menuOpenPos);
            return;
        }
        // Needs system disabled per user request 2026-05-08; the HUD-toggle
        // menu item is commented out above so id 760 is unreachable. Restore
        // by uncommenting both this dispatch case and the menu entry.
        // if (id == 760)
        // {
        //     _needs.ShowHud = !_needs.ShowHud;
        //     return;
        // }
        // Wardrobe range — pick a costume (None at 800 clears).
        if (id >= 800 && id < 800 + CostumeRenderer.All.Length)
        {
            _costume = CostumeRenderer.All[id - 800].Type;
            _settings.Costume = _costume.ToString();
            _settings.Save();
            return;
        }
        if (id == 749)
        {
            _toys.Clear();
            // If the pet was using a toy, drop the reference and idle out.
            _currentToy = null;
            if (_pet.State == PetState.UsingToy || _pet.State == PetState.SeekingToy)
                _pet.EnterIdle();
            return;
        }

        switch (id)
        {
            case 0: _pet.EnterSleeping(); break;
            case 1: _pet.EnterIdle(); break;
            case 15: _pet.EnterJumping(); break;
            case 17: _pet.EnterWalkingDirection(1f); break;
            case 18: _pet.EnterWalkingDirection(-1f); break;

            // Zones
            case 100: OpenActivity(new ZoneActivity(_assets, "beach")); break;
            case 101: OpenActivity(new ZoneActivity(_assets, "apartment")); break;
            case 102: OpenActivity(new ZoneActivity(_assets, "bedroom")); break;
            case 103: OpenActivity(new ZoneActivity(_assets, "camping")); break;

            // Activities
            case 6: OpenActivity(new FishingActivity(_assets, _audio)); break;
            case 41: OpenActivity(new CookingActivity(_assets, _audio)); break;
            case 42: OpenActivity(new StargazingActivity(_assets, _audio)); break;
            case 43: OpenActivity(new GardeningActivity(_assets, _audio)); break;
            case 44: OpenActivity(new DanceActivity(_assets, _audio)); break;
            case 45: OpenActivity(new KiteFlyingActivity(_assets, _audio)); break;
            // Paint runs in the sibling MouseHouse.Activities process so the
            // user can open multiple Paint windows side-by-side and copy /
            // paste between them via the system clipboard.
            case 7: LaunchRetroGame(7); break;
            case 3: OpenActivity(new SolitaireActivity(_assets)); break;
            // Chess puzzle runs in the sibling MouseHouse.Activities process so
            // its window can be put behind other apps while the pet stays
            // pinned above everything (Raylib being single-window means an
            // in-process activity would force the pet to drop with it).
            case 8: LaunchRetroGame(8); break;
            // Trailing OpenActivity ref left so the type stays linked into
            // the main exe build for any code paths that still construct it
            // directly. The companion-process build owns the active instance.
            // Retro Pack 1-4 games + chess puzzles run in the sibling
            // MouseHouse.Activities executable so their windows have normal
            // OS-level Z order — the user can put other apps on top of them
            // while the pet's main window stays pinned above everything.
            case 200:
            case >= 201 and <= 208:
            case >= 210 and <= 216:
            case >= 240 and <= 246:
            case >= 250 and <= 256:
            case 260:
            case 261:
            case 270:
                LaunchRetroGame(id);
                break;
            case >= 220 and < 220 + 16:
                int themeIdx = id - 220;
                if (themeIdx < RetroSkin.AllThemes.Length)
                {
                    RetroSkin.Current = RetroSkin.AllThemes[themeIdx];
                    _settings.RetroThemeName = RetroSkin.Current.Name;
                    _settings.Save();
                    MouseHouse.Core.ThemeSync.Write(RetroSkin.Current.Name);
                    // Update the previewed-from baseline so the
                    // OnItemHover(-1) that fires after Hide() (menu
                    // close) doesn't snap us back to the old theme.
                    _committedThemeName = RetroSkin.Current.Name;
                }
                break;
            case 80: OpenActivity(new FontPreviewActivity(_assets, OnFontSelected)); break;
            case 87: OpenActivity(new FontSizeActivity(FontManager.LoadSize, OnFontSizeChanged)); break;
            case 88: OpenActivity(new MenuFontSizeActivity(PopupMenu.FontSize, OnMenuFontSizeChanged)); break;

            // Color modes
            case 20: SetColorMode("2color"); break;
            case 21: SetColorMode("1color"); break;
            case 22: SetColorMode("fullcolor"); break;

            // Scale
            case 30: SetScale(1f); break;
            case 33: SetScale(1.5f); break;
            case 31: SetScale(2f); break;
            case 32: SetScale(3f); break;

            // Activity Scale
            case 34: SetUIScale(1f); break;
            case 36: SetUIScale(2f); break;

            // Font filters
            case 81: SetFontFilter(TextureFilter.Point); break;
            case 82: SetFontFilter(TextureFilter.Bilinear); break;
            case 83: SetFontFilter(TextureFilter.Trilinear); break;
            case 84: SetFontFilter(TextureFilter.Anisotropic4X); break;
            case 85: SetFontFilter(TextureFilter.Anisotropic8X); break;
            case 86: SetFontFilter(TextureFilter.Anisotropic16X); break;

            case 16:
                _audio.Muted = !_audio.Muted;
                _settings.Muted = _audio.Muted;
                _settings.Save();
                break;

            case 70:
                // Set or edit — StartEditing preserves the existing
                // text when the bubble is already visible.
                _statusBubble.StartEditing();
                break;

            case 295:
                if (_buddyWidget != null)
                {
                    _buddyWidget.Visible = !_buddyWidget.Visible;
                    if (_buddyWidget.Visible)
                    {
                        // Centre under the pet so the user can see
                        // it on open without dragging — this is the
                        // pet's first frame of buddy-list visibility.
                        _buddyWidget.Position = _menuOpenPos
                            - new System.Numerics.Vector2(
                                MouseHouse.UI.BuddyList.BuddyListWidget.W / 2f, 0);
                    }
                }
                break;
            case 290:
                _radio.Visible = !_radio.Visible;
                if (!_radio.Visible) _radioPlayer.Stop();
                UpdateTopmost();
                SaveRadioState();
                break;

            case 292:
                // Radio (Standalone Debug) — spawns the same widget in the
                // MouseHouse.Activities companion process so the user can
                // sanity-check the out-of-process port. The in-process
                // radio above stays untouched.
                if (IsRadioRunning())
                {
                    try { _radioProc?.Kill(); } catch { }
                    _radioProc?.Dispose();
                    _radioProc = null;
                    _settings.RadioOpen = false;
                    _settings.Save();
                }
                else
                {
                    LaunchRetroGame(9);
                }
                break;

            case 291:
                _destroyer.Toggle();
                break;

            case 50: _events.ForceSpawn(); break;
            case 51: _events.Enabled = !_events.Enabled; break;

            // Multiplayer
            case 60: // Host
                _mp.Host();
                var code = _mp.GetVisitCode();
                if (code != null)
                    Console.WriteLine($"Visit code: {code}");
                break;
            case 61: // Join — for now just log; TODO: text input UI
                Console.WriteLine("Join: paste visit code as command-line argument");
                break;
            case 63: _mp.Disconnect(); break;
            case 64: _mp.KickVisitor(); break;

            case 95: OpenActivity(new CreditsActivity(_assets)); break;
            case 280: OpenActivity(new NotepadActivity()); break;
            case 281: OpenActivity(new ClipboardManagerActivity()); break;
            case 282: OpenActivity(new SleepSoundsActivity(_assets)); break;
            case 283: OpenActivity(new FishAquariumActivity()); break;
            case 284: OpenActivity(new DrumMachineActivity()); break;
            case 285: OpenActivity(new SpreadsheetActivity()); break;
            case 286: OpenActivity(new EReaderActivity()); break;
            case 287: OpenActivity(new MathFlashcardsActivity()); break;
            case 288: OpenActivity(new ClockQuizActivity()); break;
            case 289: OpenActivity(new HollysNumDropsActivity()); break;
            case 297: OpenActivity(new NekoActivity()); break;
            case 298: OpenActivity(new SpiroplotActivity()); break;
            case 299: OpenActivity(new SokobanActivity()); break;
            case 300: OpenActivity(new LinkFourActivity()); break;
            case 301: OpenActivity(new WinRoachActivity()); break;
            case 302: OpenActivity(new BlackoutActivity()); break;
            case 303: OpenActivity(new MedievalWarActivity()); break;
            case 304: OpenActivity(new PentominoesActivity()); break;
            case 296:
                MouseHouse.UI.AmoebaTheme.Enabled = !MouseHouse.UI.AmoebaTheme.Enabled;
                _settings.AmoebaDrips = MouseHouse.UI.AmoebaTheme.Enabled;
                _settings.Save();
                break;
            case 99: Environment.Exit(0); break;
        }
    }

    private void SetColorMode(string mode)
    {
        _settings.ColorMode = mode;
        _settings.Save();
        ApplyColorMode(mode);
        _events.SetColorMode(mode);
    }

    private void OnMenuFontSizeChanged(int size)
    {
        PopupMenu.FontSize = size;
        _settings.MenuFontSize = size;
        _settings.Save();
    }

    private void OnFontSizeChanged(int size)
    {
        _settings.FontLoadSize = size;
        _settings.Save();
    }

    private void SetFontFilter(TextureFilter filter)
    {
        FontManager.SetFilter(filter);
        _settings.FontFilter = filter.ToString();
        _settings.Save();
    }

    private void OnFontSelected(string fontFile)
    {
        FontManager.SetFont(fontFile);
        _settings.FontFile = fontFile;
        _settings.Save();
    }

    private void SetScale(float scale)
    {
        _pet.Scale = scale;
        _settings.ScaleOverride = scale;
        _settings.Save();
    }

    private void SetUIScale(float scale)
    {
        float oldW = _screenWidth;
        float oldH = _screenHeight;
        UIScaling.Factor = scale;
        _screenWidth = (int)(App.PhysicalWidth / scale);
        _screenHeight = (int)(App.PhysicalHeight / scale);
        // Reposition pet proportionally so it doesn't jump off-screen.
        _pet.Position = new System.Numerics.Vector2(
            _pet.Position.X * _screenWidth / oldW,
            _pet.Position.Y * _screenHeight / oldH);
        _settings.UIScaleOverride = scale;
        _settings.Save();
    }

    public void Draw()
    {
        // Draw events behind everything
        _events.Draw();

        // Toys sit on the desktop, drawn under cheese (cheese might be
        // tossed into / on top of toys). Both go under the pet.
        _toys.Draw();

        // Cheeses + crumbs sit on the desktop, under the pet but above
        // background events. Reactions/HUD are drawn after the pet so they
        // float above its sprite.
        _cheese.Draw(CheeseCellPx());

        // Floating widgets sit *under* the pet so the mouse always wins z-order.
        _radio.Draw();
        if (_radio.Visible)
            MouseHouse.UI.AmoebaTheme.DrawDrips(_radio.Position,
                new Vector2(MouseHouse.UI.RadioWidget.W, MouseHouse.UI.RadioWidget.H));
        _buddyWidget?.Draw();
        if (_buddyWidget != null && _buddyWidget.Visible)
            MouseHouse.UI.AmoebaTheme.DrawDrips(_buddyWidget.Position,
                new Vector2(MouseHouse.UI.BuddyList.BuddyListWidget.W,
                            MouseHouse.UI.BuddyList.BuddyListWidget.H));
        _destroyer.Draw();

        // Status bubble (view-mode wave-text and edit-mode input chrome) drawn
        // under the pet so the pet sprite remains visible above it.
        var (bubblePetPos, bubblePetSize) = _pet.GetBounds();
        _statusBubble.DrawWaveText(bubblePetPos, bubblePetSize);
        _statusBubble.Draw(bubblePetPos, bubblePetSize);

        // Activity panel goes under the pet so the mouse always wins z-order.
        if (_activeActivity != null)
        {
            if (!_activeActivity.TransparentBackground)
            {
                Raylib.DrawRectangle((int)_activityOffset.X + 4, (int)_activityOffset.Y + 4,
                    (int)_activeActivity.PanelSize.X, (int)_activeActivity.PanelSize.Y,
                    new Color(0, 0, 0, 80));
            }
            _activeActivity.Draw(_activityOffset);
            if (!_activeActivity.TransparentBackground)
                MouseHouse.UI.AmoebaTheme.DrawDrips(_activityOffset, _activeActivity.PanelSize);
        }

        // Popup menu also under the pet — it anchors next to the cursor,
        // so the pet rarely overlaps it in practice.
        _menu.Draw();

        // Pet sprite — drawn last so the mouse is always on top of every UI element.
        var sheet = _pet.ActiveSheet;
        sheet?.DrawFrame(_pet.CurrentFrame, _pet.Position, _pet.Scale, _pet.FlipH);

        // Costume rides on top of the pet sprite. Tiny vertical bob during
        // walking/idle so the hat doesn't feel rigidly glued on.
        if (_costume != CostumeType.None)
        {
            float bob = (_pet.State == PetState.Walking || _pet.State == PetState.SeekingCheese
                      || _pet.State == PetState.SeekingToy)
                ? MathF.Sin((float)Raylib.GetTime() * 9f) * 0.6f
                : MathF.Sin((float)Raylib.GetTime() * 1.5f) * 0.3f;
            CostumeRenderer.Draw(_costume, _pet.Position, _pet.Scale, _pet.FlipH, bob);
        }

        // Floating reactions (hearts / sneeze stars / ♪ notes) above pet.
        _reactions.Draw();

        // Cheese-name wave text — letter-bob effect lifted from the golf
        // celebration banner, smaller, drifting upward and fading out.
        DrawCheeseWaves();

        // Needs HUD disabled per user request 2026-05-08; uncomment to restore.
        // if (_needs.ShowHud) DrawNeedsHud();
        if (_placingCheese) DrawCheeseGhost();

        // Debug click marker — disabled. Re-enable by uncommenting the
        // _debugLastClick capture in Update() and the draw block below.
        // if (_debugLastClick.HasValue)
        // {
        //     var p = _debugLastClick.Value;
        //     var col = new Color((byte)255, (byte)0, (byte)255, (byte)255);
        //     Raylib.DrawPixel((int)p.X, (int)p.Y, col);
        //     Raylib.DrawLine((int)p.X - 6, (int)p.Y, (int)p.X - 2, (int)p.Y, col);
        //     Raylib.DrawLine((int)p.X + 2, (int)p.Y, (int)p.X + 6, (int)p.Y, col);
        //     Raylib.DrawLine((int)p.X, (int)p.Y - 6, (int)p.X, (int)p.Y - 2, col);
        //     Raylib.DrawLine((int)p.X, (int)p.Y + 2, (int)p.X, (int)p.Y + 6, col);
        // }
    }

    private void DrawNeedsHud()
    {
        // Compact 4-bar HUD pinned to the bottom-left of the screen so it
        // doesn't interfere with the pet's roaming area.
        int x = 12, y = _screenHeight - 80;
        int w = 140, h = 64, pad = 6;
        Raylib.DrawRectangle(x, y, w, h, new Color((byte)20, (byte)20, (byte)24, (byte)160));
        Raylib.DrawRectangleLines(x, y, w, h, new Color((byte)180, (byte)180, (byte)190, (byte)200));

        DrawNeedBar("hunger ",  _needs.Hunger,  x + pad, y + pad,        w - pad * 2,
                    new Color((byte)244, (byte)180, (byte)80, (byte)255));
        DrawNeedBar("energy ",  _needs.Energy,  x + pad, y + pad + 14,   w - pad * 2,
                    new Color((byte)160, (byte)220, (byte)110, (byte)255));
        DrawNeedBar("happy  ",  _needs.Happy,   x + pad, y + pad + 28,   w - pad * 2,
                    new Color((byte)240, (byte)160, (byte)200, (byte)255));
        DrawNeedBar("hygiene", _needs.Hygiene, x + pad, y + pad + 42,   w - pad * 2,
                    new Color((byte)110, (byte)200, (byte)240, (byte)255));
    }

    private static void DrawNeedBar(string label, float v, int x, int y, int width, Color fill)
    {
        FontManager.DrawText(label, x, y, 10, new Color((byte)220, (byte)220, (byte)220, (byte)255));
        int barX = x + 50;
        int barW = width - 50;
        Raylib.DrawRectangle(barX, y + 1, barW, 8, new Color((byte)40, (byte)40, (byte)50, (byte)255));
        int filled = (int)(barW * Math.Clamp(v, 0, 100) / 100f);
        Raylib.DrawRectangle(barX, y + 1, filled, 8, fill);
        Raylib.DrawRectangleLines(barX, y + 1, barW, 8, new Color((byte)100, (byte)100, (byte)110, (byte)255));
    }

    /// <summary>
    /// Cell-block size for the cheese sprite + dissolve. Tracks pet scale
    /// so cheese reads at proportional chunkiness to the rendered mouse,
    /// but uses floor (not round) on the float scale: at pet scale 1.5,
    /// banker's rounding promoted cellPx to 2 — same as scale 2 — making
    /// the cheese visibly oversized against a mouse that's only 1.5×.
    /// Floor gives 1.5 → 1, 2.0 → 2, 3.0 → 3, matching the available
    /// scale presets cleanly.
    /// </summary>
    private int CheeseCellPx() => Math.Max(1, (int)MathF.Floor(_pet.Scale));

    private void DrawCheeseGhost()
    {
        // Solid preview at the cursor — no transparency, matches how the
        // placed cheese will actually render once the user clicks. Renders
        // the variety that'll be dropped (re-rolled after every place).
        var p = WindowHelper.GetGlobalCursorPosition();
        CheeseImages.Draw(_placingCheeseType, p, CheeseCellPx());

        // Floating retro tooltip below + right of the cursor with the
        // instructions and a Cancel chip mouse-only users can click.
        // Offset enough that the tooltip never sits under the cheese
        // sprite (~ 24-32 px tall depending on scale).
        const int FontSize = 13;
        const string Line = "Click to place — right-click to stop";
        const string Chip = " X ";
        int tw = RetroSkin.MeasureText(Line, FontSize);
        int chipW = RetroSkin.MeasureText(Chip, FontSize) + 8;
        int pad = 6;
        int totalW = tw + 6 + chipW + pad * 2;
        int totalH = FontSize + pad * 2;
        int tipX = (int)p.X + 18;
        int tipY = (int)p.Y + 18;
        // Keep on-screen — flip left/up against the cursor when the
        // tooltip would clip the monitor edge.
        int sw = Raylib.GetRenderWidth();
        int sh = Raylib.GetRenderHeight();
        if (tipX + totalW > sw) tipX = (int)p.X - totalW - 8;
        if (tipY + totalH > sh) tipY = (int)p.Y - totalH - 8;

        var bg = new Rectangle(tipX, tipY, totalW, totalH);
        RetroSkin.DrawRaised(bg);
        RetroSkin.DrawText(Line, tipX + pad, tipY + pad,
            RetroSkin.BodyText, FontSize);
        _placementCancelChip = new Rectangle(
            tipX + pad + tw + 6, tipY + 3, chipW, totalH - 6);
        RetroSkin.DrawPressed(_placementCancelChip);
        int chipTextW = RetroSkin.MeasureText(Chip, FontSize);
        RetroSkin.DrawText(Chip,
            (int)_placementCancelChip.X
            + ((int)_placementCancelChip.Width - chipTextW) / 2,
            (int)_placementCancelChip.Y + 2,
            new Color((byte)160, (byte)32, (byte)32, (byte)255),
            FontSize);
    }

    /// <summary>
    /// Render every active cheese-name wave overlay. Each character bobs
    /// on a sine wave staggered by index — same effect the golf game uses
    /// for HOLE IN ONE / EAGLE / etc — but at a smaller font size and
    /// drifting upward as it ages so it reads as a quick "yum, goat
    /// cheese!" pop above the mouse rather than a full-screen banner.
    /// </summary>
    private void DrawCheeseWaves()
    {
        const int FontSize = 16;
        const float RisePx = 12f;          // total upward drift across life
        foreach (var w in _cheeseWaves)
        {
            float t01 = Math.Clamp(w.Time / w.Life, 0f, 1f);
            // Quick fade-in over the first 10%, hold to 80%, fade to 0
            // for the last 20%. Keeps the text crisp when it matters.
            float alpha01 = t01 < 0.1f ? t01 / 0.1f
                          : t01 > 0.8f ? (1f - t01) / 0.2f
                          : 1f;
            byte alpha = (byte)Math.Clamp(255 * alpha01, 0, 255);

            // Anchor sits at the visible mouse head (not the sprite-bounds
            // top). Place the text bottom right at the head and drift
            // upward over life so the celebration reads as a comic-style
            // pop right next to the mouse, not floating off in the sky.
            int cx = (int)w.Anchor.X;
            int baselineY = (int)(w.Anchor.Y - FontSize - RisePx * t01);
            // If a user status is currently visible, push the cheese
            // celebration up by the status's font size + a small gap so
            // the two stack cleanly instead of overlapping.
            if (_statusBubble.Visible && !_statusBubble.IsEditing)
                baselineY -= FontSize + 4;
            DrawPetWaveText(w.Text, cx, baselineY, FontSize, w.Time, alpha,
                CheeseWavePalette);
        }
    }

    /// <summary>Warm orange/yellow palette for cheese celebrations.</summary>
    private static readonly (byte R, byte G, byte B)[] CheeseWavePalette =
    {
        (255, 220,  80),
        (255, 180,  80),
        (255, 140,  80),
        (255, 200, 120),
        (255, 240, 140),
    };

    /// <summary>Cool teal/blue palette for the pet's user-set status —
    /// distinct from the cheese celebration so the two can sit stacked
    /// above the pet without colour-fighting each other.</summary>
    public static readonly (byte R, byte G, byte B)[] StatusWavePalette =
    {
        (180, 220, 255),
        (140, 200, 240),
        (200, 230, 255),
        (160, 215, 250),
        (220, 240, 255),
    };

    /// <summary>
    /// Letter-by-letter bobbing wave-text helper, ported from the golf
    /// activity's celebration banner (DrawWaveText) and tuned for the
    /// pet overlay: smaller font, smaller bob amplitude, alpha-aware so
    /// the rise-and-fade reads cleanly. Palette is parameterised so
    /// cheese celebrations and status display can share the rendering
    /// path without colliding visually.
    /// </summary>
    public static void DrawPetWaveText(string text, int cx, int baselineY,
                                       int size, float time, byte alpha,
                                       (byte R, byte G, byte B)[] palette,
                                       bool animate = true)
    {
        int tracking = Math.Max(1, size / 12);
        int charsW = 0;
        for (int i = 0; i < text.Length; i++)
            charsW += FontManager.MeasureText(text[i].ToString(), size);
        int totalW = charsW + Math.Max(0, text.Length - 1) * tracking;
        int x = cx - totalW / 2;
        var shadow = new Color((byte)0, (byte)0, (byte)0, (byte)(alpha * 200 / 255));
        for (int i = 0; i < text.Length; i++)
        {
            string ch = text[i].ToString();
            int chW = FontManager.MeasureText(ch, size);
            float bob = animate ? MathF.Sin(time * 6f + i * 0.55f) * 4f : 0f;
            var (r, g, b) = palette[i % palette.Length];
            var col = new Color(r, g, b, alpha);
            FontManager.DrawText(ch, x + 1, baselineY + (int)bob + 1, size, shadow);
            FontManager.DrawText(ch, x, baselineY + (int)bob, size, col);
            x += chW + tracking;
        }
    }

    /// <summary>
    /// Measures the laid-out width of a wave-text string at the given size,
    /// matching DrawPetWaveText's tracking math. Used by hover-gated callers
    /// (e.g. the status bubble) to size a hit-test rect around the text.
    /// </summary>
    public static int MeasurePetWaveTextWidth(string text, int size)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int tracking = Math.Max(1, size / 12);
        int charsW = 0;
        for (int i = 0; i < text.Length; i++)
            charsW += FontManager.MeasureText(text[i].ToString(), size);
        return charsW + Math.Max(0, text.Length - 1) * tracking;
    }

    private Dictionary<IdleActionType, SpriteSheet> LoadIdleActionSheets()
    {
        return new Dictionary<IdleActionType, SpriteSheet>
        {
            [IdleActionType.Grooming] = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/idle_grooming.png", 8),
            [IdleActionType.Sniffing] = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/idle_sniffing.png", 4),
            [IdleActionType.Yawning] = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/idle_yawning.png", 6),
            [IdleActionType.LookingAround] = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/idle_looking.png", 6),
            [IdleActionType.TailWag] = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/idle_tailwag.png", 4),
            [IdleActionType.Stretching] = _assets.GetSpriteSheetWithAlpha("assets/pet/sprites/idle_stretching.png", 6),
        };
    }

    private class SpriteSheetSet
    {
        public SpriteSheet Walk = null!;
        public SpriteSheet Idle = null!;
        public SpriteSheet Sleep = null!;
        public SpriteSheet SleepLoop = null!;
        public SpriteSheet Jump = null!;
        public Dictionary<IdleActionType, SpriteSheet> IdleActions = new();
    }
}
