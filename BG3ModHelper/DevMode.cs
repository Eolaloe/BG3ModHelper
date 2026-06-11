namespace BG3ModHelper;

/// <summary>
/// Session-only developer flags.
/// Never persisted to disk — state resets on every app launch.
/// Activated via Konami code (↑↑↓↓←→←→) in the Settings window.
/// </summary>
internal static class DevMode
{
    public static bool IsActive { get; private set; }

    // ── Per-feature flags ────────────────────────────────────────────────────

    /// <summary>
    /// When true, the changelog API call (changelogs.json) is skipped during
    /// update checks. Useful when the hourly API quota is nearly exhausted.
    /// </summary>
    public static bool SkipChangelog { get; set; }

    // ── Activation ───────────────────────────────────────────────────────────

    /// <summary>Fired once when debug mode is first activated this session.</summary>
    public static event Action? Activated;

    internal static void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        Activated?.Invoke();
        Services.Logger.Info("[DevMode] Debug mode activated");
    }
}
