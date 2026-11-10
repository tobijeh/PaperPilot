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

/// <summary>Saved pill position on one monitor, as a fraction of that monitor's work area.</summary>
public sealed record PillPosition(double X, double Y);

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\PaperPilot\settings.json.</summary>
public sealed class AppSettings
{
    public Dictionary<HotkeyAction, HotkeyBinding?> Hotkeys { get; set; } = DefaultHotkeys();

    /// <summary>Where the current line should sit, as a fraction of the window height (0.20–0.50).</summary>
    public double AnchorFraction { get; set; } = 0.33;

    public PillSize PillSize { get; set; } = PillSize.Small;
    public double PillIdleOpacity { get; set; } = 0.55;
    public Dictionary<string, PillPosition> PillPositions { get; set; } = new();

    /// <summary>Wheel delta for one nudge (120 = one mouse-wheel notch).</summary>
    public int NudgeWheelDelta { get; set; } = 120;

    public bool ShowDebugWindow { get; set; } = true;

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
