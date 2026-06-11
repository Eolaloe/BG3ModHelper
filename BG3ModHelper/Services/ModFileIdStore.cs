using System.IO;
using Newtonsoft.Json;

namespace BG3ModHelper.Services;

/// <summary>
/// Persists the last-installed Nexus fileId per mod UUID.
///
/// Used for accurate update detection (spec §4.11):
///   - After download: save UUID → fileId + fileName
///   - On update check: compare saved fileId vs DB fileId
///   - If pakFileName changed but fileName matches → case 2 fallback
///   - If no record → fall back to version string comparison
///
/// File: %LOCALAPPDATA%\BG3ModHelper\mod_fileids.json
/// Format: { "uuid": { "modId": 1234, "fileId": 56789, "fileName": "Main File" } }
/// </summary>
public class ModFileIdStore
{
    private static readonly string FilePath =
        Path.Combine(SettingsStore.GetDataFolder(), "mod_fileids.json");

    private Dictionary<string, FileIdEntry> _store = new();

    // === Initialization ===

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var raw = JsonConvert.DeserializeObject<Dictionary<string, FileIdEntry>>(
                File.ReadAllText(FilePath));
            if (raw != null)
                _store = raw;

            Logger.Info($"ModFileIdStore: loaded {_store.Count} entries");
        }
        catch (Exception ex)
        {
            Logger.Warn($"ModFileIdStore.Load: {ex.Message}");
        }
    }

    // === Lookup ===

    public IReadOnlyDictionary<string, FileIdEntry> GetAll() => _store;

    public long? GetFileId(string uuid)
    {
        if (_store.TryGetValue(uuid.ToLowerInvariant(), out var entry))
            return entry.NexusFileId;
        return null;
    }

    public FileIdEntry? GetEntry(string uuid)
    {
        _store.TryGetValue(uuid.ToLowerInvariant(), out var entry);
        return entry;
    }

    // === Update ===

    public void SetFileId(string uuid, int modId, long fileId, string fileName = "")
    {
        _store[uuid.ToLowerInvariant()] = new FileIdEntry(modId, fileId, fileName);
        Save();
        Logger.Info($"ModFileIdStore: saved fileId={fileId} fileName={fileName} for uuid={uuid}");
    }

    /// <summary>
    /// Removes records for UUIDs no longer present in the mods folder.
    /// Called after each scan to prevent stale entries causing missed updates.
    /// </summary>
    public void PruneOrphans(HashSet<string> activeUuids)
    {
        var orphans = _store.Keys
            .Where(k => !activeUuids.Contains(k))
            .ToList();
        if (orphans.Count == 0) return;
        foreach (var k in orphans) _store.Remove(k);
        Save();
        Logger.Info($"ModFileIdStore: pruned {orphans.Count} orphan(s)");
    }

    /// <summary>Removes all entries and saves. Returns the number of entries deleted.</summary>
    public int ClearAll()
    {
        int count = _store.Count;
        _store.Clear();
        if (count > 0) Save();
        return count;
    }

    /// <summary>
    /// Removes all entries whose mod ID matches <paramref name="nexusModId"/>.
    /// Returns the number of entries deleted.
    /// </summary>
    public int RemoveByModId(int nexusModId)
    {
        var toRemove = _store
            .Where(kvp => kvp.Value.NexusModId == nexusModId)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in toRemove)
            _store.Remove(key);
        if (toRemove.Count > 0) Save();
        return toRemove.Count;
    }

    // === Persistence ===

    private void Save()
    {
        try
        {
            FileHelper.WriteAllTextAtomic(FilePath,
                JsonConvert.SerializeObject(_store, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"ModFileIdStore.Save: {ex.Message}");
        }
    }
}

/// <summary>Single entry in the local fileId store.</summary>
public record FileIdEntry(int NexusModId, long NexusFileId, string NexusFileName = "");
