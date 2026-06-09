using System.IO;

namespace BG3ModHelper.Services;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// Minimal file logger. Writes to %LOCALAPPDATA%\BG3ModHelper\logs\{date}.log.
/// Uses only BCL — no external packages required.
///
/// Default MinLevel is Info: DEBUG messages are suppressed in normal use.
/// Set MinLevel = LogLevel.Debug at startup to enable verbose output.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();

    /// <summary>
    /// Messages below this level are not written to the log file.
    /// Default: Info — suppresses routine DEBUG noise for end users.
    /// </summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    private static string LogFolder => Path.Combine(SettingsStore.GetDataFolder(), "logs");
    private static string LogFilePath =>
        Path.Combine(LogFolder, $"{DateTime.Now:yyyy-MM-dd}.log");

    public static void Debug(string message) => Write(LogLevel.Debug, "DEBUG", message);
    public static void Info(string message)  => Write(LogLevel.Info,  "INFO",  message);
    public static void Warn(string message)  => Write(LogLevel.Warn,  "WARN",  message);
    public static void Error(string message) => Write(LogLevel.Error, "ERROR", message);

    private static void Write(LogLevel level, string label, string message)
    {
        if (level < MinLevel) return;
        try
        {
            Directory.CreateDirectory(LogFolder);
            var line = $"[{DateTime.Now:HH:mm:ss}] [{label}] {message}{Environment.NewLine}";
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
