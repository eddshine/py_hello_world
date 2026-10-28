using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace MasLauncher;

/// <summary>
/// Renders the pseudoconsole's screen. ConPTY does not stream plain lines: it paints a
/// fixed screen with cursor addressing, clears and overwrites, and moves the cursor down
/// with literal newlines. Treating that as a text stream produces huge blank gaps and
/// text glued onto the previous line, so this keeps a small screen grid instead:
///
/// * cursor moves, overwrites, erases and colours are applied to the grid;
/// * lines that scroll off the top are appended to the log (history);
/// * `cls` archives the whole screen to the log and starts a new section;
/// * the visible screen is re-rendered in place, with leading/trailing blank rows dropped
///   and blank runs collapsed, so the log reads compactly.
/// </summary>
internal sealed class TerminalView
{
    private static readonly Color Default = Color.FromArgb(214, 214, 220);
    private static readonly Color Separator = Color.FromArgb(96, 96, 106);

    private const int MinColumns = 118;
    private const int MaxHistory = 400_000;
    private const int HistoryTrim = 80_000;

    private readonly RichTextBox _box;
    private readonly Queue<string> _pending = new();
    private readonly StringBuilder _sequence = new();
    private readonly List<Cell[]> _grid = new();
    private readonly int _rows;

    private int _cols;
    private int _row;
    private int _col;

    private int _state;
    private bool _bold;
    private Color _foreground = Default;

    private int _screenStart;
    private int _screenLength;
    private bool _screenDirty = true;
    private bool _lastLineBlank;
    private bool _contentSinceDivider;

    private struct Cell
    {
        public char Ch;
        public Color Fg;
    }

    public TerminalView(RichTextBox box, int rows = 38)
    {
        _box = box;
        _rows = Math.Max(8, rows);
        _cols = MinColumns;
        for (var i = 0; i < _rows; i++) _grid.Add(NewRow());
    }

    public void Enqueue(string chunk)
    {
        lock (_pending) _pending.Enqueue(chunk);
    }

    /// <summary>Drains queued output and repaints the visible screen. Call from the UI thread.</summary>
    public void Pump()
    {
        while (true)
        {
            string chunk;
            lock (_pending)
            {
                if (_pending.Count == 0) break;
                chunk = _pending.Dequeue();
            }
            Parse(chunk);
        }
        RenderScreen();
    }

    /// <summary>Appends a line of our own text (status messages), after archiving the live screen.</summary>
    public void WriteLine(string text, Color color)
    {
        ArchiveScreen();
        TrimTrailingBlankLine();

        Append(text, color);
        _box.AppendText(Environment.NewLine);
        _lastLineBlank = string.IsNullOrWhiteSpace(text);
        if (!_lastLineBlank) _contentSinceDivider = true;

        _screenStart = _box.TextLength;
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
        TrimHistory();
    }

    public void Clear()
    {
        for (var i = 0; i < _rows; i++) _grid[i] = NewRow();
        _row = 0;
        _col = 0;
        _state = 0;
        _bold = false;
        _foreground = Default;
        _screenStart = 0;
        _screenLength = 0;
        _screenDirty = true;
        _lastLineBlank = false;
        _contentSinceDivider = false;
        lock (_pending) _pending.Clear();
        _box.Clear();
    }

    // ---------------------------------------------------------------- parsing

    private void Parse(string text)
    {
        foreach (var ch in text)
        {
            switch (_state)
            {
                case 0: // plain text
                    if (ch == '\x1b') { _state = 1; break; }
                    if (ch == '\n') { NewLine(); break; }
                    if (ch == '\r') { _col = 0; break; }
                    if (ch == '\b') { if (_col > 0) _col--; break; }
                    if (ch == '\t') { _col = (_col / 8 + 1) * 8; EnsureColumn(_col); break; }
                    if (ch < ' ') break;
                    Put(ch);
                    break;

                case 1: // after ESC
                    if (ch == '[') { _sequence.Clear(); _state = 2; }
                    else if (ch == ']') { _sequence.Clear(); _state = 3; }
                    else _state = 0;
                    break;

                case 2: // inside CSI, collecting parameters
                    if (ch >= 0x40 && ch <= 0x7e)
                    {
                        HandleControl(_sequence.ToString(), ch);
                        _state = 0;
                    }
                    else _sequence.Append(ch);
                    break;

                case 3: // OSC, ignored until BEL or ST
                    if (ch == '\a') _state = 0;
                    else if (ch == '\x1b') _state = 4;
                    break;

                case 4: // ESC inside OSC (ST terminator)
                    _state = ch == '\\' ? 0 : 3;
                    break;
            }
        }
    }

    private void HandleControl(string parameters, char command)
    {
        switch (command)
        {
            case 'm':
                ApplyColour(parameters);
                break;

            case 'H':
            case 'f':
            {
                var (row, column) = TwoNumbers(parameters);
                MoveTo(row - 1, column - 1);
                break;
            }

            case 'A': MoveTo(_row - Number(parameters, 1), _col); break;
            case 'B': MoveTo(_row + Number(parameters, 1), _col); break;
            case 'C':
                _col += Number(parameters, 1);
                EnsureColumn(_col);
                break;
            case 'D': _col = Math.Max(0, _col - Number(parameters, 1)); break;
            case 'E': MoveTo(_row + Number(parameters, 1), 0); break;
            case 'F': MoveTo(_row - Number(parameters, 1), 0); break;
            case 'G':
                _col = Math.Max(0, Number(parameters, 1) - 1);
                EnsureColumn(_col);
                break;
            case 'd': MoveTo(Number(parameters, 1) - 1, _col); break;

            case 'J':
                EraseDisplay(parameters.Length == 0 ? 0 : Number(parameters, 0));
                break;

            case 'K':
                EraseLine(parameters.Length == 0 ? 0 : Number(parameters, 0));
                break;

            case 'X':
                EraseChars(Number(parameters, 1));
                break;
        }
    }

    private static int Number(string parameters, int fallback)
    {
        if (parameters.Length == 0) return fallback;
        return int.TryParse(parameters, out var value) ? value : fallback;
    }

    private static (int Row, int Column) TwoNumbers(string parameters)
    {
        if (parameters.Length == 0) return (1, 1);
        var parts = parameters.Split(';');
        var row = int.TryParse(parts[0], out var r) && r > 0 ? r : 1;
        var column = parts.Length > 1 && int.TryParse(parts[1], out var c) && c > 0 ? c : 1;
        return (row, column);
    }

    private void ApplyColour(string parameters)
    {
        if (parameters.StartsWith('?')) return;
        var parts = parameters.Length == 0 ? new[] { "0" } : parameters.Split(';');
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var code)) continue;
            switch (code)
            {
                case 0: _bold = false; _foreground = Default; break;
                case 1: _bold = true; break;
                case 22: _bold = false; break;
                case >= 30 and <= 37: _foreground = Basic(code - 30, _bold); break;
                case 39: _foreground = Default; break;
                case >= 90 and <= 97: _foreground = Basic(code - 90, true); break;
                // background colours are ignored: the pane is intentionally dark
            }
        }
    }

    private static Color Basic(int index, bool bright) => index switch
    {
        0 => bright ? Color.FromArgb(150, 150, 158) : Color.FromArgb(88, 88, 96),
        1 => bright ? Color.FromArgb(255, 120, 120) : Color.FromArgb(200, 80, 80),
        2 => bright ? Color.FromArgb(120, 235, 140) : Color.FromArgb(80, 190, 100),
        3 => bright ? Color.FromArgb(250, 220, 110) : Color.FromArgb(210, 180, 80),
        4 => bright ? Color.FromArgb(120, 175, 255) : Color.FromArgb(90, 140, 210),
        5 => bright ? Color.FromArgb(230, 140, 240) : Color.FromArgb(190, 110, 200),
        6 => bright ? Color.FromArgb(120, 225, 235) : Color.FromArgb(80, 190, 200),
        _ => bright ? Color.FromArgb(240, 240, 245) : Color.FromArgb(200, 200, 208),
    };

    // ---------------------------------------------------------------- grid

    private Cell[] NewRow()
    {
        var row = new Cell[_cols];
        for (var i = 0; i < row.Length; i++) row[i] = new Cell { Ch = ' ', Fg = Default };
        return row;
    }

    private void EnsureColumn(int column)
    {
        if (column < _cols) return;

        var cols = Math.Max(column + 1, _cols + 32);
        for (var i = 0; i < _grid.Count; i++)
        {
            var old = _grid[i];
            var bigger = new Cell[cols];
            Array.Copy(old, bigger, old.Length);
            for (var c = old.Length; c < cols; c++) bigger[c] = new Cell { Ch = ' ', Fg = Default };
            _grid[i] = bigger;
        }
        _cols = cols;
    }

    private void Put(char ch)
    {
        EnsureColumn(_col);
        _grid[_row][_col] = new Cell { Ch = ch, Fg = _foreground };
        _col++;
        _screenDirty = true;
    }

    private void NewLine()
    {
        if (_row < _rows - 1) _row++;
        else ScrollUp();
        _screenDirty = true;
    }

    private void MoveTo(int row, int column)
    {
        _row = Math.Clamp(row, 0, _rows - 1);
        if (column < 0) column = 0;
        EnsureColumn(column);
        _col = column;
    }

    private void ScrollUp()
    {
        FlushRow(_grid[0]);
        for (var i = 1; i < _rows; i++) _grid[i - 1] = _grid[i];
        _grid[_rows - 1] = NewRow();
        _screenDirty = true;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 2:
            case 3:
                NewSection();
                break;

            case 0:
                for (var c = _col; c < _grid[_row].Length; c++)
                    _grid[_row][c] = new Cell { Ch = ' ', Fg = Default };
                for (var r = _row + 1; r < _rows; r++)
                    _grid[r] = NewRow();
                _screenDirty = true;
                break;

            case 1:
                for (var r = 0; r < _row; r++) _grid[r] = NewRow();
                for (var c = 0; c <= _col && c < _grid[_row].Length; c++)
                    _grid[_row][c] = new Cell { Ch = ' ', Fg = Default };
                _screenDirty = true;
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var row = _grid[_row];
        switch (mode)
        {
            case 0:
                for (var c = _col; c < row.Length; c++) row[c] = new Cell { Ch = ' ', Fg = Default };
                break;
            case 1:
                for (var c = 0; c <= _col && c < row.Length; c++) row[c] = new Cell { Ch = ' ', Fg = Default };
                break;
            case 2:
                for (var c = 0; c < row.Length; c++) row[c] = new Cell { Ch = ' ', Fg = Default };
                break;
        }
        _screenDirty = true;
    }

    private void EraseChars(int count)
    {
        var row = _grid[_row];
        var end = Math.Min(row.Length, _col + count);
        for (var c = _col; c < end; c++) row[c] = new Cell { Ch = ' ', Fg = Default };
        _screenDirty = true;
    }

    // ---------------------------------------------------------------- history

    /// <summary>`cls`: keep the finished screen in the log behind a divider.</summary>
    private void NewSection()
    {
        ArchiveScreen();

        if (_box.TextLength == 0 || !_contentSinceDivider) return;

        TrimTrailingBlankLine();
        _contentSinceDivider = false;
        _lastLineBlank = false;

        Append(new string('─', 64), Separator);
        _box.AppendText(Environment.NewLine);
        _screenStart = _box.TextLength;
    }

    private void ArchiveScreen()
    {
        var last = LastNonBlankRow();
        if (last >= 0)
        {
            var started = false;
            for (var r = 0; r <= last; r++)
            {
                var blank = LastNonSpace(_grid[r]) == 0;
                if (!started && blank) continue;
                started = true;
                FlushRow(_grid[r]);
            }
        }

        // If the screen was erased pixel-by-pixel there is nothing to flush, but a
        // previously rendered copy may still sit at the end of the box.
        RemoveScreen();
        ClearGrid();
    }

    private void ClearGrid()
    {
        for (var i = 0; i < _rows; i++) _grid[i] = NewRow();
        _row = 0;
        _col = 0;
        _screenDirty = true;
    }

    /// <summary>Appends one screen row to the log, collapsing repeated blank lines.</summary>
    private void FlushRow(Cell[] row)
    {
        var length = LastNonSpace(row);

        if (length == 0)
        {
            if (_lastLineBlank) return;
            RemoveScreen();
            _box.AppendText(Environment.NewLine);
            _lastLineBlank = true;
            _screenStart = _box.TextLength;
            return;
        }

        RemoveScreen();
        AppendRuns(row, length);
        _box.AppendText(Environment.NewLine);
        _lastLineBlank = false;
        _contentSinceDivider = true;
        _screenStart = _box.TextLength;
        TrimHistory();
    }

    private int LastNonBlankRow()
    {
        for (var r = _rows - 1; r >= 0; r--)
            if (LastNonSpace(_grid[r]) > 0) return r;
        return -1;
    }

    private int LastNonSpace(Cell[] row)
    {
        for (var i = row.Length - 1; i >= 0; i--)
            if (row[i].Ch != ' ') return i + 1;
        return 0;
    }

    // ---------------------------------------------------------------- output

    private void RenderScreen()
    {
        if (!_screenDirty) return;
        _screenDirty = false;

        RemoveScreen();

        var last = LastNonBlankRow();
        if (last < 0)
        {
            _screenStart = _box.TextLength;
            return;
        }

        var started = false;
        var blankRun = false;

        for (var r = 0; r <= last; r++)
        {
            var length = LastNonSpace(_grid[r]);
            if (length == 0)
            {
                // Drop leading blanks and squash runs of empty rows. ConPTY produces
                // one newline per row when it moves the cursor down the screen.
                if (!started || blankRun) continue;
                blankRun = true;
                _box.AppendText(Environment.NewLine);
                continue;
            }

            started = true;
            blankRun = false;
            AppendRuns(_grid[r], length);
            _box.AppendText(Environment.NewLine);
        }

        _screenLength = _box.TextLength - _screenStart;
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
    }

    private void AppendRuns(Cell[] row, int length)
    {
        var index = 0;
        while (index < length)
        {
            var colour = row[index].Fg;
            var start = index;
            while (index < length && row[index].Fg == colour) index++;

            var chars = new char[index - start];
            for (var i = 0; i < chars.Length; i++) chars[i] = row[start + i].Ch;
            Append(new string(chars), colour);
        }
    }

    private void Append(string text, Color color)
    {
        if (text.Length == 0) return;
        _box.SelectionStart = _box.TextLength;
        _box.SelectionLength = 0;
        _box.SelectionColor = color;
        _box.AppendText(text);
        _box.SelectionColor = Default;
    }

    private void RemoveScreen()
    {
        if (_screenLength > 0) DeleteRange(_screenStart, _screenLength);
        _screenLength = 0;
        _screenStart = _box.TextLength;
    }

    /// <summary>Drops a trailing empty line so a divider or status line sits tight under the text.</summary>
    private void TrimTrailingBlankLine()
    {
        if (!_lastLineBlank || _screenLength > 0) return;
        var text = _box.Text;
        if (text.Length < 2 || !text.EndsWith("\n\n")) return;

        DeleteRange(_box.TextLength - 1, 1);
        _lastLineBlank = false;
        _screenStart = _box.TextLength;
    }

    private void TrimHistory()
    {
        if (_box.TextLength <= MaxHistory) return;

        var take = Math.Min(HistoryTrim, _screenStart);
        if (take <= 0) return;

        DeleteRange(0, take);
        _screenStart = _box.TextLength;
    }

    /// <summary>
    /// Removes text from the pane. A read-only RichTextBox silently ignores
    /// SelectedText assignments, so read-only is lifted for the edit itself.
    /// </summary>
    private void DeleteRange(int start, int length)
    {
        if (length <= 0 || start < 0) return;

        var readOnly = _box.ReadOnly;
        try
        {
            if (readOnly) _box.ReadOnly = false;
            _box.Select(start, length);
            _box.SelectedText = string.Empty;
        }
        finally
        {
            if (readOnly) _box.ReadOnly = true;
        }
    }
}
