using System.Runtime.InteropServices;
using PaperPilot.Diagnostics;
using PaperPilot.Interop;

namespace PaperPilot.Core;

public enum ManualInputKind
{
    Wheel,
    Touchpad,
    Key,
    Click,
    Nudge,
    ContentMoved,
}

/// <summary>
/// Notices the user's own input to the target window via low-level mouse/keyboard hooks.
/// Hooks run on a dedicated thread so they never add lag to the user's scrolling, and Paper
/// Pilot's own injected input (tagged with <see cref="OwnInputTag"/>) is ignored.
/// </summary>
public sealed class InputMonitor : IDisposable
{
    /// <summary>dwExtraInfo value on every event Paper Pilot injects with SendInput ("PPLT").</summary>
    public static readonly IntPtr OwnInputTag = new(0x50504C54);

    // Keys that move a page no matter which modifiers are held.
    private static readonly HashSet<uint> NavigationKeys = new()
    {
        0x21, 0x22, // Page Up / Page Down
        0x23, 0x24, // End / Home
        0x25, 0x26, 0x27, 0x28, // arrows
        0x20, // Space
    };

    private static readonly HashSet<uint> ModifierKeys = new()
    {
        0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x14, // shift/ctrl/alt/win/caps
    };

    private readonly Thread _thread;
    private readonly NativeMethods.LowLevelProc _mouseProc;
    private readonly NativeMethods.LowLevelProc _keyboardProc;
    private uint _threadId;
    private IntPtr _mouseHook, _keyboardHook;
    private volatile IntPtr _target;
    private volatile HashSet<uint> _ignoredKeys = new();
    private long _lastLogTicks;

    public InputMonitor()
    {
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "PaperPilot input hooks" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Raised on the hook thread — keep handlers tiny (cancel a scroll, then BeginInvoke the rest).
    /// </summary>
    public event Action<ManualInputKind, string>? ManualInput;

    /// <summary>Top-level window being preached from (or zero when off).</summary>
    public IntPtr TargetWindow
    {
        get => _target;
        set => _target = value;
    }

    /// <summary>Virtual keys of Paper Pilot's own hotkeys (toggle/pause), which aren't page input.</summary>
    public void SetIgnoredKeys(IEnumerable<uint> virtualKeys) => _ignoredKeys = new HashSet<uint>(virtualKeys);

    /// <summary>Lets other monitors (touchpad, nudges, OCR) report manual input through the same event.</summary>
    public void Report(ManualInputKind kind, string detail) => Raise(kind, detail);

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        var module = NativeMethods.GetModuleHandle(null);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
            Log.Error($"InputMonitor: hook install failed (mouse {_mouseHook != IntPtr.Zero}, keyboard {_keyboardHook != IntPtr.Zero}, error {Marshal.GetLastWin32Error()}).");
        else
            Log.Info("InputMonitor: mouse and keyboard hooks installed.");
        ready.Set();

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            // Hooks are serviced inside GetMessage; nothing else to dispatch.
        }

        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _target != IntPtr.Zero)
        {
            try
            {
                int msg = wParam.ToInt32();
                if (msg is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL
                    or NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    if (data.dwExtraInfo != OwnInputTag && NativeMethods.RootWindowFromPoint(data.pt) == _target)
                    {
                        bool injected = (data.flags & NativeMethods.LLMHF_INJECTED) != 0;
                        if (msg is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
                            Raise(ManualInputKind.Wheel, $"wheel {(short)(data.mouseData >> 16)}{(injected ? " (injected by another app/driver)" : "")}");
                        else
                            Raise(ManualInputKind.Click, msg == NativeMethods.WM_LBUTTONDOWN ? "left click" : "middle click");
                    }
                }
            }
            catch
            {
                // never let an exception escape a hook
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _target != IntPtr.Zero)
        {
            try
            {
                int msg = wParam.ToInt32();
                if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    uint vk = data.vkCode;
                    if (data.dwExtraInfo != OwnInputTag && !ModifierKeys.Contains(vk) && !_ignoredKeys.Contains(vk))
                    {
                        // Navigation keys (what clickers send) count wherever focus is; other keys
                        // only when typing into the target window.
                        if (NavigationKeys.Contains(vk) || IsTargetFocused())
                            Raise(ManualInputKind.Key, $"key 0x{vk:X2}");
                    }
                }
            }
            catch
            {
                // never let an exception escape a hook
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool IsTargetFocused() =>
        NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GA_ROOT) == _target;

    private void Raise(ManualInputKind kind, string detail)
    {
        // Throttle the log (a wheel spin is dozens of events), never the event itself.
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastLogTicks) > 500)
        {
            Interlocked.Exchange(ref _lastLogTicks, now);
            Log.Info($"Manual input: {kind} ({detail}).");
        }
        try { ManualInput?.Invoke(kind, detail); }
        catch (Exception ex) { Log.Error("ManualInput handler failed", ex); }
    }

    public void Dispose()
    {
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
