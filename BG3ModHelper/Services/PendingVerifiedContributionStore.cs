using System.IO;
using Newtonsoft.Json;

namespace BG3ModHelper.Services;

/// <summary>
/// Queues download-verified UUID contributions for batch submission at next update check.
///
/// "Verified" means the pak was directly downloaded through the app (nxm:// or built-in
/// downloader) with a confirmed Nexus modId + fileId — a stronger guarantee than the
/// normal community-scan contributions which rely on 3-vote majority.
///
/// Lifecycle:
///   • Write: immediately after a successful download (NxmInstaller / Downloader)
///   • Read + Clear: at update check time, merged with regular contributions
///
/// File: %LOCALAPPDATA%\BG3ModHelper\pending_verified_contributions.json
/// Format: [ { "pakFileName": "...", "metaUuid": "...", "nexusModId": 0, "nexusFileId": 0 } ]
/// </summary>
public class PendingVerifiedContributionStore
{
    private static readonly string FilePath =
        Path.Combine(SettingsStore.GetDataFolder(), "pending_verified_contributions.json");

    private List<PendingVerifiedEntry> _queue = new();

    // === Initialization ===

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var raw = JsonConvert.DeserializeObject<List<PendingVerifiedEntry>>(
                File.ReadAllText(FilePath));
            if (raw != null)
                _queue = raw;

            if (_queue.Count > 0)
                Logger.Info($"PendingVerifiedContributionStore: loaded {_queue.Count} pending entry(ies)");
        }
        catch (Exception ex)
        {
            Logger.Warn($"PendingVerifiedContributionStore.Load: {ex.Message}");
        }
    }

    // === Write ===

    /// <summary>
    /// Enqueues a verified contribution. Deduplicates by pakFileName + modId + fileId.
    /// Saves immediately so data survives app restarts.
    /// </summary>
    public void Add(string pakFileName, string metaUuid, int nexusModId, long nexusFileId)
    {
        if (string.IsNullOrEmpty(pakFileName) || string.IsNullOrEmpty(metaUuid)) return;
        if (nexusModId == 0 || nexusFileId == 0) return;

        var pak = pakFileName.ToLowerInvariant();

        // Deduplicate: same pak + modId + fileId already queued
        bool exists = _queue.Any(e =>
            string.Equals(e.PakFileName, pak, StringComparison.OrdinalIgnoreCase) &&
            e.NexusModId == nexusModId &&
            e.NexusFileId == nexusFileId);

        if (exists) return;

        _queue.Add(new PendingVerifiedEntry(pak, metaUuid.ToLowerInvariant(), nexusModId, nexusFileId));
        Save();
        Logger.Info($"PendingVerifiedContributionStore: queued {pakFileName} → uuid={metaUuid} modId={nexusModId} fileId={nexusFileId}");
    }

    // === Read ===

    /// <summary>Returns all pending verified contributions.</summary>
    public IReadOnlyList<PendingVerifiedEntry> GetAll() => _queue;

    // === Clear ===

    /// <summary>Clears the queue after successful contribution send.</summary>
    public void Clear()
    {
        if (_queue.Count == 0) return;
        var count = _queue.Count;
        _queue.Clear();
        Save();
        Logger.Info($"PendingVerifiedContributionStore: cleared {count} entry(ies) after successful send");
    }

    // === Persistence ===

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonConvert.SerializeObject(_queue, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"PendingVerifiedContributionStore.Save: {ex.Message}");
        }
    }
}

/// <summary>One pending verified contribution entry.</summary>
public record PendingVerifiedEntry(
    [property: JsonProperty("pakFileName")]  string PakFileName,
    [property: JsonProperty("metaUuid")]     string MetaUuid,
    [property: JsonProperty("nexusModId")]   int    NexusModId,
    [property: JsonProperty("nexusFileId")]  long   NexusFileId);
