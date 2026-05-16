using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Models.Cache;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Orchestrates the update check against Nexus Mods and mod.io.
///
/// Flow:
///   1. Load cached API responses (NexusCachedData, ModioCachedData).
///   2. If cache is expired, call the APIs in parallel to refresh.
///   3. Compare installed version vs. latest API version for each mod.
///   4. Return a list of ModUpdateEntry for mods that have a newer version.
///
/// Matching strategy (spec §4.6):
///   - mod.io:  PublishHandle != 0 → query mod.io by PublishHandle
///   - Nexus:   InstalledMod.NexusModId != null → query Nexus by ModId
///   - Both:    produce a single ModUpdateEntry with both sources listed
///
/// Quiet-fallback (spec §4.7):
///   - 404 / empty response → skip that market silently
///   - No update available → omit from result list
/// </summary>
public static class UpdateChecker
{
    /// <summary>
    /// Checks for updates across Nexus and mod.io for the given installed mods.
    /// </summary>
    /// <param name="installedMods">Result of ModScanner.ScanAsync.</param>
    /// <param name="nexusApi">Nexus client (may be null if key not set).</param>
    /// <param name="modioApi">mod.io client (may be null if key not set).</param>
    /// <param name="nexusIsPremium">Whether the Nexus user holds a Premium subscription.</param>
    /// <param name="cacheExpiryHours">Cache TTL from settings.</param>
    /// <param name="progress">Optional: reports number of mods checked so far.</param>
    public static async Task<List<ModUpdateEntry>> CheckAsync(
        List<InstalledMod> installedMods,
        NexusApi?          nexusApi,
        ModioApi?          modioApi,
        bool               nexusIsPremium,
        int                cacheExpiryHours,
        IProgress<int>?    progress = null)
    {
        // ── Load caches ───────────────────────────────────────────────────
        var nexusCache = NexusApi.LoadCache();
        var modioCache = ModioApi.LoadCache();

        // ── Determine which mods need API calls ───────────────────────────
        var modioMods = installedMods
            .Where(m => m.PublishHandle != 0 && modioApi?.CanMakeRequest() == true)
            .ToList();

        var nexusMods = installedMods
            .Where(m => m.NexusModId.HasValue && nexusApi?.CanMakeRequest() == true)
            .ToList();

        // ── Always refresh caches on each check ──────────────────────────
        // 캐시 TTL로 스킵하면 다운로드 완료 후에도 목록에 남는 stale 문제 발생.
        // rate limit 보호는 NexusApi.Clone()으로 다운로드 인스턴스를 분리하여 처리.
        var tasks = new List<Task>();

        if (modioApi != null && modioMods.Count > 0)
            tasks.Add(RefreshModioCache(modioMods, modioApi, modioCache));

        if (nexusApi != null && nexusMods.Count > 0)
            tasks.Add(RefreshNexusCache(nexusMods, nexusApi, nexusCache));

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
            ModioApi.SaveCache(modioCache);
            NexusApi.SaveCache(nexusCache);
        }

        // ── Build update entries ──────────────────────────────────────────
        var entries      = new Dictionary<string, ModUpdateEntry>(); // key = UUID
        var checkedCount = 0;

        foreach (var mod in installedMods)
        {
            ModUpdateEntry? entry = null;

            // mod.io check
            if (mod.PublishHandle != 0 &&
                modioCache.Mods.TryGetValue(mod.UUID, out var modioData))
            {
                if (IsNewer(modioData.LatestVersion, mod.Version))
                {
                    entry = EnsureEntry(entries, mod);
                    entry.NewVersion      = modioData.LatestVersion;
                    entry.ModioNewVersion = modioData.LatestVersion;  // 소스별 버전
                    entry.ModioUrl        = modioData.ProfileUrl;
                    entry.AvailableSources.Add(UpdateSource.MODIO);
                    entry.Changelog  = "";
                }
            }

            // Nexus check
            if (mod.NexusModId.HasValue &&
                nexusCache.Mods.TryGetValue(mod.UUID, out var nexusData))
            {
                if (IsNewer(nexusData.Version, mod.Version))
                {
                    entry = EnsureEntry(entries, mod);
                    entry.NexusNewVersion = nexusData.Version;  // 소스별 버전
                    // Keep the higher of the two new versions for display
                    if (string.IsNullOrEmpty(entry.NewVersion) ||
                        IsNewer(nexusData.Version, entry.NewVersion))
                        entry.NewVersion = nexusData.Version;

                    entry.NexusUrl = nexusData.ProfileUrl;
                    entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
                }
            }

            if (entry != null)
                DetermineDefaultSource(entry, nexusIsPremium);

            checkedCount++;
            progress?.Report(checkedCount);
        }

        Logger.Info($"UpdateChecker: {entries.Count} update(s) found out of {installedMods.Count} mods");
        return entries.Values.ToList();
    }

    // ── Cache refresh helpers ─────────────────────────────────────────────

    private static async Task RefreshModioCache(
        List<InstalledMod> mods, ModioApi api, ModioCachedData cache)
    {
        Logger.Info($"UpdateChecker: refreshing mod.io cache for {mods.Count} mods (batch)");

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

    private static async Task RefreshNexusCache(
        List<InstalledMod> mods, NexusApi api, NexusCachedData cache)
    {
        var targets = mods.Where(m => m.NexusModId.HasValue).ToList();
        Logger.Info($"UpdateChecker: refreshing Nexus cache for {targets.Count} mods (parallel)");

        // 동시 50개 — Nexus rate limit(하루 20,000회) 안에서 안전
        var semaphore = new SemaphoreSlim(50);
        var tasks     = targets.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!api.CanMakeRequest()) return;
                var data = await api.GetModInfoAsync(mod.NexusModId!.Value);
                if (data != null)
                    lock (cache.Mods) { cache.Mods[mod.UUID] = data; }
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        cache.LastUpdated = DateTime.UtcNow;
        Logger.Info($"UpdateChecker: Nexus cache refresh complete ({cache.Mods.Count} entries)");
    }

    // ── Version comparison ────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="candidate"/> is strictly newer than
    /// <paramref name="current"/>.
    ///
    /// Strategy (spec §4.6):
    ///   1. Try SemVer / System.Version parse.
    ///   2. Strip leading "v" before parsing.
    ///   3. If parsing fails for either side, fall back to string inequality
    ///      (treats any difference as "newer" — conservative).
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current))   return true;

        var c = Normalize(candidate);
        var v = Normalize(current);

        if (Version.TryParse(c, out var cv) && Version.TryParse(v, out var vv))
            return cv > vv;

        // Fallback: string inequality (any change is treated as an update)
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
                NexusModId     = mod.NexusModId,   // ← BUG FIX: NexusModId를 entry에 전달
                ModName        = mod.Name,
                CurrentVersion = mod.Version,
                PakFilePath    = mod.PakFilePath,
            };
            dict[mod.UUID] = entry;
        }
        return entry;
    }

    /// <summary>
    /// Decides DefaultSource and CanAutoDownload per spec §4.5 / §4.6:
    ///   1. mod.io available → prefer it (auto download always works)
    ///   2. Nexus Premium → Nexus is fine too
    ///   3. Nexus free → no auto download
    ///   4. Both → mod.io preferred unless overridden by PreferredSource
    /// </summary>
    private static void DetermineDefaultSource(ModUpdateEntry entry, bool nexusIsPremium)
    {
        var hasMod = entry.AvailableSources.Contains(UpdateSource.MODIO);
        var hasNex = entry.AvailableSources.Contains(UpdateSource.NEXUSMODS);

        // Honor user pin
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
            entry.CanAutoDownload = false; // free Nexus user → page-open fallback
        }
    }
}
