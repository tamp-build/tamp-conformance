namespace Tamp.Conformance.Quality;

/// <summary>
/// A pluggable producer of normalized findings (SonarQube/SonarCloud, Roslyn/SonarAnalyzer SARIF,
/// opengrep, ESLint, …). A source emits findings in the common <see cref="NormalizedFinding"/> shape;
/// the <see cref="QualityAdapter"/> dedups + routes them. Several sources produce both quality and
/// SAST findings — routing is by finding type, never by source.
/// </summary>
public interface IQualitySource
{
    /// <summary>The producing scanner's wire name (e.g. "sonarqube", "roslyn", "opengrep", "eslint").</summary>
    string Scanner { get; }

    /// <summary>Read and normalize all findings this source can see. Deterministic; no network unless the source is a live API.</summary>
    IReadOnlyList<NormalizedFinding> Read();
}
