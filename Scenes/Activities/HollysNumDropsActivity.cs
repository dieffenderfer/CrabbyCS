using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Faithful port of Holly's NumDrops (Wayne Zimmerle / Zimco, 1991) from
/// the "201 Learning Games" MATH category. Recommended ages 3-6 in the
/// original; ten levels of falling-number mechanics:
///
///   Lv 1  (SAME AS, =)   press the same number that's falling
///   Lv 2  (NEXT ONE, +)  press the next number after the one falling
///   Lv 3  (BEFORE, -)    press the number before the one falling
///   Lv 4-6              same as 1-3 but the bottom hint row disappears
///                       and numbers fall faster
///   Lv 7-9              same as 4-6, plus only one keypress is allowed
///                       per fall (no trial-and-error)
///   Lv 10               random mix of =, +, -; one keypress per fall;
///                       fastest speed
///
/// The "ladder race" framing is also from the original: when the player
/// misses, Mugsy climbs *his* ladder. If Mugsy reaches the top before
/// the player finishes, Mugsy and Bugsy rain numbers down on the picnic
/// and the game ends.
///
/// Visuals are original (the source EXE renders in CGA/EGA which we
/// can't read), so the project's pet motif is reused — Mugsy is a small
/// grey rat-blob to match the rest of MouseHouse.
/// </summary>
public class HollysNumDropsActivity : IActivity
{
    public Vector2 PanelSize => new(500, 480);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int LadderRungs = 8;
    private const float BaseFallSpeed = 60f;          // px/sec at level 1
    private const float FallSpeedPerLevel = 18f;      // additive scaling

    private enum Rule { SameAs, NextOne, Before }
    private enum Phase { Playing, LevelComplete, GameOver, Win }

    private int _level = 1;
    private Phase _phase = Phase.Playing;
    private int _playerRungs;
    private int _mugsyRungs;
    private int _falling;          // 0..9
    private Rule _activeRule;      // current rule for level 10's random mix
    private float _fallY;          // pixel-Y position of the falling number
    private bool _triedThisFall;   // for levels 7-10's one-try rule
    private string _status = "Press a number to climb your ladder!";
    private float _phaseTimer;
    private readonly Random _rng = new();

    public void Load() => StartLevel(1);
    public void Close() => IsFinished = true;

    private void StartLevel(int n)
    {
        _level = n;
        _phase = Phase.Playing;
        _playerRungs = 0;
        _mugsyRungs = 0;
        _status = LevelBanner();
        SpawnFalling();
    }

    private string LevelBanner()
    {
        string rule = RuleForLevel() switch
        {
            Rule.SameAs => "press the SAME number",
            Rule.NextOne => "press the NEXT number",
            Rule.Before => "press the number BEFORE",
        };
        if (_level == 10) rule = "= / + / - mix — watch the symbol!";
        bool oneTry = _level >= 7;
        return $"Level {_level} · {rule}" + (oneTry ? "  (one try!)" : "");
    }

    private Rule RuleForLevel()
    {
        if (_level == 10) return _activeRule;
        int idx = (_level - 1) % 3;
        return idx switch { 0 => Rule.SameAs, 1 => Rule.NextOne, _ => Rule.Before };
    }

    private bool ShowsHintRow() => _level <= 3;
    private bool OneTryPerFall() => _level >= 7;

    private void SpawnFalling()
    {
        // Avoid 9 for NextOne (would wrap, confusing for ages 3-6) and 0
        // for Before, matching the educational intent of the original.
        var rule = _level == 10
            ? new[] { Rule.SameAs, Rule.NextOne, Rule.Before }[_rng.Next(3)]
            : RuleForLevel();
        _activeRule = rule;
        int min = rule == Rule.Before ? 1 : 0;
        int max = rule == Rule.NextOne ? 8 : 9;
        _falling = _rng.Next(min, max + 1);
        _fallY = FallTopY();
        _triedThisFall = false;
    }

    private float FallTopY() => FrameInset + RetroWidgets.TitleBarHeight
        + RetroWidgets.MenuBarHeight + 20;

    private float FallBottomY() => PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight
        - (ShowsHintRow() ? 38 : 12);

    private float CurrentSpeed() => BaseFallSpeed + (_level - 1) * FallSpeedPerLevel;

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        switch (RetroWidgets.MenuBarHitTest(menuBar,
                    new[] { "Restart", "Skip Level", "About" }, local, leftPressed))
        {
            case 0: StartLevel(1); return;
            case 1: if (_phase == Phase.Playing) AdvanceLevel(); return;
            case 2:
                _status = "Holly's NumDrops by Wayne Zimmerle / Zimco, 1991";
                return;
        }

        if (_phase == Phase.LevelComplete || _phase == Phase.GameOver || _phase == Phase.Win)
        {
            _phaseTimer += delta;
            // ESC / Enter / Space dismisses the banner.
            if (Raylib.IsKeyPressed(KeyboardKey.Enter)
                || Raylib.IsKeyPressed(KeyboardKey.Space))
            {
                if (_phase == Phase.LevelComplete) StartLevel(_level + 1);
                else StartLevel(1);
                _phaseTimer = 0;
            }
            return;
        }

        // Number key input — accept main row and numpad.
        int ch = Raylib.GetCharPressed();
        while (ch > 0)
        {
            if (ch >= '0' && ch <= '9') Submit(ch - '0');
            ch = Raylib.GetCharPressed();
        }

        // Fall the number.
        _fallY += CurrentSpeed() * delta;
        if (_fallY >= FallBottomY())
        {
            // Reached the ground without (or after) a correct press: Mugsy
            // climbs. In one-try levels we already credited the miss when
            // the wrong key was pressed.
            if (!_triedThisFall) MugsyMissed();
            SpawnFalling();
        }
    }

    private void Submit(int key)
    {
        if (_phase != Phase.Playing) return;
        if (_triedThisFall && OneTryPerFall()) return;
        _triedThisFall = true;

        int expected = _activeRule switch
        {
            Rule.SameAs => _falling,
            Rule.NextOne => _falling + 1,
            Rule.Before => _falling - 1,
        };
        if (key == expected)
        {
            _playerRungs++;
            _status = "Up you go!";
            if (_playerRungs >= LadderRungs) { CompleteLevel(); return; }
            SpawnFalling();
        }
        else
        {
            // On levels 1-6 the player may try again on the same fall. Only
            // count the missed-the-ground case (or one-try level miss) as
            // a Mugsy advance.
            if (OneTryPerFall())
            {
                MugsyMissed();
                _status = $"Not quite — was {expected}.";
                SpawnFalling();
            }
            else
            {
                _status = "Try again on this one!";
            }
        }
    }

    private void MugsyMissed()
    {
        _mugsyRungs++;
        if (_mugsyRungs >= LadderRungs) { _phase = Phase.GameOver; _phaseTimer = 0;
            _status = "Mugsy & Bugsy rained numbers on the picnic! Enter to try again.";
        }
    }

    private void CompleteLevel()
    {
        if (_level >= 10) { _phase = Phase.Win; _phaseTimer = 0;
            _status = "You beat Holly's NumDrops! Enter to play again.";
            return;
        }
        _phase = Phase.LevelComplete;
        _phaseTimer = 0;
        _status = $"Level {_level} cleared! Enter for level {_level + 1}.";
    }

    private void AdvanceLevel()
    {
        if (_level >= 10) StartLevel(1);
        else StartLevel(_level + 1);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Holly's NumDrops", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Restart", "Skip Level", "About" }, -1);

        DrawPicnic(panelOffset);
        DrawLadders(panelOffset);
        DrawFallingNumber(panelOffset);
        if (ShowsHintRow()) DrawHintRow(panelOffset);
        if (_phase != Phase.Playing) DrawPhaseBanner(panelOffset);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status,
            $"L{_level}  You {_playerRungs}/{LadderRungs}  Mugsy {_mugsyRungs}/{LadderRungs}");
    }

    private void DrawPicnic(Vector2 panelOffset)
    {
        // Background grass band along the bottom of the play area.
        float bottom = panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 4;
        float grassTop = bottom - 30;
        Raylib.DrawRectangle((int)(panelOffset.X + FrameInset),
            (int)grassTop, (int)(PanelSize.X - 2 * FrameInset), 30,
            new Color(112, 174, 96, 255));

        // Small red+white checkered blanket in the middle to match the
        // original's "picnic" framing.
        int blanketW = 120, blanketH = 24;
        int blanketX = (int)(panelOffset.X + PanelSize.X / 2 - blanketW / 2);
        int blanketY = (int)(grassTop + 3);
        int cells = 6;
        int cellW = blanketW / cells;
        for (int c = 0; c < cells; c++)
        {
            for (int r = 0; r < 2; r++)
            {
                bool red = (c + r) % 2 == 0;
                Raylib.DrawRectangle(blanketX + c * cellW, blanketY + r * (blanketH / 2),
                    cellW, blanketH / 2, red
                        ? new Color(200, 60, 64, 255)
                        : new Color(248, 240, 232, 255));
            }
        }
    }

    private void DrawLadders(Vector2 panelOffset)
    {
        float playAreaTop = FallTopY();
        float playAreaBottom = FallBottomY() + 8;
        // Player ladder on the left, Mugsy ladder on the right.
        DrawLadder(panelOffset, 36, playAreaTop, playAreaBottom, _playerRungs,
            playerColor: new Color(252, 200, 80, 255), isPlayer: true);
        DrawLadder(panelOffset, PanelSize.X - 60, playAreaTop, playAreaBottom, _mugsyRungs,
            playerColor: new Color(120, 120, 132, 255), isPlayer: false);
    }

    private void DrawLadder(Vector2 panelOffset, float xLocal, float top, float bottom,
                            int rungsClimbed, Color playerColor, bool isPlayer)
    {
        float x = panelOffset.X + xLocal;
        float h = bottom - top;
        float rungGap = h / LadderRungs;
        // Rails
        Raylib.DrawRectangle((int)x, (int)top, 2, (int)h, RetroSkin.DarkShadow);
        Raylib.DrawRectangle((int)(x + 20), (int)top, 2, (int)h, RetroSkin.DarkShadow);
        // Rungs
        for (int r = 1; r <= LadderRungs; r++)
        {
            float ry = top + r * rungGap;
            Raylib.DrawRectangle((int)x, (int)ry, 22, 2, RetroSkin.Shadow);
        }
        // Climber sits on the rung corresponding to rungsClimbed (0 = bottom).
        float climberY = bottom - rungsClimbed * rungGap - 14;
        float climberX = x + 11;
        if (isPlayer)
        {
            // Smiley face.
            Raylib.DrawCircle((int)climberX, (int)climberY, 10, playerColor);
            Raylib.DrawCircle((int)climberX - 3, (int)climberY - 2, 1.5f, RetroSkin.BodyText);
            Raylib.DrawCircle((int)climberX + 3, (int)climberY - 2, 1.5f, RetroSkin.BodyText);
            Raylib.DrawLineEx(new Vector2(climberX - 3, climberY + 3),
                new Vector2(climberX + 3, climberY + 3), 1.5f, RetroSkin.BodyText);
        }
        else
        {
            // Mugsy: small grey blob with two ears + a tail line — matches
            // the project's mouse motif.
            Raylib.DrawEllipse((int)climberX, (int)climberY, 10, 8, playerColor);
            Raylib.DrawCircle((int)climberX - 5, (int)climberY - 6, 3, playerColor);
            Raylib.DrawCircle((int)climberX + 5, (int)climberY - 6, 3, playerColor);
            Raylib.DrawCircle((int)climberX - 3, (int)climberY - 2, 1.5f, RetroSkin.DarkShadow);
            Raylib.DrawCircle((int)climberX + 3, (int)climberY - 2, 1.5f, RetroSkin.DarkShadow);
            Raylib.DrawCircle((int)climberX, (int)climberY + 1, 1.2f, new Color(80, 50, 50, 255));
        }

        // Label under the ladder.
        string label = isPlayer ? "You" : "Mugsy";
        int lw = FontManager.MeasureText(label, 12);
        FontManager.DrawText(label, (int)(x + 11 - lw / 2f), (int)(bottom + 6),
            12, RetroSkin.BodyText);
    }

    private void DrawFallingNumber(Vector2 panelOffset)
    {
        if (_phase != Phase.Playing) return;
        // Show the operator symbol next to the number on level 10 since the
        // rule varies per fall.
        string sym = _activeRule switch
        {
            Rule.SameAs => "=", Rule.NextOne => "+", _ => "-",
        };
        string text = _level == 10 ? sym + " " + _falling : _falling.ToString();
        int textW = FontManager.MeasureText(text, 40);
        int x = (int)(panelOffset.X + PanelSize.X / 2 - textW / 2);
        FontManager.DrawText(text, x, (int)(panelOffset.Y + _fallY), 40, RetroSkin.TitleActive);
    }

    private void DrawHintRow(Vector2 panelOffset)
    {
        // Bottom row of digits 0-9 acting as a reference for the player.
        // The original blinks these; we just dim them so they read as a
        // reference strip without competing for attention.
        float y = panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 36;
        int gap = 38;
        int totalW = 10 * gap;
        int startX = (int)(panelOffset.X + PanelSize.X / 2 - totalW / 2);
        for (int d = 0; d < 10; d++)
        {
            FontManager.DrawText(d.ToString(), startX + d * gap, (int)y, 14,
                RetroSkin.DisabledText);
        }
    }

    private void DrawPhaseBanner(Vector2 panelOffset)
    {
        var bannerR = new Rectangle(
            panelOffset.X + PanelSize.X / 2 - 200,
            panelOffset.Y + PanelSize.Y / 2 - 50,
            400, 100);
        RetroSkin.DrawRaised(bannerR);
        string msg = _phase switch
        {
            Phase.LevelComplete => $"Level {_level} cleared!",
            Phase.GameOver => "Game over — Mugsy won this time.",
            _ => "You beat Holly's NumDrops!",
        };
        int mw = FontManager.MeasureText(msg, 22);
        FontManager.DrawText(msg,
            (int)(bannerR.X + (bannerR.Width - mw) / 2),
            (int)(bannerR.Y + 28),
            22, RetroSkin.BodyText);
        const string hint = "Press Enter to continue";
        int hw = FontManager.MeasureText(hint, 14);
        FontManager.DrawText(hint,
            (int)(bannerR.X + (bannerR.Width - hw) / 2),
            (int)(bannerR.Y + 64),
            14, RetroSkin.DisabledText);
    }
}
