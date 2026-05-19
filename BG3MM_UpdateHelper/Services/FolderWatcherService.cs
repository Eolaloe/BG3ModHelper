using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BG3MM_UpdateHelper.Models;
using LSLib.LS;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace BG3MM_UpdateHelper.Services;

// === Data ===

/// <summary>
/// Source detection result for an archive dropped or detected in the watch folder.
/// </summary>
public sealed record ArchiveSourceInfo(
    /// <summary>"Nexus" / "ModIO" / "Both" / "Others"</summary>
    string  Source,

    /// <summary>
    /// "Confirmed" — 99%+ certain (DB match or PublishHandle-based)
    /// "Estimated" — 85~90% likely (zip filename pattern-based)
    /// "Required"  — user must choose (Both + no zip pattern)
    /// </summary>
    string  Confidence,

    /// <summary>Source hint inferred from zip filename pattern. "Nexus" / "ModIO" / null</summary>
    string? ZipHintSource,

    string  ModName,
    string  PakFileName,
    int?    NexusModId,
    string? NexusPageUrl,
    ulong   PublishHandle,

    /// <summary>Full path of the original archive file.</summary>
    string  ArchivePath,

    /// <summary>Mod version decoded from meta.lsx Version64. Empty if not available.</summary>
    string  ModVersion,

    /// <summary>Platform mod name from Nexus DB lookup. Empty for mod.io (fetched later via API).</summary>
    string  PlatformModName
);

// === Service ===

/// <summary>
/// Shared archive-processing utilities (Downloader, FolderWatcher, drag-and-drop).
/// Also manages FileSystemWatcher for the download folder.
/// </summary>
public sealed class FolderWatcherService : IDisposable
{
    private readonly NexusIdDatabase                     _nexusDb;
    private readonly Func<ArchiveSourceInfo, Task>       _onDetected;
    private readonly List<FileSystemWatcher>             _watchers = new();
    private readonly Dictionary<string, DateTime>        _recent   = new();
    private readonly object                              _recentLock = new();
    private const    int                                 DupGuardSeconds = 5;

    private static readonly string[] SupportedExtensions = { ".zip", ".7z", ".rar" };

    // === Zip filename patterns ===

    /// <summary>
    /// Nexus enforced naming: ModName-{modId}-{v1}-{v2}-{v3}-{v4}-{timestamp}.zip
    /// Version is always 4 numeric groups. Last group is Unix timestamp.
    /// e.g. ShovelEvolved-14242-3-2-5-0-1779051758.zip
    /// </summary>
    private static readonly Regex NexusZipPattern =
        new(@"^.+-(\d+)-(\d+)-(\d+)-(\d+)-(\d+)-(\d+)\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// mod.io toolkit naming: projectname_{first-3-UUID-groups}-{encoded}.zip
    /// First 3 UUID groups match exactly; remainder is mod.io-encoded.
    /// e.g. dnd2024_897914ef-5c96-053c-44a-e6go.zip
    /// </summary>
    private static readonly Regex ModioZipPattern =
        new(@"^[a-z0-9]+_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-.+\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public FolderWatcherService(NexusIdDatabase nexusDb,
                                Func<ArchiveSourceInfo, Task> onDetected)
    {
        _nexusDb    = nexusDb;
        _onDetected = onDetected;
    }

    // === Watcher lifecycle ===

    public void Start(string folderPath)
    {
        Stop();
        if (!Directory.Exists(folderPath)) return;

        foreach (var ext in SupportedExtensions)
        {
            var w = new FileSystemWatcher(folderPath, $"*{ext}")
            {
                NotifyFilter        = NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            w.Created += OnFileCreated;
            w.Renamed += OnFileRenamed;
            _watchers.Add(w);
        }
        Logger.Info($"FolderWatcherService: watching {folderPath}");
    }

    public void Stop()
    {
        foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();
    }

    public void Dispose() => Stop();

    // === OS default Downloads folder ===

    public static string GetDefaultDownloadsFolder() =>
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile), "Downloads");

    // === FileSystemWatcher callback ===

    private async void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        try { await HandleNewFile(e.FullPath); }
        catch (Exception ex) { Logger.Error($"OnFileCreated failed: {ex.Message}"); }
    }

    private async void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        try { await HandleNewFile(e.FullPath); }
        catch (Exception ex) { Logger.Error($"OnFileRenamed failed: {ex.Message}"); }
    }

    private async Task HandleNewFile(string path)
    {

        lock (_recentLock)
        {
            if (_recent.TryGetValue(path, out var last) &&
                (DateTime.UtcNow - last).TotalSeconds < DupGuardSeconds)
                return;
            _recent[path] = DateTime.UtcNow;
        }

        // Wait until file size is stable (download/copy complete)
        if (!await WaitForStableFileAsync(path))
        {
            Logger.Warn($"FolderWatcher: file never stabilized — {Path.GetFileName(path)}");
            return;
        }

        // Retry analysis with backoff — file may still be locked by AV/etc.
        ArchiveSourceInfo? info = null;
        int[] backoffSeconds = { 1, 2, 4, 8 };
        for (int attempt = 0; attempt < backoffSeconds.Length; attempt++)
        {
            info = await Task.Run(() => AnalyzeArchive(path));
            if (info != null) break;
            if (attempt < backoffSeconds.Length - 1)
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds[attempt]));
        }

        if (info == null)
        {
            Logger.Warn($"FolderWatcher: gave up on {Path.GetFileName(path)} after retries");
            return;
        }

        await _onDetected(info);
    }

    /// <summary>
    /// Polls file size every 500ms until stable for 3 consecutive checks (~1.5s),
    /// or until maxWaitSec elapses. Returns false on timeout or file deletion.
    /// </summary>
    private static async Task<bool> WaitForStableFileAsync(string path, int maxWaitSec = 60)
    {
        long lastSize    = -1;
        int  stableCount = 0;
        var  deadline    = DateTime.UtcNow.AddSeconds(maxWaitSec);

        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path)) return false;
            try
            {
                var size = new FileInfo(path).Length;
                if (size == lastSize)
                {
                    if (++stableCount >= 3) return true;
                }
                else
                {
                    stableCount = 0;
                    lastSize    = size;
                }
            }
            catch
            {
                // File locked or transient IO error — treat as unstable
                stableCount = 0;
            }
            await Task.Delay(500);
        }
        return false;
    }

    // === Archive analysis ===

    /// <summary>
    /// Two-step source detection:
    ///   Step 1: zip filename pattern (Nexus/mod.io hint)
    ///   Step 2: pak meta.lsx (PublishHandle + Nexus DB lookup)
    /// Returns null if no pak found.
    /// </summary>
    public ArchiveSourceInfo? AnalyzeArchive(string archivePath)
    {
        try
        {
            // Step 1: analyze zip filename pattern
            var zipName = Path.GetFileName(archivePath);
            var (zipHint, zipModId) = AnalyzeZipFileName(zipName);

            // Check for pak entry
            var pakFileName = FindFirstPakName(archivePath);
            if (pakFileName == null) return null;

            // Extract pak to temp + parse meta
            var tempDir = Path.Combine(Path.GetTempPath(), "BG3MM_FolderWatch",
                                       Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var extracted = ExtractPakFiles(archivePath, tempDir);
                if (extracted.Count == 0) return null;

                var mod     = ParsePakMeta(extracted[0]);
                return BuildSourceInfo(mod, pakFileName, archivePath, zipHint, zipModId, isDirectPak: false);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { Logger.Warn($"Failed to delete temp dir: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"FolderWatcherService.AnalyzeArchive: {ex.Message}");
            return null;
        }
    }

    // === Zip filename analysis ===

    /// <summary>
    /// Extracts source hint and modId from the zip filename.
    /// Returns (hint, modId): hint = "Nexus" / "ModIO" / null
    /// </summary>
    private static (string? hint, int modId) AnalyzeZipFileName(string zipName)
    {
        // Nexus pattern: .+-modId-v1-v2-v3-v4-timestamp.zip
        var nexusMatch = NexusZipPattern.Match(zipName);
        if (nexusMatch.Success &&
            int.TryParse(nexusMatch.Groups[1].Value, out var modId))
        {
            return ("Nexus", modId);
        }

        // mod.io pattern: projectname_UUID-first3groups-encoded.zip
        if (ModioZipPattern.IsMatch(zipName))
            return ("ModIO", 0);

        return (null, 0);
    }

    // === Public static utilities (shared with Downloader) ===

    /// <summary>Returns true if the archive contains at least one .pak file.</summary>
    public static bool ContainsPak(string archivePath)
    {
        try
        {
            using var stream  = File.OpenRead(archivePath);
            using var archive = ArchiveFactory.OpenArchive(stream);
            return archive.Entries.Any(static e =>
                !e.IsDirectory &&
                (e.Key ?? "").EndsWith(".pak", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Extracts all .pak files from the archive to destDir.
    /// Supports zip, 7z, rar (SharpCompress auto-detect).
    /// </summary>
    public static List<string> ExtractPakFiles(string archivePath, string destDir)
    {
        using var stream  = File.OpenRead(archivePath);
        using var archive = ArchiveFactory.OpenArchive(stream);

        var pakEntries = archive.Entries
            .Where(static e => !e.IsDirectory &&
                        (e.Key ?? "").EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pakEntries.Count == 0)
            Logger.Warn($"FolderWatcherService: no .pak in archive — entries: " +
                        string.Join(", ", archive.Entries.Take(10).Select(static e => e.Key ?? "")));

        var extracted = new List<string>();
        foreach (var entry in pakEntries)
        {
            string dest = Path.Combine(destDir, Path.GetFileName(entry.Key ?? ""));
            entry.WriteToFile(dest, new ExtractionOptions { Overwrite = true });
            extracted.Add(dest);
            Logger.Info($"FolderWatcherService: extracted {Path.GetFileName(entry.Key ?? "")}");
        }
        return extracted;
    }

    // === Private helpers ===

    private static string? FindFirstPakName(string archivePath)
    {
        using var stream  = File.OpenRead(archivePath);
        using var archive = ArchiveFactory.OpenArchive(stream);
        return archive.Entries
            .Where(static e => !e.IsDirectory &&
                        (e.Key ?? "").EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            .Select(e => Path.GetFileName(e.Key ?? ""))
            .FirstOrDefault();
    }

    private static string BuildVersion(XElement moduleInfo)
    {
        var v64Raw = moduleInfo.Elements("attribute")
            .FirstOrDefault(a => a.Attribute("id")?.Value == "Version64")
            ?.Attribute("value")?.Value;

        if (!string.IsNullOrEmpty(v64Raw) && long.TryParse(v64Raw, out var v64))
        {
            // Same bit layout as ModScanner / BG3ModManager / LSLib
            var major    = (v64 >> 55) & 0x7FF;
            var minor    = (v64 >> 47) & 0xFF;
            var revision = (v64 >> 31) & 0xFFFF;
            var build    = v64 & 0x7FFFFFFF;
            return $"{major}.{minor}.{revision}.{build}";
        }
        return "";
    }

    /// <summary>
    /// Analyzes a .pak file directly (no archive extraction).
    /// Used for drag-and-drop of raw pak files.
    /// No zip filename hint — switch button always enabled.
    /// </summary>
    public ArchiveSourceInfo? AnalyzePakFile(string pakPath)
    {
        try
        {
            var mod = ParsePakMeta(pakPath);
            if (mod == null) return null;

            return BuildSourceInfo(mod, Path.GetFileName(pakPath), pakPath,
                                   zipHint: null, zipModId: 0, isDirectPak: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"FolderWatcherService.AnalyzePakFile: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Core source detection logic shared by AnalyzeArchive and AnalyzePakFile.
    /// isDirectPak: no zip hint — ModIO/Others become Estimated so switch stays enabled.
    /// </summary>
    private ArchiveSourceInfo BuildSourceInfo(
        InstalledMod? mod,
        string        pakFileName,
        string        archivePath,
        string?       zipHint,
        int           zipModId,
        bool          isDirectPak)
    {
        var modName  = mod?.MetaModuleName ?? Path.GetFileNameWithoutExtension(pakFileName);
        var handle   = mod?.ModioPublishHandle ?? 0;
        var fileBase = Path.GetFileNameWithoutExtension(pakFileName);

        var nexusEntry = _nexusDb.LookupSingle(fileBase);
        bool hasNexus  = nexusEntry != null;
        bool hasModio  = handle != 0;

        bool isBoth = (hasModio && hasNexus)
                   || (hasModio && zipHint == "Nexus")
                   || (hasNexus && zipHint == "ModIO");

        string source;
        string confidence;

        if (isBoth)
        {
            source     = "Both";
            confidence = zipHint != null ? "Estimated" : "Required";
        }
        else if (hasModio)
        {
            source     = "ModIO";
            confidence = isDirectPak ? "Estimated" : "Confirmed";
        }
        else if (hasNexus)
        {
            source     = "Nexus";
            confidence = "Confirmed";
        }
        else if (zipHint == "Nexus")
        {
            source     = "Nexus";
            confidence = "Estimated";
        }
        else
        {
            source     = "Others";
            confidence = isDirectPak ? "Estimated" : "Confirmed";
        }

        var nexusModId = nexusEntry?.NexusModId
            ?? (zipHint == "Nexus" && zipModId > 0 ? zipModId : (int?)null);

        var nexusPageUrl = nexusModId.HasValue
            ? $"https://www.nexusmods.com/baldursgate3/mods/{nexusModId.Value}"
            : null;

        return new ArchiveSourceInfo(
            Source:          source,
            Confidence:      confidence,
            ZipHintSource:   zipHint,
            ModName:         modName,
            PakFileName:     pakFileName,
            NexusModId:      nexusModId,
            NexusPageUrl:    nexusPageUrl,
            PublishHandle:   handle,
            ArchivePath:     archivePath,
            ModVersion:      mod?.MetaVersion ?? "",
            PlatformModName: nexusEntry?.NexusModName ?? "");
    }

    /// <summary>
    /// Public wrapper for pak meta parsing — used by drag-and-drop pak validation.
    /// Returns null if not a valid BG3 mod pak.
    /// </summary>
    public static InstalledMod? ParsePakMetaPublic(string pakPath) => ParsePakMeta(pakPath);

    /// <summary>
    /// Opens a pak with LSLib and parses meta.lsx — same logic as ModScanner.
    /// Extracts Name (ModuleInfo/Name) and PublishHandle.
    /// </summary>
    private static InstalledMod? ParsePakMeta(string pakPath)
    {
        try
        {
            using var pak = new PackageReader().Read(pakPath);
            var metaFile  = pak.Files.FirstOrDefault(f =>
                f.Name.EndsWith("meta.lsx", StringComparison.OrdinalIgnoreCase));
            if (metaFile == null) return null;

            using var stream = metaFile.CreateContentReader();
            var doc          = XDocument.Load(stream);
            var moduleInfo   = doc.Descendants("node")
                .FirstOrDefault(n => n.Attribute("id")?.Value == "ModuleInfo");
            if (moduleInfo == null) return null;

            string XAttr(string key) =>
                moduleInfo.Elements("attribute")
                    .FirstOrDefault(a => a.Attribute("id")?.Value == key)
                    ?.Attribute("value")?.Value ?? "";

            ulong XULong(string key) =>
                ulong.TryParse(XAttr(key), out var v) ? v : 0;

            return new InstalledMod
            {
                MetaUuid          = XAttr("UUID"),
                MetaModuleName    = XAttr("Name"),
                MetaAuthor        = XAttr("Author"),
                ModioPublishHandle = XULong("PublishHandle"),
                MetaVersion       = BuildVersion(moduleInfo),
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"FolderWatcherService.ParsePakMeta: {ex.Message}");
            return null;
        }
    }
}
