using System.Web;

namespace BG3ModHelper.Models;

/// <summary>
/// Parsed nxm:// URL.
/// Format: nxm://&lt;game&gt;/mods/&lt;modId&gt;/files/&lt;fileId&gt;?key=&lt;key&gt;&amp;expires=&lt;unix&gt;&amp;user_id=&lt;id&gt;
/// </summary>
public sealed record NxmUrl(
    string Game,
    int    NexusModId,
    long   NexusFileId,
    string Key,
    long   Expires,
    int?   UserId = null)
{
    /// <summary>
    /// Parses a raw nxm URL string. Throws on invalid format or scheme.
    /// Use NxmValidator.TryParse for non-throwing parsing.
    /// </summary>
    public static NxmUrl Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Empty URL");

        var u = new Uri(raw);
        if (u.Scheme != "nxm")
            throw new ArgumentException($"Not nxm scheme: {u.Scheme}");

        // path = "/mods/<modId>/files/<fileId>"
        var parts = u.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 4 || parts[0] != "mods" || parts[2] != "files")
            throw new ArgumentException("Malformed nxm path");

        if (!int.TryParse(parts[1], out var modId) || modId <= 0)
            throw new ArgumentException("Invalid mod ID");
        if (!int.TryParse(parts[3], out var fileId) || fileId <= 0)
            throw new ArgumentException("Invalid file ID");

        var q       = HttpUtility.ParseQueryString(u.Query);
        var key     = q["key"] ?? "";
        var expires = long.TryParse(q["expires"], out var e) ? e : 0;
        var userId  = int.TryParse(q["user_id"], out var uid) ? (int?)uid : null;

        return new NxmUrl(
            Game:        u.Host,
            NexusModId:  modId,
            NexusFileId: fileId,
            Key:         key,
            Expires:     expires,
            UserId:      userId
        );
    }

    /// <summary>True if the URL's expires timestamp has passed.</summary>
    public bool IsExpired => Expires > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= Expires;

    /// <summary>True if this URL targets Baldur's Gate 3.</summary>
    public bool IsBG3 => Game.Equals("baldursgate3", StringComparison.OrdinalIgnoreCase);
}
