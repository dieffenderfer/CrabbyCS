using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Faithful port of Neko (Dara T. Khani, 1991-1992) from the FUNGAMES
/// category — the Windows 3.x reimagining of Masayuki Koba's original
/// Mac/Unix Neko. Behaviors from NEKO.TXT:
///
///   • The cat follows the mouse cursor as long as it's inside the panel.
///   • If the cursor is inside the panel and not moving, Neko goes to sleep.
///   • If the cursor leaves the panel and stays put, Neko scratches at
///     the window boundary nearest the cursor, then sleeps.
///
/// Drawn from primitives — no original sprite assets are bundled, so the
/// cat is a small grey kitten shape (head, body, ears, tail) styled to
/// fit MouseHouse's pixel-ish look. Color is intentionally not configurable
/// in this port; the Settings/Color dialog from the original would belong
/// in a follow-up.
/// </summary>
public class NekoActivity : IActivity
{
    public Vector2 PanelSize => new(440, 320);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const float WalkSpeed = 80f;             // px/sec
    private const float RunSpeed = 140f;             // when far from cursor
    private const float StopRadius = 16f;            // close-enough threshold
    private const float SleepIdleSec = 2.5f;         // idle time → sleep
    private const float ScratchSec = 1.6f;           // total scratch animation

    private enum State { Walking, Sitting, Sleeping, Scratching }
    private enum Edge { None, Left, Right, Top, Bottom }

    private Vector2 _pos;
    private Vector2 _facing = new(1, 0);
    private State _state = State.Sleeping;
    private float _stateTimer;
    private float _idleTimer;
    private Vector2 _lastCursor;
    private Edge _scratchEdge = Edge.None;
    private string _status = "Move the mouse inside the window to wake Neko.";

    public void Load()
    {
        // Start Neko sitting in the middle of the panel.
        _pos = new Vector2(PanelSize.X / 2f, PanelSize.Y / 2f);
        _state = State.Sleeping;
    }

    public void Close() => IsFinished = true;

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var play = PlayRect();
        bool cursorInPanel = RetroSkin.PointInRect(local, play);
        var cursorLocal = local;

        // Idle tracking — bumped whenever the cursor moves, used by both
        // "go to sleep" and "stop scratching".
        if (Vector2.Distance(cursorLocal, _lastCursor) > 1f)
        {
            _idleTimer = 0;
            _lastCursor = cursorLocal;
        }
        else
        {
            _idleTimer += delta;
        }

        _stateTimer += delta;

        if (_state == State.Scratching)
        {
            if (_stateTimer >= ScratchSec || cursorInPanel)
            {
                _state = State.Sleeping;
                _stateTimer = 0;
            }
            _status = "Neko is scratching at the wall.";
            return;
        }

        if (cursorInPanel)
        {
            float dist = Vector2.Distance(cursorLocal, _pos);
            if (dist > StopRadius)
            {
                _state = State.Walking;
                _stateTimer = 0;
                var dir = Vector2.Normalize(cursorLocal - _pos);
                _facing = dir;
                float speed = dist > 90f ? RunSpeed : WalkSpeed;
                _pos += dir * speed * delta;
                ClampToPlay(play);
                _status = dist > 90f ? "Neko is chasing the mouse." : "Neko is creeping up on the mouse.";
            }
            else
            {
                // Cursor close enough — settle, then doze.
                if (_idleTimer > SleepIdleSec)
                {
                    _state = State.Sleeping;
                    _status = "Neko is sleeping... shh.";
                }
                else
                {
                    _state = State.Sitting;
                    _status = "Neko caught up — watching the cursor.";
                }
            }
        }
        else
        {
            // Cursor outside the panel — march to the nearest edge and
            // scratch, the way the doc describes.
            var target = NearestEdgePoint(play, cursorLocal);
            float d = Vector2.Distance(target, _pos);
            if (d > 6f)
            {
                _state = State.Walking;
                var dir = Vector2.Normalize(target - _pos);
                _facing = dir;
                _pos += dir * WalkSpeed * delta;
                ClampToPlay(play);
                _status = "Neko is heading to the wall.";
            }
            else
            {
                _state = State.Scratching;
                _stateTimer = 0;
                _scratchEdge = NearestEdge(play, cursorLocal);
            }
        }
    }

    private Rectangle PlayRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 4;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(FrameInset + 6, top,
            PanelSize.X - 2 * FrameInset - 12, bottom - top);
    }

    private void ClampToPlay(Rectangle play)
    {
        _pos.X = Math.Clamp(_pos.X, play.X + 16, play.X + play.Width - 16);
        _pos.Y = Math.Clamp(_pos.Y, play.Y + 16, play.Y + play.Height - 16);
    }

    private static Edge NearestEdge(Rectangle play, Vector2 outsidePoint)
    {
        // Pick whichever edge the cursor sits beyond (or nearest to). Ties
        // go to vertical edges so left/right scratching reads better in
        // the typical landscape window orientation.
        float dLeft = outsidePoint.X - play.X;
        float dRight = (play.X + play.Width) - outsidePoint.X;
        float dTop = outsidePoint.Y - play.Y;
        float dBottom = (play.Y + play.Height) - outsidePoint.Y;
        float min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
        if (min == dLeft) return Edge.Left;
        if (min == dRight) return Edge.Right;
        if (min == dTop) return Edge.Top;
        return Edge.Bottom;
    }

    private static Vector2 NearestEdgePoint(Rectangle play, Vector2 outsidePoint)
    {
        return NearestEdge(play, outsidePoint) switch
        {
            Edge.Left => new Vector2(play.X + 16, Math.Clamp(outsidePoint.Y, play.Y + 16, play.Y + play.Height - 16)),
            Edge.Right => new Vector2(play.X + play.Width - 16, Math.Clamp(outsidePoint.Y, play.Y + 16, play.Y + play.Height - 16)),
            Edge.Top => new Vector2(Math.Clamp(outsidePoint.X, play.X + 16, play.X + play.Width - 16), play.Y + 16),
            _ => new Vector2(Math.Clamp(outsidePoint.X, play.X + 16, play.X + play.Width - 16), play.Y + play.Height - 16),
        };
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Neko", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Move mouse to play" }, -1);

        var play = PlayRect();
        var playAbs = new Rectangle(panelOffset.X + play.X, panelOffset.Y + play.Y,
            play.Width, play.Height);
        RetroSkin.DrawSunken(playAbs, RetroSkin.SunkenBg);

        DrawNeko(panelOffset);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status, _state.ToString());
    }

    private void DrawNeko(Vector2 panelOffset)
    {
        float cx = panelOffset.X + _pos.X;
        float cy = panelOffset.Y + _pos.Y;
        var body = new Color(150, 145, 155, 255);
        var inner = new Color(232, 224, 228, 255);
        var dark = new Color(60, 50, 60, 255);

        // Walking bob — small vertical wobble while moving so it doesn't
        // glide flat across the floor.
        float bob = _state == State.Walking
            ? MathF.Sin((float)Raylib.GetTime() * 12f) * 1.6f
            : 0f;
        cy += bob;

        // Body — orient slightly along facing direction so chasing reads.
        float bodyLen = 18, bodyHt = 11;
        Raylib.DrawEllipse((int)cx, (int)cy, bodyLen, bodyHt, body);

        // Head: a slightly smaller circle offset in the facing direction.
        float headOff = 10f * (_facing.X >= 0 ? 1 : -1);
        float headX = cx + headOff;
        float headY = cy - 4;
        Raylib.DrawCircle((int)headX, (int)headY, 8f, body);

        // Ears — two small triangles on top of the head.
        var earColor = body;
        var earL = new Vector2(headX - 5, headY - 6);
        var earLT = new Vector2(headX - 6, headY - 12);
        var earLR = new Vector2(headX - 1, headY - 6);
        Raylib.DrawTriangle(earLT, earL, earLR, earColor);
        var earR = new Vector2(headX + 5, headY - 6);
        var earRT = new Vector2(headX + 6, headY - 12);
        var earRR = new Vector2(headX + 1, headY - 6);
        Raylib.DrawTriangle(earR, earRT, earRR, earColor);

        // Inner ear blush.
        Raylib.DrawTriangle(
            new Vector2(headX - 4, headY - 7),
            new Vector2(headX - 5, headY - 11),
            new Vector2(headX - 2, headY - 7),
            inner);
        Raylib.DrawTriangle(
            new Vector2(headX + 2, headY - 7),
            new Vector2(headX + 5, headY - 11),
            new Vector2(headX + 4, headY - 7),
            inner);

        // Eyes — closed (line) when sleeping, dots otherwise.
        bool eyesClosed = _state == State.Sleeping;
        if (eyesClosed)
        {
            Raylib.DrawLine((int)headX - 3, (int)headY - 1, (int)headX - 1, (int)headY - 1, dark);
            Raylib.DrawLine((int)headX + 1, (int)headY - 1, (int)headX + 3, (int)headY - 1, dark);
        }
        else
        {
            Raylib.DrawCircle((int)headX - 2, (int)headY - 1, 1.4f, dark);
            Raylib.DrawCircle((int)headX + 2, (int)headY - 1, 1.4f, dark);
        }
        // Nose
        Raylib.DrawCircle((int)headX + (headOff > 0 ? 1 : -1), (int)headY + 2, 1f, new Color(200, 100, 120, 255));

        // Tail — opposite the head, wavy when walking, curled when sitting.
        float tailBaseX = cx - headOff * 0.6f;
        float tailBaseY = cy - 2;
        float tailWave = _state switch
        {
            State.Walking => MathF.Sin((float)Raylib.GetTime() * 14f) * 4f,
            State.Scratching => MathF.Sin((float)Raylib.GetTime() * 22f) * 5f,
            _ => 0f,
        };
        var tailTip = new Vector2(tailBaseX - headOff * 0.6f, tailBaseY - 6 + tailWave);
        Raylib.DrawLineEx(new Vector2(tailBaseX, tailBaseY), tailTip, 3f, body);

        // "Z"s when sleeping, like the original Neko's idle.
        if (_state == State.Sleeping)
        {
            FontManager.DrawText("z", (int)(cx + 14), (int)(cy - 18), 14, dark);
            FontManager.DrawText("Z", (int)(cx + 22), (int)(cy - 26),
                18 + (int)(MathF.Sin((float)Raylib.GetTime() * 2f) * 1.5f), dark);
        }

        // Scratch effect — small claw marks on the appropriate edge.
        if (_state == State.Scratching)
        {
            var play = PlayRect();
            var origin = panelOffset;
            int flash = ((int)(_stateTimer * 18) % 2);
            var clawCol = new Color((byte)160, (byte)80, (byte)80,
                (byte)(flash == 0 ? 220 : 160));
            float x = 0, y = 0;
            switch (_scratchEdge)
            {
                case Edge.Left: x = origin.X + play.X + 4; y = cy; break;
                case Edge.Right: x = origin.X + play.X + play.Width - 4; y = cy; break;
                case Edge.Top: x = cx; y = origin.Y + play.Y + 4; break;
                case Edge.Bottom: x = cx; y = origin.Y + play.Y + play.Height - 4; break;
            }
            for (int i = -1; i <= 1; i++)
            {
                Raylib.DrawLineEx(
                    new Vector2(x + i * 4, y - 5),
                    new Vector2(x + i * 4, y + 5),
                    1.5f, clawCol);
            }
        }
    }
}
