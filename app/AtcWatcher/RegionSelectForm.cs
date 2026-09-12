using System.Drawing;
using System.Windows.Forms;
using AtcWatcher.Core;

namespace AtcWatcher;

/// <summary>Full-desktop dimmed overlay where the user drags a box around the ATC panel.</summary>
public sealed class RegionSelectForm : Form
{
    private Point? _start;
    private Rectangle _current;
    public Rectangle? Result { get; private set; }

    public RegionSelectForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = ScreenCapture.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.4;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;

        var label = new Label
        {
            Text = "Drag a box around the ATC panel, including the numbered replies.   Esc = cancel",
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            AutoSize = true,
        };
        Controls.Add(label);
        Load += (_, _) => label.Location = new Point((Width - label.Width) / 2, 40);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _start = e.Location;
        _current = new Rectangle(e.Location, Size.Empty);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_start is null) return;
        var s = _start.Value;
        _current = new Rectangle(Math.Min(s.X, e.X), Math.Min(s.Y, e.Y), Math.Abs(e.X - s.X), Math.Abs(e.Y - s.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_start is null) return;
        _start = null;
        if (_current.Width > 20 && _current.Height > 20)
        {
            var r = _current;
            r.Offset(Bounds.Left, Bounds.Top);   // client -> virtual-screen coordinates
            Result = r;
        }
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_current.Width > 0 && _current.Height > 0)
        {
            using var pen = new Pen(Color.Red, 3);
            e.Graphics.DrawRectangle(pen, _current);
        }
    }

    public static Rectangle? Pick()
    {
        using var f = new RegionSelectForm();
        f.ShowDialog();
        return f.Result;
    }
}
