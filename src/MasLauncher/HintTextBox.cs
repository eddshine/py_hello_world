using System.Drawing;
using System.Windows.Forms;

namespace MasLauncher;

/// <summary>
/// Single-line answer box with an indented hint. WinForms draws its own placeholder flush
/// against the client edge and ignores the edit margins, so the hint is painted here at the
/// same inset as typed text (see <c>EM_SETMARGINS</c> in <see cref="MainForm"/>).
/// </summary>
internal sealed class HintTextBox : TextBox
{
    private const int Inset = 10;
    private const int WmPaint = 0x000F;

    private static readonly Color HintColor = Color.FromArgb(132, 132, 146);

    private string _hint = string.Empty;

    /// <summary>Text shown while the box is empty and unfocused.</summary>
    public string Hint
    {
        get => _hint;
        set
        {
            var next = value ?? string.Empty;
            if (_hint == next) return;
            _hint = next;
            Invalidate();
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmPaint || _hint.Length == 0 || TextLength != 0 || Focused) return;

        using var g = Graphics.FromHwnd(Handle);
        var bounds = new Rectangle(Inset, 0, Math.Max(ClientSize.Width - Inset - 2, 0), ClientSize.Height);
        TextRenderer.DrawText(
            g,
            _hint,
            Font,
            bounds,
            HintColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }
}
