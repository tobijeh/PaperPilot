using System.IO;
using System.Text;

namespace PaperPilot.Diagnostics;

/// <summary>
/// Local rolling log: one file per day in %LOCALAPPDATA%\PaperPilot\logs, kept for 7 days.
/// Never sends anything anywhere.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _currentPath;
    private static DateTime _currentDay;

    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperPilot");

    public static string LogFolder { get; } = Path.Combine(AppDataFolder, "logs");

    /// <summary>Raised for every line written (used by the debug window).</summary>
    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            foreach (var file in Directory.GetFiles(LogFolder, "paperpilot-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7))
                    File.Delete(file);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss.fff} {level} {message}";
        lock (Gate)
        {
            try
            {
                if (_currentPath is null || now.Date != _currentDay)
                {
                    _currentDay = now.Date;
                    Directory.CreateDirectory(LogFolder);
                    _currentPath = Path.Combine(LogFolder, $"paperpilot-{now:yyyyMMdd}.log");
                }
                File.AppendAllText(_currentPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }
        LineWritten?.Invoke(line);
    }
}
