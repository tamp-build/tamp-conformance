using System.Text;
using System.Text.Json;

namespace Tamp.Conformance;

/// <summary>
/// Provider-agnostic <see cref="IDecisionDetector"/> for reverse examination — given cheap deterministically
/// triggered <see cref="DecisionCandidate"/>s and summaries of the existing ADRs, it judges which candidates
/// are genuinely <b>architecturally significant decisions that no ADR covers</b> (a CM-3 change-control
/// signal). It abstains generously — most candidates are routine and dropped. <b>Advisory only</b>: the
/// result never gates a build; it suggests "write an ADR."
/// </summary>
public sealed class LlmDecisionDetector : IDecisionDetector
{
    private readonly IChatCompletion _model;

    public LlmDecisionDetector(IChatCompletion model) => _model = model;

    private const string System = """
        You review candidate code "decisions" to decide which are architecturally significant AND not already
        covered by an existing Architecture Decision Record (ADR).

        You are given: (1) CANDIDATES found by cheap deterministic triggers (e.g. a new dependency, a new
        external endpoint), and (2) SUMMARIES of the ADRs that already exist.

        Return ONLY the candidates that are BOTH:
          - a real, consequential, hard-to-reverse architectural decision (not a routine/config detail), AND
          - not already covered by any listed ADR.

        ABSTAIN generously: most candidates are routine and must be dropped. An empty result is the common,
        correct answer. Do not invent decisions that aren't in the candidates.

        Respond with ONLY JSON: {"decisions":[{"kind":"...","summary":"...","file":"...","line":<int|null>}]}.
        No prose, no markdown fences.
        """;

    public IReadOnlyList<UndocumentedDecision> Detect(
        IReadOnlyList<DecisionCandidate> candidates, IReadOnlyList<string> knownAdrSummaries)
    {
        if (candidates.Count == 0)
            return Array.Empty<UndocumentedDecision>();

        var user = BuildPrompt(candidates, knownAdrSummaries);
        var json = LlmRuleExtractor.ExtractJsonObject(_model.Complete(System, user));
        var parsed = JsonSerializer.Deserialize<DetectOut>(json, LlmRuleExtractor.JsonOpts);
        return parsed?.Decisions ?? Array.Empty<UndocumentedDecision>();
    }

    private static string BuildPrompt(IReadOnlyList<DecisionCandidate> candidates, IReadOnlyList<string> adrs)
    {
        var sb = new StringBuilder("CANDIDATES:\n");
        foreach (var c in candidates)
            sb.Append("- [").Append(c.Kind).Append("] ").Append(c.Detail)
              .Append(" (").Append(c.File).Append(':').Append(c.Line?.ToString() ?? "?").Append(")\n");
        sb.Append("\nEXISTING ADRs:\n");
        if (adrs.Count == 0)
            sb.Append("(none)\n");
        else
            foreach (var a in adrs)
                sb.Append("- ").Append(a).Append('\n');
        return sb.ToString();
    }

    private sealed record DetectOut
    {
        public IReadOnlyList<UndocumentedDecision>? Decisions { get; init; }
    }
}
