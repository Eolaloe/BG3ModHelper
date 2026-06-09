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
        await Task.WhenAll(recentlyUpdatedTask, modioTask).ConfigureAwait(false);

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

                // Pak-rename recovery: if user previously linked this mod under a different
                // pak name (UUID is stable), migrate the UserModLinkStore entry to the new name.
                // Must run before ALL fallback lookups so every subsequent GetNexusLink(mod.PakFileName)
                // call in Cases 2/3 and the disambiguation block can find the migrated entry.
                if (userLinkStore != null && !string.IsNullOrEmpty(mod.MetaUuid) &&
                    userLinkStore.GetNexusLink(mod.PakFileName) == null)
                {
                    var found = userLinkStore.FindByUuid(mod.MetaUuid);
                    if (found.HasValue)
                    {
                        Logger.Info($"UpdateChecker: [{mod.MetaModuleName}] pak renamed — migrating user link [{found.Value.PakFileName}] → [{mod.PakFileName}]");
                        userLinkStore.RekeyEntry(found.Value.PakFileName, mod.PakFileName);
                    }
                }

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
                    var userLink = userLinkStore.GetNexusLink(mod.PakFileName);
                    if (userLink != null && IsUserLinkHashValid(userLink, mod.PakFilePath, userLinkStore, mod.PakFileName))
                    {
                        dbEntries = nexusDb.LookupByModId(userLink.ModId);
                        if (dbEntries.Count > 0)
                            Logger.Info($"UpdateChecker: [{mod.MetaModuleName}] resolved via user link → Nexus mod {userLink.ModId}");
                    }
                }

                // Resolve conflict: userLink → uuid match → fileIdStore fileId/modId → ambiguous
                PakLookupEntry? dbEntry;
                bool            needsDisambiguation = false;
                // Pre-compute: does this pak match multiple distinct Nexus mods?
                // Stored as a lightweight flag on the entry — candidates are re-queried on demand.
                bool hasMultipleCandidates = dbEntries.Count > 1 &&
                    dbEntries.Select(e => e.NexusModId).Distinct().Count() > 1;

                if (dbEntries.Count == 0)
                {
                    dbEntry = null; // no DB match — handled by logging/fallback below
                }
                else if (dbEntries.Count == 1)
                {
                    dbEntry = dbEntries[0];
                }
                else
                {
                    // If the user has already made a disambiguation choice for this pak, honour it —
                    // but only if the pak file hash still matches (guards against mod replacement).
                    if (userLinkStore != null)
                    {
                        var userLink = userLinkStore.GetNexusLink(mod.PakFileName);
                        if (userLink != null && IsUserLinkHashValid(userLink, mod.PakFilePath, userLinkStore, mod.PakFileName))
                        {
                            var preferred = dbEntries.Where(e => e.NexusModId == userLink.ModId).ToList();
                            if (preferred.Count > 0)
                            {
                                dbEntries = preferred;
                                dbEntry   = preferred[0];
                                goto resolvedEntry;
                            }
                        }
                    }

                    var uuidMatches = dbEntries
                        .Where(e => string.Equals(e.MetaUuid, mod.MetaUuid, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (uuidMatches.Count == 1)
                    {
                        dbEntry = uuidMatches[0];
                    }
                    else if (uuidMatches.Count > 1)
                    {
                        // Multiple mods share the same pak UUID.
                        // Try stored fileId → stored modId before asking the user.
                        var stored = fileIdStore?.GetEntry(mod.MetaUuid);
                        dbEntry = (stored?.NexusFileId > 0
                                    ? uuidMatches.FirstOrDefault(e => e.NexusFileId == stored.NexusFileId)
                                    : null)
                               ?? (stored?.NexusModId > 0
                                    ? uuidMatches.FirstOrDefault(e => e.NexusModId == stored.NexusModId)
                                    : null);

                        if (dbEntry == null)
                        {
                            // Still ambiguous — flag for user disambiguation.
                            needsDisambiguation = true;
                            dbEntry = null;
                        }
                    }
                    else
                    {
                        // No UUID match in DB entries.
                        // Try stored fileId (most precise) → stored modId before asking the user.
                        var stored = fileIdStore?.GetEntry(mod.MetaUuid);
                        dbEntry = (stored?.NexusFileId > 0
                                    ? dbEntries.FirstOrDefault(e => e.NexusFileId == stored.NexusFileId)
                                    : null)
                               ?? (stored?.NexusModId > 0
                                    ? dbEntries.FirstOrDefault(e => e.NexusModId == stored.NexusModId)
                                    : null);

                        if (dbEntry == null)
                        {
                            // No stored preference found — user must identify.
                            // This covers both: multiple different mods sharing the same pak filename,
                            // AND a single mod with multiple file variants (e.g. legacy vs current slot).
                            // Auto-picking the highest fileId risks downloading the wrong variant.
                            needsDisambiguation = true;
                        }
                    }
                }

                resolvedEntry:

                // Ambiguous: create a stub entry so the user can pick the correct mod.
                if (needsDisambiguation)
                {
                    entry = EnsureEntry(entries, mod);
                    entry.IsAmbiguous                = true;
                    entry.HasMultipleNexusCandidates = true;
                    entry.AmbiguousCandidates        = dbEntries
                        .Select(e => new NexusModCandidate(
                            e.NexusModId,
                            e.NexusModName    ?? "",
                            e.NexusUploadedBy ?? "",
                            e.NexusFileId,
                            e.NexusFileName   ?? "",
                            e.NexusFileVersion ?? ""))
                        .ToList();
                    entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
                    Logger.Info($"Nexus [{mod.MetaModuleName}] ambiguous pak match ({dbEntries.Count} candidates) — needs user disambiguation");
                }

                // Upgrade to latest fileId for the resolved modId.
                // Priority for variant track: UserModLinkStore.fileId (explicit user choice)
                // > ModFileIdStore.fileId (last downloaded).
                // Fall back to highest fileId only when the stored fileId is no longer in the DB
                // (archived/removed) or when no prior record exists.
                if (dbEntry != null && dbEntries.Count > 1)
                {
                    var sameMod = dbEntries.Where(e => e.NexusModId == dbEntry.NexusModId).ToList();

                    var userFileId   = userLinkStore?.GetNexusLink(mod.PakFileName)?.FileId;
                    var storedFileId = (userFileId > 0 ? userFileId : null)
                                     ?? fileIdStore?.GetFileId(mod.MetaUuid);

                    if (storedFileId > 0)
                    {
                        // User has a stored fileId preference — find the matching DB entry.
                        var storedEntry = sameMod.FirstOrDefault(e => e.NexusFileId == storedFileId);
                        if (storedEntry != null)
                        {
                            // Variant still in DB → stay on this track (don't jump to another variant).
                            dbEntry = storedEntry;
                        }
                        else
                        {
                            // Stored fileId no longer in DB (archived/removed) → upgrade to latest.
                            dbEntry = sameMod.OrderByDescending(e => e.NexusFileId).First();
                            Logger.Info($"Nexus [{mod.MetaModuleName}] stored fileId {storedFileId} not in DB — upgraded to latest");
                        }
                    }
                    else
                    {
                        // No prior preference → pick latest fileId.
                        var latest = sameMod.OrderByDescending(e => e.NexusFileId).FirstOrDefault();
                        if (latest != null) dbEntry = latest;
                    }
                }

                if (dbEntries.Count == 0)
                    Logger.Debug($"Nexus [{mod.MetaModuleName}] no DB match for pak={mod.PakFileName}");
                else if (dbEntry == null && !needsDisambiguation)
                    Logger.Debug($"Nexus [{mod.MetaModuleName}] ambiguous DB match ({dbEntries.Count} results, uuid mismatch) — skipped");

                if (dbEntry != null)
                {
                    if (!mod.NexusModId.HasValue)
                        mod.NexusModId = dbEntry.NexusModId;

                    var userLink = userLinkStore?.GetNexusLink(mod.PakFileName);
                    bool hasUpdate = HasNexusUpdate(mod, dbEntry, fileIdStore, userLink);

                    // Stale DB fallback: DB says "no update" but DB hasn't been maintained
                    // for 7+ days AND this mod appears in the recently-updated list.
                    // The DB entry may not reflect the latest fileId — verify via direct API call.
                    if (!hasUpdate &&
                        nexusApi != null &&
                        nexusDb.IsStale &&
                        recentlyUpdated.Contains(dbEntry.NexusModId))
                    {
                        Logger.Info($"[PERF] Nexus [{mod.MetaModuleName}] DB stale + in recently-updated — verifying via API");
                        var latestFile = await nexusApi.GetLatestFileAsync(dbEntry.NexusModId);
                        if (latestFile != null && latestFile.NexusFileId != dbEntry.NexusFileId)
                        {
                            var localFileId = (userLink?.FileId > 0 ? userLink.FileId : (long?)null)
                                           ?? fileIdStore?.GetFileId(mod.MetaUuid);
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
                                if (!string.IsNullOrEmpty(latestFile.NexusFileName))
                                    entry.NexusFileName = latestFile.NexusFileName;
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
                        if (!string.IsNullOrEmpty(dbEntry.NexusFileName))
                            entry.NexusFileName = dbEntry.NexusFileName;
                    }

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
                        if (!string.IsNullOrEmpty(dbEntry.NexusFileName))
                            entry.NexusFileName = dbEntry.NexusFileName;
                        Logger.Debug($"Nexus [{mod.MetaModuleName}] sync-required: installed={mod.MetaVersion} db={dbEntry.NexusFileVersion}");
                    }
                }

                if (entry != null && hasMultipleCandidates)
                    entry.HasMultipleNexusCandidates = true;
                }
                else
                {
                    var knownModId = mod.NexusModId
                        ?? fileIdStore?.GetEntry(mod.MetaUuid)?.NexusModId;

                    // Pak-rename detection (DB-only, no API calls).
                    // If the installed pakFileName has no DB match but the mod is known and has
                    // current entries under a different pak name, the author renamed the pak in a
                    // newer version. Flag for manual check so the user isn't silently left behind.
                    if (dbEntries.Count == 0 && nexusDb != null && knownModId.HasValue)
                    {
                        var modPaks = nexusDb.LookupByModId(knownModId.Value);
                        if (modPaks.Count > 0)
                        {
                            var latest         = modPaks.OrderByDescending(p => p.NexusFileId).First();
                            var availableNames = string.Join(", ", modPaks.Select(p => p.PakFileName).Distinct());
                            entry = EnsureEntry(entries, mod);
                            entry.NexusModPageUrl     = $"https://www.nexusmods.com/{Constants.NEXUS_GAME_DOMAIN}/mods/{knownModId.Value}";
                            entry.RequiresManualCheck = true;
                            entry.NexusFileId         = latest.NexusFileId;
                            entry.NexusFileVersion    = latest.NexusFileVersion;
                            entry.UpdateNewVersion    = latest.NexusFileVersion;
                            entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
                            entry.Changelog           = $"Pak file renamed or reorganized — current Nexus file(s): {availableNames}. Please verify on the mod page.";
                            if (!string.IsNullOrEmpty(latest.NexusModName))  entry.NexusModName  = latest.NexusModName;
                            if (!string.IsNullOrEmpty(latest.NexusFileName)) entry.NexusFileName = latest.NexusFileName;
                            Logger.Info($"Nexus [{mod.MetaModuleName}] pak renamed: installed={mod.PakFileName} current=[{availableNames}]");
                        }
                    }

                    // API fallback: no DB entry but modId known and in recentlyUpdated
                    if (nexusApi != null && knownModId.HasValue &&
                        recentlyUpdated.Count > 0 && recentlyUpdated.Contains(knownModId.Value))
                    {
                        Logger.Info($"[PERF] Nexus [{mod.MetaModuleName}] API fallback — calling GetLatestFileAsync + GetModNameAsync");
                        var latestFileTask = nexusApi.GetLatestFileAsync(knownModId.Value);
                        var modNameTask    = nexusApi.GetModNameAsync(knownModId.Value);
                        await Task.WhenAll(latestFileTask, modNameTask);
                        var latestFile = latestFileTask.Result;
                        if (latestFile != null)
                        {
                            var apiFallbackLink = userLinkStore?.GetNexusLink(mod.PakFileName);
                            var localFileId = (apiFallbackLink?.FileId > 0 ? apiFallbackLink.FileId : (long?)null)
                                           ?? fileIdStore?.GetFileId(mod.MetaUuid);
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
                                if (!string.IsNullOrEmpty(latestFile.NexusFileName))
                                    entry.NexusFileName = latestFile.NexusFileName;
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
        InstalledMod    mod,
        PakLookupEntry  dbEntry,
        ModFileIdStore? store,
        NexusLinkEntry? userLink = null)
    {
        // Priority: UserModLinkStore.fileId (explicit user choice) > ModFileIdStore.fileId (download history)
        var localFileId = (userLink?.FileId > 0 ? userLink.FileId : (long?)null)
                        ?? store?.GetFileId(mod.MetaUuid);
        if (localFileId.HasValue)
        {
            var result = localFileId.Value != dbEntry.NexusFileId;
            if (result)
                Logger.Debug($"Nexus [{mod.MetaModuleName}] fileId check: local={localFileId.Value} db={dbEntry.NexusFileId} → UPDATE");
            return result;
        }

        var verResult = IsNewer(dbEntry.NexusFileVersion, mod.MetaVersion);
        if (verResult)
            Logger.Debug($"Nexus [{mod.MetaModuleName}] version fallback: local={mod.MetaVersion} db={dbEntry.NexusFileVersion} → UPDATE");
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

    // === User link hash validation ===

    /// <summary>
    /// Returns true if the stored user link is still valid for the current pak file.
    /// Validity: no hash stored (legacy entry) OR hash matches current pak.
    /// If the hash mismatches, the link is removed and false is returned so the caller
    /// can fall through to re-disambiguation.
    /// </summary>
    private static bool IsUserLinkHashValid(
        NexusLinkEntry    link,
        string?           pakFilePath,
        UserModLinkStore  store,
        string            pakFileName)
    {
        // No hash stored (legacy entry or hash unavailable) → trust the link as before.
        if (string.IsNullOrEmpty(link.PakHash) || string.IsNullOrEmpty(pakFilePath))
            return true;

        var currentHash = UserModLinkStore.ComputeQuickHash(pakFilePath);
        if (string.IsNullOrEmpty(currentHash))
            return true; // can't compute → don't invalidate

        if (currentHash == link.PakHash)
            return true;

        // Hash mismatch: pak file has been replaced externally.
        Logger.Info($"UpdateChecker: [{pakFileName}] pak hash changed — user link invalidated (was mod {link.ModId}). Re-disambiguating.");
        store.RemoveLink(pakFileName);
        return false;
    }

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
                LocalAuthor          = mod.MetaAuthor ?? "",
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
