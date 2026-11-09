using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using PaperPilot.Diagnostics;
using PaperPilot.Interop;

namespace PaperPilot.Core;

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a hidden message-only window, so they work while
/// another app has focus and never move focus. (Milestone 2 adds the recorder and settings.)
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;

    public GlobalHotkeys()
    {
        var p = new HwndSourceParameters("PaperPilotHotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: invisible, never activatable
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey. Returns an id, or an error message if Windows refused.</summary>
    public bool TryRegister(ModifierKeys modifiers, Key key, Action handler, out int id, out string? error)
    {
        id = _nextId++;
        uint mods = NativeMethods.MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (!NativeMethods.RegisterHotKey(_source.Handle, id, mods, vk))
        {
            int code = Marshal.GetLastWin32Error();
            error = code == 1409 // ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{Describe(modifiers, key)} is already used by another app. Pick another key."
                : $"Could not register {Describe(modifiers, key)} (error {code}).";
            Log.Warn($"Hotkeys: {error}");
            return false;
        }

        _handlers[id] = handler;
        error = null;
        Log.Info($"Hotkeys: registered {Describe(modifiers, key)}.");
        return true;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id))
            NativeMethods.UnregisterHotKey(_source.Handle, id);
    }

    public static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            try { handler(); }
            catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList())
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
