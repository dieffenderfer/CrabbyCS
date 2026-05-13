using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Plain-text E-reader. Lists .txt files inside <c>{SaveDir}/library/</c>,
/// lets the user open one, and renders the text wrapped to the panel
/// width with PageUp / PageDown paging. Reading position is remembered
/// per file in <c>ereader_progress.json</c> so reopening a long story
/// drops the user back near where they were. A sample text is dropped
/// in the library folder on first run.
/// </summary>
public class EReaderActivity : IActivity
{
    public Vector2 PanelSize => new(620, 520);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int Pad = 14;
    private const int FontSize = 16;
    private const int LineH = 22;
    private const string FolderName = "library";
    private const string SampleName = "welcome.txt";
    private const string ProgressFile = "ereader_progress.json";

    private enum Mode { Picker, Reading }
    private Mode _mode = Mode.Picker;
    private readonly List<string> _files = new();
    private string _currentFile = "";
    private List<string> _wrapped = new();
    private int _topLine;
    private int _hoverFile = -1;
    private string _status = "";
    private float _scrollY;          // picker scroll
    private Dictionary<string, int> _progress = new();

    public void Load()
    {
        EnsureSample();
        LoadProgress();
        RefreshFiles();
    }

    public void Close()
    {
        if (_mode == Mode.Reading) SaveProgress();
        IsFinished = true;
    }

    private static string Folder() => Path.Combine(SaveManager.SaveDirectory, FolderName);

    private void EnsureSample()
    {
        try
        {
            Directory.CreateDirectory(Folder());
            var p = Path.Combine(Folder(), SampleName);
            if (File.Exists(p)) return;
            File.WriteAllText(p,
                "Welcome to the Library\n\n"
              + "Drop any .txt file into the library/ folder beside this one and it'll show up "
              + "in the picker when you reopen the reader. The picker remembers which page you "
              + "left off on for each file, so longer stories pick back up where you were.\n\n"
              + "Use PageDown / PageUp (or the arrow keys) to turn pages. Press B to return to "
              + "the file list. The reader wraps text to fit the window width, so resizing the "
              + "window in a future update will reflow automatically.\n\n"
              + "Happy reading.\n");
        }
        catch { /* best-effort */ }
    }

    private void RefreshFiles()
    {
        _files.Clear();
        try
        {
            foreach (var f in Directory.GetFiles(Folder(), "*.txt").OrderBy(p => p))
                _files.Add(Path.GetFileName(f));
        }
        catch { }
        _status = _files.Count == 0
            ? "Drop .txt files into " + FolderName + " to read them."
            : $"{_files.Count} file{(_files.Count == 1 ? "" : "s")} in the library.";
    }

    private void OpenFile(string name)
    {
        try
        {
            var path = Path.Combine(Folder(), name);
            var raw = File.ReadAllText(path);
            _wrapped = WrapText(raw, (int)(PanelSize.X - 2 * (FrameInset + Pad)));
            _currentFile = name;
            _mode = Mode.Reading;
            _topLine = _progress.TryGetValue(name, out var t) ? t : 0;
            _topLine = Math.Clamp(_topLine, 0, Math.Max(0, _wrapped.Count - LinesPerPage()));
            _status = $"{_wrapped.Count} line{(_wrapped.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex) { _status = "Open failed: " + ex.Message; }
    }

    private static List<string> WrapText(string text, int maxWidthPx)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(""); continue; }
            var words = paragraph.Split(' ');
            string cur = "";
            foreach (var w in words)
            {
                string trial = cur.Length == 0 ? w : cur + " " + w;
                if (FontManager.MeasureText(trial, FontSize) > maxWidthPx && cur.Length > 0)
                {
                    lines.Add(cur);
                    cur = w;
                }
                else cur = trial;
            }
            if (cur.Length > 0) lines.Add(cur);
        }
        return lines;
    }

    private int LinesPerPage()
        => Math.Max(1, (int)((PanelSize.Y - 2 * FrameInset - RetroWidgets.TitleBarHeight
                    - RetroWidgets.MenuBarHeight - RetroWidgets.StatusBarHeight - 2 * Pad) / LineH));

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        {
            if (_mode == Mode.Reading) SaveProgress();
            IsFinished = true;
            return;
        }

        var menuItems = _mode == Mode.Picker
            ? new[] { "Refresh", "Open Folder" }
            : new[] { "Back to List", "Prev Page", "Next Page" };
        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        int hit = RetroWidgets.MenuBarHitTest(menuBar, menuItems, local, leftPressed);
        if (hit >= 0)
        {
            if (_mode == Mode.Picker)
            {
                if (hit == 0) RefreshFiles();
                else OpenFolder();
                return;
            }
            else
            {
                if (hit == 0) { SaveProgress(); _mode = Mode.Picker; RefreshFiles(); return; }
                if (hit == 1) { TurnPage(-1); return; }
                if (hit == 2) { TurnPage(+1); return; }
            }
        }

        if (_mode == Mode.Picker) UpdatePicker(local, leftPressed);
        else UpdateReading(local, leftPressed);
    }

    private void OpenFolder()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", new[] { Folder() });
            else if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder()) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start("xdg-open", new[] { Folder() });
        }
        catch (Exception ex) { _status = "Couldn't open folder: " + ex.Message; }
    }

    private void UpdatePicker(Vector2 local, bool leftPressed)
    {
        var list = ContentRect();
        _hoverFile = -1;
        if (!RetroSkin.PointInRect(local, list)) return;
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0) _scrollY = Math.Clamp(_scrollY - wheel * 22 * 3, 0,
            Math.Max(0, _files.Count * 22 - list.Height));
        int row = (int)((local.Y - list.Y + _scrollY) / 22);
        if (row >= 0 && row < _files.Count) _hoverFile = row;
        if (leftPressed && _hoverFile >= 0) OpenFile(_files[_hoverFile]);
    }

    private void UpdateReading(Vector2 local, bool leftPressed)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.PageDown) || Raylib.IsKeyPressed(KeyboardKey.Right)
            || Raylib.IsKeyPressed(KeyboardKey.Down)
            || Raylib.IsKeyPressed(KeyboardKey.Space))
        { TurnPage(+1); }
        if (Raylib.IsKeyPressed(KeyboardKey.PageUp) || Raylib.IsKeyPressed(KeyboardKey.Left)
            || Raylib.IsKeyPressed(KeyboardKey.Up))
        { TurnPage(-1); }
        if (Raylib.IsKeyPressed(KeyboardKey.B) || Raylib.IsKeyPressed(KeyboardKey.Backspace))
        { SaveProgress(); _mode = Mode.Picker; RefreshFiles(); }
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0) TurnPage(wheel < 0 ? +1 : -1);
        // Click left half = prev, right half = next.
        if (leftPressed)
        {
            var list = ContentRect();
            if (RetroSkin.PointInRect(local, list))
                TurnPage(local.X < list.X + list.Width / 2 ? -1 : +1);
        }
    }

    private void TurnPage(int dir)
    {
        int step = LinesPerPage();
        _topLine = Math.Clamp(_topLine + dir * step, 0, Math.Max(0, _wrapped.Count - step));
        SaveProgress();
    }

    private Rectangle ContentRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 4;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(FrameInset + 6, top,
            PanelSize.X - 2 * FrameInset - 12, bottom - top);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Dictionary<string,int> is trivially serializable; reflection-based JSON is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — primitive map only.")]
    private void LoadProgress()
    {
        try
        {
            var p = Path.Combine(SaveManager.SaveDirectory, ProgressFile);
            if (!File.Exists(p)) return;
            var json = File.ReadAllText(p);
            _progress = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                        ?? new Dictionary<string, int>();
        }
        catch { _progress = new Dictionary<string, int>(); }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Dictionary<string,int> is trivially serializable; reflection-based JSON is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — primitive map only.")]
    private void SaveProgress()
    {
        if (_currentFile.Length > 0) _progress[_currentFile] = _topLine;
        try
        {
            var p = Path.Combine(SaveManager.SaveDirectory, ProgressFile);
            File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(_progress));
        }
        catch { /* best-effort */ }
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        string title = _mode == Mode.Picker ? "Library" : "Reader — " + _currentFile;
        RetroWidgets.DrawTitleBarVisual(titleBar, title, true);

        var menuItems = _mode == Mode.Picker
            ? new[] { "Refresh", "Open Folder" }
            : new[] { "Back to List", "Prev Page", "Next Page" };
        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, menuItems, -1);

        var content = ContentRect();
        var contentAbs = new Rectangle(panelOffset.X + content.X, panelOffset.Y + content.Y,
            content.Width, content.Height);
        RetroSkin.DrawSunken(contentAbs, RetroSkin.SunkenBg);

        if (_mode == Mode.Picker) DrawPicker(contentAbs);
        else DrawReader(contentAbs);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        string right = _mode == Mode.Reading
            ? $"Line {_topLine + 1}–{Math.Min(_topLine + LinesPerPage(), _wrapped.Count)} / {_wrapped.Count}"
            : "Click a file to open";
        RetroWidgets.StatusBar(status, _status, right);
    }

    private void DrawPicker(Rectangle list)
    {
        if (_files.Count == 0)
        {
            const string s = "(no .txt files yet)";
            int w = FontManager.MeasureText(s, 14);
            FontManager.DrawText(s,
                (int)(list.X + (list.Width - w) / 2),
                (int)(list.Y + list.Height / 2 - 8),
                14, RetroSkin.DisabledText);
            return;
        }
        float topY = list.Y + 2 - _scrollY;
        for (int i = 0; i < _files.Count; i++)
        {
            float y = topY + i * 22;
            if (y + 22 < list.Y) continue;
            if (y > list.Y + list.Height) break;
            if (i == _hoverFile)
            {
                Raylib.DrawRectangle((int)(list.X + 1), (int)y,
                    (int)(list.Width - 2), 22, RetroSkin.Highlight);
            }
            FontManager.DrawText(_files[i], (int)(list.X + 12), (int)(y + 4),
                14, RetroSkin.BodyText);
        }
    }

    private void DrawReader(Rectangle area)
    {
        int textX = (int)(area.X + Pad);
        int y = (int)(area.Y + Pad);
        int linesShown = LinesPerPage();
        for (int i = 0; i < linesShown && _topLine + i < _wrapped.Count; i++)
        {
            FontManager.DrawText(_wrapped[_topLine + i], textX, y + i * LineH,
                FontSize, RetroSkin.BodyText);
        }
        // Progress sliver at the right edge of the reader pane.
        if (_wrapped.Count > 0)
        {
            float frac = (float)_topLine / Math.Max(1, _wrapped.Count - linesShown);
            int trackX = (int)(area.X + area.Width - 6);
            int trackY = (int)area.Y + 4;
            int trackH = (int)area.Height - 8;
            Raylib.DrawRectangle(trackX, trackY, 3, trackH, RetroSkin.Face);
            int thumbH = Math.Max(20, (int)(trackH * ((float)linesShown / _wrapped.Count)));
            int thumbY = trackY + (int)((trackH - thumbH) * Math.Clamp(frac, 0f, 1f));
            Raylib.DrawRectangle(trackX, thumbY, 3, thumbH, RetroSkin.Shadow);
        }
    }
}
