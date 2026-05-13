using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Clipboard history: polls the OS clipboard once per second, deduplicates
/// against the most recent entry, and keeps the last <see cref="MaxEntries"/>
/// strings. Click a row to copy that entry back to the clipboard (replacing
/// whatever is there). Entries persist to clipboard.json so a restart doesn't
/// lose the recent buffer.
/// </summary>
public class ClipboardManagerActivity : IActivity
{
    public Vector2 PanelSize => new(460, 420);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int RowH = 22;
    private const int Padding = 8;
    private const int MaxEntries = 50;
    private const int MaxPreviewChars = 80;
    private const float PollIntervalSec = 1.0f;
    private const string SaveFileName = "clipboard.json";

    private readonly List<string> _history = new();
    private float _pollTimer;
    private string _lastClipboard = "";
    private string _status = "";
    private float _scrollY;
    private int _hoverRow = -1;
    private float _toastTimer;

    public void Load()
    {
        try
        {
            var path = Path.Combine(SaveManager.SaveDirectory, SaveFileName);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                if (arr != null) _history.AddRange(arr);
            }
        }
        catch { /* best-effort load — bad json just yields an empty history */ }

        try { _lastClipboard = Raylib.GetClipboardText_() ?? ""; } catch { _lastClipboard = ""; }
        _status = _history.Count == 0
            ? "Copy something to start a history."
            : $"{_history.Count} entries.";
    }

    public void Close() => SaveHistory();

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            var path = Path.Combine(SaveManager.SaveDirectory, SaveFileName);
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(_history, opts));
        }
        catch { /* best-effort */ }
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        _toastTimer = Math.Max(0, _toastTimer - delta);

        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "Clear All" }, local, leftPressed))
        {
            case 0:
                _history.Clear();
                SaveHistory();
                _status = "Cleared.";
                return;
        }

        // Poll clipboard. Adds the new value at index 0 (most recent first)
        // and dedupes — if the user re-copies the same text, the entry moves
        // to the top instead of stacking duplicates.
        _pollTimer += delta;
        if (_pollTimer >= PollIntervalSec)
        {
            _pollTimer = 0;
            string current = "";
            try { current = Raylib.GetClipboardText_() ?? ""; } catch { }
            if (current.Length > 0 && current != _lastClipboard)
            {
                _lastClipboard = current;
                _history.RemoveAll(e => e == current);
                _history.Insert(0, current);
                while (_history.Count > MaxEntries) _history.RemoveAt(_history.Count - 1);
                SaveHistory();
                _status = $"Captured a new entry ({_history.Count} total).";
            }
        }

        var list = ListRect();
        _hoverRow = -1;
        if (RetroSkin.PointInRect(local, list))
        {
            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
            {
                _scrollY -= wheel * RowH * 3;
                ClampScroll();
            }
            int row = (int)((local.Y - list.Y + _scrollY) / RowH);
            if (row >= 0 && row < _history.Count) _hoverRow = row;

            if (leftPressed && _hoverRow >= 0)
            {
                var entry = _history[_hoverRow];
                // Right side of the row is a small ✕ delete hit-target —
                // clicking the body restores, clicking the ✕ removes.
                bool deleteHit = local.X > list.X + list.Width - 28;
                if (deleteHit)
                {
                    _history.RemoveAt(_hoverRow);
                    SaveHistory();
                    _status = "Removed entry.";
                }
                else
                {
                    try { Raylib.SetClipboardText(entry); _lastClipboard = entry; } catch { }
                    // Move restored entry to top so repeat-use surfaces it.
                    _history.RemoveAt(_hoverRow);
                    _history.Insert(0, entry);
                    SaveHistory();
                    _status = "Copied to clipboard.";
                    _toastTimer = 1.2f;
                }
            }
        }
    }

    private Rectangle ListRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 4;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(FrameInset + 6, top,
            PanelSize.X - 2 * FrameInset - 12, bottom - top);
    }

    private void ClampScroll()
    {
        float maxScroll = Math.Max(0, _history.Count * RowH - ListRect().Height);
        _scrollY = Math.Clamp(_scrollY, 0, maxScroll);
    }

    private static string Preview(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        if (s.Length > MaxPreviewChars) s = s[..MaxPreviewChars] + "...";
        return s;
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "Clipboard Manager", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "Clear All" }, -1);

        var list = ListRect();
        var listAbs = new Rectangle(panelOffset.X + list.X, panelOffset.Y + list.Y,
            list.Width, list.Height);
        RetroSkin.DrawSunken(listAbs, RetroSkin.SunkenBg);

        if (_history.Count == 0)
        {
            string empty = "(empty — copy some text and it'll appear here)";
            int w = FontManager.MeasureText(empty, 14);
            FontManager.DrawText(empty,
                (int)(listAbs.X + (listAbs.Width - w) / 2),
                (int)(listAbs.Y + listAbs.Height / 2 - 8),
                14, RetroSkin.DisabledText);
        }

        // Rows — manual y-clip (no scissor; see project memory on Retina bugs).
        float topY = listAbs.Y + 2 - _scrollY;
        for (int i = 0; i < _history.Count; i++)
        {
            float y = topY + i * RowH;
            if (y + RowH < listAbs.Y) continue;
            if (y > listAbs.Y + listAbs.Height) break;

            if (i == _hoverRow)
            {
                Raylib.DrawRectangle((int)(listAbs.X + 1), (int)y,
                    (int)(listAbs.Width - 2), RowH, RetroSkin.Highlight);
            }

            string preview = Preview(_history[i]);
            FontManager.DrawText(preview, (int)(listAbs.X + Padding), (int)(y + 4),
                14, RetroSkin.BodyText);

            // Delete hit-area: small ✕ on the right.
            FontManager.DrawText("x",
                (int)(listAbs.X + listAbs.Width - 18), (int)(y + 4),
                14, _hoverRow == i ? RetroSkin.BodyText : RetroSkin.DisabledText);
        }

        // Toast — brief overlay confirming a restore so the user sees feedback
        // even though "copy this to clipboard" is otherwise invisible.
        if (_toastTimer > 0)
        {
            const string toast = "Copied!";
            int tw = FontManager.MeasureText(toast, 18);
            int th = 24;
            var tr = new Rectangle(
                panelOffset.X + (PanelSize.X - tw - 24) / 2,
                panelOffset.Y + PanelSize.Y - th - 56,
                tw + 24, th + 8);
            RetroSkin.DrawRaised(tr);
            FontManager.DrawText(toast, (int)(tr.X + 12), (int)(tr.Y + 6),
                18, RetroSkin.BodyText);
        }

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status, $"{_history.Count}/{MaxEntries}");
    }
}
