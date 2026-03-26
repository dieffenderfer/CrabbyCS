using System.Numerics;
using MouseHouse.Core;

namespace MouseHouse.Scenes.DesktopPet.Events;

// --- Linear fly-across events ---

public class SeagullEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _startY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "seagull";
        AnimSpeed = 0.15f;
        _speed = Rng.NextSingle() * 100f + 120f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _startY = Rng.NextSingle() * (screenH * 0.4f) + 50;
        Position = new Vector2(_goingRight ? -130 : screenW + 30, _startY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = new Vector2(
            Position.X + _speed * dir * delta,
            _startY + MathF.Sin(Lifetime * 1.5f) * 15f
        );
        if (Position.X > ScreenW + 150 || Position.X < -150) Finished = true;
    }
}

public class PaperAirplaneEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _startY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "paper_airplane";
        AnimSpeed = 0.12f;
        _speed = Rng.NextSingle() * 80f + 100f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _startY = Rng.NextSingle() * (screenH * 0.5f) + 30;
        Position = new Vector2(_goingRight ? -80 : screenW + 20, _startY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = new Vector2(
            Position.X + _speed * dir * delta,
            _startY + MathF.Sin(Lifetime * 2f) * 20f
        );
        if (Position.X > ScreenW + 100 || Position.X < -100) Finished = true;
    }
}

public class DolphinEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _baseY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "dolphin";
        AnimSpeed = 0.12f;
        _speed = Rng.NextSingle() * 60f + 140f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _baseY = screenH * 0.7f + Rng.NextSingle() * (screenH * 0.2f);
        Position = new Vector2(_goingRight ? -120 : screenW + 20, _baseY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = new Vector2(
            Position.X + _speed * dir * delta,
            _baseY + MathF.Sin(Lifetime * 2.5f) * 25f
        );
        if (Position.X > ScreenW + 150 || Position.X < -150) Finished = true;
    }
}

public class BatEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _startY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "bat";
        AnimSpeed = 0.1f;
        _speed = Rng.NextSingle() * 80f + 150f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _startY = Rng.NextSingle() * (screenH * 0.3f) + 30;
        Position = new Vector2(_goingRight ? -80 : screenW + 20, _startY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = new Vector2(
            Position.X + _speed * dir * delta,
            _startY + MathF.Sin(Lifetime * 3f) * 30f
        );
        if (Position.X > ScreenW + 100 || Position.X < -100) Finished = true;
    }
}

public class PelicanEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _startY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "pelican";
        AnimSpeed = 0.18f;
        _speed = Rng.NextSingle() * 60f + 80f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _startY = Rng.NextSingle() * (screenH * 0.3f) + 40;
        Position = new Vector2(_goingRight ? -120 : screenW + 20, _startY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = new Vector2(
            Position.X + _speed * dir * delta,
            _startY + MathF.Sin(Lifetime * 1.2f) * 10f
        );
        if (Position.X > ScreenW + 150 || Position.X < -150) Finished = true;
    }
}

public class HermitCrabEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _baseY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "hermit_crab";
        AnimSpeed = 0.12f;
        _speed = Rng.NextSingle() * 40f + 50f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _baseY = screenH - Rng.NextSingle() * 60f - 60f;
        Position = new Vector2(_goingRight ? -70 : screenW + 20, _baseY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = Position with { X = Position.X + _speed * dir * delta };
        if (Position.X > ScreenW + 100 || Position.X < -100) Finished = true;
    }
}

public class DustDevilEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _baseY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "dust_devil";
        AnimSpeed = 0.1f;
        _speed = Rng.NextSingle() * 60f + 80f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _baseY = screenH * 0.6f + Rng.NextSingle() * (screenH * 0.25f);
        Position = new Vector2(_goingRight ? -80 : screenW + 20, _baseY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = new Vector2(
            Position.X + _speed * dir * delta,
            _baseY + MathF.Sin(Lifetime * 2f) * 8f
        );
        if (Position.X > ScreenW + 100 || Position.X < -100) Finished = true;
    }
}

public class FrogEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _baseY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "frog";
        AnimSpeed = 0.15f;
        _speed = Rng.NextSingle() * 40f + 60f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _baseY = screenH - Rng.NextSingle() * 80f - 50f;
        Position = new Vector2(_goingRight ? -60 : screenW + 20, _baseY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        // Frog hops: periodic vertical bounce
        float hop = MathF.Abs(MathF.Sin(Lifetime * 4f)) * 20f;
        Position = new Vector2(
            Position.X + _speed * dir * delta,
            _baseY - hop
        );
        if (Position.X > ScreenW + 100 || Position.X < -100) Finished = true;
    }
}

public class DragonFlyEvent : EventBase
{
    private float _speed;
    private bool _goingRight;
    private float _startY;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "dragonfly";
        AnimSpeed = 0.08f;
        _speed = Rng.NextSingle() * 100f + 160f;
        _goingRight = Rng.NextSingle() > 0.5f;
        _startY = Rng.NextSingle() * (screenH * 0.4f) + 40;
        Position = new Vector2(_goingRight ? -100 : screenW + 20, _startY);
        FlipH = !_goingRight;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = new Vector2(
            Position.X + _speed * dir * delta,
            _startY + MathF.Sin(Lifetime * 4f) * 12f
        );
        if (Position.X > ScreenW + 120 || Position.X < -120) Finished = true;
    }
}

// --- Vertical descent events ---

public class FallingLeafEvent : EventBase
{
    private float _fallSpeed;
    private float _swaySpeed;
    private float _swayAmount;
    private float _startX;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "falling_leaf";
        AnimSpeed = 0.2f;
        _fallSpeed = Rng.NextSingle() * 40f + 60f;
        _swaySpeed = Rng.NextSingle() * 1.5f + 1.5f;
        _swayAmount = Rng.NextSingle() * 20f + 20f;
        _startX = Rng.NextSingle() * (screenW - 100) + 50;
        Position = new Vector2(_startX, -20);
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        Position = new Vector2(
            _startX + MathF.Sin(Lifetime * _swaySpeed) * _swayAmount,
            Position.Y + _fallSpeed * delta
        );
        if (Position.Y > ScreenH + 50) Finished = true;
    }
}

public class BalloonEvent : EventBase
{
    private float _riseSpeed;
    private float _swaySpeed;
    private float _swayAmount;
    private float _startX;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "balloon";
        AnimSpeed = 0.25f;
        _riseSpeed = Rng.NextSingle() * 30f + 40f;
        _swaySpeed = Rng.NextSingle() * 1f + 1f;
        _swayAmount = Rng.NextSingle() * 15f + 10f;
        _startX = Rng.NextSingle() * (screenW - 100) + 50;
        Position = new Vector2(_startX, screenH + 20);
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        Position = new Vector2(
            _startX + MathF.Sin(Lifetime * _swaySpeed) * _swayAmount,
            Position.Y - _riseSpeed * delta
        );
        if (Position.Y < -100) Finished = true;
    }
}

public class HotAirBalloonEvent : EventBase
{
    private float _riseSpeed;
    private float _driftSpeed;
    private bool _goingRight;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "hot_air_balloon";
        AnimSpeed = 0.3f;
        _riseSpeed = Rng.NextSingle() * 15f + 20f;
        _driftSpeed = Rng.NextSingle() * 30f + 20f;
        _goingRight = Rng.NextSingle() > 0.5f;
        float startX = Rng.NextSingle() * (screenW * 0.6f) + screenW * 0.2f;
        Position = new Vector2(startX, screenH + 30);
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        float dir = _goingRight ? 1 : -1;
        Position = new Vector2(
            Position.X + _driftSpeed * dir * delta,
            Position.Y - _riseSpeed * delta
        );
        if (Position.Y < -150) Finished = true;
    }
}

// --- Bounded random walk events ---

public class ButterflyEvent : EventBase
{
    private Vector2 _velocity;
    private float _changeTimer;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "butterfly";
        AnimSpeed = 0.15f;
        Position = new Vector2(
            Rng.NextSingle() * (screenW - 200) + 100,
            Rng.NextSingle() * (screenH - 200) + 100
        );
        _changeTimer = Rng.NextSingle() * 1.2f + 0.3f;
        RandomizeVelocity();
    }

    private void RandomizeVelocity()
    {
        _velocity = new Vector2(
            Rng.NextSingle() * 120f - 60f,
            Rng.NextSingle() * 120f - 60f
        );
        FlipH = _velocity.X < 0;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        _changeTimer -= delta;
        if (_changeTimer <= 0)
        {
            _changeTimer = Rng.NextSingle() * 1.2f + 0.3f;
            RandomizeVelocity();
        }

        Position += _velocity * delta;

        // Bounce off edges
        if (Position.X < 20) { Position = Position with { X = 20 }; _velocity = _velocity with { X = MathF.Abs(_velocity.X) }; }
        if (Position.X > ScreenW - 80) { Position = Position with { X = ScreenW - 80 }; _velocity = _velocity with { X = -MathF.Abs(_velocity.X) }; }
        if (Position.Y < 50) { Position = Position with { Y = 50 }; _velocity = _velocity with { Y = MathF.Abs(_velocity.Y) }; }
        if (Position.Y > ScreenH - 80) { Position = Position with { Y = ScreenH - 80 }; _velocity = _velocity with { Y = -MathF.Abs(_velocity.Y) }; }

        if (Lifetime > Rng.NextSingle() * 15f + 10f) Finished = true;
    }
}

public class FireflyEvent : EventBase
{
    private Vector2 _velocity;
    private float _changeTimer;
    private float _maxLifetime;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "firefly";
        AnimSpeed = 0.2f;
        Position = new Vector2(
            Rng.NextSingle() * (screenW - 200) + 100,
            Rng.NextSingle() * (screenH - 200) + 100
        );
        _changeTimer = Rng.NextSingle() * 1.5f + 0.5f;
        _maxLifetime = Rng.NextSingle() * 8f + 12f;
        RandomizeVelocity();
    }

    private void RandomizeVelocity()
    {
        _velocity = new Vector2(
            Rng.NextSingle() * 100f - 50f,
            Rng.NextSingle() * 100f - 50f
        );
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        _changeTimer -= delta;
        if (_changeTimer <= 0)
        {
            _changeTimer = Rng.NextSingle() * 1.5f + 0.5f;
            RandomizeVelocity();
        }

        Position += _velocity * delta;

        if (Position.X < 20) { Position = Position with { X = 20 }; _velocity = _velocity with { X = MathF.Abs(_velocity.X) }; }
        if (Position.X > ScreenW - 60) { Position = Position with { X = ScreenW - 60 }; _velocity = _velocity with { X = -MathF.Abs(_velocity.X) }; }
        if (Position.Y < 50) { Position = Position with { Y = 50 }; _velocity = _velocity with { Y = MathF.Abs(_velocity.Y) }; }
        if (Position.Y > ScreenH - 60) { Position = Position with { Y = ScreenH - 60 }; _velocity = _velocity with { Y = -MathF.Abs(_velocity.Y) }; }

        if (Lifetime > _maxLifetime) Finished = true;
    }
}

public class LadybugEvent : EventBase
{
    private Vector2 _velocity;
    private float _changeTimer;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "ladybug";
        AnimSpeed = 0.15f;
        Position = new Vector2(
            Rng.NextSingle() * (screenW - 200) + 100,
            Rng.NextSingle() * (screenH - 200) + 100
        );
        _changeTimer = Rng.NextSingle() * 2f + 1f;
        RandomizeVelocity();
    }

    private void RandomizeVelocity()
    {
        _velocity = new Vector2(
            Rng.NextSingle() * 80f - 40f,
            Rng.NextSingle() * 80f - 40f
        );
        FlipH = _velocity.X < 0;
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        _changeTimer -= delta;
        if (_changeTimer <= 0)
        {
            _changeTimer = Rng.NextSingle() * 2f + 1f;
            RandomizeVelocity();
        }

        Position += _velocity * delta;
        if (Position.X < 20) { Position = Position with { X = 20 }; _velocity = _velocity with { X = MathF.Abs(_velocity.X) }; }
        if (Position.X > ScreenW - 60) { Position = Position with { X = ScreenW - 60 }; _velocity = _velocity with { X = -MathF.Abs(_velocity.X) }; }
        if (Position.Y < 50) { Position = Position with { Y = 50 }; _velocity = _velocity with { Y = MathF.Abs(_velocity.Y) }; }
        if (Position.Y > ScreenH - 60) { Position = Position with { Y = ScreenH - 60 }; _velocity = _velocity with { Y = -MathF.Abs(_velocity.Y) }; }

        if (Lifetime > Rng.NextSingle() * 10f + 12f) Finished = true;
    }
}

// --- Fast events ---

public class ShootingStarEvent : EventBase
{
    private float _speed;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "shooting_star";
        AnimSpeed = 0.1f;
        _speed = Rng.NextSingle() * 600f + 600f;
        Position = new Vector2(screenW + 20, Rng.NextSingle() * 150f + 20);
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        Position = new Vector2(
            Position.X - _speed * delta,
            Position.Y + _speed * 0.15f * delta
        );
        if (Position.X < -220 || Lifetime > 3f) Finished = true;
    }
}

public class CometEvent : EventBase
{
    private float _speed;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "comet";
        AnimSpeed = 0.08f;
        _speed = Rng.NextSingle() * 400f + 400f;
        Position = new Vector2(screenW + 20, Rng.NextSingle() * 200f + 20);
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        Position = new Vector2(
            Position.X - _speed * delta,
            Position.Y + _speed * 0.1f * delta
        );
        if (Position.X < -200 || Lifetime > 4f) Finished = true;
    }
}

// --- Stationary / special events ---

public class JellyfishEvent : EventBase
{
    private float _startX;
    private float _maxLifetime;

    public override void Init(int screenW, int screenH)
    {
        base.Init(screenW, screenH);
        Name = "jellyfish";
        AnimSpeed = 0.25f;
        _startX = Rng.NextSingle() * (screenW - 100) + 50;
        _maxLifetime = Rng.NextSingle() * 10f + 10f;
        Position = new Vector2(_startX, screenH + 20);
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        Position = new Vector2(
            _startX + MathF.Sin(Lifetime * 1.5f) * 15f,
            Position.Y - 30f * delta
        );
        if (Position.Y < -100 || Lifetime > _maxLifetime) Finished = true;
    }
}
