using System.Numerics;
using Raylib_cs;
using MouseHouse.Data;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.UI;

/// <summary>
/// Modal editor for the radio station library. Lets the user toggle which
/// stations are in the prev/next rotation, add custom streams, and remove
/// existing entries (with a confirm step). Drawn over the radio widget; the
/// widget intercepts input while <see cref="IsOpen"/>.
/// </summary>
public class RadioStationEditor
{
    public bool IsOpen { get; private set; }

    /// <summary>Fired after any library mutation so the widget can rebind its rotation index.</summary>
    public Action? LibraryChanged;

    private const int PanelW = 380;
    private const int PanelH = 420;
    private const int RowH = 22;
    private const int FieldH = 20;
    private const int Gap = 4;

    // Per-frame layout — cached during Draw, read back during Update for hit
    // testing. Both run sequentially each frame so this is safe.
    private Rectangle _panel;
    private Rectangle _listRect;
    private Rectangle _addRect;
    private Rectangle _closeBtn;
    private Rectangle _addBtn;
    private Rectangle _revealJsonBtn;
    private Rectangle[] _fieldRects = new Rectangle[4];
    // Scroll
    private int _scroll;
    // Add form: Name / URL / Genre / Slug
    private readonly string[] _addFields = new[] { "", "", "", "" };
    private static readonly string[] _addLabels = { "Name", "URL", "Genre", "Slug" };
    private int _focusField = -1;            // -1 = no focus
    private float _caretBlink;
    // Pending-delete confirmation
    private int _pendingDeleteIdx = -1;
    private string _pendingDeleteName = "";

    public void Open()
    {
        IsOpen = true;
        _scroll = 0;
        _focusField = -1;
        _pendingDeleteIdx = -1;
    }

    public void Close()
    {
        IsOpen = false;
        _focusField = -1;
        _pendingDeleteIdx = -1;
    }

    /// <summary>Returns true if the editor consumed input this frame.</summary>
    public bool Update(float delta, Vector2 mouse,
                       bool leftPressed, bool leftReleased, bool rightPressed,
                       int screenW, int screenH)
    {
        if (!IsOpen) return false;
        _ = leftReleased; _ = rightPressed;
        _caretBlink += delta;

        // Confirm dialog has exclusive focus when active.
        if (_pendingDeleteIdx >= 0)
        {
            UpdateConfirm(mouse, leftPressed, screenW, screenH);
            return true;
        }

        // Esc closes editor
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            Close();
            return true;
        }

        // Close X
        if (leftPressed && RetroSkin.PointInRect(mouse, _closeBtn))
        {
            Close();
            return true;
        }

        // Scroll wheel
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0 && RetroSkin.PointInRect(mouse, _listRect))
        {
            _scroll = Math.Max(0, _scroll - (int)(wheel * RowH));
            int maxScroll = MaxScroll();
            if (_scroll > maxScroll) _scroll = maxScroll;
        }

        // Click on a list row
        if (leftPressed && RetroSkin.PointInRect(mouse, _listRect))
        {
            int rel = (int)(mouse.Y - _listRect.Y) + _scroll;
            int rowIdx = rel / RowH;
            var stations = RadioStations.All;
            if (rowIdx >= 0 && rowIdx < stations.Count)
            {
                var rowR = RowRect(rowIdx);
                var checkR = new Rectangle(rowR.X + 4, rowR.Y + 4, 14, 14);
                var delR = new Rectangle(rowR.X + rowR.Width - 22, rowR.Y + 4, 18, 14);
                if (RetroSkin.PointInRect(mouse, checkR))
                {
                    RadioStations.SetActive(rowIdx, !stations[rowIdx].Active);
                    LibraryChanged?.Invoke();
                    _focusField = -1;
                    return true;
                }
                if (RetroSkin.PointInRect(mouse, delR))
                {
                    _pendingDeleteIdx = rowIdx;
                    _pendingDeleteName = stations[rowIdx].Name;
                    _focusField = -1;
                    return true;
                }
            }
            // Plain row click clears focus on the form.
            _focusField = -1;
            return true;
        }

        // Click on a field focuses it.
        if (leftPressed)
        {
            _focusField = -1;
            for (int i = 0; i < _fieldRects.Length; i++)
            {
                if (RetroSkin.PointInRect(mouse, _fieldRects[i]))
                {
                    _focusField = i;
                    _caretBlink = 0;
                    break;
                }
            }
            // Add button
            if (RetroSkin.PointInRect(mouse, _addBtn))
            {
                TrySubmitAdd();
                return true;
            }
            // "Reveal" button — open the stations.json directory in the
            // OS file browser so the user can hand-edit the file when the
            // app is closed. It's the path the library reads on launch
            // and writes whenever the editor mutates a station.
            if (RetroSkin.PointInRect(mouse, _revealJsonBtn))
            {
                RevealStationsJson();
                return true;
            }
            // Click outside the panel does nothing — the editor is modal but
            // not auto-dismiss; user clicks the X (or Esc) to close.
            if (!RetroSkin.PointInRect(mouse, _panel)) return true;
            return true;
        }

        // Keyboard input goes to the focused field.
        if (_focusField >= 0)
        {
            HandleFieldKeys(_focusField);
        }

        return true;
    }

    private void UpdateConfirm(Vector2 mouse, bool leftPressed, int screenW, int screenH)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            _pendingDeleteIdx = -1;
            return;
        }
        var (cancelR, removeR) = ConfirmButtonRects(screenW, screenH);
        if (leftPressed)
        {
            if (RetroSkin.PointInRect(mouse, cancelR))
            {
                _pendingDeleteIdx = -1;
                return;
            }
            if (RetroSkin.PointInRect(mouse, removeR))
            {
                int idx = _pendingDeleteIdx;
                _pendingDeleteIdx = -1;
                RadioStations.Remove(idx);
                LibraryChanged?.Invoke();
                int max = MaxScroll();
                if (_scroll > max) _scroll = max;
                return;
            }
        }
    }

    private void HandleFieldKeys(int fieldIdx)
    {
        bool cmd = Raylib.IsKeyDown(KeyboardKey.LeftSuper) || Raylib.IsKeyDown(KeyboardKey.RightSuper)
                || Raylib.IsKeyDown(KeyboardKey.LeftControl) || Raylib.IsKeyDown(KeyboardKey.RightControl);

        if (cmd && Raylib.IsKeyPressed(KeyboardKey.V))
        {
            var clip = Raylib.GetClipboardText_();
            if (!string.IsNullOrEmpty(clip))
            {
                clip = clip.Replace("\r", "").Replace("\n", " ").Trim();
                _addFields[fieldIdx] += clip;
            }
        }

        // Tab → next field (Shift+Tab → previous)
        if (Raylib.IsKeyPressed(KeyboardKey.Tab))
        {
            bool shift = Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);
            _focusField = (fieldIdx + (shift ? -1 : 1) + _addFields.Length) % _addFields.Length;
            _caretBlink = 0;
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Enter) || Raylib.IsKeyPressed(KeyboardKey.KpEnter))
        {
            TrySubmitAdd();
            return;
        }

        // Backspace (with key-repeat awareness)
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) || Raylib.IsKeyPressedRepeat(KeyboardKey.Backspace))
        {
            if (_addFields[fieldIdx].Length > 0)
                _addFields[fieldIdx] = _addFields[fieldIdx][..^1];
            _caretBlink = 0;
        }

        int ch;
        while ((ch = Raylib.GetCharPressed()) > 0)
        {
            if (ch >= 32 && ch <= 126)
            {
                _addFields[fieldIdx] += (char)ch;
                _caretBlink = 0;
            }
        }
    }

    private void TrySubmitAdd()
    {
        string name = _addFields[0].Trim();
        string url = _addFields[1].Trim();
        string genre = _addFields[2].Trim();
        string slug = _addFields[3].Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) return;
        if (string.IsNullOrEmpty(genre)) genre = "stream";
        RadioStations.Add(new RadioStation(name, url, genre, slug, Active: true));
        for (int i = 0; i < _addFields.Length; i++) _addFields[i] = "";
        _focusField = -1;
        LibraryChanged?.Invoke();
        // Scroll to the bottom so the user sees the row that was added.
        _scroll = MaxScroll();
    }

    public void Draw(int screenW, int screenH)
    {
        if (!IsOpen) return;

        // Dim backdrop
        Raylib.DrawRectangle(0, 0, screenW, screenH, new Color((byte)0, (byte)0, (byte)0, (byte)140));

        int px = (screenW - PanelW) / 2;
        int py = (screenH - PanelH) / 2;
        _panel = new Rectangle(px, py, PanelW, PanelH);
        RetroSkin.DrawRaised(_panel);

        // Title bar
        var title = new Rectangle(px + 2, py + 2, PanelW - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)title.X, (int)title.Y, (int)title.Width, (int)title.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText("Radio Stations", (int)title.X + 6, (int)title.Y + 1,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);
        _closeBtn = new Rectangle(title.X + title.Width - 18, title.Y + 2, 16, 14);
        RetroSkin.DrawRaised(_closeBtn);
        DrawXGlyph(_closeBtn);

        // Body layout
        int bodyTop = (int)(title.Y + title.Height + 4);
        int listTop = bodyTop;
        int formH = ComputeFormHeight();
        int listH = PanelH - (bodyTop - py) - formH - 8;
        _listRect = new Rectangle(px + 6, listTop, PanelW - 12, listH);
        RetroSkin.DrawSunken(_listRect, fill: RetroSkin.SunkenBg);
        DrawList();

        _addRect = new Rectangle(px + 6, listTop + listH + 4, PanelW - 12, formH);
        DrawAddForm();

        // Confirm dialog on top if active
        if (_pendingDeleteIdx >= 0) DrawConfirm(screenW, screenH);
    }

    private int ComputeFormHeight()
    {
        // Header + 4 fields + button row + paddings.
        return 8 + 14 + (FieldH + Gap) * 4 + 22 + 8;
    }

    private void DrawList()
    {
        var stations = RadioStations.All;
        int x = (int)_listRect.X;
        int y = (int)_listRect.Y;
        int w = (int)_listRect.Width;
        int h = (int)_listRect.Height;

        // Manual viewport clip: skip rows whose pixels fall outside _listRect.
        // Each row drawn at y_row = listY + i*RowH - scroll.
        for (int i = 0; i < stations.Count; i++)
        {
            int ry = y + i * RowH - _scroll;
            if (ry + RowH < y || ry > y + h) continue;
            DrawRow(stations[i], i, ry, x, w, y, h);
        }

        // Scrollbar (right edge of listRect).
        DrawScrollbar(x, y, w, h, stations.Count);
    }

    private Rectangle RowRect(int rowIdx)
    {
        int ry = (int)_listRect.Y + rowIdx * RowH - _scroll;
        return new Rectangle(_listRect.X, ry, _listRect.Width, RowH);
    }

    private void DrawRow(RadioStation s, int idx, int ry, int x, int w, int clipY, int clipH)
    {
        // Alternating row tint for readability — clipped manually since we
        // can't safely BeginScissorMode (macOS Retina bug — see CLAUDE.md).
        int rowTop = Math.Max(ry, clipY);
        int rowBot = Math.Min(ry + RowH, clipY + clipH);
        if (rowBot <= rowTop) return;
        if ((idx & 1) == 1)
        {
            Raylib.DrawRectangle(x + 1, rowTop, w - 2 - 8, rowBot - rowTop,
                new Color((byte)0, (byte)0, (byte)0, (byte)18));
        }

        // Checkbox at left
        var checkR = new Rectangle(x + 4, ry + 4, 14, 14);
        if (rowTop <= ry + RowH && ry + 4 < clipY + clipH)
        {
            RetroSkin.DrawSunken(checkR, fill: RetroSkin.SunkenBg);
            if (s.Active)
            {
                // Bold check mark drawn as two diagonals.
                int cx = (int)checkR.X + 3;
                int cy = (int)checkR.Y + 7;
                for (int t = 0; t < 4; t++)
                {
                    Raylib.DrawPixel(cx + t, cy + t, RetroSkin.BodyText);
                    Raylib.DrawPixel(cx + t, cy + t + 1, RetroSkin.BodyText);
                }
                for (int t = 0; t < 6; t++)
                {
                    Raylib.DrawPixel(cx + 3 + t, cy + 3 - t, RetroSkin.BodyText);
                    Raylib.DrawPixel(cx + 3 + t, cy + 3 - t + 1, RetroSkin.BodyText);
                }
            }
        }

        // Label: "Name — genre" (slug appended in parens if SomaFM)
        string label = $"{s.Name}  ·  {s.Genre}";
        if (!string.IsNullOrEmpty(s.Slug)) label += $"  ({s.Slug})";
        var textCol = s.Active ? RetroSkin.BodyText : RetroSkin.DisabledText;
        if (ry + 4 >= clipY && ry + 4 + RetroSkin.BodyFontSize <= clipY + clipH)
        {
            RetroSkin.DrawText(label, x + 24, ry + 3, textCol, RetroSkin.BodyFontSize - 2);
        }

        // Delete X at right
        var delR = new Rectangle(x + w - 22 - 6, ry + 4, 18, 14);
        if (rowTop <= ry + RowH && ry + 4 < clipY + clipH)
        {
            RetroSkin.DrawRaised(delR);
            // small "X" centered (use the same convention as title close)
            int dxC = (int)delR.X + 8;
            int dyC = (int)delR.Y + 6;
            for (int t = -2; t <= 2; t++)
            {
                Raylib.DrawPixel(dxC + t, dyC + t, RetroSkin.BodyText);
                Raylib.DrawPixel(dxC + t, dyC - t, RetroSkin.BodyText);
            }
        }
    }

    private void DrawScrollbar(int x, int y, int w, int h, int rowCount)
    {
        int total = rowCount * RowH;
        if (total <= h) return;
        int trackX = x + w - 8;
        var track = new Rectangle(trackX, y, 6, h);
        Raylib.DrawRectangleRec(track, new Color((byte)0, (byte)0, (byte)0, (byte)28));
        float frac = h / (float)total;
        int thumbH = Math.Max(16, (int)(h * frac));
        int thumbY = y + (int)((_scroll / (float)(total - h)) * (h - thumbH));
        Raylib.DrawRectangle(trackX, thumbY, 6, thumbH, RetroSkin.Shadow);
    }

    private int MaxScroll()
    {
        int total = RadioStations.All.Count * RowH;
        int viewH = (int)_listRect.Height;
        return Math.Max(0, total - viewH);
    }

    private void DrawAddForm()
    {
        int x = (int)_addRect.X;
        int y = (int)_addRect.Y;
        int w = (int)_addRect.Width;

        RetroSkin.DrawText("Add station:", x + 2, y, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        int fy = y + 14;
        int labelW = 38;
        for (int i = 0; i < _addFields.Length; i++)
        {
            RetroSkin.DrawText(_addLabels[i], x + 2, fy + 3,
                RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
            var fr = new Rectangle(x + labelW, fy, w - labelW - 4, FieldH);
            _fieldRects[i] = fr;
            RetroSkin.DrawSunken(fr, fill: RetroSkin.SunkenBg);
            string text = _addFields[i];
            // Show a placeholder hint for the slug field since it's optional.
            if (i == 3 && string.IsNullOrEmpty(text) && _focusField != 3)
            {
                RetroSkin.DrawText("(SomaFM channel id, optional)",
                    (int)fr.X + 4, (int)fr.Y + 4, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);
            }
            else
            {
                RetroSkin.DrawText(text, (int)fr.X + 4, (int)fr.Y + 4,
                    RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
                if (_focusField == i && ((int)(_caretBlink * 2) & 1) == 0)
                {
                    int tw = RetroSkin.MeasureText(text, RetroSkin.BodyFontSize - 2);
                    int caretX = (int)fr.X + 4 + tw + 1;
                    Raylib.DrawRectangle(caretX, (int)fr.Y + 3, 1,
                        RetroSkin.BodyFontSize - 2, RetroSkin.BodyText);
                }
            }
            fy += FieldH + Gap;
        }

        _addBtn = new Rectangle(x + w - 60, fy, 56, 20);
        RetroSkin.DrawRaised(_addBtn);
        int btnLabelW = RetroSkin.MeasureText("Add", RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Add",
            (int)_addBtn.X + ((int)_addBtn.Width - btnLabelW) / 2,
            (int)_addBtn.Y + 3, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);

        // "Reveal stations.json" button — same row as Add, on the left.
        // The library auto-writes this file on every edit; the button is
        // here so the user can find it without spelunking through their
        // platform's app-data dir.
        _revealJsonBtn = new Rectangle(x, fy, 130, 20);
        RetroSkin.DrawRaised(_revealJsonBtn);
        int rw = RetroSkin.MeasureText("Reveal stations.json", RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Reveal stations.json",
            (int)_revealJsonBtn.X + ((int)_revealJsonBtn.Width - rw) / 2,
            (int)_revealJsonBtn.Y + 3, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
    }

    private static void RevealStationsJson()
    {
        try
        {
            var dir = MouseHouse.Core.SaveManager.SaveDirectory;
            string fileName, args;
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX))
            {
                fileName = "open"; args = $"\"{dir}\"";
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
            {
                fileName = "explorer"; args = $"\"{dir}\"";
            }
            else
            {
                fileName = "xdg-open"; args = $"\"{dir}\"";
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { /* best-effort — silently ignore if the OS shell call fails */ }
    }

    private (Rectangle Cancel, Rectangle Remove) ConfirmButtonRects(int screenW, int screenH)
    {
        int w = 280, h = 110;
        int x = (screenW - w) / 2;
        int y = (screenH - h) / 2;
        var cancel = new Rectangle(x + w - 70 - 70 - 8, y + h - 30, 70, 22);
        var remove = new Rectangle(x + w - 70 - 6, y + h - 30, 70, 22);
        return (cancel, remove);
    }

    private void DrawConfirm(int screenW, int screenH)
    {
        int w = 280, h = 110;
        int x = (screenW - w) / 2;
        int y = (screenH - h) / 2;
        Raylib.DrawRectangle(0, 0, screenW, screenH, new Color((byte)0, (byte)0, (byte)0, (byte)80));
        var r = new Rectangle(x, y, w, h);
        RetroSkin.DrawRaised(r);
        var title = new Rectangle(x + 2, y + 2, w - 4, RetroWidgets.TitleBarHeight);
        Raylib.DrawRectangleGradientH((int)title.X, (int)title.Y, (int)title.Width, (int)title.Height,
            RetroSkin.TitleActive, RetroSkin.TitleGradEnd);
        RetroSkin.DrawText("Remove station", (int)title.X + 6, (int)title.Y + 1,
            RetroSkin.TitleText, RetroSkin.TitleFontSize);

        string line1 = $"Remove \"{_pendingDeleteName}\"?";
        string line2 = "It will be deleted from the library.";
        RetroSkin.DrawText(line1, x + 12, y + 32, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText(line2, x + 12, y + 50, RetroSkin.DisabledText, RetroSkin.BodyFontSize - 2);

        var (cancelR, removeR) = ConfirmButtonRects(screenW, screenH);
        RetroSkin.DrawRaised(cancelR);
        int cw = RetroSkin.MeasureText("Cancel", RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Cancel",
            (int)cancelR.X + ((int)cancelR.Width - cw) / 2,
            (int)cancelR.Y + 4, RetroSkin.BodyText, RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawRaised(removeR);
        int rw = RetroSkin.MeasureText("Remove", RetroSkin.BodyFontSize - 2);
        RetroSkin.DrawText("Remove",
            (int)removeR.X + ((int)removeR.Width - rw) / 2,
            (int)removeR.Y + 4, new Color((byte)160, (byte)32, (byte)32, (byte)255),
            RetroSkin.BodyFontSize - 2);
    }

    private static void DrawXGlyph(Rectangle close)
    {
        int cx = (int)close.X + 7;
        int cy = (int)close.Y + 6;
        for (int i = -3; i <= 3; i++)
        {
            Raylib.DrawPixel(cx + i, cy + i, RetroSkin.BodyText);
            Raylib.DrawPixel(cx + i, cy - i, RetroSkin.BodyText);
        }
    }
}
