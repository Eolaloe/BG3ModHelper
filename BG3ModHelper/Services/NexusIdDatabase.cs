using System.IO;
using System.Net.Http;
using BG3ModHelper.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3ModHelper.Services;

/// <summary>
/// Manages the community Nexus mod database (v1.5 structure).
///
/// DB structure (GitHub):
///   {
///     "_removed": [404, 999, ...],   // nexusModIds deleted/hidden on Nexus — skipped during indexing
///     "modId": { modName, uploadedBy, modId, paks: [{ fileName, version, pakFileName, fileId, uuid }] }
///   }
///
/// In-memory index:
///   pakFileName → List&lt;PakLookupEntry&gt; (O(1) lookup)
///   _removedModIds → HashSet&lt;int&gt; for O(1) removal check
///
/// Update check flow:
///   pakFileName → DB lookup → version/fileId (no API calls)
///
/// Contribute flow:
///   pakFileName + UUID → POST to contribute endpoint
/// </summary>
public class NexusIdDatabase
{
    private static readonly HttpClient _http = HttpClientFactory.Shared;

    private static readonly string DbCachePath =
        Path.Combine(SettingsStore.GetDataFolder(), "nexusid_db.json");

    // In-memory index: pakFileName (no extension, lowercase) → entries
    private Dictionary<string, List<PakLookupEntry>> _pakIndex = new();

    // Nexus mod IDs known to be deleted or hidden — populated from DB "_removed" list
    private HashSet<int> _removedModIds = new();

    // Last time the DB was updated by the maintenance script (_meta.last_run)
    private DateTimeOffset? _lastRun;

    private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

    /// <summary>
    /// True when the DB has not been updated for more than 7 days.
    /// Used to decide whether to fall back to direct Nexus API calls
    /// for mods that appear up-to-date according to the (potentially stale) DB.
    /// </summary>
    public bool IsStale =>
        _lastRun == null ||
        (DateTimeOffset.UtcNow - _lastRun.Value) > StaleThreshold;



    /// <summary>Load DB from cache / GitHub (sync if stale).</summary>
    public async Task InitAsync()
    {
        await SyncFromGitHubAsync();
    }

    // === Lookup ===

    /// <summary>
    /// Returns <c>true</c> when this mod ID is in the DB's <c>_removed</c> list,
    /// meaning it has been deleted or hidden on Nexus Mods.
    /// Use this to suppress stale cached NexusModIds.
    /// </summary>
    public bool IsRemovedOnNexus(int modId) => _removedModIds.Contains(modId);

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
            .Where(e => e.NexusModId == modId &&
                        string.Equals(e.NexusFileName, fileName, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Returns all pak entries for a given Nexus mod ID, ordered by NexusFileId descending
    /// (latest file first). Used when the user has manually supplied a mod ID via UserModLinkStore.
    /// </summary>
    public List<PakLookupEntry> LookupByModId(int nexusModId)
    {
        return _pakIndex.Values
            .SelectMany(e => e)
            .Where(e => e.NexusModId == nexusModId)
            .OrderByDescending(e => e.NexusFileId)
            .ToList();
    }

    /// <summary>
    /// Returns the NexusModId if all matching entries share the same modId, otherwise null.
    /// Use this for platform classification when exact file disambiguation is not needed.
    /// </summary>
    public int? LookupUnambiguousModId(string pakFileName)
    {
        var entries = LookupByPakFileName(pakFileName);
        if (entries.Count == 0) return null;
        var firstId = entries[0].NexusModId;
        return entries.All(e => e.NexusModId == firstId) ? firstId : null;
    }

    /// <summary>
    /// Ambiguous-pak fallback: when multiple mod IDs share the same pak filename,
    /// narrow candidates to those whose <c>nexusUploadedBy</c> fuzzy-matches the
    /// installed mod's author (from meta.lsx).
    /// Returns the shared mod ID if the filtered set is unambiguous, otherwise null.
    ///
    /// Uses <see cref="AuthorMatcher.IsMatch"/> — handles parenthetical notes,
    /// multi-author strings, case differences, and substring containment.
    /// </summary>
    public int? LookupByPakAndAuthor(string pakFileName, string? author)
    {
        if (string.IsNullOrWhiteSpace(author) || author == "—") return null;

        var filtered = LookupByPakFileName(pakFileName)
            .Where(e => AuthorMatcher.IsMatch(e.NexusUploadedBy, author))
            .ToList();

        if (filtered.Count == 0) return null;
        var firstId = filtered[0].NexusModId;
        return filtered.All(e => e.NexusModId == firstId) ? firstId : null;
    }

    /// <summary>
    /// Ambiguous-pak fallback: narrows candidates by fuzzy-comparing the meta.lsx module
    /// name against <c>nexusModName</c> using <see cref="ModNameMatcher"/>.
    ///
    /// Handles CamelCase vs spaced titles, parenthetical suffixes, and partial containment:
    ///   "CompatibilityFramework"  ≈ "Compatibility Framework"   (compact-exact)
    ///   "ImprovedUI"              ≈ "Improved UI Assets"         (containment)
    ///   "CustomHotbar"            ≈ "Custom Hotbar Revised"      (token overlap)
    /// </summary>
    public int? LookupByPakAndModName(string pakFileName, string? metaModuleName)
    {
        if (string.IsNullOrWhiteSpace(metaModuleName)) return null;
        if (ModNameMatcher.Compact(metaModuleName).Length < 5) return null; // too short

        var filtered = LookupByPakFileName(pakFileName)
            .Where(e => ModNameMatcher.IsMatch(metaModuleName, e.NexusModName))
            .ToList();

        if (filtered.Count == 0) return null;
        var firstId = filtered[0].NexusModId;
        return filtered.All(e => e.NexusModId == firstId) ? firstId : null;
    }

    // === Contribute ===

    /// <summary>
    /// Submits a batch of UUID contributions in a single request.
    /// Returns true if the server accepted the payload (2xx), false otherwise.
    /// Verified entries (from direct app downloads) are flagged in the payload so the
    /// community DB can accept them with a lower vote threshold.
    /// </summary>
    public async Task<bool> ContributeBatchAsync(List<ContributeEntry> entries)
    {
        if (string.IsNullOrEmpty(Constants.NEXUS_UUID_SUBMIT_URL)) return false;
        if (entries.Count == 0) return true; // nothing to send = vacuous success

        try
        {
            var payload = JsonConvert.SerializeObject(entries);
            var content = new StringContent(payload,
                System.Text.Encoding.UTF8, "application/json");

            var resp = await _http.PostAsync(Constants.NEXUS_UUID_SUBMIT_URL, content);
            if (resp.IsSuccessStatusCode)
            {
                var verifiedCount = entries.Count(e => e.Verified);
                Logger.Info($"NexusIdDatabase: contributed {entries.Count} UUID(s)" +
                            (verifiedCount > 0 ? $" ({verifiedCount} verified)" : ""));
                return true;
            }
            else
            {
                Logger.Warn($"NexusIdDatabase: contribution rejected — {(int)resp.StatusCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusIdDatabase: contribution failed — {ex.Message}");
            return false;
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

    // === GitHub sync ===

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

    // === Index building ===

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
        // DB structure: { "_meta": { last_run, total_mods }, "_removed": [...], "modId": { ... } }
        var raw = JObject.Parse(json);
        var index   = new Dictionary<string, List<PakLookupEntry>>();
        var removed = new HashSet<int>();

        // Parse _meta.last_run to determine DB freshness
        if (raw["_meta"]?["last_run"]?.Value<string>() is string lastRunStr &&
            DateTimeOffset.TryParse(lastRunStr, out var lastRun))
        {
            _lastRun = lastRun;
            Logger.Info($"NexusIdDatabase: DB last updated {lastRun:yyyy-MM-dd HH:mm} UTC" +
                        (IsStale ? " — STALE (>7 days)" : ""));
        }
        else
        {
            _lastRun = null;
            Logger.Warn("NexusIdDatabase: _meta.last_run missing — treating DB as stale");
        }

        // Parse _removed list: mod IDs deleted or hidden on Nexus since last DB update
        if (raw["_removed"] is JArray removedArr)
            foreach (var token in removedArr)
            {
                var rid = token.Value<int?>();
                if (rid.HasValue) removed.Add(rid.Value);
            }

        foreach (var prop in raw.Properties())
        {
            if (prop.Name.StartsWith("_")) continue; // skip metadata keys (_removed, _meta, etc.)
            var mod = prop.Value;
            if (mod == null) continue;

            var modId      = mod["nexusModId"]?.Value<int>() ?? 0;
            if (removed.Contains(modId)) continue; // deleted/hidden on Nexus — exclude from index

            var modName    = mod["nexusModName"]?.Value<string>() ?? "";
            var uploadedBy = mod["nexusUploadedBy"]?.Value<string>() ?? "";
            var paks       = mod["paks"] as JArray;
            if (paks == null) continue;

            foreach (var pak in paks)
            {
                var pakFileName = pak["pakFileName"]?.Value<string>();
                if (string.IsNullOrEmpty(pakFileName)) continue;

                var entry = new PakLookupEntry
                {
                    NexusModId      = modId,
                    NexusModName    = modName,
                    NexusUploadedBy = uploadedBy,
                    NexusFileName   = pak["nexusFileName"]?.Value<string>() ?? "",
                    NexusFileVersion = pak["nexusFileVersion"]?.Value<string>() ?? "",
                    PakFileName     = pakFileName,
                    NexusFileId     = pak["nexusFileId"]?.Value<long>() ?? 0,
                    MetaUuid        = pak["metaUuid"]?.Value<string>(),
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

        _pakIndex      = index;
        _removedModIds = removed;
        if (removed.Count > 0)
            Logger.Info($"NexusIdDatabase: {removed.Count} removed/hidden mod ID(s) excluded from index");
    }

    private static string NormalizeKey(string pakFileName) =>
        pakFileName.ToLowerInvariant();
}

/// <summary>
/// In-memory representation of a pak entry from the community DB.
/// Used for update checking and conflict resolution UI.
/// </summary>
public class PakLookupEntry
{
    public int    NexusModId      { get; init; }
    public string NexusModName    { get; init; } = "";
    public string NexusUploadedBy { get; init; } = "";
    public string NexusFileName   { get; init; } = "";
    public string NexusFileVersion { get; init; } = "";
    public string PakFileName     { get; init; } = "";
    public long   NexusFileId     { get; init; }
    public string? MetaUuid       { get; init; }

    public string DisplayLabel => $"[{NexusUploadedBy}] {NexusModName} / {NexusFileName}";
}



/// <summary>Single UUID contribution entry.</summary>
public class ContributeEntry
{
    [JsonProperty("pakFileName")]  public string PakFileName  { get; set; } = "";
    [JsonProperty("metaUuid")]     public string MetaUuid     { get; set; } = "";
    [JsonProperty("nexusModId")]   public int    NexusModId   { get; set; }
    [JsonProperty("nexusFileId")]  public long   NexusFileId  { get; set; }

    /// <summary>
    /// True when this mapping was confirmed by a direct download through the app
    /// (nxm:// or built-in downloader). The community DB server applies a lower
    /// vote threshold for verified entries.
    /// </summary>
    [JsonProperty("verified", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool Verified { get; set; }

    public ContributeEntry() { }
    public ContributeEntry(string pakFileName, string metaUuid, int nexusModId, long nexusFileId,
                           bool verified = false)
    {
        PakFileName = pakFileName;
        MetaUuid    = metaUuid;
        NexusModId  = nexusModId;
        NexusFileId = nexusFileId;
        Verified    = verified;
    }
}
