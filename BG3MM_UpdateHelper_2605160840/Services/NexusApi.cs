using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Models.Cache;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Nexus Mods REST API client.
/// Base URL: https://api.nexusmods.com
/// Auth: apikey request header (personal API key).
///
/// Tracks rate limit headers and refuses further calls when limits are near.
/// All methods return null on failure (auth error, rate limit, network, 404)
/// rather than throwing — callers apply the quiet-fallback policy.
/// </summary>
public class NexusApi
{
    private static readonly HttpClient _http = new();

    private readonly string _apiKey;

    // Rate limit state (updated from response headers)
    public int HourlyRemaining { get; private set; } = 100;
    public int DailyRemaining  { get; private set; } = 2500;

    // Cache file path
    private static readonly string CacheFilePath = Path.Combine(
        SettingsStore.GetDataFolder(), "nexusdata.json");

    public NexusApi(string apiKey)
    {
        _apiKey = apiKey;
    }

    /// <summary>True when the key is set and rate limits allow further calls.</summary>
    public bool CanMakeRequest() =>
        !string.IsNullOrWhiteSpace(_apiKey) &&
        HourlyRemaining > 5 &&
        DailyRemaining  > 5;

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the API key and returns user info including Premium status.
    /// Returns null if the key is invalid or the request fails.
    /// </summary>
    public async Task<NexusUserInfo?> ValidateUserAsync()
    {
        var json = await GetAsync("/v1/users/validate.json");
        if (json == null) return null;

        try
        {
            var obj = JObject.Parse(json);
            return new NexusUserInfo
            {
                UserId    = obj["user_id"]?.Value<int>() ?? 0,
                Name      = obj["name"]?.Value<string>() ?? "",
                IsPremium = obj["is_premium"]?.Value<bool>() ?? false,
                Email     = obj["email"]?.Value<string>() ?? ""
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"NexusApi.ValidateUserAsync parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fetches metadata for one mod by its Nexus mod ID.</summary>
    public async Task<NexusModData?> GetModInfoAsync(int modId)
    {
        var json = await GetAsync($"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}.json");
        if (json == null) return null;

        try
        {
            var obj = JObject.Parse(json);
            return new NexusModData
            {
                ModId      = obj["mod_id"]?.Value<int>() ?? modId,
                Name       = obj["name"]?.Value<string>() ?? "",
                Summary    = obj["summary"]?.Value<string>() ?? "",
                Version    = obj["version"]?.Value<string>() ?? "",
                ProfileUrl = $"https://www.nexusmods.com/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}",
                UpdatedAt  = DateTimeOffset
                    .FromUnixTimeSeconds(obj["updated_timestamp"]?.Value<long>() ?? 0)
                    .UtcDateTime
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"NexusApi.GetModInfoAsync({modId}) parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the latest MAIN file for a mod.
    /// Falls back to the most-recently-uploaded file if no MAIN category exists.
    /// </summary>
    public async Task<NexusModFile?> GetLatestFileAsync(int modId)
    {
        var json = await GetAsync(
            $"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}/files.json");
        if (json == null) return null;

        try
        {
            var obj   = JObject.Parse(json);
            var files = obj["files"] as JArray;
            if (files == null || files.Count == 0) return null;

            // Prefer MAIN category; fall back to most recent upload
            var candidates = files
                .Select(f => new NexusModFile
                {
                    FileId        = f["file_id"]?.Value<long>() ?? 0,
                    Name          = f["name"]?.Value<string>() ?? "",
                    Version       = f["version"]?.Value<string>() ?? "",
                    CategoryName  = f["category_name"]?.Value<string>() ?? "",
                    UploadedAt    = DateTimeOffset
                        .FromUnixTimeSeconds(f["uploaded_timestamp"]?.Value<long>() ?? 0)
                        .UtcDateTime
                })
                .OrderByDescending(f => f.UploadedAt)
                .ToList();

            return candidates.FirstOrDefault(f =>
                       f.CategoryName.Equals("MAIN", StringComparison.OrdinalIgnoreCase))
                   ?? candidates.First();
        }
        catch (Exception ex)
        {
            Logger.Error($"NexusApi.GetLatestFileAsync({modId}) parse error: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Returns .pak filenames inside the latest zip for a mod.
    /// Uses files.json → content_preview_link → file-metadata (no auth needed).
    /// </summary>
    public async Task<List<string>> GetPakNamesAsync(int modId)
    {
        var json = await GetAsync(
            $"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}/files.json");
        if (json == null) return new();

        try
        {
            var files = JObject.Parse(json)["files"] as JArray;
            if (files == null) return new();

            // is_primary 우선, 없으면 MAIN, 없으면 최신
            var target = files.FirstOrDefault(f => f["is_primary"]?.Value<bool>() == true)
                      ?? files.FirstOrDefault(f =>
                             string.Equals(f["category_name"]?.Value<string>(),
                                 "MAIN", StringComparison.OrdinalIgnoreCase))
                      ?? files.OrderByDescending(f =>
                             f["uploaded_timestamp"]?.Value<long>() ?? 0)
                             .FirstOrDefault();

            var previewUrl = target?["content_preview_link"]?.Value<string>();
            if (string.IsNullOrEmpty(previewUrl)) return new();

            // file-metadata는 인증 불필요
            using var http   = new System.Net.Http.HttpClient();
            var metaResponse = await http.GetStringAsync(previewUrl);
            var children     = JObject.Parse(metaResponse)["children"] as JArray;
            if (children == null) return new();

            return children
                .Where(c => c["name"]?.Value<string>()
                    ?.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) == true)
                .Select(c => c["name"]!.Value<string>()!)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusApi.GetPakNamesAsync({modId}): {ex.Message}");
            return new();
        }
    }

    /// <summary>
    /// Returns a temporary download URL for a specific file.
    /// Premium accounts only — returns null for free users (HTTP 403).
    /// Must be called immediately before downloading (URL expires quickly).
    /// </summary>
    public async Task<string?> GetDownloadUrlAsync(int modId, long fileId)
    {
        var json = await GetAsync(
            $"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}/files/{fileId}/download_link.json");
        if (json == null) return null;

        try
        {
            var arr = JArray.Parse(json);
            // First element contains the CDN URL
            return arr.FirstOrDefault()?["URI"]?.Value<string>();
        }
        catch (Exception ex)
        {
            Logger.Error($"NexusApi.GetDownloadUrlAsync({modId},{fileId}) parse error: {ex.Message}");
            return null;
        }
    }

    // ── Cache helpers ─────────────────────────────────────────────────────

    public static NexusCachedData LoadCache()
    {
        if (!File.Exists(CacheFilePath)) return new NexusCachedData();
        try
        {
            var json = File.ReadAllText(CacheFilePath);
            return JsonConvert.DeserializeObject<NexusCachedData>(json)
                   ?? new NexusCachedData();
        }
        catch { return new NexusCachedData(); }
    }

    public static void SaveCache(NexusCachedData cache)
    {
        try
        {
            var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
            File.WriteAllText(CacheFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"NexusApi.SaveCache error: {ex.Message}");
        }
    }

    // ── HTTP internals ────────────────────────────────────────────────────

    /// <summary>
    /// Performs an authenticated GET request against the Nexus API base URL.
    /// Updates rate limit counters from response headers.
    /// Returns null on any error (auth, rate limit, network, 4xx/5xx).
    /// </summary>
    private async Task<string?> GetAsync(string path)
    {
        if (!CanMakeRequest())
        {
            Logger.Warn("NexusApi: rate limit reached — skipping request");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, Constants.NEXUS_API_BASE + path);

            request.Headers.Add("apikey", _apiKey);
            request.Headers.Add("User-Agent", "BG3MM_UpdateHelper/0.1");
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request);

            // Update rate limit counters regardless of status code
            UpdateRateLimits(response);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Logger.Warn("NexusApi: invalid API key (401)");
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Logger.Warn($"NexusApi: forbidden (403) — {path} (Premium required?)");
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Warn($"NexusApi: not found (404) — {path}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"NexusApi: HTTP {(int)response.StatusCode} — {path}");
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"NexusApi: request failed — {path} — {ex.Message}");
            return null;
        }
    }

    private void UpdateRateLimits(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RL-Hourly-Remaining", out var hourly) &&
            int.TryParse(hourly.FirstOrDefault(), out var h))
        {
            HourlyRemaining = h;
        }

        if (response.Headers.TryGetValues("X-RL-Daily-Remaining", out var daily) &&
            int.TryParse(daily.FirstOrDefault(), out var d))
        {
            DailyRemaining = d;
        }

        if (HourlyRemaining <= 5)
            Logger.Warn($"NexusApi: hourly rate limit low ({HourlyRemaining} remaining)");
        if (DailyRemaining <= 10)
            Logger.Warn($"NexusApi: daily rate limit low ({DailyRemaining} remaining)");
    }
}
