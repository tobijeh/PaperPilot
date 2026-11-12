using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PaperPilot.UI;

/// <summary>
/// Brand palette, following the Windows light/dark setting:
/// light = cream background, navy text; dark = navy background, cream text; blue accents in both.
/// Colours are swapped in place on the shared brushes, so open windows update live.
/// </summary>
public static class ThemeManager
{
    public static readonly Color Blue = Color.FromRgb(0x00, 0x59, 0xFD);
    public static readonly Color DeepBlue = Color.FromRgb(0x01, 0x42, 0xC0);
    public static readonly Color Navy = Color.FromRgb(0x0D, 0x27, 0x68);
    public static readonly Color Cream = Color.FromRgb(0xFE, 0xF7, 0xE6);
    public static readonly Color Periwinkle = Color.FromRgb(0xAB, 0xBF, 0xEA);
    public static readonly Color Grey = Color.FromRgb(0x8A, 0x8F, 0x98);

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    private static readonly (string key, Color light, Color dark)[] Tokens =
    {
        ("Bg", Cream, Navy),
        ("Sidebar", Color.FromRgb(0xF7, 0xEE, 0xD8), Color.FromRgb(0x0A, 0x1F, 0x55)),
        ("Card", Color.FromRgb(0xFF, 0xFC, 0xF5), Color.FromRgb(0x13, 0x30, 0x7C)),
        ("CardHover", Color.FromRgb(0xFF, 0xF8, 0xE9), Color.FromRgb(0x18, 0x39, 0x8C)),
        ("CardBorder", Color.FromRgb(0xDC, 0xE2, 0xF0), Color.FromRgb(0x26, 0x44, 0x94)),
        ("Text", Navy, Cream),
        ("Text2", Color.FromRgb(0x4D, 0x5C, 0x86), Color.FromRgb(0xB9, 0xC4, 0xE2)),
        ("Accent", Blue, Blue),
        ("AccentHover", DeepBlue, Color.FromRgb(0x2B, 0x74, 0xFF)),
        ("AccentSoft", Color.FromArgb(0x1C, 0x00, 0x59, 0xFD), Color.FromArgb(0x40, 0x00, 0x59, 0xFD)),
        ("OnAccent", Colors.White, Colors.White),
        ("Link", Blue, Periwinkle),
        ("Attention", DeepBlue, Periwinkle),
        ("Ok", Color.FromRgb(0x1F, 0x7A, 0x52), Color.FromRgb(0x8F, 0xE0, 0xB8)),
        ("SwitchOff", Color.FromRgb(0xC9, 0xCF, 0xDD), Color.FromRgb(0x2F, 0x4C, 0x9A)),
        ("Input", Colors.White, Color.FromRgb(0x0F, 0x29, 0x6E)),
        ("PreviewStage", Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x1B, 0x1B, 0x22)),
        // The bubble
        ("BubbleBg", Color.FromArgb(0xF5, 0xFE, 0xF7, 0xE6), Color.FromArgb(0xF5, 0x0D, 0x27, 0x68)),
        ("BubbleEdge", Navy, Color.FromRgb(0x4F, 0x6B, 0xB5)),
        ("BubbleText", Navy, Cream),
        ("PlaneFollowing", Blue, Color.FromRgb(0x5C, 0x95, 0xFF)),
        ("PlaneHolding", Navy, Cream),
        ("PlaneInactive", Grey, Grey),
        ("TrailFollowing", Blue, Color.FromRgb(0x5C, 0x95, 0xFF)),
        ("TrailHolding", Periwinkle, Periwinkle),
    };

    public static void Initialize(ResourceDictionary resources)
    {
        foreach (var (key, light, _) in Tokens)
            resources[key] = new SolidColorBrush(light);
        Apply(resources, ReadSystemDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(resources, ReadSystemDark()));
        };
    }

    public static Color Get(string key) =>
        Application.Current?.Resources[key] is SolidColorBrush b ? b.Color : Colors.Magenta;

    private static void Apply(ResourceDictionary resources, bool dark)
    {
        bool changed = dark != IsDark;
        IsDark = dark;
        foreach (var (key, light, darkColor) in Tokens)
        {
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
                brush.Color = dark ? darkColor : light;
        }
        foreach (Window w in Application.Current?.Windows ?? new WindowCollection())
            ApplyTitleBar(w);
        if (changed) Changed?.Invoke();
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark or light title bar to match the theme.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
    }
}
