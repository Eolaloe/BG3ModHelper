using System.IO;
using System.Text.RegularExpressions;
using BG3ModHelper.Models;
using Newtonsoft.Json;

namespace BG3ModHelper.Services;

/// <summary>
/// Persists download history to %LOCALAPPDATA%\BG3ModHelper\download_history.json.
/// Keeps the most recent 500 entries; older ones are trimmed automatically.
/// </summary>
public class DownloadHistoryStore
{
    private const int MaxEntries = 500;

    private static readonly Regex _nexusModIdRegex =
        new(@"/mods/(\d+)", RegexOptions.Compiled);

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

    /// <summary>Removes all entries older than <paramref name="cutoff"/> and saves.</summary>
    public void ClearBefore(DateTime cutoff)
    {
        _entries.RemoveAll(e => e.HistoryDownloadedAt < cutoff);
        Save();
        Logger.Info($"DownloadHistoryStore: cleared entries before {cutoff:u} ({_entries.Count} remaining)");
    }

    /// <summary>Removes all entries and saves.</summary>
    public void ClearAll()
    {
        _entries.Clear();
        Save();
        Logger.Info("DownloadHistoryStore: cleared all entries");
    }

    /// <summary>
    /// Removes all entries whose Nexus mod ID (parsed from <c>HistoryPageUrl</c>)
    /// matches <paramref name="nexusModId"/>. Returns the number of entries deleted.
    /// </summary>
    public int RemoveByModId(int nexusModId)
    {
        int removed = _entries.RemoveAll(e =>
        {
            if (string.IsNullOrEmpty(e.HistoryPageUrl)) return false;
            var m = _nexusModIdRegex.Match(e.HistoryPageUrl);
            return m.Success &&
                   int.TryParse(m.Groups[1].Value, out var id) &&
                   id == nexusModId;
        });
        if (removed > 0) Save();
        return removed;
    }

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
            FileHelper.WriteAllTextAtomic(FilePath,
                JsonConvert.SerializeObject(_entries, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"DownloadHistoryStore.Save: {ex.Message}");
        }
    }
}
