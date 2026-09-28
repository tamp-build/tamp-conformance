using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// Where the conformance capabilities read and write. Defaults follow the tamp convention
/// (<c>docs/adr</c> for ADRs, <c>docs/adr/.rules</c> for the committed rule-sets). Rule generation writes
/// only under <see cref="ResolvedRulesDir"/> in the working tree — never to git — and checking is
/// read-only.
/// </summary>
public sealed record ConformanceOptions
{
    /// <summary>The repository root (typically <c>TampBuild.RootDirectory</c>).</summary>
    public required AbsolutePath RepoRoot { get; init; }

    /// <summary>ADR directory; defaults to <c>RepoRoot/docs/adr</c>.</summary>
    public AbsolutePath? AdrDirectory { get; init; }

    /// <summary>Committed rule-set directory; defaults to <c>&lt;AdrDirectory&gt;/.rules</c>.</summary>
    public AbsolutePath? RulesDirectory { get; init; }

    /// <summary>When true (default), non-pass verdicts block and <c>CheckAdrConformance</c> fails the build; when false, advisory (emit only).</summary>
    public bool Enforcing { get; init; } = true;

    /// <summary>The revision under evaluation, frozen into evidence provenance.</summary>
    public string? CommitSha { get; init; }

    /// <summary>Directory names skipped when discovering code files.</summary>
    public IReadOnlyList<string> IgnoreDirs { get; init; } =
        new[] { ".git", "bin", "obj", "artifacts", "node_modules", ".vs", ".rules" };

    public AbsolutePath ResolvedAdrDir => AdrDirectory ?? RepoRoot / "docs" / "adr";
    public AbsolutePath ResolvedRulesDir => RulesDirectory ?? ResolvedAdrDir / ".rules";
}
