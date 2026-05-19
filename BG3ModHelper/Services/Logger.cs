using System.IO;

namespace BG3ModHelper.Services;

/// <summary>
/// Minimal file logger. Writes to %LOCALAPPDATA%\BG3ModHelper\logs\{date}.log.
/// Uses only BCL — no external packages required.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();

    private static string LogFolder => Path.Combine(SettingsStore.GetDataFolder(), "logs");
    private static string LogFilePath =>
        Path.Combine(LogFolder, $"{DateTime.Now:yyyy-MM-dd}.log");

    public static void Debug(string message) => Write("DEBUG", message);
    public static void Info(string message)  => Write("INFO",  message);
    public static void Warn(string message)  => Write("WARN",  message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            lock (_lock)
            {
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // Logging failures are silently swallowed — never interrupt app flow.
        }
    }
}
