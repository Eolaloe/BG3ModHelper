using System.Text.RegularExpressions;

namespace BG3ModHelper.Services;

/// <summary>
/// Fuzzy mod-name similarity matching for disambiguating community DB entries.
///
/// Handles common real-world variations between pak MetaModuleName and Nexus mod page titles:
///   • Space differences      "CompatibilityFramework"  ≈ "Compatibility Framework"
///   • Parenthetical suffixes "Improved UI (BG3)"       ≈ "Improved UI"
///   • Substring containment  "ImprovedUI"              ≈ "Improved UI Assets"
///   • Token overlap          "CustomHotbar"            ≈ "Custom Hotbar Revised"
///
/// Usage (disambiguation):
///   ModNameMatcher.IsMatch(mod.MetaModuleName, dbEntry.NexusModName)
///
/// Usage (scoring):
///   int confidence = ModNameMatcher.Score(a, b);  // 0 = no match, 10 = exact
/// </summary>
public static class ModNameMatcher
{
    // Minimum compacted length for containment check
    // (prevents short tokens like "BG3" from matching inside unrelated names)
    private const int MinContainLength = 5;

    // ── Public thresholds ─────────────────────────────────────────────────

    public const int ScoreExact        = 10;  // exact (case-insensitive)
    public const int ScoreStripped     = 9;   // exact after stripping parentheticals
    public const int ScoreCompactExact = 8;   // exact after compacting spaces/symbols
    public const int ScoreContains     = 6;   // compacted form containment
    public const int ScoreTokenOverlap = 5;   // majority of tokens overlap

    // ── Primary API ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when names are similar enough for mod disambiguation
    /// (score ≥ <see cref="ScoreContains"/>).
    /// </summary>
    public static bool IsMatch(string? a, string? b) => Score(a, b) >= ScoreContains;

    /// <summary>Returns a 0–10 similarity score. Higher = more confident.</summary>
    public static int Score(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;

        a = a.Trim();
        b = b.Trim();

        // 1. Exact (case-insensitive)
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return ScoreExact;

        // 2. Strip " (…)" suffixes, then compare
        var aS = StripParens(a);
        var bS = StripParens(b);
        if (string.Equals(aS, bS, StringComparison.OrdinalIgnoreCase)) return ScoreStripped;

        // 3. Compact both (remove spaces, hyphens, underscores, apostrophes, dots)
        //    "Compatibility Framework" → "compatibilityframework"
        //    "CompatibilityFramework"  → "compatibilityframework"  ← exact match
        var aC = Compact(aS);
        var bC = Compact(bS);
        if (aC == bC) return ScoreCompactExact;

        // 4. Compacted containment — shorter must be ≥ MinContainLength to avoid
        //    short fragments matching inside unrelated names
        var shorter = aC.Length <= bC.Length ? aC : bC;
        var longer  = aC.Length >  bC.Length ? aC : bC;
        if (shorter.Length >= MinContainLength && longer.Contains(shorter))
            return ScoreContains;

        // 5. Token overlap — split CamelCase + spaces, require majority match
        //    "CustomHotbar" → ["custom","hotbar"]  ≈  "Custom Hotbar Revised" → ["custom","hotbar","revised"]
        var aTok = Tokenize(a);
        var bTok = Tokenize(b);
        if (aTok.Count > 0 && bTok.Count > 0)
        {
            int overlap = aTok.Count(at => bTok.Any(bt =>
                string.Equals(at, bt, StringComparison.OrdinalIgnoreCase)));
            int minCount = Math.Min(aTok.Count, bTok.Count);
            // Require more than half of the shorter list's tokens to match
            if (overlap > 0 && overlap * 2 >= minCount)
                return ScoreTokenOverlap;
        }

        return 0;
    }

    // ── Helpers (public for reuse) ────────────────────────────────────────

    /// <summary>Removes trailing " (…)" clause: "Improved UI (BG3)" → "Improved UI".</summary>
    public static string StripParens(string s)
    {
        var idx = s.IndexOf('(');
        return idx > 0 ? s[..idx].TrimEnd() : s;
    }

    /// <summary>
    /// Removes spaces, hyphens, underscores, apostrophes, and dots, then lowercases.
    /// "Compatibility Framework" → "compatibilityframework"
    /// </summary>
    public static string Compact(string s) =>
        Regex.Replace(s.ToLowerInvariant(), @"[\s\-_'\.]", "");

    /// <summary>
    /// Splits on spaces and CamelCase boundaries, keeping tokens ≥ 3 chars.
    /// "CustomHotbarRevised" → ["custom","hotbar","revised"]
    /// "Custom Hotbar Revised" → ["custom","hotbar","revised"]
    /// </summary>
    public static List<string> Tokenize(string s)
    {
        // Insert space before each uppercase letter following a lowercase one (CamelCase split)
        var spaced = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])", " ");
        return spaced
            .Split(new[] { ' ', '-', '_', '.', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .ToList();
    }
}
