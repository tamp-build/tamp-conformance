using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// Host-provided, model-backed evaluator for <see cref="RuleKind.Semantic"/> rules — the seam the
/// satellite fills with an agent call. Kept an interface (not baked in) so the deterministic path has
/// zero model dependency and so the non-pure verdict can be produced, adversarially verified, and then
/// <b>frozen</b> into the attestation snapshot (never recomputed) per core ADR 0023 / tamp-findings
/// ADR 0001. The verify pass is what decides <c>fail</c> vs <c>unknown</c>: a claim that cannot quote
/// both the ADR text and the offending code is <c>unknown</c>, not <c>fail</c>.
/// </summary>
public interface ISemanticEvaluator
{
    /// <summary>Evaluate one semantic rule against the in-scope files, returning a four-valued verdict with a structured reason.</summary>
    ConformanceResult Evaluate(AdrRuleSet set, AdrRule rule, IReadOnlyList<AbsolutePath> inScopeFiles);
}

/// <summary>
/// Host-provided, model-backed rule extractor — turns an ADR's prose into machine-checkable
/// <see cref="AdrRule"/>s. Kept an interface so rule generation (a rare, frontier-model step) is a
/// plug-in the satellite supplies; the deterministic scaffolding (hashing, staleness, seeding) needs no
/// model. See <see cref="RuleGeneration"/>.
/// </summary>
public interface IRuleExtractor
{
    /// <summary>Extract the rules implied by <paramref name="adrText"/> (the ADR identified by <paramref name="adrId"/>).</summary>
    IReadOnlyList<AdrRule> Extract(string adrId, string adrText);
}

/// <summary>
/// Host-provided, model-backed detector for the reverse-examination capability — decisions the code
/// made that no ADR records. Advisory only. See <see cref="ReverseExamination"/>.
/// </summary>
public interface IDecisionDetector
{
    /// <summary>Judge whether a triggered candidate is an ADR-worthy decision, and whether an existing ADR already covers it.</summary>
    IReadOnlyList<UndocumentedDecision> Detect(IReadOnlyList<DecisionCandidate> candidates, IReadOnlyList<string> knownAdrSummaries);
}

/// <summary>A deterministically-triggered signal that <i>might</i> be an undocumented decision (e.g. a new dependency).</summary>
public sealed record DecisionCandidate
{
    public required string Kind { get; init; }        // e.g. "new-dependency", "new-project"
    public required string Detail { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
}

/// <summary>An advisory finding: a decision in the code with no covering ADR. Never blocks.</summary>
public sealed record UndocumentedDecision
{
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
}
