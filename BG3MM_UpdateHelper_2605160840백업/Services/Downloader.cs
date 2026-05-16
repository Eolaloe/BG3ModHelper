using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Downloads a mod file from a URL, extracts .pak files from the zip,
/// backs up the existing .pak, and installs the new one into the Mods folder.
/// </summary>
public static class Downloader
{
    private static readonly HttpClient _http = new();

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Full pipeline: download → extract → backup → install.
    /// Returns the installed .pak path on success.
    /// </summary>
    public static async Task<string> DownloadAndInstallAsync(
        string downloadUrl,
        string existingPakPath,
        string modsFolder,
        bool backupEnabled = false,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir  = Path.Combine(Path.GetTempPath(), "BG3MM_UpdateHelper", Guid.NewGuid().ToString("N"));
        var tempZip  = Path.Combine(tempDir, "download.zip");
        var extractDir = Path.Combine(tempDir, "extracted");

        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(extractDir);

            // Step 1: Download zip
            progress?.Report(new DownloadProgress("Downloading...", 0));
            await DownloadFileAsync(downloadUrl, tempZip, progress, ct);

            // Step 2: Extract .pak files from zip
            progress?.Report(new DownloadProgress("Extracting...", 95));
            var pakFiles = ExtractPakFiles(tempZip, extractDir);

            if (pakFiles.Count == 0)
                throw new InvalidOperationException("No .pak files found in downloaded archive.");

            // Step 3: Match pak to install — prefer same filename, else first
            var existingName = Path.GetFileName(existingPakPath);
            var sourceFile   = pakFiles.FirstOrDefault(p =>
                                   string.Equals(Path.GetFileName(p), existingName,
                                       StringComparison.OrdinalIgnoreCase))
                               ?? pakFiles[0];

            // Step 4: Backup existing pak (only if enabled in settings)
            if (backupEnabled && File.Exists(existingPakPath))
            {
                progress?.Report(new DownloadProgress("Backing up...", 97));
                BackupExistingPak(existingPakPath);
            }

            // Step 5: Install new pak
            progress?.Report(new DownloadProgress("Installing...", 99));
            var destPath = Path.Combine(modsFolder, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destPath, overwrite: true);

            progress?.Report(new DownloadProgress("Done", 100));
            Logger.Info($"Downloader: installed {Path.GetFileName(destPath)}");

            // Invalidate installedmods.json cache entry so next scan re-parses the new pak
            InvalidateCache(existingPakPath, destPath);

            return destPath;
        }
        finally
        {
            // Clean up temp files
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }

    // ── Download ──────────────────────────────────────────────────────────

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total    = response.Content.Headers.ContentLength ?? -1L;
        var received = 0L;
        var buffer   = new byte[81920]; // 80 KB chunks

        await using var src  = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destPath);

        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            if (total > 0 && progress != null)
            {
                var pct  = (int)(received * 90 / total); // 0-90% for download
                var mb   = received / (1024.0 * 1024.0);
                var text = total > 0
                    ? $"Downloading... {mb:F1} MB / {total / (1024.0 * 1024.0):F1} MB"
                    : $"Downloading... {mb:F1} MB";
                progress.Report(new DownloadProgress(text, pct));
            }
        }

        Logger.Info($"Downloader: downloaded {received / 1024.0:F0} KB → {Path.GetFileName(destPath)}");
    }

    // ── Extract ───────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts all .pak files from the zip into extractDir.
    /// Handles both flat zips (pak at root) and nested zips (pak inside folder).
    /// </summary>
    private static List<string> ExtractPakFiles(string zipPath, string extractDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var pakEntries = archive.Entries
            .Where(e => e.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pakEntries.Count == 0)
        {
            Logger.Warn($"Downloader: no .pak in zip — entries: " +
                        string.Join(", ", archive.Entries.Take(10).Select(e => e.Name)));
        }

        var extracted = new List<string>();
        foreach (var entry in pakEntries)
        {
            var dest = Path.Combine(extractDir, entry.Name);
            entry.ExtractToFile(dest, overwrite: true);
            extracted.Add(dest);
            Logger.Info($"Downloader: extracted {entry.Name}");
        }

        return extracted;
    }

    // ── Cache invalidation ───────────────────────────────────────────────

    /// <summary>
    /// Removes the installed pak entry from the cache so the next scan
    /// re-parses the newly installed file and picks up the updated version.
    /// </summary>
    private static void InvalidateCache(string oldPakPath, string newPakPath)
    {
        // 1. installedmods.json에서 해당 항목 제거 → 다음 스캔 시 새 버전 재파싱
        try
        {
            var cacheFile = Path.Combine(SettingsStore.GetDataFolder(), "installedmods.json");
            if (File.Exists(cacheFile))
            {
                var json  = File.ReadAllText(cacheFile);
                var cache = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    BG3MM_UpdateHelper.Models.Cache.InstalledModsCache>(json);
                if (cache != null)
                {
                    cache.Mods.Remove(oldPakPath);
                    cache.Mods.Remove(newPakPath);
                    File.WriteAllText(cacheFile,
                        Newtonsoft.Json.JsonConvert.SerializeObject(cache,
                            Newtonsoft.Json.Formatting.Indented));
                    Logger.Info($"Downloader: installedmods cache invalidated for {Path.GetFileName(newPakPath)}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Downloader: installedmods cache invalidation failed — {ex.Message}");
        }

        // 2. modiodata.json에서 해당 모드 항목 삭제 → 다음 체크 시 API 재조회
        try
        {
            var modioCacheFile = Path.Combine(SettingsStore.GetDataFolder(), "modiodata.json");
            if (File.Exists(modioCacheFile))
            {
                var json  = File.ReadAllText(modioCacheFile);
                var cache = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    BG3MM_UpdateHelper.Models.Cache.ModioCachedData>(json);
                if (cache != null)
                {
                    // 파일명으로 UUID 추정이 어려우므로 전체 캐시 만료 처리
                    cache.LastUpdated = DateTime.MinValue;
                    File.WriteAllText(modioCacheFile,
                        Newtonsoft.Json.JsonConvert.SerializeObject(cache,
                            Newtonsoft.Json.Formatting.Indented));
                    Logger.Info("Downloader: modiodata cache expired for next refresh");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Downloader: modiodata cache invalidation failed — {ex.Message}");
        }
    }

    // ── Backup ────────────────────────────────────────────────────────────

    private static void BackupExistingPak(string pakPath)
    {
        if (!File.Exists(pakPath)) return;

        var bakPath = pakPath + Constants.PAK_BACKUP_SUFFIX;

        // Remove old backup if present
        if (File.Exists(bakPath))
            File.Delete(bakPath);

        File.Move(pakPath, bakPath);
        Logger.Info($"Downloader: backed up {Path.GetFileName(pakPath)} → .pak.bak");
    }
}

/// <summary>Progress info for download UI.</summary>
public record DownloadProgress(string Text, int Percent);
