using System.Numerics;
using MouseHouse.Rendering;

namespace MouseHouse.Scenes.DesktopPet;

public enum PetState
{
    Idle,
    Walking,
    Sleeping,
    Dragging,
    Thrown,
    Content,
    Jumping
}

/// <summary>
/// Manages the pet's state, animation, movement, and physics.
/// Ported from desktop_pet.gd's state machine.
/// </summary>
public class PetStateMachine
{
    // State
    public PetState State { get; private set; } = PetState.Idle;

    // Position (in screen coordinates)
    public Vector2 Position;
    public Vector2 Velocity;

    // Animation
    public int CurrentFrame;
    public bool FlipH;
    private float _animTimer;
    private float _animSpeed = 0.25f;
    private bool _facingRight;

    // Timers
    private float _idleTimer;
    private float _walkTimer;
    private float _jumpTimer;
    // Dragging
    private Vector2 _dragOffset;
    private readonly List<Vector2> _dragPositions = new();
    private readonly List<float> _dragTimes = new();
    private Vector2 _throwVelocity;
    private const float ThrowFriction = 0.97f;

    // Sleep state
    private bool _sleepIntroDone;

    // Screen bounds
    public int ScreenWidth;
    public int ScreenHeight;

    // Sprite info
    public const int FrameSize = 76;   // Mouse sprites are 76x76
    public const float DefaultScale = 2f;
    public float Scale = DefaultScale;

    // Textures (set by DesktopPetScene)
    public SpriteSheet? WalkSheet;
    public SpriteSheet? IdleSheet;
    public SpriteSheet? SleepSheet;
    public SpriteSheet? SleepLoopSheet;
    public SpriteSheet? JumpSheet;

    // Currently active sheet
    public SpriteSheet? ActiveSheet;

    private readonly Random _rng = new();

    public void Init(int screenWidth, int screenHeight)
    {
        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;
        Position = new Vector2(screenWidth / 2f, screenHeight / 2f);
        EnterIdle();
    }

    public void Update(float delta, Vector2 mousePos)
    {
        switch (State)
        {
            case PetState.Thrown:
                UpdateThrown(delta, mousePos);
                return;
            case PetState.Dragging:
                UpdateDragging(mousePos);
                return;
            default:
                break;
        }

        // Check proximity to mouse cursor -> become content
        if (State == PetState.Idle)
        {
            var center = Position + new Vector2(FrameSize * Scale / 2f);
            if (Vector2.Distance(center, mousePos) < 80)
                EnterContent();
        }

        // If content but mouse moved far away, go find it
        if (State == PetState.Content)
        {
            var center = Position + new Vector2(FrameSize * Scale / 2f);
            if (Vector2.Distance(center, mousePos) > 400)
                EnterWalking(mousePos);
        }

        // Animate
        UpdateAnimation(delta);

        // State timers
        switch (State)
        {
            case PetState.Idle:
                _idleTimer -= delta;
                if (_idleTimer <= 0)
                {
                    var roll = _rng.NextSingle();
                    if (roll < 0.15f)
                        EnterJumping();
                    else
                        EnterWalking(mousePos);
                }
                break;

            case PetState.Walking:
                _walkTimer -= delta;
                if (_walkTimer <= 0)
                    EnterIdle();
                else
                    MoveWindow(delta);
                break;

            case PetState.Sleeping:
                // Sleep until clicked
                break;

            case PetState.Jumping:
                _jumpTimer -= delta;
                if (_jumpTimer <= 0)
                    EnterIdle();
                break;
        }
    }

    private void UpdateAnimation(float delta)
    {
        _animTimer += delta;

        switch (State)
        {
            case PetState.Walking:
                if (_animTimer >= _animSpeed)
                {
                    _animTimer -= _animSpeed;
                    CurrentFrame = (CurrentFrame + 1) % 8;
                }
                break;

            case PetState.Content:
                // Play idle animation once, freeze on last frame
                if (CurrentFrame < 7 && _animTimer >= 0.13f)
                {
                    _animTimer -= 0.13f;
                    CurrentFrame++;
                }
                break;

            case PetState.Sleeping:
                if (!_sleepIntroDone)
                {
                    // Play through all 12 sleep frames once
                    if (CurrentFrame < 11 && _animTimer >= 0.13f)
                    {
                        _animTimer -= 0.13f;
                        CurrentFrame++;
                    }
                    else if (CurrentFrame >= 11)
                    {
                        _sleepIntroDone = true;
                        ActiveSheet = SleepLoopSheet;
                        CurrentFrame = 0;
                        _animTimer = 0;
                    }
                }
                else
                {
                    // Loop: frame 0 holds for 10s, others at 0.3s
                    var loopDelay = CurrentFrame == 0 ? 10f : 0.3f;
                    if (_animTimer >= loopDelay)
                    {
                        _animTimer -= loopDelay;
                        CurrentFrame = (CurrentFrame + 1) % 3;
                    }
                }
                break;

            case PetState.Jumping:
                if (_animTimer >= 0.13f)
                {
                    _animTimer -= 0.13f;
                    CurrentFrame = (CurrentFrame + 1) % 8;
                }
                break;

            case PetState.Idle:
                CurrentFrame = 0;
                break;
        }
    }

    private void MoveWindow(float delta)
    {
        Position += Velocity * delta;

        // Bounce off screen edges
        var displaySize = FrameSize * Scale;
        float minX = 0, minY = 0;
        float maxX = ScreenWidth - displaySize;
        float maxY = ScreenHeight - displaySize;

        if (Position.X <= minX)
        {
            Position = Position with { X = minX + 1 };
            Velocity = Velocity with { X = MathF.Abs(Velocity.X) };
            UpdateFlip();
        }
        else if (Position.X >= maxX)
        {
            Position = Position with { X = maxX - 1 };
            Velocity = Velocity with { X = -MathF.Abs(Velocity.X) };
            UpdateFlip();
        }

        if (Position.Y <= minY)
        {
            Position = Position with { Y = minY + 1 };
            Velocity = Velocity with { Y = MathF.Abs(Velocity.Y) };
        }
        else if (Position.Y >= maxY)
        {
            Position = Position with { Y = maxY - 1 };
            Velocity = Velocity with { Y = -MathF.Abs(Velocity.Y) };
        }

        Position = new Vector2(
            Math.Clamp(Position.X, minX, maxX),
            Math.Clamp(Position.Y, minY, maxY)
        );
    }

    private void UpdateThrown(float delta, Vector2 mousePos)
    {
        _throwVelocity *= ThrowFriction;

        Position += _throwVelocity * delta;

        var displaySize = FrameSize * Scale;
        float minX = 0, minY = 0;
        float maxX = ScreenWidth - displaySize;
        float maxY = ScreenHeight - displaySize;

        if (Position.X <= minX)
        {
            Position = Position with { X = minX + 1 };
            _throwVelocity = _throwVelocity with { X = MathF.Abs(_throwVelocity.X) * 0.6f };
        }
        else if (Position.X >= maxX)
        {
            Position = Position with { X = maxX - 1 };
            _throwVelocity = _throwVelocity with { X = -MathF.Abs(_throwVelocity.X) * 0.6f };
        }

        if (Position.Y <= minY)
        {
            Position = Position with { Y = minY + 1 };
            _throwVelocity = _throwVelocity with { Y = MathF.Abs(_throwVelocity.Y) * 0.6f };
        }
        else if (Position.Y >= maxY)
        {
            Position = Position with { Y = maxY - 1 };
            _throwVelocity = _throwVelocity with { Y = -MathF.Abs(_throwVelocity.Y) * 0.6f };
        }

        Position = new Vector2(
            Math.Clamp(Position.X, minX, maxX),
            Math.Clamp(Position.Y, minY, maxY)
        );

        UpdateFlip();

        if (_throwVelocity.Length() < 15f)
        {
            _throwVelocity = Vector2.Zero;
            EnterWalking(mousePos);
        }
    }

    private void UpdateDragging(Vector2 mousePos)
    {
        var now = (float)Raylib_cs.Raylib.GetTime();
        _dragPositions.Add(mousePos);
        _dragTimes.Add(now);
        while (_dragPositions.Count > 5)
        {
            _dragPositions.RemoveAt(0);
            _dragTimes.RemoveAt(0);
        }
        Position = mousePos - _dragOffset;
    }

    private void UpdateFlip()
    {
        // Mouse sprites face left by default, flip horizontally when going right.
        // Use the velocity that actually drives motion for the current state; otherwise
        // stale _throwVelocity can override a fresh Velocity and cause backwards-walking.
        var vx = State == PetState.Thrown ? _throwVelocity.X : Velocity.X;
        if (vx != 0)
            _facingRight = vx > 0;
        FlipH = _facingRight;
    }

    // --- State transitions ---

    public void EnterIdle()
    {
        State = PetState.Idle;
        _idleTimer = _rng.NextSingle() * 234f + 6f; // 6-240 seconds
        ActiveSheet = WalkSheet;
        CurrentFrame = 0;
        _animTimer = 0;
        FlipH = _facingRight;
    }

    public void EnterWalking(Vector2 mousePos)
    {
        State = PetState.Walking;
        _walkTimer = _rng.NextSingle() * 2.5f + 1.5f; // 1.5-4.0 seconds

        var center = Position + new Vector2(FrameSize * Scale / 2f);
        var toMouse = mousePos - center;

        // Mostly horizontal movement
        var dirX = _rng.NextSingle() > 0.5f ? 1f : -1f;
        var dirY = _rng.NextSingle() * 0.4f - 0.2f;

        // 30% chance to bias toward mouse
        if (toMouse.Length() > 200 && _rng.NextSingle() < 0.3f)
        {
            dirX = toMouse.X > 20 ? 1f : (toMouse.X < -20 ? -1f : dirX);
            dirY += MathF.Sign(toMouse.Y) * 0.15f;
            dirY = Math.Clamp(dirY, -0.3f, 0.3f);
        }

        var dir = Vector2.Normalize(new Vector2(dirX, dirY));
        Velocity = dir * 80f;

        ActiveSheet = WalkSheet;
        CurrentFrame = 0;
        _animTimer = 0;
        UpdateFlip();
    }

    /// <summary>
    /// Force the pet to walk in a specific horizontal direction (-1 for left, +1 for right).
    /// </summary>
    public void EnterWalkingDirection(float dirX)
    {
        State = PetState.Walking;
        _walkTimer = _rng.NextSingle() * 2.5f + 1.5f; // 1.5-4.0 seconds

        var dirY = _rng.NextSingle() * 0.4f - 0.2f;
        var dir = Vector2.Normalize(new Vector2(MathF.Sign(dirX), dirY));
        Velocity = dir * 80f;

        ActiveSheet = WalkSheet;
        CurrentFrame = 0;
        _animTimer = 0;
        UpdateFlip();
    }

    public void EnterContent()
    {
        State = PetState.Content;
        Velocity = Vector2.Zero;
        ActiveSheet = IdleSheet;
        CurrentFrame = 0;
        _animTimer = 0;
        FlipH = _facingRight;
    }

    public void EnterSleeping()
    {
        State = PetState.Sleeping;
        ActiveSheet = SleepSheet;
        CurrentFrame = 0;
        _animTimer = 0;
        _sleepIntroDone = false;
        Velocity = Vector2.Zero;
        FlipH = _facingRight;
    }

    public void EnterJumping()
    {
        State = PetState.Jumping;
        ActiveSheet = JumpSheet;
        CurrentFrame = 0;
        _animTimer = 0;
        _jumpTimer = 8 * 0.13f;
        Velocity = Vector2.Zero;
    }

    public void StartDrag(Vector2 mousePos)
    {
        // Wake from sleep
        if (State == PetState.Sleeping)
        {
            EnterIdle();
            return;
        }

        State = PetState.Dragging;
        _dragOffset = mousePos - Position;
        _dragPositions.Clear();
        _dragTimes.Clear();
    }

    public void EndDrag()
    {
        if (_dragPositions.Count >= 2)
        {
            var dt = _dragTimes[^1] - _dragTimes[0];
            if (dt > 0.01f)
            {
                _throwVelocity = (_dragPositions[^1] - _dragPositions[0]) / dt;
                if (_throwVelocity.Length() > 2000)
                    _throwVelocity = Vector2.Normalize(_throwVelocity) * 2000;
                if (_throwVelocity.Length() > 30)
                {
                    State = PetState.Thrown;
                    UpdateFlip();
                    return;
                }
            }
        }
        EnterIdle();
    }

    /// <summary>
    /// Re-applies the correct sheet for the current state after spritesheets change (e.g. color mode switch).
    /// </summary>
    public void RefreshActiveSheet()
    {
        ActiveSheet = State switch
        {
            PetState.Walking or PetState.Idle or PetState.Thrown => WalkSheet,
            PetState.Content => IdleSheet,
            PetState.Sleeping when _sleepIntroDone => SleepLoopSheet,
            PetState.Sleeping => SleepSheet,
            PetState.Jumping => JumpSheet,
            _ => WalkSheet
        };
    }

    /// <summary>
    /// Returns the bounding rectangle of the pet in screen coords.
    /// </summary>
    public (Vector2 pos, Vector2 size) GetBounds()
    {
        var size = new Vector2(FrameSize * Scale);
        return (Position, size);
    }
}
