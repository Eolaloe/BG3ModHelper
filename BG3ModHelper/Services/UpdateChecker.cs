using System.IO;
using BG3ModHelper.Models;
using BG3ModHelper.Models.Cache;

namespace BG3ModHelper.Services;

/// <summary>
/// Orchestrates the update check against Nexus Mods and mod.io.
///
/// Flow:
///   mod.io:  ModioPublishHandle → batch API call → version compare
///   Nexus:   pakFileName → DB lookup → fileId/version compare (no API calls)
///
/// Nexus update detection (spec §4.11):
///   1st priority: local fileId vs DB fileId (accurate)
///   Fallback:     version string compare (when no local fileId recorded)
/// </summary>
public static class UpdateChecker
{
    public static async Task<List<ModUpdateEntry>> CheckAsync(
        List<InstalledMod>  installedMods,
        NexusApi?           nexusApi,
        ModioApi?           modioApi,
        NexusIdDatabase?    nexusDb,
        ModFileIdStore?     fileIdStore,
        bool                nexusIsPremium,
        IProgress<int>?     progress = null)
    {
        // === mod.io: refresh cache via API (batch, unchanged) ===
        var modioCache = ModioApi.LoadCache();

        var modioMods = installedMods
            .Where(m => m.ModioPublishHandle != 0 && modioApi?.CanMakeRequest() == true)
            .ToList();

        if (modioApi != null && modioMods.Count > 0)
        {
            await RefreshModioCache(modioMods, modioApi, modioCache);
            ModioApi.SaveCache(modioCache);
        }

        // === Build update entries ===
        var entries      = new Dictionary<string, ModUpdateEntry>();
        var checkedCount = 0;
        var contributions = new List<ContributeEntry>();

        foreach (var mod in installedMods)
        {
            ModUpdateEntry? entry = null;

            // === mod.io check (unchanged) ===
            if (mod.ModioPublishHandle != 0 &&
                modioCache.Mods.TryGetValue(mod.MetaUuid, out var modioData))
            {
                if (IsNewer(modioData.ModioFileVersion, mod.MetaVersion))
                {
                    entry = EnsureEntry(entries, mod);
                    entry.UpdateNewVersion  = modioData.ModioFileVersion;
                    entry.ModioFileVersion  = modioData.ModioFileVersion;
                    entry.ModioProfileUrl   = modioData.ModioProfileUrl;
                    entry.AvailableSources.Add(UpdateSource.MODIO);
                    entry.Changelog         = "";
                    if (!string.IsNullOrEmpty(modioData.ModioModName))
                        entry.ModioModName = modioData.ModioModName;
                }
            }

            // === Nexus check: DB lookup, no API calls ===
            if (nexusDb != null)
            {
                var dbEntries = nexusDb.LookupByPakFileName(mod.PakFileName);

                // Case 2 fallback: pakFileName changed but same modId+fileName
                if (dbEntries.Count == 0 && fileIdStore != null)
                {
                    var stored = fileIdStore.GetEntry(mod.MetaUuid);
                    if (stored != null && stored.NexusModId != 0 && !string.IsNullOrEmpty(stored.NexusFileName))
                        dbEntries = nexusDb.LookupByModIdAndFileName(stored.NexusModId, stored.NexusFileName);
                }

                // Resolve conflict: uuid match → fileIdStore modId match → skip
                var dbEntry = dbEntries.Count == 1
                    ? dbEntries[0]
                    : dbEntries.FirstOrDefault(e =>
                        string.Equals(e.MetaUuid, mod.MetaUuid,
                            StringComparison.OrdinalIgnoreCase));

                if (dbEntry == null && dbEntries.Count > 1 && fileIdStore != null)
                {
                    var stored = fileIdStore.GetEntry(mod.MetaUuid);
                    if (stored != null && stored.NexusModId != 0)
                        dbEntry = dbEntries.FirstOrDefault(e => e.NexusModId == stored.NexusModId);
                }

                if (dbEntries.Count == 0)
                    Logger.Debug($"Nexus [{mod.MetaModuleName}] no DB match for pak={mod.PakFileName}");
                else if (dbEntry == null)
                    Logger.Debug($"Nexus [{mod.MetaModuleName}] ambiguous DB match ({dbEntries.Count} results, uuid mismatch) — skipped");

                if (dbEntry != null)
                {
                    if (!mod.NexusModId.HasValue)
                        mod.NexusModId = dbEntry.NexusModId;

                    bool hasUpdate = HasNexusUpdate(mod, dbEntry, fileIdStore);
                    if (hasUpdate)
                    {
                        entry = EnsureEntry(entries, mod);
                        entry.NexusFileVersion = dbEntry.NexusFileVersion;
                        entry.NexusFileId      = dbEntry.NexusFileId;
                        if (string.IsNullOrEmpty(entry.UpdateNewVersion) ||
                            IsNewer(dbEntry.NexusFileVersion, entry.UpdateNewVersion))
                            entry.UpdateNewVersion = dbEntry.NexusFileVersion;

                        entry.NexusModPageUrl = $"https://www.nexusmods.com/{Constants.NEXUS_GAME_DOMAIN}/mods/{dbEntry.NexusModId}";
                        entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
                        if (!string.IsNullOrEmpty(dbEntry.NexusModName))
                            entry.NexusModName = dbEntry.NexusModName;
                    }

                    if (dbEntry.MetaUuid == null && !string.IsNullOrEmpty(mod.MetaUuid))
                        contributions.Add(new ContributeEntry(
                            dbEntry.PakFileName, mod.MetaUuid, dbEntry.NexusModId, dbEntry.NexusFileId));
                }
            }

            if (entry != null)
                DetermineDefaultSource(entry, nexusIsPremium);

            checkedCount++;
            progress?.Report(checkedCount);
        }

        Logger.Info($"UpdateChecker: {entries.Count} update(s) found out of {installedMods.Count} mods");

        // Send UUID contributions in a single batch request
        if (nexusDb != null && contributions.Count > 0)
            _ = LogIfFails(nexusDb.ContributeBatchAsync(contributions), "ContributeBatchAsync");

        return [.. entries.Values];
    }

    /// <summary>Logs exceptions from a fire-and-forget task.</summary>
    private static async Task LogIfFails(Task task, string name)
    {
        try { await task; }
        catch (Exception ex) { Logger.Warn($"{name} failed: {ex.Message}"); }
    }

    // === Nexus update detection ===

    /// <summary>
    /// Determines whether a Nexus update is available (spec §4.11).
    /// 1st: fileId comparison (accurate).
    /// Fallback: version string comparison.
    /// </summary>
    private static bool HasNexusUpdate(
        InstalledMod mod, PakLookupEntry dbEntry, ModFileIdStore? store)
    {
        var localFileId = store?.GetFileId(mod.MetaUuid);
        if (localFileId.HasValue)
        {
            var result = localFileId.Value != dbEntry.NexusFileId;
            Logger.Debug($"Nexus [{mod.MetaModuleName}] fileId check: local={localFileId.Value} db={dbEntry.NexusFileId} → {(result ? "UPDATE" : "up-to-date")}");
            return result;
        }

        var verResult = IsNewer(dbEntry.NexusFileVersion, mod.MetaVersion);
        Logger.Debug($"Nexus [{mod.MetaModuleName}] version fallback: local={mod.MetaVersion} db={dbEntry.NexusFileVersion} → {(verResult ? "UPDATE" : "up-to-date")}");
        return verResult;
    }

    // === mod.io cache refresh ===

    private static async Task RefreshModioCache(
        List<InstalledMod> mods, ModioApi api, ModioCachedData cache)
    {
        Logger.Info($"UpdateChecker: refreshing mod.io cache for {mods.Count} mods");

        var handleToUuid = new Dictionary<ulong, string>();
        foreach (var mod in mods)
            handleToUuid.TryAdd(mod.ModioPublishHandle, mod.MetaUuid);

        var batch = await api.GetModsBatchAsync(handleToUuid.Keys);
        foreach (var (handle, data) in batch)
            if (handleToUuid.TryGetValue(handle, out var uuid))
                cache.Mods[uuid] = data;

        cache.LastUpdated = DateTime.UtcNow;
        Logger.Info($"UpdateChecker: mod.io cache refresh complete ({batch.Count} mods)");
    }

    // === Version comparison ===

    public static bool IsNewer(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current))   return true;

        var c = Normalize(candidate);
        var v = Normalize(current);

        if (Version.TryParse(c, out var cv) && Version.TryParse(v, out var vv))
            return cv > vv;

        return !string.Equals(c, v, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string ver) =>
        ver.TrimStart('v', 'V').Trim();

    // === Entry helpers ===

    private static ModUpdateEntry EnsureEntry(
        Dictionary<string, ModUpdateEntry> dict, InstalledMod mod)
    {
        if (!dict.TryGetValue(mod.MetaUuid, out var entry))
        {
            entry = new ModUpdateEntry
            {
                MetaUuid             = mod.MetaUuid,
                ModioPublishHandle   = mod.ModioPublishHandle,
                NexusModId           = mod.NexusModId,
                UpdateModName        = mod.MetaModuleName,
                UpdateCurrentVersion = mod.MetaVersion,
                PakFilePath          = mod.PakFilePath,
            };
            dict[mod.MetaUuid] = entry;
        }
        return entry;
    }

    private static void DetermineDefaultSource(ModUpdateEntry entry, bool nexusIsPremium)
    {
        var hasMod = entry.AvailableSources.Contains(UpdateSource.MODIO);
        var hasNex = entry.AvailableSources.Contains(UpdateSource.NEXUSMODS);

        if (entry.PreferredSource.HasValue)
        {
            entry.DefaultSource   = entry.PreferredSource.Value;
            entry.CanAutoDownload = entry.DefaultSource == UpdateSource.MODIO ||
                                    (entry.DefaultSource == UpdateSource.NEXUSMODS && nexusIsPremium);
            return;
        }

        if (hasMod)
        {
            entry.DefaultSource   = UpdateSource.MODIO;
            entry.CanAutoDownload = true;
        }
        else if (hasNex && nexusIsPremium)
        {
            entry.DefaultSource   = UpdateSource.NEXUSMODS;
            entry.CanAutoDownload = true;
        }
        else
        {
            entry.DefaultSource   = UpdateSource.NEXUSMODS;
            entry.CanAutoDownload = false;
        }
    }
}
