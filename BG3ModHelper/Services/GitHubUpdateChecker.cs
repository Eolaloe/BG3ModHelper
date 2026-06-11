using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using BG3ModHelper.Models;
using Newtonsoft.Json.Linq;

namespace BG3ModHelper.Services;

public static class GitHubUpdateChecker
{
    private const string GitHubApiUrl  = "https://api.github.com/repos/Eolaloe/BG3ModHelper/releases/latest";
    private const string NexusApiUrl   = "https://api.nexusmods.com/v1/games/baldursgate3/mods/23204.json";
    public  const string ReleasesUrl   = "https://github.com/Eolaloe/BG3ModHelper/releases";
    public  const string NexusPageUrl  = "https://www.nexusmods.com/baldursgate3/mods/23204";

    private static readonly HttpClient _http = new();

    static GitHubUpdateChecker()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("BG3ModHelper", GetCurrentVersion()));
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public static bool ShouldCheck(AppSettings settings) =>
        settings.LastGitHubUpdateCheck is null ||
        (DateTime.UtcNow - settings.LastGitHubUpdateCheck.Value).TotalHours >= 24;

    public record VersionEntry(string Version, string Date, bool IsNewer);

    public record UpdateInfo(
        string        CurrentVersion,
        VersionEntry? GitHub,
        VersionEntry? Nexus)
    {
        public bool HasUpdate => (GitHub?.IsNewer ?? false) || (Nexus?.IsNewer ?? false);
    }

    public static async Task<UpdateInfo> CheckForUpdateAsync(AppSettings settings)
    {
        var currentAsm = Assembly.GetExecutingAssembly().GetName().Version;
        var currentStr = currentAsm?.ToString(4) ?? "unknown";

        var githubTask = FetchGitHubAsync(currentAsm);
        var nexusTask  = string.IsNullOrWhiteSpace(settings.NexusAPIKey)
            ? Task.FromResult<VersionEntry?>(null)
            : FetchNexusAsync(settings.NexusAPIKey, currentAsm);

        await Task.WhenAll(githubTask, nexusTask);

        return new UpdateInfo(currentStr, githubTask.Result, nexusTask.Result);
    }

    private static async Task<VersionEntry?> FetchGitHubAsync(Version? current)
    {
        try
        {
            var json = await _http.GetStringAsync(GitHubApiUrl);
            var obj  = JObject.Parse(json);

            var tagName = obj["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tagName)) return null;

            var versionStr = tagName.TrimStart('v');
            if (!Version.TryParse(versionStr, out var parsed)) return null;

            var date = "";
            if (DateTime.TryParse(obj["published_at"]?.ToString(), out var dt))
                date = dt.ToLocalTime().ToString("yyyy-MM-dd");

            return new VersionEntry(versionStr, date, parsed > current);
        }
        catch (Exception ex)
        {
            Logger.Warn($"GitHub update check failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<VersionEntry?> FetchNexusAsync(string apiKey, Version? current)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, NexusApiUrl);
            req.Headers.Add("apikey", apiKey);

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var obj        = JObject.Parse(await resp.Content.ReadAsStringAsync());
            var versionStr = obj["version"]?.ToString();
            if (string.IsNullOrEmpty(versionStr)) return null;

            var isNewer = Version.TryParse(versionStr, out var parsed) && parsed > current;

            var date = "";
            var updatedToken = obj["updated_time"];
            if (updatedToken != null)
            {
                if (updatedToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                    date = DateTimeOffset.FromUnixTimeSeconds(updatedToken.Value<long>())
                        .ToLocalTime().ToString("yyyy-MM-dd");
                else if (DateTime.TryParse(updatedToken.ToString(), out var dt))
                    date = dt.ToLocalTime().ToString("yyyy-MM-dd");
            }

            return new VersionEntry(versionStr, date, isNewer);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Nexus update check failed: {ex.Message}");
            return null;
        }
    }

    private static string GetCurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
