namespace Tamp.Conformance.Quality;

/// <summary>
/// The finding type that drives routing. This is the single source of truth for whether a finding
/// is a quality concern or a security concern — it OVERRIDES which scanner produced it (a Roslyn
/// finding typed <see cref="CodeSmell"/> lands in quality, not SAST). Mirrors SonarQube's issue type.
/// </summary>
public enum FindingType
{
    Bug,
    CodeSmell,
    Vulnerability,
    SecurityHotspot,
}

/// <summary>Which evidence report a finding routes into. Derived from <see cref="FindingType"/>.</summary>
public enum QualityBucket
{
    /// <summary><see cref="FindingType.Bug"/> and <see cref="FindingType.CodeSmell"/>.</summary>
    Quality,

    /// <summary><see cref="FindingType.Vulnerability"/> and <see cref="FindingType.SecurityHotspot"/>.</summary>
    Sast,
}

/// <summary>The canonical 5-value severity scale tamp-findings ingests (matches Tamp.Ingest.V1.Severity).</summary>
public enum NormalizedSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// Native source values preserved through normalization so findings can re-derive severity/type under
/// its own policy (e.g. SonarQube MQR impacts) without losing information. Pure provenance.
/// </summary>
/// <param name="Tool">The producing tool's wire name (e.g. "sonarqube", "roslyn", "opengrep").</param>
/// <param name="SeverityRaw">The source's native severity string, verbatim (e.g. "MINOR", "Blocker").</param>
/// <param name="TypeRaw">The source's native type string, verbatim (e.g. "VULNERABILITY", "Code Smell").</param>
/// <param name="Effort">Remediation effort as the source reported it, if any (e.g. "15min").</param>
/// <param name="AnalysisId">The source analysis identifier, if any — pins the exact run.</param>
public sealed record FindingSource(
    string Tool,
    string? SeverityRaw = null,
    string? TypeRaw = null,
    string? Effort = null,
    string? AnalysisId = null);

/// <summary>
/// A single finding normalized to the tamp evidence contract, independent of its source tool.
/// The dedup identity is (<see cref="RuleId"/>, <see cref="FilePath"/>, <see cref="Line"/>).
/// </summary>
public sealed record NormalizedFinding(
    string RuleId,
    FindingType Type,
    NormalizedSeverity Severity,
    string Title,
    string FilePath,
    int? Line,
    string? Description = null,
    string? Snippet = null,
    string? SubCategory = null,
    FindingSource? Source = null)
{
    /// <summary>The report this finding routes into, derived from <see cref="Type"/>.</summary>
    public QualityBucket Bucket => Routing.BucketFor(Type);
}

/// <summary>The one place that maps finding type → report bucket. Type wins over scanner, always.</summary>
public static class Routing
{
    /// <summary>bug/code_smell → quality; vulnerability/security_hotspot → SAST.</summary>
    public static QualityBucket BucketFor(FindingType type) => type switch
    {
        FindingType.Bug or FindingType.CodeSmell => QualityBucket.Quality,
        FindingType.Vulnerability or FindingType.SecurityHotspot => QualityBucket.Sast,
        _ => QualityBucket.Quality,
    };

    /// <summary>Wire string for a finding type, as posted to findings (lower_snake_case, matches SonarQube).</summary>
    public static string ToWire(FindingType type) => type switch
    {
        FindingType.Bug => "bug",
        FindingType.CodeSmell => "code_smell",
        FindingType.Vulnerability => "vulnerability",
        FindingType.SecurityHotspot => "security_hotspot",
        _ => "code_smell",
    };
}
