using System.IO;
using BG3MM_UpdateHelper.Models;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Worker handler for NxmDownloadQueue.
/// Pure service layer — no UI access. Caller handles UI updates.
/// </summary>
public static class NxmInstaller
{
    private static string? _lastDownloadError;

    /// <summary>Returns and clears the last download-stage error message (if any).</summary>
    public static string? ConsumeLastError()
    {
        var e = _lastDownloadError;
        _lastDownloadError = null;
        return e;
    }

    /// <summary>
    /// Processes one queue item: token → download URL → download + install.
    /// Returns install result on success, null on failure.
    /// </summary>
    public static async Task<NxmInstallResult?> HandleAsync(
        NxmQueueItem item,
        IProgress<DownloadProgress>? progress = null)
    {
        _lastDownloadError = null;
        var settings = SettingsStore.Load();

        var modsFolder = !string.IsNullOrEmpty(settings.ModsFolderPath)
            ? settings.ModsFolderPath
            : PathDiscovery.GetDefaultModsFolder();

        if (string.IsNullOrEmpty(modsFolder) || !Directory.Exists(modsFolder))
        {
            Logger.Warn($"NxmInstaller: invalid mods folder='{modsFolder}' — skipping {item.RawUrl}");
            return null;
        }

        var api = new NexusApi(settings.NexusAPIKey ?? "");

        Logger.Info($"NxmInstaller: resolving download URL for mod={item.Url.NexusModId} file={item.Url.NexusFileId}");

        var downloadUrl = await api.GetDownloadUrlAsync(
            item.Url.NexusModId,
            item.Url.NexusFileId,
            nxmKey:     item.Url.Key,
            nxmExpires: item.Url.Expires,
            nxmUserId:  item.Url.UserId);

        if (string.IsNullOrEmpty(downloadUrl))
        {
            _lastDownloadError = api.LastError;
            Logger.Warn($"NxmInstaller: failed to resolve download URL for {item.RawUrl}");
            return null;
        }

        try
        {
            var installedPath = await Downloader.DownloadAndInstallAsync(
                downloadUrl:     downloadUrl,
                existingPakPath: "",
                modsFolder:      modsFolder,
                backupEnabled:   settings.BackupBeforeUpdate,
                uuid:            null,
                modId:           item.Url.NexusModId,
                fileId:          item.Url.NexusFileId,
                fileName:        "",
                fileIdStore:     null,
                progress:        progress);

            var pakFileName = Path.GetFileName(installedPath);
            var pak         = ModScanner.InspectPak(installedPath);
            var modName     = (!string.IsNullOrEmpty(pak?.MetaModuleName)) ? pak!.MetaModuleName : pakFileName;

            // Nexus file version + name are more accurate than meta.lsx
            var (nexusVersion, nexusFileName) = await api.GetFileMetaAsync(item.Url.NexusModId, item.Url.NexusFileId);
            var modVersion  = nexusVersion  ?? pak?.MetaVersion ?? "";
            var nexusFile   = nexusFileName ?? "";

            Logger.Info($"NxmInstaller: installed {pakFileName} from nxm (version: {modVersion})");

            await ContributeAsync(installedPath, pak, item.Url.NexusModId, item.Url.NexusFileId, nexusFile);

            return new NxmInstallResult(installedPath, pakFileName, modName, modVersion, item.Url.NexusModId, item.Url.NexusFileId);
        }
        catch (Exception ex)
        {
            Logger.Error($"NxmInstaller: install failed for {item.RawUrl} — {ex.Message}");
            return null;
        }
    }

    private static async Task ContributeAsync(
        string installedPath, InstalledMod? pak, int modId, long fileId, string nexusFileName)
    {
        try
        {
            if (pak is null || string.IsNullOrEmpty(pak.MetaUuid))
            {
                Logger.Warn("NxmInstaller: pak inspection returned no UUID — skipping");
                return;
            }

            var pakFileName = Path.GetFileName(installedPath);

            // 1) Local store — nexusFileName is the Nexus display name (e.g. "Main File")
            try
            {
                var fileIdStore = new ModFileIdStore();
                fileIdStore.Load();
                fileIdStore.SetFileId(pak.MetaUuid, modId, fileId, nexusFileName);
            }
            catch (Exception ex)
            {
                Logger.Warn($"NxmInstaller: local fileId store update failed — {ex.Message}");
            }

            // 2) Community DB
            var db = new NexusIdDatabase();
            await db.ContributeUuidAsync(pakFileName, pak.MetaUuid, modId, fileId);
        }
        catch (Exception ex)
        {
            Logger.Warn($"NxmInstaller: contribution failed — {ex.Message}");
        }
    }
}

/// <summary>Result of a successful nxm install.</summary>
public sealed record NxmInstallResult(
    string InstalledPath,
    string PakFileName,
    string UpdateModName,
    string UpdateNewVersion,
    int    NexusModId,
    long   NexusFileId);
