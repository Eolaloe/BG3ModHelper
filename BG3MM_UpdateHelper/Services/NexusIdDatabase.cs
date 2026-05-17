using System.IO;
using System.Net.Http;
using BG3MM_UpdateHelper.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Manages the community Nexus mod database (v1.5 structure).
///
/// DB structure (GitHub):
///   { "modId": { modName, uploadedBy, modId, paks: [{ fileName, version, pakFileName, fileId, uuid }] } }
///
/// In-memory index:
///   pakFileName → List&lt;PakLookupEntry&gt; (O(1) lookup)
///
/// Update check flow:
///   pakFileName → DB lookup → version/fileId (no API calls)
///
/// Contribute flow:
///   pakFileName + UUID → POST to contribute endpoint
/// </summary>
public class NexusIdDatabase
{
    private static readonly HttpClient _http = new();

    private static readonly string DbCachePath =
        Path.Combine(SettingsStore.GetDataFolder(), "nexusid_db.json");

    // In-memory index: pakFileName (no extension, lowercase) → entries
    private Dictionary<string, List<PakLookupEntry>> _pakIndex = new();



    /// <summary>Load DB from cache / GitHub (sync if stale).</summary>
    public async Task InitAsync()
    {
        await SyncFromGitHubAsync();
    }

    // ── Lookup ────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks up pak entries by pakFileName (without extension).
    /// Returns empty list if not found.
    /// </summary>
    public List<PakLookupEntry> LookupByPakFileName(string pakFileName)
    {
        var key = NormalizeKey(pakFileName);
        return _pakIndex.TryGetValue(key, out var entries) ? entries : new();
    }

    /// <summary>
    /// Case 2 fallback: pakFileName changed but same modId + fileName.
    /// </summary>
    public List<PakLookupEntry> LookupByModIdAndFileName(int modId, string fileName)
    {
        return _pakIndex.Values
            .SelectMany(e => e)
            .Where(e => e.ModId == modId &&
                        string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Returns a single unambiguous entry, or null if there are multiple matches.
    /// Callers should show conflict UI when null + LookupByPakFileName returns > 1.
    /// </summary>
    public PakLookupEntry? LookupSingle(string pakFileName)
    {
        var entries = LookupByPakFileName(pakFileName);
        return entries.Count == 1 ? entries[0] : null;
    }

    // ── Contribute ────────────────────────────────────────────────────────

    /// <summary>
    /// Submits a batch of UUID contributions in a single request.
    /// Only called for DB entries where uuid == null.
    /// </summary>
    public async Task ContributeBatchAsync(List<ContributeEntry> entries)
    {
        if (string.IsNullOrEmpty(Constants.NEXUS_UUID_SUBMIT_URL)) return;
        if (entries.Count == 0) return;

        try
        {
            var payload = JsonConvert.SerializeObject(entries);
            var content = new StringContent(payload,
                System.Text.Encoding.UTF8, "application/json");

            var resp = await _http.PostAsync(Constants.NEXUS_UUID_SUBMIT_URL, content);
            if (resp.IsSuccessStatusCode)
                Logger.Info($"NexusIdDatabase: contributed {entries.Count} UUID(s)");
            else
                Logger.Warn($"NexusIdDatabase: contribution rejected — {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusIdDatabase: contribution failed — {ex.Message}");
        }
    }

    // Keep single entry method for backward compatibility
    public async Task ContributeUuidAsync(string pakFileName, string uuid, int modId, long fileId)
    {
        await ContributeBatchAsync(new List<ContributeEntry>
        {
            new ContributeEntry(pakFileName, uuid, modId, fileId)
        });
    }

    // ── GitHub sync ───────────────────────────────────────────────────────

    private string? _cachedETag;

    private async Task SyncFromGitHubAsync()
    {
        // Always check GitHub for changes via ETag (no fixed timer)
        try
        {
            Logger.Info("NexusIdDatabase: checking GitHub for updates...");

            using var request = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, Constants.NEXUS_UUID_DB_URL);

            if (!string.IsNullOrEmpty(_cachedETag))
                request.Headers.TryAddWithoutValidation("If-None-Match", _cachedETag);

            using var response = await _http.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                Logger.Info("NexusIdDatabase: DB unchanged, using cache.");
                LoadFromCache();
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"NexusIdDatabase: GitHub returned {(int)response.StatusCode}. Using cache.");
                LoadFromCache();
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            _cachedETag = response.Headers.ETag?.Tag;

            File.WriteAllText(DbCachePath, json);
            BuildIndex(json);
            Logger.Info($"NexusIdDatabase: synced — {_pakIndex.Count} pak entries indexed");
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusIdDatabase: GitHub sync failed — {ex.Message}. Using cache.");
            LoadFromCache();
        }
    }

    // ── Index building ────────────────────────────────────────────────────

    private void LoadFromCache()
    {
        if (!File.Exists(DbCachePath)) return;
        try
        {
            BuildIndex(File.ReadAllText(DbCachePath));
            Logger.Info($"NexusIdDatabase: loaded from cache — {_pakIndex.Count} pak entries");
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusIdDatabase.LoadFromCache: {ex.Message}");
        }
    }

    private void BuildIndex(string json)
    {
        // DB structure: { "modId": { modName, uploadedBy, modId, paks: [...] } }
        var raw = JObject.Parse(json);
        var index = new Dictionary<string, List<PakLookupEntry>>();

        foreach (var prop in raw.Properties())
        {
            if (prop.Name.StartsWith("_")) continue; // skip metadata keys
            var mod = prop.Value;
            if (mod == null) continue;

            var modId      = mod["modId"]?.Value<int>() ?? 0;
            var modName    = mod["modName"]?.Value<string>() ?? "";
            var uploadedBy = mod["uploadedBy"]?.Value<string>() ?? "";
            var paks       = mod["paks"] as JArray;
            if (paks == null) continue;

            foreach (var pak in paks)
            {
                var pakFileName = pak["pakFileName"]?.Value<string>();
                if (string.IsNullOrEmpty(pakFileName)) continue;

                var entry = new PakLookupEntry
                {
                    ModId      = modId,
                    ModName    = modName,
                    UploadedBy = uploadedBy,
                    FileName   = pak["fileName"]?.Value<string>() ?? "",
                    Version    = pak["version"]?.Value<string>() ?? "",
                    PakFileName = pakFileName,
                    FileId     = pak["fileId"]?.Value<long>() ?? 0,
                    Uuid       = pak["uuid"]?.Value<string>(),
                };

                var key = NormalizeKey(pakFileName);
                if (!index.TryGetValue(key, out var list))
                {
                    list = new List<PakLookupEntry>();
                    index[key] = list;
                }
                list.Add(entry);
            }
        }

        _pakIndex = index;
    }

    private static string NormalizeKey(string pakFileName) =>
        Path.GetFileNameWithoutExtension(pakFileName).ToLowerInvariant();
}

/// <summary>
/// In-memory representation of a pak entry from the community DB.
/// Used for update checking and conflict resolution UI.
/// </summary>
public class PakLookupEntry
{
    public int    ModId      { get; init; }
    public string ModName    { get; init; } = "";
    public string UploadedBy { get; init; } = "";
    public string FileName   { get; init; } = "";  // File tab title on Nexus
    public string Version    { get; init; } = "";
    public string PakFileName { get; init; } = "";
    public long   FileId     { get; init; }
    public string? Uuid      { get; init; }

    /// <summary>Display string for conflict resolution UI: [uploadedBy] ModName / FileName</summary>
    public string DisplayLabel => $"[{UploadedBy}] {ModName} / {FileName}";
}



/// <summary>Single UUID contribution entry.</summary>
public class ContributeEntry
{
    [JsonProperty("pakFileName")] public string PakFileName { get; set; } = "";
    [JsonProperty("uuid")]        public string Uuid        { get; set; } = "";
    [JsonProperty("modId")]       public int    ModId       { get; set; }
    [JsonProperty("fileId")]      public long   FileId      { get; set; }

    public ContributeEntry() { }
    public ContributeEntry(string pakFileName, string uuid, int modId, long fileId)
    {
        PakFileName = pakFileName;
        Uuid        = uuid;
        ModId       = modId;
        FileId      = fileId;
    }
}
