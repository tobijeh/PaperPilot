using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperPilot.Diagnostics;

namespace PaperPilot.Core;

public enum HotkeyAction
{
    Toggle,
    PauseResume,
    NudgeDown,
    NudgeUp,
}

public enum PillSize
{
    Small,
    Medium,
}

public enum BubblePosition
{
    TopRight,
    TopCenter,
}

public enum NudgeSize
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// Saved bubble position on one monitor, as fractions of that monitor's work area:
/// X = the bubble's right edge, Y = its top.
/// </summary>
public sealed record PillPosition(double X, double Y);

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\PaperPilot\settings.json.</summary>
public sealed class AppSettings
{
    public Dictionary<HotkeyAction, HotkeyBinding?> Hotkeys { get; set; } = DefaultHotkeys();

    /// <summary>Where the current line should sit, as a fraction of the window height (0.20–0.50).</summary>
    public double AnchorFraction { get; set; } = 0.33;

    /// <summary>On = hotkeys are active. Off = Paper Pilot ignores its hotkeys until switched back on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Settings opens by itself on the very first launch only.</summary>
    public bool FirstRunDone { get; set; }

    public bool StartWithWindows { get; set; }

    public PillSize PillSize { get; set; } = PillSize.Small;
    public double PillIdleOpacity { get; set; } = 0.55;
    public BubblePosition BubblePosition { get; set; } = BubblePosition.TopRight;
    public bool ShowStateWords { get; set; } = true;
    public Dictionary<string, PillPosition> PillPositions { get; set; } = new();

    public NudgeSize NudgeSize { get; set; } = NudgeSize.Medium;

    /// <summary>Wheel delta for one nudge (120 = one mouse-wheel notch).</summary>
    [JsonIgnore]
    public int NudgeWheelDelta => NudgeSize switch
    {
        NudgeSize.Small => 60,
        NudgeSize.Large => 240,
        _ => 120,
    };

    /// <summary>Developer options: unlocked by clicking the version in About seven times.</summary>
    public bool DeveloperOptions { get; set; }
    public bool ShowDebugWindow { get; set; }

    public static Dictionary<HotkeyAction, HotkeyBinding?> DefaultHotkeys() => new()
    {
        [HotkeyAction.Toggle] = new HotkeyBinding(0, 0x72), // F3
        [HotkeyAction.PauseResume] = null,
        [HotkeyAction.NudgeDown] = null,
        [HotkeyAction.NudgeUp] = null,
    };

    // ---- Persistence -------------------------------------------------------------------------

    public static string FilePath { get; } = Path.Combine(Log.AppDataFolder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                foreach (var action in Enum.GetValues<HotkeyAction>())
                    s.Hotkeys.TryAdd(action, null);
                s.AnchorFraction = Math.Clamp(s.AnchorFraction, 0.2, 0.5);
                s.PillIdleOpacity = Math.Clamp(s.PillIdleOpacity, 0.2, 1.0);
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: could not read {FilePath}, using defaults ({ex.Message}).");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Settings: save failed", ex);
        }
    }
}
