using System.IO;
using System.Net.Http;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Models.Cache;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// mod.io REST API client for BG3 (game ID 6715).
/// Base URL: https://g-6715.modapi.io/v1
/// Auth: ?api_key={key} query parameter.
///
/// All methods return null on failure (auth error, 404, network) rather than
/// throwing — callers apply the quiet-fallback policy from the spec §4.7.
/// </summary>
public class ModioApi
{
    private static readonly HttpClient _http = new();

    private readonly string _apiKey;

    private static readonly string CacheFilePath = Path.Combine(
        SettingsStore.GetDataFolder(), "modiodata.json");

    public ModioApi(string apiKey)
    {
        _apiKey = apiKey;
    }

    public bool CanMakeRequest() => !string.IsNullOrWhiteSpace(_apiKey);

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches metadata for a mod by its PublishHandle (= mod.io global mod ID).
    /// Returns null on 404 (mod delisted / console-only) or any other error.
    /// </summary>
    public async Task<ModioModData?> GetModInfoAsync(ulong publishHandle)
    {
        var json = await GetAsync($"/v1/games/{Constants.MODIO_GAME_ID}/mods/{publishHandle}");
        if (json == null) return null;

        try
        {
            var obj = JObject.Parse(json);

            // The "modfile" sub-object contains the latest file info
            var modfile = obj["modfile"] as JObject;

            // Fall back to platforms[].modfile_live when modfile is an empty object
            if (modfile == null || !modfile.HasValues)
            {
                var platforms = obj["platforms"] as JArray;
                var liveFileId = platforms
                    ?.Select(p => p["modfile_live"]?.Value<long?>())
                    .FirstOrDefault(id => id.HasValue && id.Value > 0);

                if (liveFileId.HasValue)
                {
                    var fileJson = await GetAsync(
                        $"/v1/games/{Constants.MODIO_GAME_ID}/mods/{publishHandle}/files/{liveFileId.Value}");
                    if (fileJson != null)
                        modfile = JObject.Parse(fileJson);
                }
            }

            return new ModioModData
            {
                ModId         = publishHandle,
                Name          = obj["name"]?.Value<string>() ?? "",
                Summary       = obj["summary"]?.Value<string>() ?? "",
                ProfileUrl    = obj["profile_url"]?.Value<string>() ?? "",
                LatestVersion = modfile?["version"]?.Value<string>() ?? "",
                UpdatedAt     = DateTimeOffset
                    .FromUnixTimeSeconds(obj["date_updated"]?.Value<long>() ?? 0)
                    .UtcDateTime,
                LatestFileId  = modfile?["id"]?.Value<long>() ?? 0
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"ModioApi.GetModInfoAsync({publishHandle}) parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches the latest modfile (with a fresh download URL) for a mod.
    /// Always call this immediately before downloading — URLs expire.
    /// Returns null on any error.
    /// </summary>
    public async Task<ModioModFile?> GetLatestFileAsync(ulong publishHandle)
    {
        // Use files endpoint — modfile is returned as empty object for single mod queries
        var json = await GetAsync(
            $"/v1/games/{Constants.MODIO_GAME_ID}/mods/{publishHandle}/files?_sort=-date_added&_limit=1");
        if (json == null) return null;

        try
        {
            var arr   = JObject.Parse(json)["data"] as JArray;
            var first = arr?.FirstOrDefault() as JObject;
            if (first == null) return null;

            return new ModioModFile
            {
                Id          = first["id"]?.Value<long>() ?? 0,
                Version     = first["version"]?.Value<string>() ?? "",
                FileName    = first["filename"]?.Value<string>() ?? "",
                BinaryUrl   = first["download"]?["binary_url"]?.Value<string>() ?? "",
                DateExpires = first["download"]?["date_expires"]?.Value<long>() ?? 0
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"ModioApi.GetLatestFileAsync({publishHandle}) parse error: {ex.Message}");
            return null;
        }
    }


    // ── Batch query ───────────────────────────────────────────────────────

    /// <summary>
    /// Fetches metadata for multiple mods in batches of 100.
    /// Far more efficient than individual calls for cache refresh.
    /// </summary>
    public async Task<Dictionary<ulong, ModioModData>> GetModsBatchAsync(
        IEnumerable<ulong> publishHandles)
    {
        var result  = new Dictionary<ulong, ModioModData>();
        var allIds  = publishHandles.ToList();
        const int batchSize = 100;

        for (int i = 0; i < allIds.Count; i += batchSize)
        {
            var batch  = allIds.Skip(i).Take(batchSize);
            var idList = string.Join(",", batch);
            var json   = await GetAsync(
                $"/v1/games/{Constants.MODIO_GAME_ID}/mods?id-in={idList}&_limit={batchSize}");
            if (json == null) continue;

            try
            {
                var arr = Newtonsoft.Json.Linq.JObject.Parse(json)["data"]
                          as Newtonsoft.Json.Linq.JArray;
                if (arr == null) continue;

                foreach (var mod in arr)
                {
                    var id      = mod["id"]?.Value<ulong>() ?? 0;
                    var modfile = mod["modfile"] as Newtonsoft.Json.Linq.JObject;
                    result[id]  = new ModioModData
                    {
                        ModId         = id,
                        Name          = mod["name"]?.Value<string>() ?? "",
                        Summary       = mod["summary"]?.Value<string>() ?? "",
                        ProfileUrl    = mod["profile_url"]?.Value<string>() ?? "",
                        LatestVersion = modfile?["version"]?.Value<string>() ?? "",
                        UpdatedAt     = DateTimeOffset
                            .FromUnixTimeSeconds(mod["date_updated"]?.Value<long>() ?? 0)
                            .UtcDateTime,
                        LatestFileId  = modfile?["id"]?.Value<long>() ?? 0
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ModioApi.GetModsBatchAsync parse error: {ex.Message}");
            }
        }

        return result;
    }

    // ── Cache helpers ─────────────────────────────────────────────────────


    public static ModioCachedData LoadCache()
    {
        if (!File.Exists(CacheFilePath)) return new ModioCachedData();
        try
        {
            var json = File.ReadAllText(CacheFilePath);
            return JsonConvert.DeserializeObject<ModioCachedData>(json)
                   ?? new ModioCachedData();
        }
        catch { return new ModioCachedData(); }
    }

    public static void SaveCache(ModioCachedData cache)
    {
        try
        {
            var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
            File.WriteAllText(CacheFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"ModioApi.SaveCache error: {ex.Message}");
        }
    }

    // ── HTTP internals ────────────────────────────────────────────────────

    /// <summary>
    /// Performs a GET request against the mod.io API.
    /// Returns null on any error — never throws.
    /// </summary>
    private async Task<string?> GetAsync(string path)
    {
        if (!CanMakeRequest())
        {
            Logger.Warn("ModioApi: no API key set — skipping request");
            return null;
        }

        // Append api_key as a query parameter
        var separator = path.Contains('?') ? '&' : '?';
        var url = $"{Constants.MODIO_API_BASE}{path}{separator}api_key={_apiKey}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "BG3MM_UpdateHelper/0.1");

            using var response = await _http.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Logger.Warn("ModioApi: invalid API key (401)");
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 404 = mod delisted / console-only / private — quiet fallback per spec §4.7
                Logger.Info($"ModioApi: 404 — {path} (mod unavailable, skipping silently)");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"ModioApi: HTTP {(int)response.StatusCode} — {path}");
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"ModioApi: request failed — {path} — {ex.Message}");
            return null;
        }
    }
}
