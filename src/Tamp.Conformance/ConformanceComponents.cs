using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// What a build supplies to the conformance targets: where to look (<see cref="ConformanceOptions"/>), the
/// rule extractor (for <see cref="IAdrRules"/>), and optionally a semantic evaluator (for
/// <see cref="ICheckAdrConformance"/>). Mirrors the Tamp.Components <c>IHaz*</c> injection pattern.
/// </summary>
public interface IHazConformance
{
    ConformanceOptions ConformanceOptions { get; }

    /// <summary>Rule extractor used by <see cref="IAdrRules"/>. A BYOK adapter (Anthropic, OpenAI-compatible, …) wrapped by <c>LlmRuleExtractor</c>.</summary>
    IRuleExtractor RuleExtractor { get; }

    /// <summary>Optional semantic evaluator for <see cref="ICheckAdrConformance"/>; when null, semantic rules resolve to <c>unknown</c>.</summary>
    ISemanticEvaluator? SemanticEvaluator => null;
}

/// <summary>
/// The <c>AdrRules</c> target — regenerate the committed rule-sets from the ADRs. <b>Write path</b>: it
/// writes only into the working tree (under the rules directory) and performs no git operation, so branch
/// protection is respected by construction — the developer (or an automated PR) commits the result through
/// the normal reviewed workflow.
/// </summary>
public interface IAdrRules : IHazConformance
{
    Target AdrRules => _ => _
        .Description("Regenerate adr-rules from the ADRs (writes the working tree; commit via a reviewed PR).")
        .Executes(() => ConformanceRunner.GenerateRules(RuleExtractor, ConformanceOptions));
}

/// <summary>
/// The <c>CheckAdrConformance</c> target — verify code against the committed rules. <b>Read-only</b>: never
/// writes. Fails closed on stale/missing rules (the staleness gate), routes semantic rules to the evaluator
/// when configured, and throws (failing the build) when enforcing and any verdict is not <c>pass</c>.
/// </summary>
public interface ICheckAdrConformance : IHazConformance
{
    Target CheckAdrConformance => _ => _
        .Description("Check code against the committed adr-rules; fails closed on stale rules and blocking verdicts.")
        .Executes(() =>
        {
            var result = ConformanceRunner.Check(ConformanceOptions, SemanticEvaluator);
            if (!result.Passed)
                throw new InvalidOperationException(
                    $"ADR conformance failed: {result.Fails} fail, {result.Unknowns} unknown, {result.Errors} error " +
                    $"({result.Results.Count(r => r.Blocks)} blocking). See the conformance.evaluated events for detail.");
        });
}
