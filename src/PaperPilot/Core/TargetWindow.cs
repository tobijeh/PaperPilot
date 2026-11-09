using System.Diagnostics;
using PaperPilot.Interop;

namespace PaperPilot.Core;

/// <summary>
/// The window being preached from. Locked once on activation; all coordinates are physical
/// screen pixels (the app is per-monitor DPI aware v2).
/// </summary>
public sealed class TargetWindow
{
    // Child windows that hold the actual document in common apps. The largest visible match wins.
    private static readonly string[] DocumentChildClasses =
    {
        "Chrome_RenderWidgetHostHWND", // Chrome, Edge (incl. Edge's PDF viewer), other Chromium apps
        "_WwG",                        // Microsoft Word document pane
        "AVL_AVView",                  // Adobe Acrobat / Reader page view
    };

    private TargetWindow(IntPtr hwnd)
    {
        Hwnd = hwnd;
        Title = NativeMethods.GetWindowTitle(hwnd);
        ClassName = NativeMethods.GetWindowClass(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        ProcessId = (int)pid;
        try { ProcessName = Process.GetProcessById(ProcessId).ProcessName; }
        catch { ProcessName = "?"; }
    }

    public IntPtr Hwnd { get; }
    public string Title { get; private set; }
    public string ClassName { get; }
    public int ProcessId { get; }
    public string ProcessName { get; }

    public bool IsAlive => NativeMethods.IsWindow(Hwnd);
    public bool IsMinimized => NativeMethods.IsIconic(Hwnd);

    /// <summary>Locks onto the current foreground window, unless it belongs to Paper Pilot itself.</summary>
    public static TargetWindow? FromForeground(out string? reason)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            reason = "No foreground window.";
            return null;
        }
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root != IntPtr.Zero) hwnd = root;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            reason = "The foreground window is Paper Pilot itself. Click into the window you read from, then press the hotkey.";
            return null;
        }
        var cls = NativeMethods.GetWindowClass(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd")
        {
            reason = "The desktop or taskbar is in front. Click into the window you read from, then press the hotkey.";
            return null;
        }

        reason = null;
        return new TargetWindow(hwnd);
    }

    public void RefreshTitle() => Title = NativeMethods.GetWindowTitle(Hwnd);

    public uint Dpi
    {
        get
        {
            var dpi = NativeMethods.GetDpiForWindow(Hwnd);
            return dpi == 0 ? 96 : dpi;
        }
    }

    /// <summary>Visible outer bounds of the window (what Windows.Graphics.Capture captures).</summary>
    public RECT VisibleBounds => NativeMethods.GetVisibleFrameBounds(Hwnd);

    /// <summary>Raw GetWindowRect (includes invisible resize borders on Windows 10/11).</summary>
    public RECT WindowRect
    {
        get
        {
            NativeMethods.GetWindowRect(Hwnd, out var rc);
            return rc;
        }
    }

    /// <summary>Client area of the top-level window in screen coordinates.</summary>
    public RECT ClientRect => NativeMethods.GetClientRectOnScreen(Hwnd);

    /// <summary>
    /// The area that holds the document: a known document child window if one exists
    /// (e.g. Chrome's render widget, which excludes tabs/toolbars), otherwise the client area.
    /// </summary>
    public ContentArea GetContentArea()
    {
        IntPtr best = IntPtr.Zero;
        RECT bestRect = default;
        string bestClass = string.Empty;
        long bestArea = 0;

        NativeMethods.EnumChildWindows(Hwnd, (child, _) =>
        {
            if (!NativeMethods.IsWindowVisible(child)) return true;
            var cls = NativeMethods.GetWindowClass(child);
            if (Array.IndexOf(DocumentChildClasses, cls) < 0) return true;
            var rc = NativeMethods.GetClientRectOnScreen(child);
            long area = (long)rc.Width * rc.Height;
            if (area > bestArea)
            {
                best = child;
                bestRect = rc;
                bestClass = cls;
                bestArea = area;
            }
            return true;
        }, IntPtr.Zero);

        var client = ClientRect;
        if (best != IntPtr.Zero && bestArea > 0)
        {
            // Clip to the client area in case the child extends past it.
            var clipped = Intersect(bestRect, client);
            if (!clipped.IsEmpty)
                return new ContentArea(best, clipped, bestClass);
        }
        return new ContentArea(Hwnd, client, "client area");
    }

    public override string ToString() => $"\"{Title}\" [{ProcessName}, {ClassName}, 0x{Hwnd.ToInt64():X}]";

    private static RECT Intersect(RECT a, RECT b) => new(
        Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top),
        Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
}

/// <param name="Hwnd">Window that holds the document (scroll messages will go here in Milestone 2).</param>
/// <param name="ScreenRect">Document area in physical screen pixels.</param>
/// <param name="Kind">How it was found, for logging.</param>
public readonly record struct ContentArea(IntPtr Hwnd, RECT ScreenRect, string Kind);
