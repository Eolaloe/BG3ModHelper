using System.IO;
using System.Xml.Linq;
using BG3ModHelper.Models;
using BG3ModHelper.Models.Cache;
using LSLib.LS;
using Newtonsoft.Json;

namespace BG3ModHelper.Services;

/// <summary>
/// Scans the BG3 mods folder, parses each .pak file's meta.lsx, and caches results.
/// Uses LSLib's PackageReader to open .pak files, then reads meta.lsx as plain XML
/// via System.Xml.Linq.
/// Only changed .pak files are re-parsed on subsequent scans.
/// </summary>
public static class ModScanner
{
    private static readonly string CacheFilePath = Path.Combine(
        SettingsStore.GetDataFolder(), "installedmods.json");

    public static async Task<List<InstalledMod>> ScanAsync(
        string modsFolder,
        IProgress<int>? progress = null)
    {
        if (!Directory.Exists(modsFolder))
        {
            Logger.Warn($"ModScanner: mods folder not found — {modsFolder}");
            return new List<InstalledMod>();
        }

        var pakFiles = Directory.GetFiles(modsFolder, "*.pak");
        Logger.Info($"ModScanner: found {pakFiles.Length} .pak files");

        var cache        = LoadCache();
        var results      = new List<InstalledMod>();
        var cacheChanged = false;
        var processed    = 0;

        await Task.Run(() =>
        {
            foreach (var pakPath in pakFiles)
            {
                var modifiedUtc = File.GetLastWriteTimeUtc(pakPath);

                if (cache.Mods.TryGetValue(pakPath, out var entry) &&
                    entry.PakFileLastWriteTime == modifiedUtc)
                {
                    results.Add(entry.ModData);
                }
                else
                {
                    var mod = ParsePak(pakPath);
                    if (mod != null)
                    {
                        mod.PakFilePath          = pakPath;
                        mod.PakFileName          = Path.GetFileName(pakPath);
                        mod.PakFileLastWriteTime = modifiedUtc;
                        results.Add(mod);

                        cache.Mods[pakPath] = new InstalledModCacheEntry
                        {
                            PakFileLastWriteTime = modifiedUtc,
                            ModData         = mod
                        };
                        cacheChanged = true;
                    }
                }

                processed++;
                progress?.Report(processed);
            }

            var pakFilesSet = new HashSet<string>(pakFiles, StringComparer.OrdinalIgnoreCase);
            var staleKeys = cache.Mods.Keys
                .Where(k => !pakFilesSet.Contains(k))
                .ToList();

            if (staleKeys.Count > 0)
            {
                foreach (var k in staleKeys)
                    cache.Mods.Remove(k);
                cacheChanged = true;
            }
        });

        if (cacheChanged)
            SaveCache(cache);

        Logger.Info($"ModScanner: complete — {results.Count} mods parsed");
        return results;
    }

    // === .pak parsing ===

    /// <summary>
    /// Reads UUID/Name/etc from a .pak file's meta.lsx. Returns null if unreadable.
    /// Public wrapper for use by external callers (e.g. NxmInstaller).
    /// </summary>
    public static InstalledMod? InspectPak(string pakPath) => ParsePak(pakPath);

    private static InstalledMod? ParsePak(string pakPath)
    {
        try
        {
            using var pak = new PackageReader().Read(pakPath);

            var metaFile = pak.Files.FirstOrDefault(f =>
                f.Name.EndsWith("meta.lsx", StringComparison.OrdinalIgnoreCase));

            if (metaFile == null)
                return null;

            using var stream = metaFile.CreateContentReader();
            return ParseMetaLsx(stream);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ModScanner: skipping {Path.GetFileName(pakPath)} — {ex.Message}");
            return null;
        }
    }

    private static InstalledMod? ParseMetaLsx(Stream stream)
    {
        var doc = XDocument.Load(stream);

        var moduleInfo = doc.Descendants("node")
            .FirstOrDefault(n => n.Attribute("id")?.Value == "ModuleInfo");

        if (moduleInfo == null)
            return null;

        return new InstalledMod
        {
            MetaUuid           = XAttr(moduleInfo, "UUID"),
            MetaModuleName     = XAttr(moduleInfo, "Name"),
            MetaAuthor         = XAttr(moduleInfo, "Author"),
            ModioPublishHandle = XULongAttr(moduleInfo, "PublishHandle"),
            MetaVersion        = BuildVersion(moduleInfo),
        };
    }

    // === XML helpers ===

    private static string XAttr(XElement parent, string key) =>
        parent.Elements("attribute")
              .FirstOrDefault(a => a.Attribute("id")?.Value == key)
              ?.Attribute("value")?.Value ?? "";

    private static ulong XULongAttr(XElement parent, string key)
    {
        var raw = XAttr(parent, key);
        return ulong.TryParse(raw, out var result) ? result : 0;
    }

    private static string BuildVersion(XElement moduleInfo)
    {
        var v64Raw = XAttr(moduleInfo, "Version64");
        if (!string.IsNullOrEmpty(v64Raw) && long.TryParse(v64Raw, out var v64))
        {
            // BG3 Version64 uses asymmetric bit fields (matches BG3ModManager / LSLib):
            //   Major:    bits 63-55  (11 bits)
            //   Minor:    bits 54-47  (8 bits)
            //   Revision: bits 46-31 (16 bits)
            //   Build:    bits 30-0  (31 bits)
            var major    = (v64 >> 55) & 0x7FF;
            var minor    = (v64 >> 47) & 0xFF;
            var revision = (v64 >> 31) & 0xFFFF;
            var build    =  v64        & 0x7FFFFFFF;
            return $"{major}.{minor}.{revision}.{build}";
        }

        // Fallback: individual Major/Minor/Revision/Build attributes
        var maj = XAttr(moduleInfo, "Major");
        if (!string.IsNullOrEmpty(maj))
        {
            return $"{maj}.{XAttr(moduleInfo, "Minor")}" +
                   $".{XAttr(moduleInfo, "Revision")}.{XAttr(moduleInfo, "Build")}";
        }

        return XAttr(moduleInfo, "Version");
    }

    // === Cache I/O ===

    private static InstalledModsCache LoadCache()
    {
        if (!File.Exists(CacheFilePath)) return new InstalledModsCache();
        try
        {
            var json = File.ReadAllText(CacheFilePath);
            return JsonConvert.DeserializeObject<InstalledModsCache>(json)
                   ?? new InstalledModsCache();
        }
        catch { return new InstalledModsCache(); }
    }

    /// <summary>
    /// Persists NexusModId back to cache entries after an update check.
    /// Only updates entries that already exist in the cache.
    /// </summary>
    public static void SaveNexusIds(IEnumerable<InstalledMod> mods)
    {
        var cache = LoadCache();
        var changed = false;
        foreach (var mod in mods)
        {
            if (!mod.NexusModId.HasValue) continue;
            if (!cache.Mods.TryGetValue(mod.PakFilePath, out var entry)) continue;
            if (entry.ModData.NexusModId == mod.NexusModId) continue;
            entry.ModData.NexusModId = mod.NexusModId;
            changed = true;
        }
        if (changed) SaveCache(cache);
    }

    private static void SaveCache(InstalledModsCache cache)
    {
        try
        {
            var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
            File.WriteAllText(CacheFilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"ModScanner: failed to save cache — {ex.Message}");
        }
    }
}
