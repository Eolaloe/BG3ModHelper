using System.Text.RegularExpressions;
using BG3MM_UpdateHelper.Models;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Validates and safely parses nxm:// URLs.
/// </summary>
public static partial class NxmValidator
{
    // Game host must be alphanumeric (Nexus uses lowercase game IDs like "baldursgate3", "skyrimspecialedition")
    [GeneratedRegex(@"^[a-z0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex GameHostPattern();

    // Key token must be safe alphanumeric (no path/query injection)
    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex KeyPattern();

    public enum ValidationResult
    {
        Valid,
        InvalidFormat,
        InvalidScheme,
        InvalidPath,
        InvalidGame,
        InvalidKey,
        Expired,
    }

    /// <summary>
    /// Attempts to parse and validate a raw nxm URL.
    /// Returns Valid + parsed url on success, or a specific error otherwise.
    /// </summary>
    public static (ValidationResult Result, NxmUrl? Url) TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (ValidationResult.InvalidFormat, null);

        NxmUrl url;
        try
        {
            url = NxmUrl.Parse(raw);
        }
        catch (UriFormatException)
        {
            return (ValidationResult.InvalidFormat, null);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("scheme"))
        {
            return (ValidationResult.InvalidScheme, null);
        }
        catch (ArgumentException)
        {
            return (ValidationResult.InvalidPath, null);
        }

        // Game host validation
        if (!GameHostPattern().IsMatch(url.Game))
            return (ValidationResult.InvalidGame, null);

        // Key validation (presence + safe chars). Empty key = malformed (Nexus always includes it).
        if (string.IsNullOrEmpty(url.Key) || !KeyPattern().IsMatch(url.Key))
            return (ValidationResult.InvalidKey, null);

        // Expiry check (last — informational; caller may still want url for diagnostics)
        if (url.IsExpired)
            return (ValidationResult.Expired, url);

        return (ValidationResult.Valid, url);
    }

    /// <summary>Convenience: returns true if URL is fully valid and not expired.</summary>
    public static bool IsValid(string raw) => TryParse(raw).Result == ValidationResult.Valid;
}
