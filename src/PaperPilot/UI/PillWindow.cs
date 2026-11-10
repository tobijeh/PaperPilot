using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PaperPilot.Core;
using PaperPilot.Interop;

namespace PaperPilot.UI;

/// <summary>
/// The tiny status pill. Shape and colour only: no text, icons or buttons. Always on top,
/// never takes focus (not even while dragged).
/// </summary>
public sealed class PillWindow : Window
{
    private static readonly Color Blue = Color.FromRgb(0x00, 0x59, 0xFD);
    private static readonly Color Periwinkle = Color.FromRgb(0xAB, 0xBF, 0xEA);
    private static readonly Color Navy = Color.FromRgb(0x0D, 0x27, 0x68);
    private static readonly Color Grey = Color.FromRgb(0x8A, 0x8F, 0x98);
    // Almost-transparent rather than transparent: fully transparent pixels of a layered window
    // are click-through, which would make the hollow (Paused) pill impossible to drag.
    private static readonly Color Hollow = Color.FromArgb(0x02, 0xFF, 0xFF, 0xFF);

    private readonly AppSettings _settings;
    private readonly Rectangle _shape;
    private readonly SolidColorBrush _fill = new(Periwinkle);
    private readonly SolidColorBrush _stroke = new(Navy);
    private IntPtr _hwnd;
    private PilotState _state = PilotState.Off;

    private bool _dragging;
    private POINT _dragStartCursor;
    private RECT _dragStartWindow;

    public PillWindow(AppSettings settings)
    {
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Title = "Paper Pilot";
        Left = -10000;
        Top = -10000;

        _shape = new Rectangle
        {
            Fill = _fill,
            Stroke = _stroke,
            StrokeThickness = 1,
            Opacity = settings.PillIdleOpacity,
            SnapsToDevicePixels = true,
        };
        Content = _shape;
        ApplySize();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) => EndDrag();
    }

    public PilotState State => _state;

    public void ApplySize()
    {
        (double w, double h) = _settings.PillSize == PillSize.Medium ? (48.0, 18.0) : (36.0, 14.0);
        Width = w;
        Height = h;
        _shape.RadiusX = _shape.RadiusY = h / 2;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    // ---- State ---------------------------------------------------------------------------------

    /// <summary>Changes the pill's look. Off hides it. Debounce happens in the state machine, not here.</summary>
    public void SetState(PilotState state, IntPtr targetHwnd)
    {
        if (state == PilotState.Off)
        {
            _state = state;
            Hide();
            return;
        }

        bool wasHidden = !IsVisible;
        if (wasHidden)
        {
            Show();
            PlaceOnMonitorOf(targetHwnd);
        }

        if (state == _state && !wasHidden) return;
        _state = state;

        var (fill, dashed) = state switch
        {
            PilotState.Following => (Blue, false),
            PilotState.Holding => (Periwinkle, false),
            PilotState.Paused => (Hollow, false),
            PilotState.Problem => (Grey, true),
            _ => (Periwinkle, false),
        };

        if (wasHidden) _fill.Color = fill;
        else _fill.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(fill, TimeSpan.FromMilliseconds(350)) { EasingFunction = new SineEase() });
        _shape.StrokeDashArray = dashed ? new DoubleCollection { 2.5, 1.5 } : null;
        _shape.StrokeThickness = dashed ? 1.25 : 1;

        Brighten(pulse: state == PilotState.Problem);
    }

    /// <summary>Brighten to full for ~1.5 s, then fade back to the idle opacity. Problem gets one slow pulse first.</summary>
    private void Brighten(bool pulse)
    {
        double idle = _settings.PillIdleOpacity;
        var anim = new DoubleAnimationUsingKeyFrames();
        var t = TimeSpan.Zero;
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(t += TimeSpan.FromMilliseconds(250))));
        if (pulse)
        {
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(t += TimeSpan.FromMilliseconds(700)), new SineEase()));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(t += TimeSpan.FromMilliseconds(700)), new SineEase()));
        }
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(t += TimeSpan.FromMilliseconds(1500))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(idle, KeyTime.FromTimeSpan(t + TimeSpan.FromMilliseconds(700)), new SineEase()));
        _shape.BeginAnimation(OpacityProperty, anim);
    }

    public void ApplyIdleOpacity()
    {
        _shape.BeginAnimation(OpacityProperty, null);
        _shape.Opacity = _settings.PillIdleOpacity;
    }

    // ---- Placement -----------------------------------------------------------------------------

    private void PlaceOnMonitorOf(IntPtr targetHwnd)
    {
        var monitor = targetHwnd != IntPtr.Zero
            ? NativeMethods.MonitorFromWindow(targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : NativeMethods.MonitorFromPoint(new POINT(0, 0), NativeMethods.MONITOR_DEFAULTTONEAREST);
        var (id, work, dpi) = NativeMethods.GetMonitor(monitor);
        double scale = dpi / 96.0;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);

        int x, y;
        if (_settings.PillPositions.TryGetValue(id, out var saved))
        {
            x = work.Left + (int)Math.Round(saved.X * work.Width);
            y = work.Top + (int)Math.Round(saved.Y * work.Height);
        }
        else
        {
            int inset = (int)Math.Round(20 * scale);
            x = work.Right - inset - w;
            y = work.Top + inset;
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        MoveTo(x, y);
    }

    private void MoveTo(int x, int y) =>
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            0x0001 /* SWP_NOSIZE */ | NativeMethods.SWP_NOACTIVATE);

    // ---- Dragging (manual, so the window is never activated) -------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out _dragStartCursor)) return;
        NativeMethods.GetWindowRect(_hwnd, out _dragStartWindow);
        _dragging = CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || !NativeMethods.GetCursorPos(out var p)) return;
        MoveTo(_dragStartWindow.Left + p.X - _dragStartCursor.X, _dragStartWindow.Top + p.Y - _dragStartCursor.Y);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;

        // Remember the position for the monitor the pill now sits on.
        NativeMethods.GetWindowRect(_hwnd, out var rc);
        var monitor = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var (id, work, _) = NativeMethods.GetMonitor(monitor);
        if (work.Width <= 0 || work.Height <= 0) return;
        _settings.PillPositions[id] = new PillPosition(
            (rc.Left - work.Left) / (double)work.Width,
            (rc.Top - work.Top) / (double)work.Height);
        _settings.Save();
    }
}
