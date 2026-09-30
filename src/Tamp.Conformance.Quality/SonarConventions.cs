namespace Tamp.Conformance.Quality;

/// <summary>
/// Shared Sonar → tamp mapping, used by both the Roslyn/SonarAnalyzer SARIF source and the
/// SonarQube/SonarCloud API source. SonarAnalyzer encodes a finding's type and severity in the
/// SARIF rule's <c>properties.category</c> as <c>"&lt;Severity&gt; &lt;Type&gt;"</c>
/// (e.g. "Minor Code Smell", "Major Bug"); the SonarQube API returns them as separate
/// <c>type</c> / <c>severity</c> fields. Both funnel through here.
/// </summary>
public static class SonarConventions
{
    // Longest-match-first: "Security Hotspot" must be tested before "Bug"/"Vulnerability" substrings.
    private static readonly (string Text, FindingType Type)[] TypeWords =
    {
        ("security hotspot", FindingType.SecurityHotspot),
        ("vulnerability", FindingType.Vulnerability),
        ("code smell", FindingType.CodeSmell),
        ("bug", FindingType.Bug),
    };

    /// <summary>
    /// Parse a SonarAnalyzer SARIF <c>properties.category</c> like "Minor Code Smell" into a
    /// (type, severity). Returns false when the string carries no recognizable Sonar type
    /// (e.g. a non-Sonar Roslyn/Roslynator rule with a plain category).
    /// </summary>
    public static bool TryParseCategory(string? category, out FindingType type, out NormalizedSeverity severity)
    {
        type = FindingType.CodeSmell;
        severity = NormalizedSeverity.Info;
        if (string.IsNullOrWhiteSpace(category)) return false;

        var lower = category.Trim().ToLowerInvariant();
        var matched = false;
        foreach (var (text, t) in TypeWords)
        {
            if (lower.EndsWith(text, StringComparison.Ordinal) || lower == text)
            {
                type = t;
                lower = lower[..^text.Length].Trim();
                matched = true;
                break;
            }
        }
        if (!matched) return false;

        severity = MapSeverity(lower);
        return true;
    }

    /// <summary>Map a SonarQube issue <c>type</c> string (BUG / CODE_SMELL / VULNERABILITY / SECURITY_HOTSPOT).</summary>
    public static FindingType MapType(string? sonarType) => (sonarType ?? "").Trim().ToLowerInvariant() switch
    {
        "bug" => FindingType.Bug,
        "vulnerability" => FindingType.Vulnerability,
        "security_hotspot" or "security hotspot" => FindingType.SecurityHotspot,
        _ => FindingType.CodeSmell,
    };

    /// <summary>
    /// Map a Sonar severity word (Blocker/Critical/Major/Minor/Info, either the SARIF category prefix
    /// or the issue-API severity) to the tamp 5-scale. Blocker→Critical, Critical→High, Major→Medium,
    /// Minor→Low, Info→Info.
    /// </summary>
    public static NormalizedSeverity MapSeverity(string? sonarSeverity) => (sonarSeverity ?? "").Trim().ToLowerInvariant() switch
    {
        "blocker" => NormalizedSeverity.Critical,
        "critical" => NormalizedSeverity.High,
        "major" => NormalizedSeverity.Medium,
        "minor" => NormalizedSeverity.Low,
        "info" => NormalizedSeverity.Info,
        _ => NormalizedSeverity.Info,
    };
}
