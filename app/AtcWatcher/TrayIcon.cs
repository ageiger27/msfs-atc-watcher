using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AtcWatcher;

/// <summary>Notification-area icon with an Armed / Disarmed colour and a small menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly Icon _armedIcon = Make(Color.FromArgb(55, 200, 113));
    private readonly Icon _disarmedIcon = Make(Color.FromArgb(120, 130, 145));
    private readonly ToolStripMenuItem _toggle = new("Disarm");

    public event Action? ShowRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        var show = new ToolStripMenuItem("Show ATC Watcher");
        show.Click += (_, _) => ShowRequested?.Invoke();
        _toggle.Click += (_, _) => ToggleRequested?.Invoke();
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(show);
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _icon.Visible = true;
        SetArmed(true);
    }

    public void SetArmed(bool armed)
    {
        _icon.Icon = armed ? _armedIcon : _disarmedIcon;
        _icon.Text = armed ? "ATC Watcher — armed" : "ATC Watcher — disarmed";
        _toggle.Text = armed ? "Disarm" : "Arm";
    }

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    private static Icon Make(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("ATC", font);
            g.DrawString("ATC", font, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
