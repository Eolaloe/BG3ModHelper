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
        IProgress<int>?     progress      = null,
        UserModLinkStore?   userLinkStore = null)
    {
        // === Nexus + mod.io: run pre-fetch in parallel ===
        // updated.json (1 call) and mod.io cache refresh are independent — run concurrently.
        var modioCache = ModioApi.LoadCache();
        var modioMods  = installedMods
            .Where(m => m.ModioPublishHandle != 0 && modioApi?.CanMakeRequest() == true)
            .ToList();

        var recentlyUpdatedTask = nexusApi != null && nexusDb != null
            ? nexusApi.GetRecentlyUpdatedModIdsAsync()
            : Task.FromResult(new HashSet<int>());

        var modioTask = modioApi != null && modioMods.Count > 0
            ? RefreshModioCache(modioMods, modioApi, modioCache)
            : Task.CompletedTask;

        Logger.Info("UpdateChecker: fetching Nexus recently-updated list and mod.io cache in parallel...");
        await Task.WhenAll(recentlyUpdatedTask, modioTask);

        var recentlyUpdated = recentlyUpdatedTask.Result;
        Logger.Info($"UpdateChecker: {recentlyUpdated.Count} mod(s) updated in the last month (Nexus)");

        if (modioApi != null && modioMods.Count > 0)
            ModioApi.SaveCache(modioCache);

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
                    entry.Changelog      = "";
                    if (!string.IsNullOrEmpty(modioData.ModioChangelog))
                        entry.ModioChangelog = modioData.ModioChangelog;
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

                // Case 3 fallback: user manually linked this pak to a Nexus mod ID
                if (dbEntries.Count == 0 && userLinkStore != null)
                {
                    var userModId = userLinkStore.GetNexusModId(mod.PakFileName);
                    if (userModId.HasValue)
                    {
                        dbEntries = nexusDb.LookupByModId(userModId.Value);
                        if (dbEntries.Count > 0)
                            Logger.Info($"UpdateChecker: [{mod.MetaModuleName}] resolved via user link → Nexus mod {userModId.Value}");
                    }
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

                // Upgrade to latest fileId for the resolved modId
                // (same modId may have multiple entries: old archived + new version)
                if (dbEntry != null && dbEntries.Count > 1)
                {
                    var latest = dbEntries
                        .Where(e => e.NexusModId == dbEntry.NexusModId)
                        .OrderByDescending(e => e.NexusFileId)
                        .FirstOrDefault();
                    if (latest != null)
                        dbEntry = latest;
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

                    // Stale DB fallback: DB says "no update" but DB hasn't been maintained
                    // for 7+ days AND this mod appears in the recently-updated list.
                    // The DB entry may not reflect the latest fileId — verify via direct API call.
                    if (!hasUpdate &&
                        nexusApi != null &&
                        nexusDb.IsStale &&
                        recentlyUpdated.Contains(dbEntry.NexusModId))
                    {
                        Logger.Debug($"Nexus [{mod.MetaModuleName}] DB stale + in recently-updated — verifying via API");
                        var latestFile = await nexusApi.GetLatestFileAsync(dbEntry.NexusModId);
                        if (latestFile != null && latestFile.NexusFileId != dbEntry.NexusFileId)
                        {
                            var localFileId = fileIdStore?.GetFileId(mod.MetaUuid);
                            hasUpdate = localFileId.HasValue
                                ? localFileId.Value != latestFile.NexusFileId
                                : IsNewer(latestFile.NexusFileVersion, mod.MetaVersion);

                            if (hasUpdate)
                            {
                                entry = EnsureEntry(entries, mod);
                                entry.NexusFileVersion = latestFile.NexusFileVersion;
                                entry.NexusFileId      = latestFile.NexusFileId;
                                if (string.IsNullOrEmpty(entry.UpdateNewVersion) ||
                                    IsNewer(latestFile.NexusFileVersion, entry.UpdateNewVersion))
                                    entry.UpdateNewVersion = latestFile.NexusFileVersion;
                                entry.NexusModPageUrl = $"https://www.nexusmods.com/{Constants.NEXUS_GAME_DOMAIN}/mods/{dbEntry.NexusModId}";
                                entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
                                if (!string.IsNullOrEmpty(dbEntry.NexusModName))
                                    entry.NexusModName = dbEntry.NexusModName;
                                Logger.Info($"Nexus [{mod.MetaModuleName}] stale DB fallback — update detected: DB fileId={dbEntry.NexusFileId} → API fileId={latestFile.NexusFileId}");
                            }
                        }
                    }

                    // Normal DB update path (stale fallback already handled its own entry)
                    if (hasUpdate && (entry == null || !entry.AvailableSources.Contains(UpdateSource.NEXUSMODS)))
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

                // Sync-required: no stored fileId AND version comparison unreliable
                // (version inverted, unparseable suffix, etc.)
                // Flags the entry for the "Sync Recommended" section — one re-download stores
                // the fileId and enables accurate tracking from then on.
                if (!hasUpdate && entry == null)
                {
                    var localFileId = fileIdStore?.GetFileId(mod.MetaUuid);
                    if (localFileId == null && IsVersionSyncRequired(mod.MetaVersion, dbEntry.NexusFileVersion))
                    {
                        entry = EnsureEntry(entries, mod);
                        entry.NexusFileVersion = dbEntry.NexusFileVersion;
                        entry.NexusFileId      = dbEntry.NexusFileId;
                        entry.UpdateNewVersion = dbEntry.NexusFileVersion;
                        entry.NexusModPageUrl  = $"https://www.nexusmods.com/{Constants.NEXUS_GAME_DOMAIN}/mods/{dbEntry.NexusModId}";
                        entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
                        entry.IsSyncRequired   = true;
                        if (!string.IsNullOrEmpty(dbEntry.NexusModName))
                            entry.NexusModName = dbEntry.NexusModName;
                        Logger.Debug($"Nexus [{mod.MetaModuleName}] sync-required: installed={mod.MetaVersion} db={dbEntry.NexusFileVersion}");
                    }
                }
                }
                else
                {
                    // API fallback: no DB entry but modId known and in recentlyUpdated
                    var knownModId = mod.NexusModId
                        ?? fileIdStore?.GetEntry(mod.MetaUuid)?.NexusModId;

                    if (nexusApi != null && knownModId.HasValue &&
                        recentlyUpdated.Count > 0 && recentlyUpdated.Contains(knownModId.Value))
                    {
                        Logger.Debug($"Nexus [{mod.MetaModuleName}] no DB entry but in recently-updated — calling GetLatestFileAsync + GetModNameAsync");
                        var latestFileTask = nexusApi.GetLatestFileAsync(knownModId.Value);
                        var modNameTask    = nexusApi.GetModNameAsync(knownModId.Value);
                        await Task.WhenAll(latestFileTask, modNameTask);
                        var latestFile = latestFileTask.Result;
                        if (latestFile != null)
                        {
                            var localFileId = fileIdStore?.GetFileId(mod.MetaUuid);
                            bool hasUpdate = localFileId.HasValue
                                ? localFileId.Value != latestFile.NexusFileId
                                : IsNewer(latestFile.NexusFileVersion, mod.MetaVersion);

                            if (hasUpdate)
                            {
                                entry = EnsureEntry(entries, mod);
                                entry.NexusFileVersion    = latestFile.NexusFileVersion;
                                entry.NexusFileId         = latestFile.NexusFileId;
                                entry.RequiresManualCheck = true;
                                if (string.IsNullOrEmpty(entry.UpdateNewVersion) ||
                                    IsNewer(latestFile.NexusFileVersion, entry.UpdateNewVersion))
                                    entry.UpdateNewVersion = latestFile.NexusFileVersion;
                                entry.NexusModPageUrl = $"https://www.nexusmods.com/{Constants.NEXUS_GAME_DOMAIN}/mods/{knownModId.Value}";
                                entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
                                var modName = modNameTask.Result;
                                if (!string.IsNullOrEmpty(modName))
                                    entry.NexusModName = modName;
                            }
                        }
                    }
                }
            }

            if (entry != null)
                DetermineDefaultSource(entry, nexusIsPremium);

            checkedCount++;
            progress?.Report(checkedCount);
        }

        Logger.Info($"UpdateChecker: {entries.Count} update(s) found out of {installedMods.Count} mods");

        // Merge pending verified contributions (from prior downloads) with regular scan contributions.
        // Verified entries are cleared only after a confirmed successful send.
        var verifiedStore = new PendingVerifiedContributionStore();
        verifiedStore.Load();
        var pendingVerified = verifiedStore.GetAll();

        foreach (var v in pendingVerified)
            contributions.Add(new ContributeEntry(v.PakFileName, v.MetaUuid, v.NexusModId, v.NexusFileId,
                                                  verified: true));

        if (pendingVerified.Count > 0)
            Logger.Info($"UpdateChecker: including {pendingVerified.Count} pending verified contribution(s)");

        // Send all contributions in a single batch request
        if (nexusDb != null && contributions.Count > 0)
            _ = LogIfFails(
                SendContributionsAsync(nexusDb, contributions, verifiedStore, pendingVerified.Count),
                "ContributeBatchAsync");

        return [.. entries.Values];
    }

    /// <summary>
    /// Sends all contributions and, on success, clears the verified pending queue.
    /// Verified entries are only cleared when the server confirms receipt (2xx) so
    /// they are retried on the next update check if the request fails.
    /// </summary>
    private static async Task SendContributionsAsync(
        NexusIdDatabase db,
        List<ContributeEntry> entries,
        PendingVerifiedContributionStore verifiedStore,
        int verifiedCount)
    {
        bool ok = await db.ContributeBatchAsync(entries);
        if (ok && verifiedCount > 0)
            verifiedStore.Clear();
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

    // === Sync-required detection ===

    /// <summary>
    /// Returns true when version comparison between installed and DB is unreliable,
    /// indicating a sync re-download is needed to establish accurate fileId tracking.
    /// Triggers: installed numerically higher than DB, unparseable suffixes, or format mismatch.
    /// </summary>
    private static bool IsVersionSyncRequired(string installedVersion, string dbVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(dbVersion))
            return false;

        var c = Normalize(dbVersion);
        var v = Normalize(installedVersion);

        if (string.Equals(c, v, StringComparison.OrdinalIgnoreCase))
            return false;

        var cBase = ExtractNumericBase(c);
        var vBase = ExtractNumericBase(v);

        var cVer = ToFourPart(cBase);
        var vVer = ToFourPart(vBase);

        // Either side unparseable → unreliable comparison
        if (cVer == null || vVer == null) return true;

        // Installed numerically higher than DB → likely different versioning schemes
        if (vVer > cVer) return true;

        // One has non-numeric suffix, the other doesn't (e.g. "1.0.0" vs "1.0.0kr")
        bool cHasSuffix = !string.Equals(c, cBase, StringComparison.OrdinalIgnoreCase);
        bool vHasSuffix = !string.Equals(v, vBase, StringComparison.OrdinalIgnoreCase);
        if (cHasSuffix != vHasSuffix) return true;

        return false;
    }

    // === Version comparison ===

    public static bool IsNewer(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current))   return true;

        var c = Normalize(candidate);
        var v = Normalize(current);

        // Extract numeric base from candidate (strips any suffix: "-1", "A", "-kr-add", etc.)
        var cBase    = ExtractNumericBase(c);
        var hasSuffix = !string.Equals(c, cBase, StringComparison.OrdinalIgnoreCase);

        // Normalize both sides to 4-part Version for comparison
        var cVer = ToFourPart(cBase);
        var vVer = ToFourPart(v);

        if (cVer != null && vVer != null)
        {
            if (cVer > vVer) return true;   // candidate base is newer
            if (cVer < vVer) return false;  // candidate base is older
            // bases equal — any suffix means potential revision → treat as update candidate
            return hasSuffix;
        }

        // fallback: string compare (both sides unparseable)
        return !string.Equals(c, v, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the leading numeric (digits and dots) portion of a version string.
    /// "1.0.16-2" → "1.0.16",  "2.2A" → "2.2",  "1.1.0.0-kr" → "1.1.0.0"
    /// </summary>
    private static string ExtractNumericBase(string ver)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in ver)
        {
            if (char.IsDigit(ch) || ch == '.') sb.Append(ch);
            else break;
        }
        return sb.ToString().TrimEnd('.');
    }

    /// <summary>
    /// Parses a version string and pads to exactly 4 parts (major.minor.build.revision).
    /// "1.0" → 1.0.0.0,  "1.0.16" → 1.0.16.0,  "1.1.0.0" → 1.1.0.0
    /// Returns null if unparseable.
    /// </summary>
    private static Version? ToFourPart(string ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return null;
        if (!Version.TryParse(ver, out var v)) return null;
        return new Version(
            Math.Max(v.Major,    0),
            Math.Max(v.Minor,    0),
            Math.Max(v.Build,    0),
            Math.Max(v.Revision, 0)
        );
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
            entry.CanAutoDownload = !entry.RequiresManualCheck &&
                                    (entry.DefaultSource == UpdateSource.MODIO ||
                                     (entry.DefaultSource == UpdateSource.NEXUSMODS && nexusIsPremium));
            return;
        }

        if (hasMod && !entry.RequiresManualCheck)
        {
            entry.DefaultSource   = UpdateSource.MODIO;
            entry.CanAutoDownload = true;
        }
        else if (hasNex && nexusIsPremium && !entry.RequiresManualCheck)
        {
            entry.DefaultSource   = UpdateSource.NEXUSMODS;
            entry.CanAutoDownload = true;
        }
        else
        {
            entry.DefaultSource   = hasNex ? UpdateSource.NEXUSMODS : UpdateSource.MODIO;
            entry.CanAutoDownload = false;
        }
    }
}
