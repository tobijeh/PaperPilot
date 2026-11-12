using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using PaperPilot.Core;
using PaperPilot.Interop;

namespace PaperPilot.UI;

/// <summary>
/// The floating bubble: the only thing on screen while Paper Pilot is on. Always on top, never
/// takes focus (not even while dragged), hidden when off. Sits at the top of the screen you
/// preach from; drag it anywhere and it remembers the spot for that screen.
/// </summary>
public sealed class BubbleWindow : Window
{
    // The window is a fixed transparent strip; the bubble is right-aligned inside it and grows
    // to the left when a word appears. Fully transparent pixels are click-through.
    private const double StripWidth = 300;

    private readonly AppSettings _settings;
    private readonly BubbleView _bubble = new();
    private IntPtr _hwnd;
    private PilotState _state = PilotState.Off;

    private bool _dragging;
    private POINT _dragStartCursor;
    private RECT _dragStartWindow;

    public BubbleWindow(AppSettings settings)
    {
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Title = "Paper Pilot";
        Left = -10000;
        Top = -10000;
        Width = StripWidth;
        Content = new Grid { Children = { _bubble } };
        ApplyAppearance();

        _bubble.MouseLeftButtonDown += OnMouseDown;
        _bubble.MouseMove += OnMouseMove;
        _bubble.MouseLeftButtonUp += OnMouseUp;
        _bubble.LostMouseCapture += (_, _) => EndDrag();
    }

    public PilotState State => _state;

    /// <summary>Re-reads size and idle opacity from settings.</summary>
    public void ApplyAppearance()
    {
        _bubble.ApplySize(_settings.PillSize);
        Height = _bubble.Height + 2;
        _bubble.BeginAnimation(OpacityProperty, null);
        _bubble.Opacity = _settings.PillIdleOpacity;
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

    /// <summary>Microphone level (0..1) for the trail behind the plane.</summary>
    public void PushLevel(double level) => _bubble.PushLevel(level);

    /// <summary>Shows the new state, with a brief word if enabled. Off hides the bubble.</summary>
    public void SetState(PilotState state, string? word, IntPtr targetHwnd)
    {
        if (state == PilotState.Off)
        {
            _state = state;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => { if (_state == PilotState.Off) Hide(); };
            _bubble.BeginAnimation(OpacityProperty, fade);
            return;
        }

        bool wasHidden = !IsVisible;
        if (wasHidden)
        {
            Show();
            PlaceOnMonitorOf(targetHwnd);
        }
        if (state == _state && !wasHidden && word is null) return;
        _state = state;
        _bubble.SetState(state, word, _settings.ShowStateWords);
        Brighten();
    }

    /// <summary>Brighten to full for ~1.5 s, then fade back to the idle opacity.</summary>
    private void Brighten()
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))));
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(_settings.PillIdleOpacity, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600)), new SineEase()));
        _bubble.BeginAnimation(OpacityProperty, anim);
    }

    // ---- Placement -----------------------------------------------------------------------------

    /// <summary>Puts the bubble back at its default spot on the current screen.</summary>
    public void ResetPosition(IntPtr targetHwnd)
    {
        _settings.PillPositions.Clear();
        _settings.Save();
        if (IsVisible) PlaceOnMonitorOf(targetHwnd);
    }

    public void PlaceOnMonitorOf(IntPtr targetHwnd)
    {
        if (_hwnd == IntPtr.Zero) return;
        var monitor = targetHwnd != IntPtr.Zero
            ? NativeMethods.MonitorFromWindow(targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : NativeMethods.MonitorFromPoint(new POINT(0, 0), NativeMethods.MONITOR_DEFAULTTONEAREST);
        var (id, work, dpi) = NativeMethods.GetMonitor(monitor);
        double scale = dpi / 96.0;
        int stripW = (int)Math.Round(StripWidth * scale);
        int h = (int)Math.Round(Height * scale);

        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int bubbleW = (int)Math.Round(_bubble.DesiredSize.Width * scale);

        int right, top;
        if (_settings.PillPositions.TryGetValue(id, out var saved))
        {
            right = work.Left + (int)Math.Round(saved.X * work.Width);
            top = work.Top + (int)Math.Round(saved.Y * work.Height);
        }
        else
        {
            int inset = (int)Math.Round(14 * scale);
            right = _settings.BubblePosition == BubblePosition.TopCenter
                ? work.Left + work.Width / 2 + bubbleW / 2
                : work.Right - inset;
            top = work.Top + inset;
        }
        right = Math.Clamp(right, work.Left + bubbleW, work.Right);
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - h));
        MoveTo(right - stripW, top);
    }

    private void MoveTo(int x, int y) =>
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            0x0001 /* SWP_NOSIZE */ | NativeMethods.SWP_NOACTIVATE);

    // ---- Dragging (manual, so the window is never activated) -------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out _dragStartCursor)) return;
        NativeMethods.GetWindowRect(_hwnd, out _dragStartWindow);
        _dragging = _bubble.CaptureMouse();
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
        _bubble.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;

        // Remember where the bubble's right edge and top now sit on this screen.
        NativeMethods.GetWindowRect(_hwnd, out var rc);
        var monitor = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var (id, work, _) = NativeMethods.GetMonitor(monitor);
        if (work.Width <= 0 || work.Height <= 0) return;
        _settings.PillPositions[id] = new PillPosition(
            (rc.Right - work.Left) / (double)work.Width,
            (rc.Top - work.Top) / (double)work.Height);
        _settings.Save();
    }
}
