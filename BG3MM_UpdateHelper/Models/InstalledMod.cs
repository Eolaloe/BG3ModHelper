namespace BG3MM_UpdateHelper.Models;

/// <summary>
/// Represents a single installed mod — data extracted from its .pak file's meta.lsx.
/// </summary>
public class InstalledMod
{
    /// <summary>Mod UUID as stored in meta.lsx (e.g. "a1b2c3d4-…").</summary>
    public string UUID { get; set; } = "";

    /// <summary>
    /// mod.io global mod ID. Matches mod.io's mods/{id} API path.
    /// 0 means the mod is not published on mod.io (Larian built-in or Nexus-only).
    /// </summary>
    public ulong PublishHandle { get; set; } = 0;

    /// <summary>Human-readable mod name from meta.lsx.</summary>
    public string Name { get; set; } = "";

    /// <summary>Author field from meta.lsx.</summary>
    public string Author { get; set; } = "";

    /// <summary>Version string as it appears in meta.lsx (e.g. "1.3.0.0").</summary>
    public string Version { get; set; } = "";

    /// <summary>Absolute path to the .pak file on disk.</summary>
    public string PakFilePath { get; set; } = "";

    /// <summary>
    /// Last-write timestamp of the .pak file.
    /// Used as the cache invalidation key: if this changes the entry is re-parsed.
    /// </summary>
    public DateTime PakFileModified { get; set; }

    /// <summary>
    /// Nexus Mods mod ID, if known.
    /// Currently populated from Nexus API responses in Phase 3.
    /// </summary>
    public int? NexusModId { get; set; }

    /// <summary>
    /// User-pinned update source. Null means "use both" (default behaviour).
    /// </summary>
    public UpdateSource? PreferredUpdateSource { get; set; }

    public override string ToString() =>
        $"{Name} v{Version} [{UUID[..Math.Min(8, UUID.Length)]}…]";
}

public enum UpdateSource { NEXUSMODS, MODIO, BOTH }
