using System.IO;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Models.Cache;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Orchestrates the update check against Nexus Mods and mod.io.
///
/// Flow:
///   mod.io:  PublishHandle → batch API call → version compare
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
        // ── mod.io: refresh cache via API (batch, unchanged) ──────────────
        var modioCache = ModioApi.LoadCache();

        var modioMods = installedMods
            .Where(m => m.PublishHandle != 0 && modioApi?.CanMakeRequest() == true)
            .ToList();

        if (modioApi != null && modioMods.Count > 0)
        {
            await RefreshModioCache(modioMods, modioApi, modioCache);
            ModioApi.SaveCache(modioCache);
        }

        // ── Build update entries ──────────────────────────────────────────
        var entries      = new Dictionary<string, ModUpdateEntry>();
        var checkedCount = 0;
        var contributions = new List<ContributeEntry>();

        foreach (var mod in installedMods)
        {
            ModUpdateEntry? entry = null;

            // ── mod.io check (unchanged) ──────────────────────────────────
            if (mod.PublishHandle != 0 &&
                modioCache.Mods.TryGetValue(mod.UUID, out var modioData))
            {
                if (IsNewer(modioData.LatestVersion, mod.Version))
                {
                    entry = EnsureEntry(entries, mod);
                    entry.NewVersion      = modioData.LatestVersion;
                    entry.ModioNewVersion = modioData.LatestVersion;
                    entry.ModioUrl        = modioData.ProfileUrl;
                    entry.AvailableSources.Add(UpdateSource.MODIO);
                    entry.Changelog       = "";
                }
            }

            // ── Nexus check: DB lookup, no API calls ──────────────────────
            if (nexusDb != null)
            {
                var pakFileName = Path.GetFileName(mod.PakFilePath);
                var dbEntries   = nexusDb.LookupByPakFileName(pakFileName);

                // Case 2 fallback: pakFileName changed but same modId+fileName
                if (dbEntries.Count == 0 && fileIdStore != null)
                {
                    var stored = fileIdStore.GetEntry(mod.UUID);
                    if (stored != null && stored.ModId != 0 && !string.IsNullOrEmpty(stored.FileName))
                        dbEntries = nexusDb.LookupByModIdAndFileName(stored.ModId, stored.FileName);
                }

                // Resolve conflict: uuid match → auto, else skip
                var dbEntry = dbEntries.Count == 1
                    ? dbEntries[0]
                    : dbEntries.FirstOrDefault(e =>
                        string.Equals(e.Uuid, mod.UUID,
                            StringComparison.OrdinalIgnoreCase));

                if (dbEntry != null)
                {
                    // Inject modId if not already set
                    if (!mod.NexusModId.HasValue)
                        mod.NexusModId = dbEntry.ModId;

                    bool hasUpdate = HasNexusUpdate(mod, dbEntry, fileIdStore);
                    if (hasUpdate)
                    {
                        entry = EnsureEntry(entries, mod);
                        entry.NexusNewVersion = dbEntry.Version;
                        entry.NexusFileId     = dbEntry.FileId;
                        if (string.IsNullOrEmpty(entry.NewVersion) ||
                            IsNewer(dbEntry.Version, entry.NewVersion))
                            entry.NewVersion = dbEntry.Version;

                        entry.NexusUrl = $"https://www.nexusmods.com/{Constants.NEXUS_GAME_DOMAIN}/mods/{dbEntry.ModId}";
                        entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
                    }

                    // Collect UUID contribution if DB entry has none
                    if (dbEntry.Uuid == null && !string.IsNullOrEmpty(mod.UUID))
                        contributions.Add(new ContributeEntry(
                            dbEntry.PakFileName, mod.UUID, dbEntry.ModId, dbEntry.FileId));
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
            _ = nexusDb.ContributeBatchAsync(contributions);

        return [.. entries.Values];
    }

    // ── Nexus update detection ────────────────────────────────────────────

    /// <summary>
    /// Determines whether a Nexus update is available (spec §4.11).
    /// 1st: fileId comparison (accurate).
    /// Fallback: version string comparison.
    /// </summary>
    private static bool HasNexusUpdate(
        InstalledMod mod, PakLookupEntry dbEntry, ModFileIdStore? store)
    {
        var localFileId = store?.GetFileId(mod.UUID);
        if (localFileId.HasValue)
            return localFileId.Value != dbEntry.FileId;

        // Fallback: version string (may produce false positives once)
        return IsNewer(dbEntry.Version, mod.Version);
    }

    // ── mod.io cache refresh ──────────────────────────────────────────────

    private static async Task RefreshModioCache(
        List<InstalledMod> mods, ModioApi api, ModioCachedData cache)
    {
        Logger.Info($"UpdateChecker: refreshing mod.io cache for {mods.Count} mods");

        var handleToUuid = new Dictionary<ulong, string>();
        foreach (var mod in mods)
            handleToUuid.TryAdd(mod.PublishHandle, mod.UUID);

        var batch = await api.GetModsBatchAsync(handleToUuid.Keys);
        foreach (var (handle, data) in batch)
            if (handleToUuid.TryGetValue(handle, out var uuid))
                cache.Mods[uuid] = data;

        cache.LastUpdated = DateTime.UtcNow;
        Logger.Info($"UpdateChecker: mod.io cache refresh complete ({batch.Count} mods)");
    }

    // ── Version comparison ────────────────────────────────────────────────

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

    // ── Entry helpers ─────────────────────────────────────────────────────

    private static ModUpdateEntry EnsureEntry(
        Dictionary<string, ModUpdateEntry> dict, InstalledMod mod)
    {
        if (!dict.TryGetValue(mod.UUID, out var entry))
        {
            entry = new ModUpdateEntry
            {
                UUID           = mod.UUID,
                PublishHandle  = mod.PublishHandle,
                NexusModId     = mod.NexusModId,
                ModName        = mod.Name,
                CurrentVersion = mod.Version,
                PakFilePath    = mod.PakFilePath,
            };
            dict[mod.UUID] = entry;
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
