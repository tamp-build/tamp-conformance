namespace Tamp.Conformance;

/// <summary>
/// How a rule is checked. <see cref="Deterministic"/> rules are pure predicates (regex/AST) — free,
/// reproducible, and safe to gate on. <see cref="Semantic"/> rules require a model-backed evaluator
/// (host-provided) and are non-pure: their verdict is frozen into the attestation snapshot rather than
/// recomputed (see core ADR 0023 / tamp-findings ADR 0001).
/// </summary>
public enum RuleKind
{
    Deterministic,
    Semantic,
}

/// <summary>
/// One machine-checkable assertion extracted from an ADR. Committed to the governed repo's git as part
/// of an <see cref="AdrRuleSet"/> ("a lockfile for architectural intent") — human-reviewable and
/// overridable, not a build artifact.
/// </summary>
public sealed record AdrRule
{
    /// <summary>Stable id within the ADR's rule-set, e.g. <c>"0018-r1"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable claim this rule encodes; used as the ADR-quote on a violation.</summary>
    public required string Claim { get; init; }

    public RuleKind Kind { get; init; } = RuleKind.Deterministic;

    /// <summary>Deterministic: a regex that MUST NOT appear in any in-scope file (its presence is a violation).</summary>
    public string? ForbiddenPattern { get; init; }

    /// <summary>Deterministic: a regex that MUST appear in at least one in-scope file (its absence is a violation).</summary>
    public string? RequiredPattern { get; init; }

    /// <summary>Glob(s) of files this rule applies to, relative to the repo root. Empty/absent means the whole tree.</summary>
    public IReadOnlyList<string>? Scope { get; init; }

    /// <summary>Control identifiers this rule is evidence for (e.g. <c>CM-6</c>, <c>SA-15</c>).</summary>
    public IReadOnlyList<string>? ControlRefs { get; init; }

    // --- ZT / mandate annotations (round-trip with the verdict; findings stores + scores on these) ---

    /// <summary>Zero Trust pillar this rule scores (maturity), e.g. <c>Identity</c>. Null for non-ZT rules.</summary>
    public string? ZtPillar { get; init; }

    /// <summary>Zero Trust function within the pillar, e.g. <c>Authentication</c>.</summary>
    public string? ZtFunction { get; init; }

    /// <summary>The maturity stage (1–4) the decision represents — orthogonal to the verdict. Null for non-ZT / binary rules.</summary>
    public int? ZtStage { get; init; }

    /// <summary>The binary mandate id this rule satisfies (e.g. <c>mfa</c>). Null for maturity/control rules.</summary>
    public string? MandateId { get; init; }

    /// <summary><c>Draft</c> | <c>Reviewed</c> — review gate rides through; policy (findings-side) decides whether a Draft rule may block.</summary>
    public string? ReviewStatus { get; init; }
}

/// <summary>
/// The rules extracted from a single ADR, plus the provenance that pins them to the ADR revision they
/// came from. <see cref="SourceSha"/> is the content hash of the ADR file at extraction time; a
/// mismatch against the live ADR means the rules are stale (see <see cref="RuleLoader.IsStale"/>).
/// </summary>
public sealed record AdrRuleSet
{
    /// <summary>The ADR these rules are for, e.g. <c>"0018"</c> (optionally repo-qualified for cross-repo).</summary>
    public required string Adr { get; init; }

    /// <summary>Content hash of the ADR file when these rules were generated.</summary>
    public required string SourceSha { get; init; }

    /// <summary>The model (or <c>"manual"</c>) that produced these rules.</summary>
    public string? ExtractedBy { get; init; }

    public required IReadOnlyList<AdrRule> Rules { get; init; }
}
