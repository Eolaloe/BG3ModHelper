using System.IO;
using System.Xml.Linq;

namespace BG3ModHelper.Services;

public static class ModSettingsParser
{
    private const string GustavDevUuid = "28ac9ce2-2aba-8cda-b3b5-6e922f71f6ee";

    public static string GetDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData,
            "Larian Studios", "Baldur's Gate 3", "PlayerProfiles", "Public", "modsettings.lsx");
    }

    /// <summary>
    /// Returns a list of UUIDs in load order (lowercase), skipping the GustavDev base-game UUID.
    /// Tries ModOrder section first; falls back to Mods section if ModOrder has no children.
    /// Returns empty list on any error.
    /// </summary>
    public static List<string> GetLoadOrder(string lsxPath)
    {
        try
        {
            var doc = XDocument.Load(lsxPath);

            // Navigate: save > region[ModuleSettings] > node[root] > children
            var region = doc.Root?
                .Elements("region")
                .FirstOrDefault(r => (string?)r.Attribute("id") == "ModuleSettings");

            var rootNode = region?
                .Elements("node")
                .FirstOrDefault(n => (string?)n.Attribute("id") == "root");

            var rootChildren = rootNode?.Element("children");
            if (rootChildren == null) return [];

            // Try ModOrder first
            var modOrderNode = rootChildren
                .Elements("node")
                .FirstOrDefault(n => (string?)n.Attribute("id") == "ModOrder");

            var modOrderChildren = modOrderNode?.Element("children");
            var modOrderModules = modOrderChildren?
                .Elements("node")
                .Where(n => (string?)n.Attribute("id") == "Module")
                .ToList();

            if (modOrderModules != null && modOrderModules.Count > 0)
            {
                return ExtractUuids(modOrderModules, "UUID");
            }

            // Fallback to Mods section
            var modsNode = rootChildren
                .Elements("node")
                .FirstOrDefault(n => (string?)n.Attribute("id") == "Mods");

            var modsChildren = modsNode?.Element("children");
            var modsModules = modsChildren?
                .Elements("node")
                .Where(n => (string?)n.Attribute("id") == "ModuleShortDesc")
                .ToList();

            if (modsModules != null && modsModules.Count > 0)
            {
                return ExtractUuids(modsModules, "UUID");
            }

            return [];
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ExtractUuids(IEnumerable<XElement> nodes, string attributeId)
    {
        var result = new List<string>();
        foreach (var node in nodes)
        {
            var uuidAttr = node.Elements("attribute")
                .FirstOrDefault(a => (string?)a.Attribute("id") == attributeId);
            var uuid = ((string?)uuidAttr?.Attribute("value"))?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(uuid) &&
                !string.Equals(uuid, GustavDevUuid, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(uuid);
            }
        }
        return result;
    }
}
