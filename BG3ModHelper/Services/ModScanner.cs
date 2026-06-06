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
            Logger.Warn($"ModScanner: check Settings → Mods Folder Path");
            return new List<InstalledMod>();
        }

        var pakFiles = Directory.GetFiles(modsFolder, "*.pak");
        Logger.Info($"ModScanner: folder = {modsFolder}");
        Logger.Info($"ModScanner: found {pakFiles.Length} .pak file(s)");

        if (pakFiles.Length == 0)
        {
            Logger.Warn($"ModScanner: folder exists but contains no .pak files — is this the right Mods folder?");
            return new List<InstalledMod>();
        }

        var cache        = LoadCache();
        var results      = new List<InstalledMod>();
        var cacheChanged = false;
        var processed    = 0;

        // Scan counters for diagnostic summary
        int fromCache      = 0;
        int parsed         = 0;
        int noMeta         = 0;   // pak opened but no meta.lsx → engine/game file, not a mod
        int failed         = 0;   // LSLib threw an exception
        int failuresLogged = 0;   // tracks how many individual failure lines have been written

        await Task.Run(() =>
        {
            foreach (var pakPath in pakFiles)
            {
                var modifiedUtc = File.GetLastWriteTimeUtc(pakPath);

                if (cache.Mods.TryGetValue(pakPath, out var entry) &&
                    entry.PakFileLastWriteTime == modifiedUtc)
                {
                    results.Add(entry.ModData);
                    fromCache++;
                }
                else
                {
                    var (mod, reason) = ParsePakDetailed(pakPath, ref failuresLogged);
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
                        parsed++;
                    }
                    else if (reason == SkipReason.NoMeta)
                    {
                        noMeta++;
                    }
                    else
                    {
                        failed++;
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

        // Diagnostic summary — always logged so users can paste the log file for support
        Logger.Info($"ModScanner: complete — total={pakFiles.Length} | parsed={parsed} cached={fromCache} no-meta={noMeta} failed={failed}");
        Logger.Info($"ModScanner: result — {results.Count} mod(s) loaded");

        if (failed > 0)
        {
            var suppressed = failed - failuresLogged;
            var detail = suppressed > 0
                ? $"first {failuresLogged} shown above, {suppressed} suppressed"
                : $"see WARN lines above for details";
            Logger.Warn($"ModScanner: {failed} pak file(s) failed to parse (LSLib error) — {detail}");
        }
        if (results.Count == 0 && noMeta == pakFiles.Length)
            Logger.Warn($"ModScanner: all pak files lack meta.lsx — folder may contain game engine files, not user mods");

        return results;
    }

    // === .pak parsing ===

    private enum SkipReason { None, NoMeta, Exception }

    // Max number of individual parse-failure lines written to the log.
    // After this limit, failures are counted silently and summarised at the end.
    private const int MaxFailureLogLines = 5;

    /// <summary>
    /// Reads UUID/Name/etc from a .pak file's meta.lsx. Returns null if unreadable.
    /// Public wrapper for use by external callers (e.g. NxmInstaller).
    /// </summary>
    public static InstalledMod? InspectPak(string pakPath) => ParsePakDetailed(pakPath, ref _noopCounter).mod;
    private static int _noopCounter = 0;

    private static (InstalledMod? mod, SkipReason reason) ParsePakDetailed(string pakPath, ref int failuresLogged)
    {
        try
        {
            using var pak = new PackageReader().Read(pakPath);

            var metaFile = pak.Files.FirstOrDefault(f =>
                f.Name.EndsWith("meta.lsx", StringComparison.OrdinalIgnoreCase));

            if (metaFile == null)
                return (null, SkipReason.NoMeta);

            using var stream = metaFile.CreateContentReader();
            return (ParseMetaLsx(stream), SkipReason.None);
        }
        catch (Exception ex)
        {
            if (failuresLogged < MaxFailureLogLines)
            {
                Logger.Warn($"ModScanner: parse failed [{Path.GetFileName(pakPath)}] — {ex.GetType().Name}: {ex.Message}");
                failuresLogged++;
            }
            return (null, SkipReason.Exception);
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
