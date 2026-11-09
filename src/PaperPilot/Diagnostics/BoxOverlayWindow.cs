using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperPilot.Core;
using PaperPilot.Interop;

namespace PaperPilot.Diagnostics;

/// <summary>
/// Debug-only, click-through, never-focused overlay that outlines the OCR lines directly on
/// screen. If the boxes sit exactly on the text, the screen coordinates are right.
/// </summary>
public sealed class BoxOverlayWindow : Window
{
    private static readonly Brush LineStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x59, 0xFD)));
    private static readonly Brush LineFill = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x59, 0xFD)));
    private static readonly Brush AreaStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xAB, 0xBF, 0xEA)));

    private readonly Canvas _canvas = new();
    private IntPtr _hwnd;

    public BoxOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _canvas;
        Left = -10000; Top = -10000; Width = 1; Height = 1;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    public void ShowResult(ScreenReadResult result)
    {
        if (!result.IsOk) return;
        if (!IsVisible) Show();

        var area = result.ContentRect;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, area.X, area.Y, area.Width, area.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        // Canvas units are DIPs; our rectangles are physical pixels relative to the screen.
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double s = (dpi == 0 ? 96 : dpi) / 96.0;

        _canvas.Children.Clear();
        AddRect(0, 0, area.Width / s, area.Height / s, AreaStroke, null, 2);
        foreach (var line in result.Lines)
        {
            var r = line.Screen;
            AddRect((r.X - area.X) / s, (r.Y - area.Y) / s, r.Width / s, r.Height / s, LineStroke, LineFill, 1);
        }
    }

    private void AddRect(double x, double y, double w, double h, Brush stroke, Brush? fill, double thickness)
    {
        var rect = new Rectangle { Width = Math.Max(1, w), Height = Math.Max(1, h), Stroke = stroke, Fill = fill, StrokeThickness = thickness };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        _canvas.Children.Add(rect);
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
