namespace AaoDcsBiosRuntimeBridge;

/// <summary>
/// Helper to interpret the optional per-input persistence flag.
/// We keep this logic in one place to avoid drift between validation, runtime and docs.
/// </summary>
internal static class PersistSpec
{
    internal static bool IsLastStateEnabled(string? persist)
    {
        if (string.IsNullOrWhiteSpace(persist))
            return false;

        // Accept a few human-friendly spellings/synonyms.
        // Recommended value in JSONC: "laststate"
        var s = persist.Trim().ToLowerInvariant();
        s = s.Replace("_", "").Replace("-", "").Replace(" ", "");

        return s is "laststate" or "last" or "init";
    }
}
