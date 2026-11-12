using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperPilot.Core;
using PaperPilot.Interop;
using Forms = System.Windows.Forms;

namespace PaperPilot.UI;

/// <summary>
/// The tray icon: Paper Pilot lives here. A cream plane on Paper Pilot Blue, legible at 16 px.
/// Grey when switched off; a small dot when something needs attention or an update is ready.
/// Menu: on/off, Settings, updates, Quit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _onItem;
    private readonly Forms.ToolStripMenuItem _updateItem;
    private System.Drawing.Icon? _current;
    private string _currentKey = "";

    public TrayIcon(AppController app, Action openSettings, Action quit)
    {
        _app = app;
        _openSettings = openSettings;
        _quit = quit;

        _onItem = new Forms.ToolStripMenuItem("Paper Pilot is on") { CheckOnClick = false };
        _onItem.Click += (_, _) => _app.SetEnabled(!_app.Settings.Enabled);
        var settings = new Forms.ToolStripMenuItem("Settings");
        settings.Font = new System.Drawing.Font(settings.Font, System.Drawing.FontStyle.Bold);
        settings.Click += (_, _) => _openSettings();
        _updateItem = new Forms.ToolStripMenuItem("Check for updates");
        _updateItem.Click += async (_, _) =>
        {
            if (_app.Updates.Status == UpdateStatus.Ready)
            {
                if (_app.State != PilotState.Off) _app.TurnOff();
                if (_app.Updates.RestartToUpdate()) _quit();
            }
            else
            {
                await _app.Updates.CheckAsync(userRequested: true);
            }
        };
        var quitItem = new Forms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => _quit();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(new Forms.ToolStripItem[] { _onItem, settings, _updateItem, new Forms.ToolStripSeparator(), quitItem });

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _openSettings(); };

        _app.Changed += Refresh;
        _app.Updates.Changed += Refresh;
        ThemeManager.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        bool enabled = _app.Settings.Enabled;
        bool attention = _app.Attention.Count > 0 || _app.State == PilotState.Problem;
        bool updateReady = _app.Updates.Status == UpdateStatus.Ready;

        _onItem.Checked = enabled;
        _updateItem.Text = _app.Updates.Status switch
        {
            UpdateStatus.Ready => $"Restart to update ({_app.Updates.Available?.Version})",
            UpdateStatus.Checking => "Checking for updates…",
            UpdateStatus.Downloading => $"Downloading update… {_app.Updates.Progress:P0}",
            UpdateStatus.NotAvailable => "Updates: not available in this copy",
            _ => "Check for updates",
        };
        _updateItem.Enabled = _app.Updates.Status is not (UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.NotAvailable);

        var hotkey = _app.Settings.Hotkeys.GetValueOrDefault(HotkeyAction.Toggle);
        string tip = !enabled ? "Paper Pilot is off"
            : _app.State switch
            {
                PilotState.Off => hotkey is null ? "Paper Pilot · ready" : $"Paper Pilot · press {hotkey} in your notes",
                PilotState.Problem => "Paper Pilot · needs attention",
                var s => $"Paper Pilot · {s.ToString().ToLowerInvariant()}",
            };
        if (attention && _app.State != PilotState.Problem) tip += " · needs attention";
        if (updateReady) tip += " · update ready";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        string key = $"{enabled}{attention}{updateReady}{ThemeManager.IsDark}";
        if (key == _currentKey) return;
        _currentKey = key;
        var old = _current;
        _current = RenderIcon(enabled, attention, updateReady);
        _icon.Icon = _current;
        old?.Dispose();
    }

    /// <summary>Draws the tray icon at the system's small-icon size (physical pixels).</summary>
    private static System.Drawing.Icon RenderIcon(bool enabled, bool attention, bool updateReady)
    {
        int size = Math.Max(16, NativeMethods.GetSystemMetrics(49 /* SM_CXSMICON */));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = enabled ? ThemeManager.Blue : ThemeManager.Grey;
            double r = size * 0.24;
            dc.DrawRoundedRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, size, size), r, r);
            double pad = size * 0.1;
            PlaneGlyph.Draw(dc, new SolidColorBrush(ThemeManager.Cream), new Rect(pad, pad, size - 2 * pad, size - 2 * pad));

            if (attention || updateReady)
            {
                // Small dot in the top-right corner: navy-ringed periwinkle for attention,
                // cream-ringed navy for "update ready".
                double d = size * 0.42;
                var center = new Point(size - d / 2, d / 2);
                var fill = attention ? ThemeManager.Periwinkle : ThemeManager.Navy;
                var ring = attention ? ThemeManager.Navy : ThemeManager.Cream;
                dc.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(ring), Math.Max(1, size / 16.0)), center, d / 2, d / 2);
            }
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        using var gdiBitmap = new System.Drawing.Bitmap(ms);
        var hIcon = gdiBitmap.GetHicon();
        using var tmp = System.Drawing.Icon.FromHandle(hIcon);
        var icon = (System.Drawing.Icon)tmp.Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _app.Changed -= Refresh;
        _app.Updates.Changed -= Refresh;
        ThemeManager.Changed -= Refresh;
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
