using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace BG3ModHelper.Services;

/// <summary>
/// Downloads a mod file from a URL, extracts .pak files from the zip,
/// backs up the existing .pak, and installs the new one into the Mods folder.
/// </summary>
public static class Downloader
{
    private static readonly HttpClient _http = HttpClientFactory.Shared;

    // === Public API ===

    /// <summary>
    /// Full pipeline: download → extract → backup → install.
    /// Returns the installed .pak path on success.
    /// </summary>
    public static async Task<DownloadInstallResult> DownloadAndInstallAsync(
        string downloadUrl,
        string existingPakPath,
        string modsFolder,
        bool backupEnabled              = false,
        string? uuid                    = null,
        int modId                       = 0,
        long fileId                     = 0,
        string fileName                 = "",
        ModFileIdStore? fileIdStore     = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir  = Path.Combine(Path.GetTempPath(), Constants.APP_DATA_FOLDER, Guid.NewGuid().ToString("N"));
        var tempZip  = Path.Combine(tempDir, "download.zip");
        var extractDir = Path.Combine(tempDir, "extracted");

        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(extractDir);

            // Step 1: Download zip
            progress?.Report(new DownloadProgress("Downloading", 0));
            await DownloadFileAsync(downloadUrl, tempZip, progress, ct);

            // Step 2: Extract .pak files from zip
            progress?.Report(new DownloadProgress("Extracting", 0));
            var pakFiles = ExtractPakFiles(tempZip, extractDir);

            if (pakFiles.Count == 0)
                throw new InvalidOperationException("No .pak files found in downloaded archive.");

            // Step 3: Identify primary pak — prefer filename match, else first
            var existingName = Path.GetFileName(existingPakPath);
            var primaryFile  = pakFiles.FirstOrDefault(p =>
                                   string.Equals(Path.GetFileName(p), existingName,
                                       StringComparison.OrdinalIgnoreCase))
                               ?? pakFiles[0];

            if (!string.IsNullOrEmpty(existingName))
            {
                if (Path.GetFileName(primaryFile) == existingName)
                    Logger.Debug($"Downloader: pak matched by name → {existingName}");
                else
                    Logger.Debug($"Downloader: pak name mismatch, using first in archive → {Path.GetFileName(primaryFile)} (expected {existingName})");
            }

            // Step 4: Backup existing primary pak (only if enabled)
            if (backupEnabled && File.Exists(existingPakPath))
            {
                progress?.Report(new DownloadProgress("Backing up", 0));
                BackupExistingPak(existingPakPath);
            }

            // Step 5: Install all paks — primary first, then any newly added components
            progress?.Report(new DownloadProgress("Applying...", 0));
            var installedPaths   = new List<string>();
            var replacedPakNames = new Dictionary<string, string>(); // destPath → replaced old filename

            foreach (var sourceFile in pakFiles)
            {
                var destPath = Path.Combine(modsFolder, Path.GetFileName(sourceFile));

                // Detect same-UUID duplicate with different filename (cross-platform rename)
                var duplicate = FindDuplicatePak(sourceFile, modsFolder);
                if (duplicate != null)
                {
                    try
                    {
                        if (backupEnabled)
                            BackupExistingPak(duplicate);  // → .bak
                        else
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                duplicate,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        replacedPakNames[destPath] = Path.GetFileName(duplicate);
                        InvalidateCache(duplicate, duplicate);
                        Logger.Info($"Downloader: {(backupEnabled ? "backed up" : "sent to Recycle Bin")} duplicate pak {Path.GetFileName(duplicate)}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Downloader: failed to remove duplicate {Path.GetFileName(duplicate)} — {ex.Message}");
                    }
                }

                try { File.Copy(sourceFile, destPath, overwrite: true); }
                catch (IOException ex) { throw new PakInUseException(Path.GetFileName(destPath), ex); }
                installedPaths.Add(destPath);
                Logger.Info($"Downloader: installed {Path.GetFileName(destPath)}{(sourceFile == primaryFile ? "" : " (new component)")}");
            }

            progress?.Report(new DownloadProgress("Updated", 100));

            // Record fileId for accurate update detection (primary pak only)
            if (fileIdStore != null && !string.IsNullOrEmpty(uuid) && fileId != 0)
                fileIdStore.SetFileId(uuid, modId, fileId, fileName);

            // Invalidate cache for all installed paks
            var primaryDest = Path.Combine(modsFolder, Path.GetFileName(primaryFile));

            // Enqueue verified contribution — flushed on next update check.
            // modId + fileId are confirmed by the caller (from Nexus API / update entry),
            // so this mapping is download-verified (higher trust than scan-based contributions).
            if (!string.IsNullOrEmpty(uuid) && modId != 0 && fileId != 0)
            {
                try
                {
                    var verifiedStore = new PendingVerifiedContributionStore();
                    verifiedStore.Load();
                    verifiedStore.Add(Path.GetFileName(primaryDest), uuid, modId, fileId);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Downloader: pending verified store update failed — {ex.Message}");
                }
            }
            InvalidateCache(existingPakPath, primaryDest);
            foreach (var path in installedPaths.Where(p => p != primaryDest))
                InvalidateCache(path, path);

            return new DownloadInstallResult(installedPaths, replacedPakNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { Logger.Warn($"Failed to delete temp dir: {ex.Message}"); }
        }
    }

    // === Download ===

    /// <summary>
    /// Installs a mod from a local archive (folder watcher / drag-and-drop).
    /// Skips download — goes straight to extract → backup → install.
    /// Returns the installed pak path.
    /// </summary>
    public static async Task<DownloadInstallResult> InstallLocalArchiveAsync(
        string archivePath,
        string modsFolder,
        bool   backupEnabled,
        IProgress<DownloadProgress>? progress = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Constants.APP_DATA_FOLDER,
                                   Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            progress?.Report(new DownloadProgress("Extracting...", 50));
            var pakFiles = FolderWatcherService.ExtractPakFiles(archivePath, tempDir);
            if (pakFiles.Count == 0)
                throw new InvalidOperationException("No .pak files found in archive.");

            var installedPaths   = new List<string>();
            var replacedPakNames = new Dictionary<string, string>();

            progress?.Report(new DownloadProgress("Applying", 80));
            foreach (var sourceFile in pakFiles)
            {
                var destPath = Path.Combine(modsFolder, Path.GetFileName(sourceFile));

                var duplicate = FindDuplicatePak(sourceFile, modsFolder);
                if (duplicate != null)
                {
                    try
                    {
                        if (backupEnabled)
                            BackupExistingPak(duplicate);
                        else
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                                duplicate,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        replacedPakNames[destPath] = Path.GetFileName(duplicate);
                        InvalidateCache(duplicate, duplicate);
                        Logger.Info($"Downloader: {(backupEnabled ? "backed up" : "sent to Recycle Bin")} duplicate pak {Path.GetFileName(duplicate)}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Downloader: failed to remove duplicate {Path.GetFileName(duplicate)} — {ex.Message}");
                    }
                }

                if (backupEnabled && File.Exists(destPath))
                    BackupExistingPak(destPath);

                try { File.Copy(sourceFile, destPath, overwrite: true); }
                catch (IOException ex) { throw new PakInUseException(Path.GetFileName(destPath), ex); }

                installedPaths.Add(destPath);
                InvalidateCache(destPath, destPath);
                Logger.Info($"Downloader: installed {Path.GetFileName(destPath)} from local archive");
            }

            progress?.Report(new DownloadProgress("Updated", 100));
            return new DownloadInstallResult(installedPaths, replacedPakNames);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { Logger.Warn($"Failed to delete temp dir: {ex.Message}"); }
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total    = response.Content.Headers.ContentLength ?? -1L;
        Logger.Info($"Downloader: starting download — size={( total > 0 ? $"{total / (1024.0 * 1024.0):F1} MB" : "unknown")} url={url}");
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
                var pct  = (int)(received * 100 / total); // 0-100% for download phase
                var mb   = received / (1024.0 * 1024.0);
                var text = total > 0
                    ? $"{SizeFormatter.FormatSize(received)} / {SizeFormatter.FormatSize(total)}"
                    : SizeFormatter.FormatSize(received);
                progress.Report(new DownloadProgress(text, pct));
            }
        }

        Logger.Info($"Downloader: downloaded {received / 1024.0:F0} KB → {Path.GetFileName(destPath)}");
    }

    // === Extract ===

    /// <summary>
    /// Extracts all .pak files from the archive into extractDir.
    /// Delegates to FolderWatcherService (shared logic, supports zip/7z/rar).
    /// </summary>
    private static List<string> ExtractPakFiles(string archivePath, string extractDir) =>
        FolderWatcherService.ExtractPakFiles(archivePath, extractDir);

    // === Cache invalidation ===

    /// <summary>
    /// Removes the installed pak entry from the cache so the next scan
    /// re-parses the newly installed file and picks up the updated version.
    /// </summary>
    private static void InvalidateCache(string oldPakPath, string newPakPath)
    {
        // 1. Remove from installedmods.json so next scan re-parses the new version
        try
        {
            var cacheFile = Path.Combine(SettingsStore.GetDataFolder(), "installedmods.json");
            if (File.Exists(cacheFile))
            {
                var json  = File.ReadAllText(cacheFile);
                var cache = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    BG3ModHelper.Models.Cache.InstalledModsCache>(json);
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

        // 2. Expire modiodata.json so next check re-fetches from API
        try
        {
            var modioCacheFile = Path.Combine(SettingsStore.GetDataFolder(), "modiodata.json");
            if (File.Exists(modioCacheFile))
            {
                var json  = File.ReadAllText(modioCacheFile);
                var cache = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    BG3ModHelper.Models.Cache.ModioCachedData>(json);
                if (cache != null)
                {
                    // Cannot reliably match by filename, so expire the entire cache
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

    // === Duplicate detection ===

    /// <summary>
    /// Looks for an existing pak in the mods folder that has the same UUID + MetaModuleName
    /// as <paramref name="newPakPath"/> but a different filename.
    /// Uses the installedmods.json cache — no pak parsing required.
    /// Returns the conflicting pak path, or null if none found.
    /// </summary>
    public static string? FindDuplicatePak(string newPakPath, string modsFolder)
    {
        try
        {
            var cacheFile = Path.Combine(SettingsStore.GetDataFolder(), "installedmods.json");
            if (!File.Exists(cacheFile)) return null;

            var cache = Newtonsoft.Json.JsonConvert.DeserializeObject<
                BG3ModHelper.Models.Cache.InstalledModsCache>(File.ReadAllText(cacheFile));
            if (cache == null) return null;

            // Parse the incoming pak to get its UUID + ModuleName
            var incoming = ModScanner.InspectPak(newPakPath);
            if (incoming == null || string.IsNullOrEmpty(incoming.MetaUuid)) return null;

            var newFileName = Path.GetFileName(newPakPath);

            foreach (var (path, entry) in cache.Mods)
            {
                var mod = entry.ModData;
                if (!string.Equals(mod.MetaUuid, incoming.MetaUuid, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(mod.MetaModuleName, incoming.MetaModuleName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(Path.GetFileName(path), newFileName, StringComparison.OrdinalIgnoreCase))
                    continue; // same filename = normal overwrite, not a duplicate
                if (!File.Exists(path))
                    continue; // stale cache entry

                Logger.Info($"Downloader: duplicate pak detected — {Path.GetFileName(path)} will be replaced by {newFileName} (same UUID+ModuleName)");
                return path;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Downloader: duplicate check failed — {ex.Message}");
        }

        return null;
    }

    // === Backup ===

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

    /// <summary>
    /// Downloads a zip once and installs all matched .pak files (group update).
    /// Targets are matched by filename; unmatched targets are skipped with a warning.
    /// </summary>
    public static async Task DownloadAndInstallGroupAsync(
    string downloadUrl,
    IReadOnlyList<PakInstallTarget> targets,
    string modsFolder,
    bool backupEnabled,
    ModFileIdStore? fileIdStore,
    IProgress<DownloadProgress>? progress,
    CancellationToken ct = default)
{
    var tempDir    = Path.Combine(Path.GetTempPath(), Constants.APP_DATA_FOLDER, Guid.NewGuid().ToString("N"));
    var tempZip    = Path.Combine(tempDir, "download.zip");
    var extractDir = Path.Combine(tempDir, "extracted");

    try
    {
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir);

        progress?.Report(new DownloadProgress("Downloading", 0));
        await DownloadFileAsync(downloadUrl, tempZip, progress, ct);

        progress?.Report(new DownloadProgress("Extracting", 0));
        var pakFiles = ExtractPakFiles(tempZip, extractDir);
        if (pakFiles.Count == 0)
            throw new InvalidOperationException("No .pak files found in downloaded archive.");

        for (int i = 0; i < targets.Count; i++)
        {
            var target       = targets[i];
            var existingName = Path.GetFileName(target.ExistingPakPath);

            var source = pakFiles.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), existingName,
                    StringComparison.OrdinalIgnoreCase));

            if (source == null)
            {
                Logger.Warn($"Downloader: no pak matched for '{existingName}' in group archive — skipping");
                continue;
            }

            progress?.Report(new DownloadProgress($"Applying {existingName}...", 0));

            if (backupEnabled && File.Exists(target.ExistingPakPath))
                BackupExistingPak(target.ExistingPakPath);

            var destPath = Path.Combine(modsFolder, Path.GetFileName(source));
            try { File.Copy(source, destPath, overwrite: true); }
            catch (IOException ex) { throw new PakInUseException(Path.GetFileName(destPath), ex); }

            if (fileIdStore != null && !string.IsNullOrEmpty(target.Uuid) && target.FileId != 0)
                fileIdStore.SetFileId(target.Uuid, target.ModId, target.FileId, target.FileName);

            // Enqueue verified contribution for group download target
            if (!string.IsNullOrEmpty(target.Uuid) && target.ModId != 0 && target.FileId != 0)
            {
                try
                {
                    var verifiedStore = new PendingVerifiedContributionStore();
                    verifiedStore.Load();
                    verifiedStore.Add(Path.GetFileName(destPath), target.Uuid, target.ModId, target.FileId);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Downloader: pending verified store update failed (group) — {ex.Message}");
                }
            }

            InvalidateCache(target.ExistingPakPath, destPath);
            Logger.Info($"Downloader: installed {Path.GetFileName(destPath)} (group {i + 1}/{targets.Count})");
        }

        progress?.Report(new DownloadProgress("Updated", 100));
    }
    finally
    {
        try { Directory.Delete(tempDir, recursive: true); }
        catch (Exception ex) { Logger.Warn($"Failed to delete temp dir: {ex.Message}"); }
    }
    }
}

/// <summary>Progress info for download UI.</summary>
public record DownloadProgress(string Text, int Percent);

/// <summary>Formats a byte count as KB / MB / GB with 1 decimal place.</summary>
file static class SizeFormatter
{
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024L * 1024L)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

/// <summary>
/// Result of a download+install operation.
/// InstalledPaths: all pak paths written to the mods folder.
/// ReplacedPakNames: maps destPath → old pak filename that was deleted (cross-platform rename).
/// </summary>
public record DownloadInstallResult(
    List<string> InstalledPaths,
    Dictionary<string, string> ReplacedPakNames)
{
    public string PrimaryPath => InstalledPaths.Count > 0 ? InstalledPaths[0] : "";
    public string? GetReplacedName(string destPath) =>
        ReplacedPakNames.TryGetValue(destPath, out var name) ? name : null;
}

/// <summary>One pak to install as part of a group download.</summary>
public record PakInstallTarget(
    string  ExistingPakPath,
    string? Uuid,
    int     ModId,
    long    FileId,
    string  FileName);

/// <summary>
/// Thrown when the .pak file cannot be overwritten because it is locked
/// (e.g. BG3 or another process has it open).
/// </summary>
public class PakInUseException(string pakFileName, Exception inner)
    : IOException($"Cannot overwrite {pakFileName} — close BG3 and retry.", inner)
{
    public string PakFileName { get; } = pakFileName;
}
