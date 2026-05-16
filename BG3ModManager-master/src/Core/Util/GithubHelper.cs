using Newtonsoft.Json.Linq;

namespace DivinityModManager.Util;

public static class GithubHelper
{
	private static readonly string GIT_URL_REPO_LATEST = "https://api.github.com/repos/{0}/releases/latest";
	private static readonly string GIT_URL_REPO_RELEASES = "https://api.github.com/repos/{0}/releases";

	public static async Task<string> GetLatestReleaseDataAsync(string repo, CancellationToken t)
	{
		return await WebHelper.DownloadUrlAsStringAsync(String.Format(GIT_URL_REPO_LATEST, repo), t);
	}

	public static async Task<string> GetAllReleaseDataAsync(string repo, CancellationToken t)
	{
		return await WebHelper.DownloadUrlAsStringAsync(String.Format(GIT_URL_REPO_RELEASES, repo), t);
	}

	private static string GetBrowserDownloadUrl(string dataString)
	{
		var jsonData = DivinityJsonUtils.SafeDeserialize<Dictionary<string, object>>(dataString);
		if (jsonData != null)
		{
			if (jsonData.TryGetValue("assets", out var assetsArray))
			{
				JArray assets = (JArray)assetsArray;
				foreach (var obj in assets.Children<JObject>())
				{
					if (obj.TryGetValue("browser_download_url", StringComparison.OrdinalIgnoreCase, out var browserUrl))
					{
						return browserUrl.ToString();
					}
				}
			}
#if DEBUG
			var lines = jsonData.Select(kvp => kvp.Key + ": " + kvp.Value.ToString());
			DivinityApp.Log($"Can't find 'browser_download_url' in:\n{String.Join(Environment.NewLine, lines)}");
#endif
		}
		return "";
	}

	public static async Task<string> GetLatestReleaseLinkAsync(string repo, CancellationToken t)
	{
		return GetBrowserDownloadUrl(await WebHelper.DownloadUrlAsStringAsync(String.Format(GIT_URL_REPO_LATEST, repo), t));
	}
}
