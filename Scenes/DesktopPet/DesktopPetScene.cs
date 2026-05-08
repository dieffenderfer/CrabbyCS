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
    private readonly int _screenWidth;
    private readonly int _screenHeight;
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
    private readonly RadioPlayer _radioPlayer = new();
    private readonly RadioWidget _radio;

    // Most-recently-launched Fuji Golf companion process. We track it so
    // the per-frame snapshot can persist its open-state to settings, and
    // the next launch can auto-restore it.
    private System.Diagnostics.Process? _fujiGolfProc;

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
    private float _hoverPetTimer;
    private float _hoverHeartCooldown;
    private const float HoverPetThreshold = 0.7f;
    private const float HoverHeartInterval = 0.9f;
    private float _snootCooldown;
    private float _zoomiesTimer;

    // Cheese placement mode — user picked "Place Cheese" from the menu and
    // the next left-click anywhere on screen drops a cheese there.
    private bool _placingCheese;

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
        _menu.OnItemSelected += OnMenuItemSelected;
        _menu.OnItemHover += OnMenuItemHover;
        _settings = PetSettings.Load();
        _radio = new RadioWidget(_radioPlayer);
        _radio.StateChanged = SaveRadioState;

        _toys.Load();
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

        // Re-spawn Fuji Golf if it was open at last shutdown — same
        // session-restore model as the radio. LaunchRetroGame stores the
        // process in _fujiGolfProc and re-asserts the persisted flag.
        if (_settings.FujiGolfOpen) LaunchRetroGame(246);

        _pet.Init(_screenWidth, _screenHeight);
        TimeSystem.Update();
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

        // Detect when Fuji Golf was closed via its own X button: the
        // child process exits, we notice on the next frame, and persist
        // FujiGolfOpen=false so it doesn't re-spawn next launch.
        if (_fujiGolfProc != null && _fujiGolfProc.HasExited)
        {
            _fujiGolfProc.Dispose();
            _fujiGolfProc = null;
            if (_settings.FujiGolfOpen)
            {
                _settings.FujiGolfOpen = false;
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
                    _activityOffset = mousePos - _activityDragOffset;
                    activityConsumed = true;
                    if (_input.LeftReleased)
                        _draggingActivity = false;
                }
                else if (_input.LeftPressed && Raylib.CheckCollisionPointRec(mousePos, closeRect))
                {
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
                    _draggingActivity = true;
                    _activityDragOffset = mousePos - _activityOffset;
                    activityConsumed = true;
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
            || _destroyer.ContainsPoint(mousePos);

        bool radioConsumed = _radio.Update(delta, mousePos,
            !activityConsumed && _input.LeftPressed,
            _input.LeftReleased,
            !activityConsumed && _input.RightPressed);
        if (radioConsumed) activityConsumed = true;

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

        bool wantCapture = activityConsumed || _draggingActivity
            || _destroyer.ShouldCaptureMouse
            || _mouseOverPet || _mouseOverUI
            || _pet.State == PetState.Dragging
            || _menu.Visible || _statusBubble.IsEditing
            || _placingCheese;

        // Hysteresis: once we want capture, hold it briefly even after the
        // cursor moves out, so a click queued during the same frame as the
        // exit still finds passthrough off. Kept short — the previous 250 ms
        // extended the dead zone long enough that clicks in transparent
        // space near the pet felt eaten until the user moved noticeably away.
        // 60 ms ≈ 3-4 frames at 60 Hz, plenty for OS event-dispatch latency.
        const float CaptureHoldSeconds = 0.06f;
        if (wantCapture) _captureHoldTimer = CaptureHoldSeconds;
        else _captureHoldTimer = MathF.Max(0, _captureHoldTimer - delta);
        bool shouldCapture = wantCapture || _captureHoldTimer > 0f;
        WindowHelper.SetMousePassthrough(!shouldCapture);

        // Cheese placement mode — drops at cursor on left click, cancels on
        // right click or ESC. Consumes input so the pet/menu logic below
        // doesn't also see this click as a drag-grab or context-menu open.
        bool placementConsumed = false;
        if (_placingCheese && !activityConsumed && !menuConsumed && !statusConsumed)
        {
            if (_input.IsKeyPressed(KeyboardKey.Escape) || _input.RightPressed)
            {
                _placingCheese = false;
                placementConsumed = true;
            }
            else if (_input.LeftPressed)
            {
                _cheese.Drop(CheeseType.Cheddar, mousePos);
                _placingCheese = false;
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
                    _needs.Add(NeedKind.Happy, 4);
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
        UpdatePetting(delta, mousePos);
        _snootCooldown = MathF.Max(0, _snootCooldown - delta);

        // Tick needs after AI so the same frame's interaction effects
        // (RecordEat, finished UsingToy, etc.) bump them up before decay.
        bool sleeping = _pet.State == PetState.Sleeping
                     || (_pet.State == PetState.UsingToy && _currentToy?.Type == ToyType.Bed);
        _needs.Tick(delta, sleeping);

        UpdateZoomies(delta);
    }

    /// <summary>
    /// Hover-to-pet: holding the cursor over the pet's body for ~0.7 s spawns
    /// a heart and bumps Happy. Continuing to hover keeps emitting hearts at
    /// a slower rate. Resets if the cursor leaves the sprite, an activity is
    /// open, or the pet is being dragged.
    /// </summary>
    private void UpdatePetting(float delta, Vector2 mousePos)
    {
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

        // From a free state, route to a toy. If a need is unmet we look for
        // the toy type that best satisfies it; otherwise pick the nearest
        // unused toy at random idle moments.
        if (_pet.State == PetState.Idle && _toys.Toys.Count > 0)
        {
            ToyInstance? target = null;
            var unmet = _needs.LowestUnmet();
            if (unmet.HasValue)
            {
                var prefer = ToyForNeed(unmet.Value);
                if (prefer.HasValue) target = _toys.FindClosestUnused(center, prefer);
            }
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
            // interrupted) — apply the reward and free the toy.
            ApplyToyReward(_currentToy.Type);
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
            // Shrink it over the eat duration.
            float total = MouseHouse.Scenes.DesktopPet.Cheese.Cheeses.Get(c.Type).EatSeconds;
            if (total <= 0.01f) total = 1.4f;
            float dec = (1f / total) * delta;
            c.Size -= dec;
            _cheese.OnBiteTaken(c, center);

            // Match the pet's eat-state duration to the cheese variety so
            // brie really does linger longer than cheddar.
            _pet.CheeseEatTotal = total;

            if (c.Size <= 0.01f)
            {
                _cheese.Active.Remove(c);
                _cheese.RecordEat(c, center);
                _needs.Add(NeedKind.Hunger, 35f);
                _needs.Add(NeedKind.Happy, 8f);
                _pet.HasCheeseTarget = false;
                _pet.EnterIdle();
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
            statusFontSize: RetroWidgets.StatusFontSize);
        // Track Fuji Golf so we can persist its open-state and re-launch
        // it on next start. Other retro games aren't tracked — only Fuji
        // Golf and the radio currently support session restore.
        if (activityId == 246 && proc != null)
        {
            _fujiGolfProc = proc;
            _settings.FujiGolfOpen = true;
            _settings.Save();
        }
    }

    private void ShowContextMenu(Vector2 position)
    {
        _menuOpenPos = position;
        var items = new List<MenuItem>();

        if (_pet.State == PetState.Sleeping)
            items.Add(MenuItem.Item("Wake Up", 1));
        else
            items.Add(MenuItem.Item("Sleep", 0));

        items.Add(MenuItem.Item("Jump", 15));
        items.Add(MenuItem.Item("Walk Right", 17));
        items.Add(MenuItem.Item("Walk Left", 18));
        items.Add(MenuItem.Item(_statusBubble.Visible ? "Clear Status" : "Set Status", 70));
        items.Add(MenuItem.Separator());

        // Cheese — pick "Place Cheese" then click anywhere to drop one.
        items.Add(MenuItem.Item("Place Cheese", 700));
        if (_cheese.Active.Count > 0)
            items.Add(MenuItem.Item($"Clear cheese ({_cheese.Active.Count})", 719));

        // Toys — placeable persistent objects. IDs 730..749.
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
        items.Add(MenuItem.Submenu("Toys", toyItems));

        // Wardrobe — pick a hat / accessory or "None" to remove. IDs 800..815.
        var wardrobeItems = new List<MenuItem>();
        for (int i = 0; i < CostumeRenderer.All.Length; i++)
        {
            var (type, name) = CostumeRenderer.All[i];
            string label = _costume == type ? $"• {name}" : name;
            wardrobeItems.Add(MenuItem.Item(label, 800 + i));
        }
        items.Add(MenuItem.Submenu("Wardrobe", wardrobeItems));

        items.Add(MenuItem.Item(_needs.ShowHud ? "Hide Needs HUD" : "Show Needs HUD", 760));
        items.Add(MenuItem.Separator());

        // Zones submenu (floating prop scenes on the desktop)
        items.Add(MenuItem.Submenu("Zones", new List<MenuItem>
        {
            MenuItem.Item("Beach", 100),
            MenuItem.Item("Apartment", 101),
            MenuItem.Item("Bedroom", 102),
            MenuItem.Item("Camping", 103),
        }));

        // Activities submenu (non-Pack mini-games and tools)
        items.Add(MenuItem.Submenu("Activities", new List<MenuItem>
        {
            MenuItem.Item("Go Fishing", 6),
            MenuItem.Item("Cooking", 41),
            MenuItem.Item("Gardening", 43),
            MenuItem.Item("Dance", 44),
            MenuItem.Item("Kite Flying", 45),
            MenuItem.Item("Stargazing", 42),
            MenuItem.Item("Paint", 7),
            MenuItem.Item("Solitaire", 3),
            MenuItem.Item("Chess Puzzles", 8),
            MenuItem.Item("Chess Puzzles (Retro)", 260),
            MenuItem.Separator(),
            MenuItem.Item("Retro Chrome Demo", 200),
        }));

        // Entertainment Pack games — sectioned by pack with disabled header
        // rows acting as labels. Beta games (uncertain mechanics or stand-ins)
        // live in a sibling submenu so the main list stays trustworthy.
        items.Add(MenuItem.Submenu("Games", new List<MenuItem>
        {
            MenuItem.Item("── Pack 1 ──", -2, false),
            MenuItem.Item("Minesweeper", 201),
            MenuItem.Item("Golf", 202),
            MenuItem.Item("Cruel", 203),
            MenuItem.Item("Taipei", 204),
            MenuItem.Item("Pegged", 205),
            MenuItem.Item("TicTactics", 206),
            MenuItem.Item("Tetris", 207),
            MenuItem.Item("IdleWild", 208),
            MenuItem.Item("── Pack 2 ──", -2, false),
            MenuItem.Item("FreeCell", 210),
            MenuItem.Item("Tut's Tomb", 211),
            MenuItem.Item("Jigsawed", 213),
            MenuItem.Item("Rattler Race", 214),
            MenuItem.Item("Pipe Dream", 215),
            MenuItem.Item("Rodent's Revenge", 216),
            MenuItem.Item("── Pack 3 ──", -2, false),
            MenuItem.Item("TriPeaks", 240),
            MenuItem.Item("TetraVex", 241),
            MenuItem.Item("Klotski", 242),
            MenuItem.Item("Life Genesis", 243),
            MenuItem.Item("Fuji Golf", 246),
            MenuItem.Item("── Pack 4 ──", -2, false),
            MenuItem.Item("Chess", 250),
            MenuItem.Item("Dr. Black Jack", 252),
            MenuItem.Item("Go Figure!", 253),
            MenuItem.Item("JezzBall", 254),
            MenuItem.Item("Tic Tac Drop", 256),
        }));

        // Beta games: stand-in mechanics, single-level demos, or otherwise
        // significantly reduced compared to the original.
        items.Add(MenuItem.Submenu("Games (Beta)", new List<MenuItem>
        {
            MenuItem.Item("Stones",            212),  // 5-in-a-row stand-in
            MenuItem.Item("Maxwell's Maniac",  255),  // Simon stand-in
            MenuItem.Item("WordZap",           244),  // small built-in dictionary
            MenuItem.Item("SkiFree",           245),  // no slalom mode
            MenuItem.Item("Chip's Challenge",  251),  // 3 small original levels
        }));

        // Retro theme picker (applies to all Entertainment Pack games + chrome)
        var themeItems = new List<MenuItem>();
        for (int i = 0; i < RetroSkin.AllThemes.Length; i++)
        {
            var t = RetroSkin.AllThemes[i];
            themeItems.Add(MenuItem.Item(t.Name, 220 + i, RetroSkin.Current.Name != t.Name));
        }
        items.Add(MenuItem.Submenu("Retro Theme", themeItems));

        // Appearance submenu
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
        }));

        items.Add(MenuItem.Separator());
        items.Add(MenuItem.Item(_radio.Visible ? "Hide Radio" : "Show Radio", 290));
        items.Add(MenuItem.Item(_destroyer.Active ? "Stop Destroying Desktop" : "Destroy Desktop", 291));
        items.Add(MenuItem.Item(_audio.Muted ? "Unmute Audio" : "Mute Audio", 16));
        items.Add(MenuItem.Item(_events.Enabled ? "Disable Events" : "Enable Events", 51));
        items.Add(MenuItem.Item("Spawn Event", 50, _events.Enabled));

        // Multiplayer submenu (only shown when enabled)
        if (_mp.Enabled)
        {
            var mpItems = new List<MenuItem>();
            if (!_mp.IsConnected)
            {
                mpItems.Add(MenuItem.Item("Host Game", 60));
                mpItems.Add(MenuItem.Item("Join Game...", 61));
            }
            else
            {
                var who = _mp.RemoteName ?? "peer";
                mpItems.Add(MenuItem.Item($"Connected: {who}", 62, false));
                mpItems.Add(MenuItem.Item("Disconnect", 63));
                if (_mp.IsHost)
                    mpItems.Add(MenuItem.Item("Kick Visitor", 64));
            }
            items.Add(MenuItem.Submenu("Multiplayer", mpItems));
        }

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
        // desktop drops a cheese there and the pet goes to eat it.
        if (id == 700)
        {
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
        if (id == 760)
        {
            _needs.ShowHud = !_needs.ShowHud;
            return;
        }
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
            case 8: OpenActivity(new ChessPuzzleActivity(_assets)); break;
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
                if (_statusBubble.Visible) _statusBubble.Hide();
                else _statusBubble.StartEditing();
                break;

            case 290:
                _radio.Visible = !_radio.Visible;
                if (!_radio.Visible) _radioPlayer.Stop();
                UpdateTopmost();
                SaveRadioState();
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
        _cheese.Draw();

        // Floating widgets sit *under* the pet so the mouse always wins z-order.
        _radio.Draw();
        _destroyer.Draw();

        // Draw pet on top of widgets so the mouse is always visible
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

        if (_needs.ShowHud) DrawNeedsHud();
        if (_placingCheese) DrawCheeseGhost();

        // Draw status bubble above pet
        var (bubblePetPos, bubblePetSize) = _pet.GetBounds();
        _statusBubble.Draw(bubblePetPos, bubblePetSize);

        // Draw activity panel on top (no full-screen dim — pet stays visible)
        if (_activeActivity != null)
        {
            // Drop shadow (skipped for transparent activities like zones)
            if (!_activeActivity.TransparentBackground)
            {
                Raylib.DrawRectangle((int)_activityOffset.X + 4, (int)_activityOffset.Y + 4,
                    (int)_activeActivity.PanelSize.X, (int)_activeActivity.PanelSize.Y,
                    new Color(0, 0, 0, 80));
            }
            _activeActivity.Draw(_activityOffset);
        }

        // Draw popup menu on top of everything
        _menu.Draw();

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

    private static int[]? _ghostOrder;
    private void DrawCheeseGhost()
    {
        // Solid preview at the cursor — no transparency, matches how the
        // placed cheese will actually render once the user clicks.
        if (_ghostOrder == null)
        {
            _ghostOrder = new int[CheeseSprites.CheddarCellCount];
            for (int i = 0; i < _ghostOrder.Length; i++) _ghostOrder[i] = i;
        }
        var p = WindowHelper.GetGlobalCursorPosition();
        CheeseSprites.DrawCheddarDissolve(p, 1.2f, _ghostOrder, hideCount: 0);
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
