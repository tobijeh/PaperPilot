using Microsoft.Win32;
using PaperPilot.Diagnostics;

namespace PaperPilot.Core;

/// <summary>"Start with Windows": a per-user Run entry that launches Paper Pilot quietly into the tray.</summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Paper Pilot";
    public const string BackgroundArg = "--background";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(Name, $"\"{exe}\" {BackgroundArg}");
            else
                key.DeleteValue(Name, throwOnMissingValue: false);
            Log.Info($"Start with Windows: {(enabled ? "on" : "off")}.");
        }
        catch (Exception ex)
        {
            Log.Error("Start with Windows: could not update the Run key", ex);
        }
    }
}
