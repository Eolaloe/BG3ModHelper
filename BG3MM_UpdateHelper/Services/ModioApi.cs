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
    private static readonly HttpClient _http = HttpClientFactory.Shared;

    private static readonly string _appVersion =
        typeof(ModioApi).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly string _apiKey;

    private static readonly string CacheFilePath = Path.Combine(
        SettingsStore.GetDataFolder(), "modiodata.json");

    public ModioApi(string apiKey)
    {
        _apiKey = apiKey;
    }

    public bool CanMakeRequest() => !string.IsNullOrWhiteSpace(_apiKey);

    // === Public API ===

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
            return await BuildModioDataAsync(obj);
        }
        catch (Exception ex)
        {
            Logger.Error($"ModioApi.GetModInfoAsync({publishHandle}) parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Shared parser. Applies platforms[].modfile_live fallback when modfile is empty.
    /// May make one additional API call when fallback is triggered.
    /// </summary>
    private async Task<ModioModData> BuildModioDataAsync(JObject mod)
    {
        var id      = mod["id"]?.Value<ulong>() ?? 0;
        var modfile = mod["modfile"] as JObject;

        // Fall back to platforms[].modfile_live when modfile is missing or empty
        if (modfile == null || !modfile.HasValues)
        {
            var platforms = mod["platforms"] as JArray;
            var liveFileId = platforms
                ?.Select(p => p["modfile_live"]?.Value<long?>())
                .FirstOrDefault(fid => fid.HasValue && fid.Value > 0);

            if (liveFileId.HasValue)
            {
                var fileJson = await GetAsync(
                    $"/v1/games/{Constants.MODIO_GAME_ID}/mods/{id}/files/{liveFileId.Value}");
                if (fileJson != null)
                    modfile = JObject.Parse(fileJson);
            }
        }

        return new ModioModData
        {
            ModioModId      = id,
            ModioModName    = mod["name"]?.Value<string>() ?? "",
            ModioSummary    = mod["summary"]?.Value<string>() ?? "",
            ModioProfileUrl = mod["profile_url"]?.Value<string>() ?? "",
            ModioFileVersion = modfile?["version"]?.Value<string>() ?? "",
            ModioDateUpdated = DateTimeOffset
                .FromUnixTimeSeconds(mod["date_updated"]?.Value<long>() ?? 0)
                .UtcDateTime,
            ModioFileId     = modfile?["id"]?.Value<long>() ?? 0
        };
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
                ModioFileId      = first["id"]?.Value<long>() ?? 0,
                ModioFileVersion = first["version"]?.Value<string>() ?? "",
                ModioFileName    = first["filename"]?.Value<string>() ?? "",
                ModioBinaryUrl   = first["download"]?["binary_url"]?.Value<string>() ?? "",
                DateExpires      = first["download"]?["date_expires"]?.Value<long>() ?? 0
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"ModioApi.GetLatestFileAsync({publishHandle}) parse error: {ex.Message}");
            return null;
        }
    }


    // === Batch query ===

    /// <summary>
    /// Fetches metadata for multiple mods in batches of 100.
    /// Far more efficient than individual calls for cache refresh.
    /// Applies the same modfile_live fallback as single-mod lookup.
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
                var arr = JObject.Parse(json)["data"] as JArray;
                if (arr == null) continue;

                foreach (var mod in arr.OfType<JObject>())
                {
                    var data = await BuildModioDataAsync(mod);
                    if (data.ModioModId > 0)
                        result[data.ModioModId] = data;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ModioApi.GetModsBatchAsync parse error: {ex.Message}");
            }
        }

        return result;
    }

    // === Cache helpers ===


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

    // === HTTP internals ===

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
            request.Headers.Add("User-Agent", $"BG3MM_UpdateHelper/{_appVersion}");

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
