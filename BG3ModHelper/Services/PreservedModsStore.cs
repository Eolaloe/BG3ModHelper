using System.IO;
using Newtonsoft.Json;

namespace BG3ModHelper.Services;

/// <summary>
/// Persists the set of mod UUIDs that the user has chosen to preserve
/// (lock at current version — excluded from update checks and batch downloads).
/// </summary>
public sealed class PreservedModsStore
{
    public static PreservedModsStore Instance { get; } = new();

    private static string FilePath =>
        Path.Combine(SettingsStore.GetDataFolder(), "preserved_mods.json");

    private readonly HashSet<string> _uuids;

    private PreservedModsStore()
    {
        _uuids = Load();
    }

    public bool IsPreserved(string uuid) =>
        _uuids.Contains(uuid);

    /// <summary>Toggles preserved state. Returns true if the mod is now preserved.</summary>
    public bool Toggle(string uuid)
    {
        if (!_uuids.Remove(uuid))
            _uuids.Add(uuid);
        Save();
        return _uuids.Contains(uuid);
    }

    // ── persistence ──────────────────────────────────────────────────────────

    private HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var data = JsonConvert.DeserializeObject<PersistedData>(File.ReadAllText(FilePath));
            return data?.Preserved is null ? [] : [..data.Preserved];
        }
        catch (Exception ex)
        {
            Logger.Warn($"PreservedModsStore: load failed — {ex.Message}");
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            FileHelper.WriteAllTextAtomic(FilePath,
                JsonConvert.SerializeObject(
                    new PersistedData { Preserved = [.._uuids] },
                    Formatting.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"PreservedModsStore: save failed — {ex.Message}");
        }
    }

    private sealed class PersistedData
    {
        public List<string> Preserved { get; set; } = [];
    }
}
