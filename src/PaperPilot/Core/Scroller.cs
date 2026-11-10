using System.Runtime.InteropServices;
using PaperPilot.Diagnostics;
using PaperPilot.Interop;

namespace PaperPilot.Core;

public enum ScrollMethod
{
    /// <summary>Not calibrated yet.</summary>
    None,
    /// <summary>PostMessage WM_MOUSEWHEEL to the document child window (e.g. Chrome_RenderWidgetHostHWND).</summary>
    PostToDocument,
    /// <summary>PostMessage WM_MOUSEWHEEL to the top-level window.</summary>
    PostToWindow,
    /// <summary>SendInput wheel events (cursor briefly moved over the document if needed, then restored).</summary>
    SendInput,
}

public sealed record CalibrationResult(bool Success, ScrollMethod Method, double PixelsPerNotch, string Message);

/// <summary>
/// Scrolls the target window without taking focus. Movement is split into small eased wheel steps
/// and can be cancelled instantly from any thread (e.g. the input hook).
/// </summary>
public sealed class Scroller
{
    public const int Notch = 120;
    private const int CalibrationSettleMs = 500;

    private readonly object _gate = new();
    private TargetWindow? _target;
    private CancellationTokenSource? _current;

    public ScrollMethod Method { get; private set; } = ScrollMethod.None;

    /// <summary>Pixels moved per wheel-delta unit (×120 = pixels per notch). 0 until calibrated.</summary>
    public double PixelsPerDelta { get; private set; }

    public bool IsCalibrated => Method != ScrollMethod.None && PixelsPerDelta > 0;
    public bool IsScrolling { get; private set; }

    /// <summary>Raised (on the UI thread) when a scroll starts or stops.</summary>
    public event Action<bool>? ScrollingChanged;

    public void SetTarget(TargetWindow? target)
    {
        CancelNow();
        _target = target;
        Method = ScrollMethod.None;
        PixelsPerDelta = 0;
    }

    /// <summary>Stops any scroll in progress immediately. Safe to call from any thread.</summary>
    public void CancelNow()
    {
        lock (_gate)
        {
            _current?.Cancel();
        }
    }

    /// <summary>Scroll by a number of screen pixels (positive = down). Needs calibration.</summary>
    public Task<bool> ScrollPixelsAsync(int pixels, int? durationMs = null)
    {
        if (!IsCalibrated) throw new InvalidOperationException("Scrolling isn't calibrated yet.");
        int delta = -(int)Math.Round(pixels / PixelsPerDelta);
        return ScrollDeltaAsync(delta, durationMs ?? ScrollPlanner.DurationFor(pixels), Method);
    }

    /// <summary>
    /// Sends <paramref name="wheelDelta"/> (negative = down, 120 per notch) as eased small steps.
    /// Returns false if cancelled or nothing could be sent.
    /// </summary>
    public async Task<bool> ScrollDeltaAsync(int wheelDelta, int durationMs, ScrollMethod? method = null)
    {
        var target = _target;
        var how = method ?? (Method == ScrollMethod.None ? ScrollMethod.PostToDocument : Method);
        if (target is null || !target.IsAlive || wheelDelta == 0) return false;

        CancellationTokenSource cts;
        lock (_gate)
        {
            _current?.Cancel();
            cts = _current = new CancellationTokenSource();
        }

        var area = target.GetContentArea();
        var steps = ScrollPlanner.Plan(wheelDelta, durationMs);
        SetScrolling(true);
        try
        {
            foreach (var step in steps)
            {
                if (cts.IsCancellationRequested) return false;
                if (!SendStep(target, area, step, how)) return false;
                try { await Task.Delay(ScrollPlanner.StepIntervalMs, cts.Token); }
                catch (TaskCanceledException) { return false; }
            }
            return true;
        }
        finally
        {
            bool anotherRunning;
            lock (_gate)
            {
                if (_current == cts) _current = null;
                anotherRunning = _current is not null;
            }
            cts.Dispose();
            if (!anotherRunning) SetScrolling(false);
        }
    }

    private void SetScrolling(bool value)
    {
        if (IsScrolling == value) return;
        IsScrolling = value;
        ScrollingChanged?.Invoke(value);
    }

    private static bool SendStep(TargetWindow target, ContentArea area, int delta, ScrollMethod how)
    {
        var r = area.ScreenRect;
        int x = r.Left + r.Width / 2, y = r.Top + r.Height / 2;

        switch (how)
        {
            case ScrollMethod.PostToDocument:
            case ScrollMethod.PostToWindow:
            {
                var hwnd = how == ScrollMethod.PostToDocument ? area.Hwnd : target.Hwnd;
                var wParam = new IntPtr((long)(ushort)(short)delta << 16);
                var lParam = new IntPtr(((long)(ushort)(short)y << 16) | (ushort)(short)x);
                if (!NativeMethods.PostMessage(hwnd, NativeMethods.WM_MOUSEWHEEL, wParam, lParam))
                {
                    Log.Warn($"Scroller: PostMessage failed (error {Marshal.GetLastWin32Error()}).");
                    return false;
                }
                return true;
            }

            case ScrollMethod.SendInput:
                return SendWheelInput(target, r, x, y, delta);

            default:
                return false;
        }
    }

    /// <summary>
    /// Wheel via SendInput. If the cursor isn't over the document, it's moved there and back inside
    /// the same SendInput batch so the user's cursor never stays moved.
    /// </summary>
    private static bool SendWheelInput(TargetWindow target, RECT area, int x, int y, int delta)
    {
        NativeMethods.GetCursorPos(out var cursor);
        bool overDocument = cursor.X >= area.Left && cursor.X < area.Right && cursor.Y >= area.Top && cursor.Y < area.Bottom
                            && NativeMethods.RootWindowFromPoint(cursor) == target.Hwnd;

        var inputs = new List<NativeMethods.INPUT>(3);
        if (!overDocument) inputs.Add(MoveTo(x, y));
        inputs.Add(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.INPUTUNION
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    mouseData = delta,
                    dwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
                    dwExtraInfo = InputMonitor.OwnInputTag,
                },
            },
        });
        if (!overDocument) inputs.Add(MoveTo(cursor.X, cursor.Y));

        var arr = inputs.ToArray();
        uint sent = NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != arr.Length)
        {
            Log.Warn($"Scroller: SendInput sent {sent}/{arr.Length} (error {Marshal.GetLastWin32Error()}).");
            return false;
        }
        return true;
    }

    private static NativeMethods.INPUT MoveTo(int x, int y)
    {
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(2, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.INPUTUNION
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
                    dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1)),
                    dwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = InputMonitor.OwnInputTag,
                },
            },
        };
    }

    /// <summary>
    /// Finds a scroll method that works for this window and measures pixels per wheel notch:
    /// scrolls one notch down, measures with OCR, then scrolls back so the page ends where it was.
    /// </summary>
    public async Task<CalibrationResult> CalibrateAsync(ScreenReader reader)
    {
        var target = _target;
        if (target is null) return Fail("No target window.");

        var methods = new List<ScrollMethod>();
        if (target.GetContentArea().Hwnd != target.Hwnd) methods.Add(ScrollMethod.PostToDocument);
        methods.Add(ScrollMethod.PostToWindow);
        methods.Add(ScrollMethod.SendInput);

        bool sawText = false;
        foreach (var method in methods)
        {
            var before = await reader.ReadAsync();
            if (!before.IsOk) return Fail($"Can't read the window: {before.Message}");
            if (before.Lines.Count < 2) continue;
            sawText = true;

            if (!await ScrollDeltaAsync(-Notch, 200, method)) return Fail("Calibration was interrupted.");
            await Task.Delay(CalibrationSettleMs);
            var after = await reader.ReadAsync();
            if (!after.IsOk) return Fail($"Can't read the window: {after.Message}");

            var shift = ScrollMeasurer.Measure(before.Lines, after.Lines);
            Log.Info($"Calibration: {method}: {(shift is null ? "no measurable movement" : shift.ToString())}.");
            if (shift is { Pixels: >= 5 } s)
            {
                Method = method;
                PixelsPerDelta = s.Pixels / (double)Notch;
                // Put the page back where the user left it.
                await ScrollDeltaAsync(Notch, 200, method);
                await Task.Delay(CalibrationSettleMs);
                var msg = $"{method}: {s.Pixels} px per wheel notch.";
                Log.Info($"Calibration: using {msg}");
                return new CalibrationResult(true, method, s.Pixels, msg);
            }
        }

        return Fail(sawText
            ? "No method moved the page. Is it already at the very bottom? Scroll up a little and try again."
            : "Not enough text on screen to measure scrolling.");

        CalibrationResult Fail(string message)
        {
            Log.Warn($"Calibration failed: {message}");
            return new CalibrationResult(false, ScrollMethod.None, 0, message);
        }
    }

    /// <summary>Refines pixels-per-delta after a scroll whose real movement was measured.</summary>
    public void Refine(int requestedDelta, int measuredPixels)
    {
        if (requestedDelta == 0 || measuredPixels <= 0 || !IsCalibrated) return;
        double observed = measuredPixels / (double)Math.Abs(requestedDelta);
        // Ignore wild outliers (page end reached, content changed); otherwise blend in gently.
        if (observed < PixelsPerDelta * 0.5 || observed > PixelsPerDelta * 2) return;
        PixelsPerDelta = PixelsPerDelta * 0.7 + observed * 0.3;
    }
}
