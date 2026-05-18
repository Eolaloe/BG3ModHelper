using System.IO;
using BG3MM_UpdateHelper.Models;
using Newtonsoft.Json;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Persists download history to %LOCALAPPDATA%\BG3MM_UpdateHelper\download_history.json.
/// Keeps the most recent 500 entries; older ones are trimmed automatically.
/// </summary>
public class DownloadHistoryStore
{
    private const int MaxEntries = 500;

    private static readonly string FilePath =
        Path.Combine(SettingsStore.GetDataFolder(), "download_history.json");

    private readonly List<DownloadHistoryEntry> _entries = new();

    // === Initialization ===

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var raw = JsonConvert.DeserializeObject<List<DownloadHistoryEntry>>(
                File.ReadAllText(FilePath));
            if (raw != null)
            {
                _entries.Clear();
                _entries.AddRange(raw);
            }
            Logger.Info($"DownloadHistoryStore: loaded {_entries.Count} entries");
        }
        catch (Exception ex)
        {
            Logger.Warn($"DownloadHistoryStore.Load: {ex.Message}");
        }
    }

    // === Public API ===

    /// <summary>
    /// Adds a new entry and saves to disk.
    /// Trims oldest entries if over the 500-entry limit.
    /// </summary>
    public void Add(DownloadHistoryEntry entry)
    {
        _entries.Insert(0, entry); // newest first
        Trim();
        Save();
    }

    /// <summary>Returns all entries, newest first.</summary>
    public IReadOnlyList<DownloadHistoryEntry> GetAll() => _entries.AsReadOnly();

    // === Helpers ===

    private void Trim()
    {
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Logger.Info($"DownloadHistoryStore: trimmed to {MaxEntries} entries");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonConvert.SerializeObject(_entries, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"DownloadHistoryStore.Save: {ex.Message}");
        }
    }
}
