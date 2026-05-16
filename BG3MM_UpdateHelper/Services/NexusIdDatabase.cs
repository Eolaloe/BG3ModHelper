using System.IO;
using System.Net.Http;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Models.Cache;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Manages the UUID ↔ NexusModId community database.
///
/// Sources (priority order):
///   1. Local cache  — nexusid_local.json  (user's own mappings)
///   2. GitHub DB    — nexusid_db.json     (community, synced every 12h)
///
/// Contribution flow:
///   User submits UUID↔modId → Vercel validates → merged into GitHub DB
/// </summary>
public class NexusIdDatabase
{
    private static readonly HttpClient _http = new();

    private static readonly string DbCachePath =
        Path.Combine(SettingsStore.GetDataFolder(), "nexusid_db.json");
    private static readonly string LocalPath =
        Path.Combine(SettingsStore.GetDataFolder(), "nexusid_local.json");

    // In-memory merged view: UUID → entry
    private Dictionary<string, NexusIdEntry> _db = new();

    private DateTime _lastSynced = DateTime.MinValue;

    // ── Initialization ────────────────────────────────────────────────────

    /// <summary>Load local cache + GitHub DB (sync if stale).</summary>
    public async Task InitAsync()
    {
        LoadLocal();
        await SyncFromGitHubAsync();
    }

    // ── Lookup ────────────────────────────────────────────────────────────

    /// <summary>Returns the Nexus mod ID for a UUID, or null if unknown.</summary>
    public int? GetNexusModId(string uuid)
    {
        if (_db.TryGetValue(uuid.ToLowerInvariant(), out var entry))
            return entry.ModId;
        return null;
    }

    // ── Matching: history + file-metadata ─────────────────────────────────

    /// <summary>
    /// Matches Nexus download history entries against installed mods
    /// using file-metadata pak name lookup. Saves new mappings locally
    /// and submits them to the community DB.
    /// </summary>
    public async Task<int> SyncFromHistoryAsync(
        List<NexusDownloadEntry> history,
        List<InstalledMod> installedMods,
        NexusApi nexusApi,
        IProgress<(int done, int total)>? progress = null)
    {
        // pak 파일명 → UUID 역매핑
        var pakToUuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installedMods)
        {
            var pakName = Path.GetFileNameWithoutExtension(mod.PakFilePath);
            if (!string.IsNullOrEmpty(pakName))
                pakToUuid.TryAdd(pakName, mod.UUID);
        }

        var newMappings = new Dictionary<string, NexusIdEntry>();
        int done = 0;

        // 이미 DB에 있는 mod_id 스킵
        var knownModIds = _db.Values.Select(e => e.ModId).ToHashSet();

        var targets = history
            .Where(h => !knownModIds.Contains(h.ModId))
            .ToList();

        foreach (var entry in targets)
        {
            progress?.Report((++done, targets.Count));

            try
            {
                // 최신 파일 정보 → pak명 목록
                var pakNames = await nexusApi.GetPakNamesAsync(entry.ModId);
                foreach (var pak in pakNames)
                {
                    var key = Path.GetFileNameWithoutExtension(pak);
                    if (pakToUuid.TryGetValue(key, out var uuid))
                    {
                        var idEntry = new NexusIdEntry(entry.ModId, entry.Name);
                        newMappings[uuid.ToLowerInvariant()] = idEntry;
                        _db[uuid.ToLowerInvariant()]         = idEntry;
                        Logger.Info($"NexusIdDatabase: matched {entry.Name} (mod_id={entry.ModId}) → {uuid}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"NexusIdDatabase: skip mod_id={entry.ModId} — {ex.Message}");
            }
        }

        if (newMappings.Count > 0)
        {
            SaveLocal();
            await SubmitContributionAsync(newMappings);
        }

        Logger.Info($"NexusIdDatabase: history sync complete — {newMappings.Count} new mappings");
        return newMappings.Count;
    }

    /// <summary>Manually register a UUID↔modId mapping from user URL input.</summary>
    public async Task RegisterManualAsync(string uuid, int modId, string modName)
    {
        var entry = new NexusIdEntry(modId, modName);
        _db[uuid.ToLowerInvariant()] = entry;
        SaveLocal();

        await SubmitContributionAsync(
            new Dictionary<string, NexusIdEntry>
            {
                [uuid.ToLowerInvariant()] = entry
            });

        Logger.Info($"NexusIdDatabase: manual register mod_id={modId} → {uuid}");
    }

    // ── GitHub sync ───────────────────────────────────────────────────────

    private async Task SyncFromGitHubAsync()
    {
        if (DateTime.UtcNow - _lastSynced < TimeSpan.FromHours(Constants.NEXUS_UUID_DB_CACHE_HOURS))
        {
            LoadDbCache();
            return;
        }

        try
        {
            Logger.Info("NexusIdDatabase: syncing from GitHub...");
            var json = await _http.GetStringAsync(Constants.NEXUS_UUID_DB_URL);

            // GitHub DB 형식: {uuid: {modId, name}}
            var raw = JsonConvert.DeserializeObject<Dictionary<string, NexusIdEntry>>(json);
            if (raw != null)
            {
                // 로컬 우선 머지
                foreach (var kv in raw)
                {
                    var key = kv.Key.ToLowerInvariant();
                    if (!_db.ContainsKey(key))
                        _db[key] = kv.Value;
                }

                File.WriteAllText(DbCachePath, json);
                _lastSynced = DateTime.UtcNow;
                Logger.Info($"NexusIdDatabase: synced {raw.Count} entries from GitHub");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusIdDatabase: GitHub sync failed — {ex.Message}. Using local cache.");
            LoadDbCache();
        }
    }

    // ── Vercel contribution ───────────────────────────────────────────────

    private static async Task SubmitContributionAsync(
        Dictionary<string, NexusIdEntry> mappings)
    {
        if (string.IsNullOrEmpty(Constants.NEXUS_UUID_SUBMIT_URL)) return;
        if (mappings.Count == 0) return;

        try
        {
            var payload = JsonConvert.SerializeObject(mappings);
            var content = new StringContent(payload,
                System.Text.Encoding.UTF8, "application/json");

            var resp = await _http.PostAsync(Constants.NEXUS_UUID_SUBMIT_URL, content);
            if (resp.IsSuccessStatusCode)
                Logger.Info($"NexusIdDatabase: contributed {mappings.Count} mappings");
            else
                Logger.Warn($"NexusIdDatabase: contribution rejected — {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusIdDatabase: contribution failed — {ex.Message}");
        }
    }

    // ── Local I/O ─────────────────────────────────────────────────────────

    private void LoadLocal()
    {
        if (!File.Exists(LocalPath)) return;
        try
        {
            var raw = JsonConvert.DeserializeObject<Dictionary<string, NexusIdEntry>>(
                File.ReadAllText(LocalPath));
            if (raw == null) return;

            foreach (var kv in raw)
                _db[kv.Key.ToLowerInvariant()] = kv.Value;

            Logger.Info($"NexusIdDatabase: loaded {raw.Count} local entries");
        }
        catch (Exception ex) { Logger.Warn($"NexusIdDatabase.LoadLocal: {ex.Message}"); }
    }

    private void LoadDbCache()
    {
        if (!File.Exists(DbCachePath)) return;
        try
        {
            var raw = JsonConvert.DeserializeObject<Dictionary<string, NexusIdEntry>>(
                File.ReadAllText(DbCachePath));
            if (raw == null) return;

            foreach (var kv in raw)
            {
                var key = kv.Key.ToLowerInvariant();
                if (!_db.ContainsKey(key))
                    _db[key] = kv.Value;
            }
        }
        catch (Exception ex) { Logger.Warn($"NexusIdDatabase.LoadDbCache: {ex.Message}"); }
    }

    private void SaveLocal()
    {
        try
        {
            File.WriteAllText(LocalPath,
                JsonConvert.SerializeObject(_db, Formatting.Indented));
        }
        catch (Exception ex) { Logger.Warn($"NexusIdDatabase.SaveLocal: {ex.Message}"); }
    }
}

/// <summary>Single entry in the UUID↔NexusModId database.</summary>
public record NexusIdEntry(int ModId, string Name);
