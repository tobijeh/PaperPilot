using System.Windows.Input;
using PaperPilot.Interop;

namespace PaperPilot.Core;

/// <summary>A key plus modifiers. Stored as Win32 values so any key a clicker sends can be bound.</summary>
/// <param name="Modifiers">MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN flags.</param>
/// <param name="VirtualKey">Win32 virtual-key code.</param>
public sealed record HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    public static HotkeyBinding FromWpf(ModifierKeys modifiers, Key key)
    {
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;
        return new HotkeyBinding(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None;

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string KeyName(uint vk)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        return key switch
        {
            Key.Next => "Page Down",
            Key.Prior => "Page Up",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.Space => "Space",
            Key.Escape => "Esc",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            Key.None => $"Key 0x{vk:X2}",
            _ => key.ToString(),
        };
    }
}
