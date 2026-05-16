

using DivinityModManager.Models;
using DivinityModManager.Models.Cache;
using DivinityModManager.Util;

using Newtonsoft.Json;

using System.Text;

namespace DivinityModManager.ModUpdater.Cache;

public interface IExternalModCacheHandler<T> where T : IModCacheData
{
	ModSourceType SourceType { get; }
	string FileName { get; }
	JsonSerializerSettings SerializerSettings { get; }

	bool IsEnabled { get; set; }
	T CacheData { get; set; }
	Task<bool> Update(IEnumerable<DivinityModData> mods, CancellationToken cts);
}

public static class IExternalModCacheDataExtensions
{
	public static async Task<T> LoadCacheAsync<T>(this IExternalModCacheHandler<T> cacheData, string currentAppVersion, CancellationToken cts) where T : IModCacheData
	{
		var filePath = DivinityApp.GetAppDirectory("Data", cacheData.FileName);

		if (File.Exists(filePath))
		{
			var cachedData = await DivinityJsonUtils.DeserializeFromPathAsync<T>(filePath, cts);
			if (cachedData != null)
			{
				if (string.IsNullOrEmpty(cachedData.LastVersion) || cachedData.LastVersion != currentAppVersion)
				{
					cachedData.LastUpdated = -1;
				}
				cachedData.CacheUpdated = true;
				return cachedData;
			}
		}
		return default;
	}

	public static async Task<bool> SaveCacheAsync<T>(this IExternalModCacheHandler<T> handler, bool updateLastTimestamp, string currentAppVersion, CancellationToken cts) where T : IModCacheData
	{
		try
		{
			var parentDir = DivinityApp.GetAppDirectory("Data");
			var filePath = Path.Combine(parentDir, handler.FileName);
			if (!Directory.Exists(parentDir)) Directory.CreateDirectory(parentDir);

			if (updateLastTimestamp)
			{
				handler.CacheData.LastUpdated = DateTimeOffset.Now.ToUnixTimeSeconds();
			}
			handler.CacheData.LastVersion = currentAppVersion;

			string contents = JsonConvert.SerializeObject(handler.CacheData, handler.SerializerSettings);

			var buffer = Encoding.UTF8.GetBytes(contents);
			using var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create,
				System.IO.FileAccess.Write, System.IO.FileShare.None, buffer.Length, true);
			await fs.WriteAsync(buffer, 0, buffer.Length, cts);

			return true;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error saving cache:\n{ex}");
		}
		return false;
	}

	public static bool DeleteCache<T>(this IExternalModCacheHandler<T> handler, bool permanent = false) where T : IModCacheData
	{
		try
		{
			var parentDir = DivinityApp.GetAppDirectory("Data");
			var filePath = Path.Combine(parentDir, handler.FileName);
			if (File.Exists(filePath))
			{
				RecycleBinHelper.DeleteFile(filePath, false, permanent);
				return true;
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error saving cache:\n{ex}");
		}
		return false;
	}
}
