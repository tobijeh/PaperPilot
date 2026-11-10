using System.Runtime.InteropServices;
using System.Windows.Interop;
using PaperPilot.Diagnostics;
using PaperPilot.Interop;

namespace PaperPilot.Core;

/// <summary>
/// Registers the configured global hotkeys (RegisterHotKey on a hidden message-only window), so they
/// work while another app has focus and never move focus.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private readonly Dictionary<HotkeyAction, HotkeyBinding?> _bindings = new();
    private bool _suspended;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("PaperPilotHotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: invisible, never activatable
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Errors from the last registration attempt, per action (e.g. "in use by another app").</summary>
    public Dictionary<HotkeyAction, string> Errors { get; } = new();

    public IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> Bindings => _bindings;

    /// <summary>Registers all bindings. Returns true if every one succeeded.</summary>
    public bool Apply(IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> bindings)
    {
        UnregisterAll();
        _bindings.Clear();
        foreach (var (a, b) in bindings) _bindings[a] = b;
        Errors.Clear();
        if (_suspended) return true;

        foreach (var (action, binding) in _bindings)
        {
            if (binding is null) continue;
            if (!TryRegister(action, binding, out var error))
                Errors[action] = error!;
        }
        return Errors.Count == 0;
    }

    /// <summary>Tests whether a binding can be registered (used by the recorder before saving).</summary>
    public bool CanRegister(HotkeyBinding binding, out string? error)
    {
        int id = 0xBFFF;
        if (NativeMethods.RegisterHotKey(_source.Handle, id, binding.Modifiers | NativeMethods.MOD_NOREPEAT, binding.VirtualKey))
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
            error = null;
            return true;
        }
        error = Describe(binding, Marshal.GetLastWin32Error());
        return false;
    }

    /// <summary>Temporarily releases all hotkeys (while the recorder is listening for a new key).</summary>
    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        _suspended = false;
        Apply(new Dictionary<HotkeyAction, HotkeyBinding?>(_bindings));
    }

    /// <summary>Virtual-key codes used by hotkeys, so the input monitor doesn't treat them as page input.</summary>
    public IEnumerable<(HotkeyAction action, HotkeyBinding binding)> Active =>
        _bindings.Where(kv => kv.Value is not null).Select(kv => (kv.Key, kv.Value!));

    private bool TryRegister(HotkeyAction action, HotkeyBinding binding, out string? error)
    {
        int id = (int)action + 1;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, binding.Modifiers | NativeMethods.MOD_NOREPEAT, binding.VirtualKey))
        {
            error = Describe(binding, Marshal.GetLastWin32Error());
            Log.Warn($"Hotkeys: {action} = {binding}: {error}");
            return false;
        }
        _registered[id] = action;
        error = null;
        Log.Info($"Hotkeys: {action} = {binding}.");
        return true;
    }

    private static string Describe(HotkeyBinding binding, int code) => code == 1409 // ERROR_HOTKEY_ALREADY_REGISTERED
        ? $"{binding} is in use by another app. Pick another."
        : $"Could not use {binding} (Windows error {code}). Pick another.";

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { Pressed?.Invoke(action); }
            catch (Exception ex) { Log.Error($"Hotkey {action} failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
