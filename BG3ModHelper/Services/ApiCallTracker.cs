namespace BG3ModHelper.Services;

/// <summary>
/// Session-only API call counter for DevMode diagnostics.
/// Tracks how many calls were made per category during a session window.
/// Thread-safe; all mutations are lock-protected.
/// </summary>
internal static class ApiCallTracker
{
    private static readonly Dictionary<string, int> _counts = new();
    private static int _snapshotHourly = -1;
    private static int _snapshotDaily  = -1;

    // ── Session control ───────────────────────────────────────────────────────

    /// <summary>
    /// Resets counters and captures the current rate-limit values as the baseline.
    /// Call at the start of an update check so the delta reflects only that check.
    /// </summary>
    public static void BeginSession()
    {
        lock (_counts) _counts.Clear();
        _snapshotHourly = NexusApi.LastKnownHourlyRemaining;
        _snapshotDaily  = NexusApi.LastKnownDailyRemaining;
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    /// <summary>Records one API call. No-ops when DevMode is inactive.</summary>
    public static void Record(string category)
    {
        if (!DevMode.IsActive) return;
        lock (_counts)
        {
            _counts.TryGetValue(category, out var n);
            _counts[category] = n + 1;
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Returns a snapshot of (category, count) pairs sorted by count descending.</summary>
    public static IReadOnlyList<(string Category, int Count)> GetSnapshot()
    {
        lock (_counts)
            return _counts.Count == 0
                ? []
                : _counts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
    }

    /// <summary>Total calls recorded since the last BeginSession / app start.</summary>
    public static int TotalCalls
    {
        get { lock (_counts) return _counts.Values.Sum(); }
    }

    /// <summary>
    /// Returns how many hourly / daily calls were consumed since BeginSession.
    /// Returns (-1, -1) when no baseline is available.
    /// </summary>
    public static (int HourlyUsed, int DailyUsed) GetRateLimitDelta()
    {
        var h = NexusApi.LastKnownHourlyRemaining;
        var d = NexusApi.LastKnownDailyRemaining;
        return (
            _snapshotHourly >= 0 && h >= 0 ? _snapshotHourly - h : -1,
            _snapshotDaily  >= 0 && d >= 0 ? _snapshotDaily  - d : -1
        );
    }

    // ── Path categorisation (used by NexusApi.GetAsync) ──────────────────────

    /// <summary>Maps an API path to a human-readable category label.</summary>
    internal static string CategorizeCall(string path) => path switch
    {
        var p when p.Contains("validate.json")      => "Rate limit check",
        var p when p.Contains("/updated.json")       => "Monthly update list",
        var p when p.Contains("/changelogs.json")    => "Changelog",
        var p when p.Contains("/md5_search/")        => "MD5 lookup",
        var p when p.Contains("/download_link.json") => "Download URL",
        var p when p.Contains("/files/")             => "File meta",
        var p when p.Contains("/files.json")         => "Files page",
        var p when p.Contains("/mods/")              => "Mod info",
        _                                            => "Other"
    };
}
