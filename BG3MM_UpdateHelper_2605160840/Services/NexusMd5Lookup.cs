using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Models.Cache;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Resolves Nexus mod IDs for installed mods by computing each .pak file's MD5
/// and querying the Nexus md5_search endpoint.
///
/// Nexus md5_search: GET /v1/games/baldursgate3/mods/md5_search/{md5}.json
/// Returns the mod_id + file_id for any file that has been uploaded to Nexus.
///
/// This lookup only needs to run once per .pak — results are cached in
/// nexusmd5.json keyed by MD5. If the .pak changes (mod update), the old MD5
/// entry is simply unused and a new one is computed on the next run.
///
/// Rate limit: 500 req/hour, 20 000 req/day.
/// With ~1400 mods the first run takes about 3 minutes at 8 req/sec throttle.
/// Subsequent runs are instant (cache hit).
/// </summary>
public class NexusMd5Lookup
{
    private static readonly HttpClient _http = new();

    private static readonly string CacheFilePath = Path.Combine(
        SettingsStore.GetDataFolder(), "nexusmd5.json");

    private readonly string _apiKey;

    // Parallel requests — 20 concurrent stays well under 500/hour rate limit
    private const int MaxParallelRequests = 20;

    public NexusMd5Lookup(string apiKey)
    {
        _apiKey = apiKey;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// For each mod that has no NexusModId yet, compute the .pak MD5 and
    /// query Nexus to resolve the mod ID. Updates InstalledMod.NexusModId
    /// in place. Skips mods whose MD5 is already in the cache.
    /// </summary>
    public async Task ResolveMissingNexusIdsAsync(
        List<InstalledMod> mods,
        IProgress<(int done, int total, string name)>? progress = null)
    {
        var cache = LoadCache();

        // First pass: apply already-cached UUID mappings
        foreach (var mod in mods)
        {
            if (mod.NexusModId.HasValue) continue;

            if (cache.UuidToNexusModId.TryGetValue(mod.UUID, out var cachedId))
            {
                mod.NexusModId = cachedId == 0 ? null : cachedId;
            }
        }

        // Second pass: identify mods that still need lookup
        // Skip mods already on mod.io — they're covered by the mod.io update check.
        var needLookup = mods
            .Where(m => !m.NexusModId.HasValue &&
                        m.PublishHandle == 0 &&        // skip mod.io mods
                        File.Exists(m.PakFilePath) &&
                        !string.IsNullOrEmpty(m.UUID))
            .ToList();

        if (needLookup.Count == 0)
        {
            Logger.Info("NexusMd5Lookup: all mods already resolved (cache hit)");
            return;
        }

        Logger.Info($"NexusMd5Lookup: resolving {needLookup.Count} mods via md5_search " +
                    $"(skipped {mods.Count(m => m.PublishHandle != 0)} mod.io mods)");

        var done    = 0;
        var total   = needLookup.Count;
        var changed = false;
        var lockObj = new object();

        // Split into chunks and process in parallel
        var semaphore = new SemaphoreSlim(MaxParallelRequests);
        var tasks = needLookup.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
                // Compute MD5
                string md5;
                try
                {
                    md5 = ComputeMd5(mod.PakFilePath);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"NexusMd5Lookup: MD5 failed for {Path.GetFileName(mod.PakFilePath)} — {ex.Message}");
                    return;
                }

                // Cache hit on MD5?
                int? nexusId;
                lock (lockObj)
                {
                    if (cache.Md5ToNexusModId.TryGetValue(md5, out var knownId))
                    {
                        mod.NexusModId = knownId == 0 ? null : knownId;
                        cache.UuidToNexusModId[mod.UUID] = knownId;
                        return;
                    }
                }

                // API call
                nexusId = await QueryMd5Async(md5);

                var storeId = nexusId ?? 0;
                lock (lockObj)
                {
                    cache.Md5ToNexusModId[md5]      = storeId;
                    cache.UuidToNexusModId[mod.UUID] = storeId;
                    mod.NexusModId                   = nexusId;
                    changed                          = true;
                }
            }
            finally
            {
                semaphore.Release();
                var current = Interlocked.Increment(ref done);
                progress?.Report((current, total, mod.Name));
            }
        });

        await Task.WhenAll(tasks);

        if (changed)
        {
            SaveCache(cache);
            var resolved = needLookup.Count(m => m.NexusModId.HasValue);
            Logger.Info($"NexusMd5Lookup: resolved {resolved} / {needLookup.Count} mods");
        }
    }

    // -------------------------------------------------------------------------
    // Nexus md5_search
    // -------------------------------------------------------------------------

    private async Task<int?> QueryMd5Async(string md5)
    {
        var url = $"{Constants.NEXUS_API_BASE}/v1/games/{Constants.NEXUS_GAME_DOMAIN}" +
                  $"/mods/md5_search/{md5}.json";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("apikey", _apiKey);
            request.Headers.Add("User-Agent", "BG3MM_UpdateHelper/0.1");
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // file not on Nexus

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Logger.Warn("NexusMd5Lookup: invalid API key (401)");
                return null;
            }

            if ((int)response.StatusCode == 429)
            {
                Logger.Warn("NexusMd5Lookup: rate limit hit (429) — waiting 60s");
                await Task.Delay(TimeSpan.FromSeconds(60));
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"NexusMd5Lookup: HTTP {(int)response.StatusCode} for {md5}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var arr  = JArray.Parse(json);

            // Response is an array; first element contains mod info
            var modId = arr.FirstOrDefault()?["mod"]?["mod_id"]?.Value<int>();
            return modId;
        }
        catch (Exception ex)
        {
            Logger.Error($"NexusMd5Lookup: request failed for {md5} — {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // MD5 helper
    // -------------------------------------------------------------------------

    private static string ComputeMd5(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = MD5.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // -------------------------------------------------------------------------
    // Cache I/O
    // -------------------------------------------------------------------------

    public static NexusMd5Cache LoadCache()
    {
        if (!File.Exists(CacheFilePath)) return new NexusMd5Cache();
        try
        {
            var json = File.ReadAllText(CacheFilePath);
            return JsonConvert.DeserializeObject<NexusMd5Cache>(json)
                   ?? new NexusMd5Cache();
        }
        catch { return new NexusMd5Cache(); }
    }

    public static void SaveCache(NexusMd5Cache cache)
    {
        try
        {
            var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
            File.WriteAllText(CacheFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"NexusMd5Lookup.SaveCache: {ex.Message}");
        }
    }
}
