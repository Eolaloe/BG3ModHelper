namespace BG3ModHelper.Services;

/// <summary>
/// Fuzzy author-name matching for disambiguating community DB entries and mod metadata.
///
/// Handles common real-world variations:
///   • Case differences           "eralyne"              ≈ "Eralyne"
///   • Parenthetical notes        "bibsan (prev. Djmr)"  ≈ "bibsan"
///   • Multi-author strings       "Caites and juumeijin" ≈ "Caites"
///   • Comma / slash lists        "Alice, Bob / Charlie" ≈ "Alice"
///   • Substring containment      "bibsan"               ⊂ "bibsan_official"
///
/// Usage (disambiguation):
///   AuthorMatcher.IsMatch(installedMod.MetaAuthor, dbEntry.NexusUploadedBy)
///
/// Usage (scoring):
///   int confidence = AuthorMatcher.Score(a, b);  // 0 = no match, 10 = exact
/// </summary>
public static class AuthorMatcher
{
    // Minimum lengths for containment / token checks
    // (prevents "a" or "Ed" from spuriously matching inside longer names)
    private const int MinContainLength = 3;
    private const int MinTokenLength   = 3;

    // ── Public thresholds (callers can tune their own cutoff) ─────────────

    public const int ScoreExact            = 10;   // exact (case-insensitive)
    public const int ScoreStrippedExact    = 9;    // exact after stripping "(…)"
    public const int ScoreContains         = 7;    // whole-string containment
    public const int ScoreStrippedContains = 6;    // stripped containment
    public const int ScoreTokenExact       = 5;    // at least one token matches exactly
    public const int ScoreTokenPrefix      = 5;    // one token is a prefix of another ("DIZ" ⊂ "Diz91891")

    // ── Primary API ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the two author strings are similar enough to be treated
    /// as the same person (score ≥ <see cref="ScoreTokenExact"/>).
    /// Safe to call with null / empty / placeholder "—" values.
    /// </summary>
    public static bool IsMatch(string? a, string? b) => Score(a, b) >= ScoreTokenExact;

    /// <summary>
    /// Returns a 0–10 similarity score.  Higher = more confident.
    /// </summary>
    public static int Score(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;

        a = a.Trim().ToLowerInvariant();
        b = b.Trim().ToLowerInvariant();

        // Skip placeholder / unknown values
        if (a == "—" || b == "—") return 0;

        // 1. Exact match (case-insensitive)
        if (a == b) return ScoreExact;

        // 2. Strip parenthetical notes, then compare
        var aS = StripParens(a);
        var bS = StripParens(b);
        if (aS == bS) return ScoreStrippedExact;

        // 3. Whole-string containment  ("eola" ⊂ "eolaloe", "bibsan" ⊂ "bibsan (prev. djmr...)")
        //    Guard: shorter side must be at least MinContainLength chars to avoid "a" ⊂ "Eralyne"
        if (a.Length >= MinContainLength && b.Length >= MinContainLength)
        {
            if (a.Contains(b) || b.Contains(a)) return ScoreContains;
        }
        if (aS.Length >= MinContainLength && bS.Length >= MinContainLength)
        {
            if (aS.Contains(bS) || bS.Contains(aS)) return ScoreStrippedContains;
        }

        // 4. Token-level exact match  ("Caites" inside "Caites and juumeijin")
        var aTok = Tokenize(a);
        var bTok = Tokenize(b);
        if (aTok.Any(at => at.Length >= MinTokenLength &&
                           bTok.Any(bt => bt.Length >= MinTokenLength && at == bt)))
            return ScoreTokenExact;

        // 5. Token-level prefix match: one token is a prefix of another
        //    "DIZ" → "diz"  vs  "Diz91891" → "diz91891" → "diz91891".StartsWith("diz") ✓
        //    Handles usernames where meta.lsx stores a shortened handle and Nexus uses the full one.
        if (aTok.Any(at => at.Length >= MinTokenLength &&
                           bTok.Any(bt => bt.Length >= MinTokenLength &&
                                          (bt.StartsWith(at) || at.StartsWith(bt)))))
            return ScoreTokenPrefix;

        return 0;
    }

    // ── Helpers (public so callers can reuse) ─────────────────────────────

    /// <summary>
    /// Removes a trailing " (…)" clause.
    /// "bibsan (prev. Djmr)" → "bibsan"
    /// </summary>
    public static string StripParens(string s)
    {
        var idx = s.IndexOf('(');
        return idx > 0 ? s[..idx].TrimEnd() : s;
    }

    /// <summary>
    /// Splits an author string on all common separators so that
    /// "Alice, Bob / Charlie and Dave" → ["alice", "bob", "charlie", "dave"].
    /// </summary>
    public static List<string> Tokenize(string s)
    {
        // Hard separators first
        var parts = s.Split(new[] { ',', '/', '&', '+' },
                            StringSplitOptions.RemoveEmptyEntries);
        var tokens = new List<string>();

        foreach (var part in parts)
        {
            // Word-level separators inside each segment
            var sub = part.Replace(" and ", "|")
                          .Replace(" & ",   "|")
                          .Replace(" + ",   "|")
                          .Split('|', StringSplitOptions.RemoveEmptyEntries);

            foreach (var seg in sub)
            {
                var t = StripParens(seg.Trim());
                if (!string.IsNullOrEmpty(t))
                    tokens.Add(t);
            }
        }

        return tokens;
    }
}
