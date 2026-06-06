using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using BG3ModHelper.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3ModHelper.Services;

/// <summary>
/// Nexus Mods REST API client.
/// Base URL: https://api.nexusmods.com
/// Auth: apikey request header (personal API key).
///
/// Tracks rate limit headers and refuses further calls when limits are near.
/// All methods return null on failure (auth error, rate limit, network, 404)
/// rather than throwing — callers apply the quiet-fallback policy.
/// </summary>
public class NexusApi(string apiKey)
{
    private static readonly HttpClient _http = HttpClientFactory.Shared;

    private static readonly string _appVersion =
        typeof(NexusApi).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly string _apiKey = apiKey;

    // Rate limit state (updated from response headers)
    public int HourlyRemaining { get; private set; } = 100;
    public int DailyRemaining  { get; private set; } = 2500;

    /// <summary>Last error message from the API response body, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>HTTP status code of the last API response (0 = never called or network error).</summary>
    public int LastStatusCode { get; private set; }

    /// <summary>
    /// Returns a new instance with the same API key. Rate limit counters reset to defaults.
    /// Calling just before download avoids inheriting rate limit state from CheckUpdates.
    /// </summary>
    public NexusApi Clone() => new(_apiKey);

    /// <summary>
    /// True when the API key is set.
    /// Rate limits are managed server-side. Pre-blocking via internal counters causes
    /// downloads to fail even when the server would still allow them.
    /// A 429 from the server is handled in GetAsync by returning null.
    /// </summary>
    public bool CanMakeRequest() =>
        !string.IsNullOrWhiteSpace(_apiKey);

    // === Public API ===

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
            if (obj["files"] is not JArray files || files.Count == 0) return null;

            // Prefer MAIN category; fall back to most recent upload
            var candidates = files
                .Select(f => new NexusModFile
                {
                    NexusFileId           = f["file_id"]?.Value<long>() ?? 0,
                    NexusFileName         = f["name"]?.Value<string>() ?? "",
                    NexusFileVersion      = f["version"]?.Value<string>() ?? "",
                    NexusFileCategoryName = f["category_name"]?.Value<string>() ?? "",
                    UploadedAt            = DateTimeOffset
                        .FromUnixTimeSeconds(f["uploaded_timestamp"]?.Value<long>() ?? 0)
                        .UtcDateTime
                })
                .OrderByDescending(f => f.UploadedAt)
                .ToList();

            return candidates.FirstOrDefault(f =>
                       f.NexusFileCategoryName.Equals("MAIN", StringComparison.OrdinalIgnoreCase))
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
        if (json == null) return [];

        try
        {
            if (JObject.Parse(json)["files"] is not JArray files) return [];

            // Prefer is_primary; fall back to MAIN; then most recent
            var target = files.FirstOrDefault(f => f["is_primary"]?.Value<bool>() == true)
                      ?? files.FirstOrDefault(f =>
                             string.Equals(f["category_name"]?.Value<string>(),
                                 "MAIN", StringComparison.OrdinalIgnoreCase))
                      ?? files.OrderByDescending(f =>
                             f["uploaded_timestamp"]?.Value<long>() ?? 0)
                             .FirstOrDefault();

            var previewUrl = target?["content_preview_link"]?.Value<string>();
            if (string.IsNullOrEmpty(previewUrl)) return [];

            // file-metadata requires no authentication
            var metaResponse = await _http.GetStringAsync(previewUrl);
            var root = JObject.Parse(metaResponse);
            if (root["children"] is not JArray rootChildren) return [];

            return [.. CollectPakNames(rootChildren)];
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusApi.GetPakNamesAsync({modId}): {ex.Message}");
            return [];
        }
    }

    /// Recursively collects .pak filenames from nested content_preview_link children.
    private static IEnumerable<string> CollectPakNames(JArray children)
    {
        foreach (var node in children)
        {
            var name = node["name"]?.Value<string>() ?? "";
            if (name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                yield return name;
            }
            else if (node["children"] is JArray nested)
            {
                foreach (var pak in CollectPakNames(nested))
                    yield return pak;
            }
        }
    }

    /// <summary>
    /// Returns a temporary download URL for a specific file.
    /// Without nxm token: Premium accounts only (free users get HTTP 403).
    /// With nxm token (key + expires from nxm:// URL): works for all users including Free.
    /// Must be called immediately before downloading (URL expires quickly).
    /// </summary>
    public async Task<string?> GetDownloadUrlAsync(
        int modId, long fileId,
        string? nxmKey = null, long? nxmExpires = null, int? nxmUserId = null)
    {
        var path = $"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}/files/{fileId}/download_link.json";
        if (!string.IsNullOrEmpty(nxmKey) && nxmExpires.HasValue)
        {
            path += $"?key={nxmKey}&expires={nxmExpires.Value}";
            if (nxmUserId.HasValue)
                path += $"&user_id={nxmUserId.Value}";
        }

        var json = await GetAsync(path);
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

    /// <summary>
    /// Returns the version string and display name of a specific file as shown on the Nexus mod page.
    /// More accurate than meta.lsx version (which can contain author typos or lack patch suffix).
    /// Returns (null, null) on failure.
    /// </summary>
    public async Task<(string? Version, string? Name)> GetFileMetaAsync(int modId, long fileId)
    {
        var json = await GetAsync(
            $"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}/files/{fileId}.json");
        if (json == null) return (null, null);

        try
        {
            var obj = JObject.Parse(json);
            return (obj["version"]?.Value<string>(), obj["name"]?.Value<string>());
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusApi.GetFileMetaAsync({modId},{fileId}) parse error: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Returns the changelog text for a specific version of a mod.
    /// Looks up the exact version first; falls back to the latest entry if not found.
    /// Returns null if no changelogs exist or the request fails.
    /// </summary>
    public async Task<string?> GetChangelogAsync(int modId, string targetVersion)
    {
        var json = await GetAsync(
            $"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}/changelogs.json");
        if (json == null) return null;

        try
        {
            var obj = JObject.Parse(json);
            if (!obj.Properties().Any()) return null;

            var target = targetVersion.TrimStart('v', 'V').Trim();

            // Try exact version match first
            foreach (var prop in obj.Properties())
            {
                if (string.Equals(prop.Name.TrimStart('v', 'V').Trim(), target,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return FormatChangelog(prop.Name, prop.Value as JArray);
                }
            }

            // Fall back to latest version entry
            var latest = obj.Properties().Last();
            return FormatChangelog(latest.Name, latest.Value as JArray);
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusApi.GetChangelogAsync({modId}) parse error: {ex.Message}");
            return null;
        }
    }

    private static string? FormatChangelog(string version, JArray? items)
    {
        if (items == null || items.Count == 0) return null;
        var lines = items.Select(i => "• " + StripHtml(i.Value<string>() ?? "")).ToList();
        return $"v{version.TrimStart('v', 'V')}:\n" + string.Join("\n", lines);
    }

    private static readonly System.Text.RegularExpressions.Regex _htmlTagRegex =
        new(@"<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        var text = _htmlTagRegex.Replace(html, "");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    /// <summary>
    /// Returns the display name of a mod.
    /// Used when the local DB has no entry for a mod but the modId is known.
    /// Returns null on failure.
    /// </summary>
    public async Task<string?> GetModNameAsync(int modId)
    {
        var json = await GetAsync($"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/{modId}.json");
        if (json == null) return null;
        try { return JObject.Parse(json)["name"]?.Value<string>(); }
        catch (Exception ex)
        {
            Logger.Warn($"NexusApi.GetModNameAsync({modId}) parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the set of Nexus mod IDs updated within the last month for BG3.
    /// Single API call — use as a pre-filter before per-mod DB/API checks.
    /// Returns empty set on failure so callers fall back to DB-only logic.
    /// </summary>
    public async Task<HashSet<int>> GetRecentlyUpdatedModIdsAsync()
    {
        var json = await GetAsync(
            $"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/updated.json?period=1m");
        if (json == null) return [];

        try
        {
            var arr = JArray.Parse(json);
            return arr
                .Select(e => e["mod_id"]?.Value<int>() ?? 0)
                .Where(id => id > 0)
                .ToHashSet();
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusApi.GetRecentlyUpdatedModIdsAsync parse error: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Searches for a mod by the MD5 hash of its archive file.
    /// Returns null on 404 (hash not found) or any other failure.
    /// </summary>
    public async Task<NexusMd5Result?> Md5SearchAsync(string md5)
    {
        var json = await GetAsync(
            $"/v1/games/{Constants.NEXUS_GAME_DOMAIN}/mods/md5_search/{md5}.json");
        if (json == null) return null;

        try
        {
            var arr   = JArray.Parse(json);
            var first = arr.FirstOrDefault();
            if (first == null) return null;

            var mod  = first["mod"];
            var file = first["file_details"];
            if (mod == null || file == null) return null;

            return new NexusMd5Result(
                ModId:       mod["mod_id"]?.Value<int>()    ?? 0,
                ModName:     mod["name"]?.Value<string>()   ?? "",
                FileId:      file["file_id"]?.Value<long>() ?? 0,
                FileName:    file["file_name"]?.Value<string>() ?? "",
                FileVersion: file["version"]?.Value<string>()   ?? ""
            );
        }
        catch (Exception ex)
        {
            Logger.Warn($"NexusApi.Md5SearchAsync parse error: {ex.Message}");
            return null;
        }
    }

    // === HTTP internals ===

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
            request.Headers.Add("User-Agent", $"BG3ModHelper/{_appVersion}");
            request.Headers.Add("Application-Name", "BG3ModHelper");
            request.Headers.Add("Application-Version", _appVersion);
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

            LastStatusCode = (int)response.StatusCode;

            if ((int)response.StatusCode == 429)
            {
                Logger.Warn($"NexusApi: rate limit exceeded (429) — {path}");
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Logger.Warn($"NexusApi: forbidden (403) — {path} (Premium required or manager downloads disabled)");
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Logger.Warn($"NexusApi: not found (404) — {path}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = "";
                try { body = await response.Content.ReadAsStringAsync(); } catch { }
                LastError = TryExtractErrorMessage(body) ?? $"HTTP {(int)response.StatusCode}";
                Logger.Warn($"NexusApi: HTTP {(int)response.StatusCode} — {path}" +
                            (string.IsNullOrEmpty(body) ? "" : $"\n  Body: {body}"));
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

    private static string? TryExtractErrorMessage(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try { return JObject.Parse(body)["message"]?.Value<string>(); }
        catch { return null; }
    }

    private void UpdateRateLimits(HttpResponseMessage response)
    {
        var prevHourly = HourlyRemaining;
        var prevDaily  = DailyRemaining;

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

        // Only log on change — prevents spam during parallel calls
        if (HourlyRemaining <= 5 && HourlyRemaining != prevHourly)
            Logger.Warn($"NexusApi: hourly rate limit low ({HourlyRemaining} remaining)");
        if (DailyRemaining <= 10 && DailyRemaining != prevDaily)
            Logger.Warn($"NexusApi: daily rate limit low ({DailyRemaining} remaining)");
    }
}
