using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// Read-only spreadsheet viewer. Lists .csv files dropped into
/// <c>{SaveDir}/spreadsheets/</c>, lets the user pick one, and renders the
/// parsed rows as a scrollable grid. A sample CSV is written on first run
/// so the window has something to look at out of the box.
///
/// CSV handling supports quoted fields (so commas inside quotes don't
/// split a column) and basic doubled-quote escaping; anything fancier
/// (multi-line cells, BOMs) is intentionally out of scope.
/// </summary>
public class SpreadsheetActivity : IActivity
{
    public Vector2 PanelSize => new(640, 420);
    public bool IsFinished { get; private set; }

    private const int FrameInset = 3;
    private const int RowH = 22;
    private const int ColW = 100;
    private const int RowLabelW = 36;
    private const string FolderName = "spreadsheets";
    private const string SampleName = "sample.csv";

    private enum Mode { Picker, Grid }
    private Mode _mode = Mode.Picker;
    private readonly List<string> _files = new();
    private string _currentFile = "";
    private List<string[]> _rows = new();
    private bool _firstRowHeader = true;
    private float _scrollX;
    private float _scrollY;
    private string _status = "";
    private int _hoverFile = -1;

    public void Load()
    {
        EnsureSample();
        RefreshFiles();
    }

    public void Close() => IsFinished = true;

    private static string Folder() => Path.Combine(SaveManager.SaveDirectory, FolderName);

    private void EnsureSample()
    {
        try
        {
            Directory.CreateDirectory(Folder());
            var samplePath = Path.Combine(Folder(), SampleName);
            if (File.Exists(samplePath)) return;
            File.WriteAllText(samplePath,
                "Item,Quantity,Unit Price,Notes\n"
              + "Cheese wheel,3,4.50,\"Aged 18 months, sharp\"\n"
              + "Crackers,12,1.25,Whole grain\n"
              + "Olives,5,3.00,Pitted kalamata\n"
              + "Sparkling water,24,0.80,\n"
              + "Sourdough loaf,2,5.75,Fresh-baked daily\n"
              + "Honey,1,9.00,\"Local, raw\"\n");
        }
        catch { /* best-effort — picker will just show an empty list */ }
    }

    private void RefreshFiles()
    {
        _files.Clear();
        try
        {
            foreach (var f in Directory.GetFiles(Folder(), "*.csv").OrderBy(p => p))
                _files.Add(Path.GetFileName(f));
        }
        catch { }
        if (_files.Count == 0)
            _status = "Drop .csv files into " + FolderName + " to see them here.";
        else
            _status = $"{_files.Count} file{(_files.Count == 1 ? "" : "s")} available.";
    }

    private void OpenFile(string name)
    {
        try
        {
            var path = Path.Combine(Folder(), name);
            var text = File.ReadAllText(path);
            _rows = ParseCsv(text);
            _currentFile = name;
            _mode = Mode.Grid;
            _scrollX = 0;
            _scrollY = 0;
            int cols = _rows.Count == 0 ? 0 : _rows.Max(r => r.Length);
            _status = $"{_rows.Count} row{(_rows.Count == 1 ? "" : "s")} × {cols} column{(cols == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            _status = "Open failed: " + ex.Message;
        }
    }

    // Quote-aware CSV parser. Honors "" as an escaped quote inside a quoted
    // field. Doesn't handle multi-line quoted fields — same scope decision
    // as built-in OS spreadsheet previews.
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.Length == 0 && rows.Count > 0 && rows[^1].Length == 0) continue;
            var fields = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < rawLine.Length; i++)
            {
                char c = rawLine[i];
                if (inQuotes)
                {
                    if (c == '"' && i + 1 < rawLine.Length && rawLine[i + 1] == '"')
                    {
                        cur.Append('"'); i++;
                    }
                    else if (c == '"') inQuotes = false;
                    else cur.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
                    else cur.Append(c);
                }
            }
            fields.Add(cur.ToString());
            rows.Add(fields.ToArray());
        }
        // Trim trailing empty row caused by a final newline.
        while (rows.Count > 0 && rows[^1].All(s => s.Length == 0)) rows.RemoveAt(rows.Count - 1);
        return rows;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuItems = _mode == Mode.Picker
            ? new[] { "Refresh", "Open Folder" }
            : new[] { "Back to List", _firstRowHeader ? "Header: On" : "Header: Off" };
        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        int menuClicked = RetroWidgets.MenuBarHitTest(menuBar, menuItems, local, leftPressed);
        if (menuClicked == 0)
        {
            if (_mode == Mode.Picker) { RefreshFiles(); return; }
            _mode = Mode.Picker; RefreshFiles(); return;
        }
        if (menuClicked == 1)
        {
            if (_mode == Mode.Picker) { OpenFolder(); return; }
            _firstRowHeader = !_firstRowHeader; return;
        }

        if (_mode == Mode.Picker) UpdatePicker(local, leftPressed);
        else UpdateGrid(local, leftPressed);
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
        if (wheel != 0) { _scrollY -= wheel * RowH * 3; ClampScroll(); }
        int row = (int)((local.Y - list.Y + _scrollY) / RowH);
        if (row >= 0 && row < _files.Count) _hoverFile = row;
        if (leftPressed && _hoverFile >= 0) OpenFile(_files[_hoverFile]);
    }

    private void UpdateGrid(Vector2 local, bool leftPressed)
    {
        var grid = ContentRect();
        if (!RetroSkin.PointInRect(local, grid)) return;
        float wheelV = Raylib.GetMouseWheelMove();
        if (wheelV != 0)
        {
            if (Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift))
                _scrollX -= wheelV * ColW;
            else
                _scrollY -= wheelV * RowH * 3;
            ClampScroll();
        }
    }

    private Rectangle ContentRect()
    {
        float top = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + 4;
        float bottom = PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight - 4;
        return new Rectangle(FrameInset + 6, top,
            PanelSize.X - 2 * FrameInset - 12, bottom - top);
    }

    private void ClampScroll()
    {
        var grid = ContentRect();
        float maxY, maxX = 0;
        if (_mode == Mode.Picker)
        {
            maxY = Math.Max(0, _files.Count * RowH - grid.Height);
        }
        else
        {
            maxY = Math.Max(0, _rows.Count * RowH - grid.Height + RowH);
            int cols = _rows.Count == 0 ? 0 : _rows.Max(r => r.Length);
            maxX = Math.Max(0, RowLabelW + cols * ColW - grid.Width);
        }
        _scrollY = Math.Clamp(_scrollY, 0, maxY);
        _scrollX = Math.Clamp(_scrollX, 0, maxX);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        string title = _mode == Mode.Picker
            ? "Spreadsheet — Pick a file"
            : "Spreadsheet — " + _currentFile;
        RetroWidgets.DrawTitleBarVisual(titleBar, title, true);

        var menuItems = _mode == Mode.Picker
            ? new[] { "Refresh", "Open Folder" }
            : new[] { "Back to List", _firstRowHeader ? "Header: On" : "Header: Off" };
        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, menuItems, -1);

        var content = ContentRect();
        var contentAbs = new Rectangle(panelOffset.X + content.X, panelOffset.Y + content.Y,
            content.Width, content.Height);
        RetroSkin.DrawSunken(contentAbs, RetroSkin.SunkenBg);

        if (_mode == Mode.Picker) DrawPicker(contentAbs);
        else DrawGrid(contentAbs);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status, _status, _mode == Mode.Picker ? "Click a file to open" : "Shift+wheel: scroll right");
    }

    private void DrawPicker(Rectangle list)
    {
        if (_files.Count == 0)
        {
            const string s = "(no .csv files yet)";
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
            float y = topY + i * RowH;
            if (y + RowH < list.Y) continue;
            if (y > list.Y + list.Height) break;
            if (i == _hoverFile)
            {
                Raylib.DrawRectangle((int)(list.X + 1), (int)y,
                    (int)(list.Width - 2), RowH, RetroSkin.Highlight);
            }
            FontManager.DrawText(_files[i], (int)(list.X + 8), (int)(y + 4),
                14, RetroSkin.BodyText);
        }
    }

    private void DrawGrid(Rectangle grid)
    {
        if (_rows.Count == 0) return;
        int cols = _rows.Max(r => r.Length);

        // Header row — frozen at the top, doesn't scroll vertically. Uses
        // the first CSV row as labels when First-row-header is on, falling
        // back to spreadsheet-style "A B C..." when off.
        float headerH = RowH;
        var headerRect = new Rectangle(grid.X, grid.Y, grid.Width, headerH);
        Raylib.DrawRectangleRec(headerRect, RetroSkin.Face);
        Raylib.DrawRectangle((int)headerRect.X, (int)(headerRect.Y + headerRect.Height - 1),
            (int)headerRect.Width, 1, RetroSkin.Shadow);

        // Row labels live in their own narrow frozen column.
        Raylib.DrawRectangle((int)grid.X, (int)(grid.Y + headerH),
            RowLabelW, (int)(grid.Height - headerH), RetroSkin.Face);

        // Column headers.
        for (int c = 0; c < cols; c++)
        {
            float cx = grid.X + RowLabelW + c * ColW - _scrollX;
            if (cx + ColW < grid.X + RowLabelW) continue;
            if (cx > grid.X + grid.Width) break;
            string label = _firstRowHeader && _rows.Count > 0
                ? (c < _rows[0].Length ? _rows[0][c] : "")
                : (((char)('A' + c % 26)).ToString());
            FontManager.DrawText(label, (int)(cx + 4), (int)(grid.Y + 4),
                14, RetroSkin.BodyText);
            // Column separator.
            Raylib.DrawRectangle((int)(cx + ColW - 1), (int)grid.Y, 1,
                (int)grid.Height, RetroSkin.Shadow);
        }

        // Body rows (skip row 0 if first-row-header is on).
        int firstDataRow = _firstRowHeader ? 1 : 0;
        float bodyTop = grid.Y + headerH - _scrollY;
        for (int r = firstDataRow; r < _rows.Count; r++)
        {
            float ry = bodyTop + (r - firstDataRow) * RowH;
            if (ry + RowH < grid.Y + headerH) continue;
            if (ry > grid.Y + grid.Height) break;

            // Zebra striping for readability.
            if ((r - firstDataRow) % 2 == 1)
            {
                Raylib.DrawRectangle((int)(grid.X + RowLabelW), (int)ry,
                    (int)(grid.Width - RowLabelW), RowH, RetroSkin.Highlight);
            }

            FontManager.DrawText($"{r - firstDataRow + 1}",
                (int)(grid.X + 4), (int)(ry + 4),
                12, RetroSkin.BodyText);
            // Row separator.
            Raylib.DrawRectangle((int)grid.X, (int)(ry + RowH - 1),
                (int)grid.Width, 1, RetroSkin.Shadow);

            var row = _rows[r];
            for (int c = 0; c < cols; c++)
            {
                float cx = grid.X + RowLabelW + c * ColW - _scrollX;
                if (cx + ColW < grid.X + RowLabelW) continue;
                if (cx > grid.X + grid.Width) break;
                string cell = c < row.Length ? row[c] : "";
                // Clip text by simple substring length — measured truncation
                // would be more elegant but costs a call per cell; this is
                // close enough for a read-only viewer.
                if (cell.Length > 16) cell = cell[..14] + "...";
                FontManager.DrawText(cell, (int)(cx + 4), (int)(ry + 4),
                    13, RetroSkin.BodyText);
            }
        }
    }
}
