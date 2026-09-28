using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamp.Conformance;

/// <summary>
/// Provider-agnostic <see cref="IRuleExtractor"/>: turns an ADR's prose into machine-checkable
/// <see cref="AdrRule"/>s via any <see cref="IChatCompletion"/> (the BYOK seam). The prompt enforces the
/// design's load-bearing rules — decompose into assertions, classify each <c>deterministic</c> (a
/// checkable regex) vs <c>semantic</c> (needs judgement), and <b>abstain</b> (emit nothing for a claim it
/// can't make checkable) rather than invent. Output is strict JSON, parsed here; the human then reviews
/// the committed <c>adr-rules.json</c>, so a weak model degrades to "more to correct," never to a silent
/// bad gate.
/// </summary>
public sealed class LlmRuleExtractor : IRuleExtractor
{
    private readonly IChatCompletion _model;
    private readonly ExtractionProfile _profile;

    public LlmRuleExtractor(IChatCompletion model, ExtractionProfile? profile = null)
    {
        _model = model;
        _profile = profile ?? ExtractionProfile.Default;
    }

    // The fixed integrity spine. These clauses are NOT project-tunable — they are the tool's soundness.
    private const string Spine = """
        You extract machine-checkable conformance rules from a software Architecture Decision Record (ADR).

        For each consequence or constraint the ADR commits to, emit one rule. Classify each rule:
        - "deterministic": checkable by a regular expression over source files. Provide EITHER
          "forbiddenPattern" (a regex whose presence in an in-scope file is a violation) OR
          "requiredPattern" (a regex whose absence from all in-scope files is a violation). Also give
          "scope": an array of file globs (e.g. ["**/*.csproj"]) the rule applies to.
        - "semantic": a genuine architectural judgement no regex can settle (e.g. "the UI is native, not
          a web app"). Give only "claim" (and optional "scope"/"controlRefs"); no patterns.

        Rules:
        - Prefer deterministic. Use semantic ONLY when no regex could decide it.
        - ABSTAIN: if a consequence cannot be made into a sound, low-false-positive rule, DO NOT emit it.
          Emitting nothing is correct; inventing a shaky rule is not.
        - "id": "<adr>-r<n>" (e.g. "0018-r1"). "claim": a short quote/paraphrase of the ADR text.
        """;

    // Composes the fixed spine with the injected per-project context (framework, stack, domain).
    internal string BuildSystemPrompt()
    {
        var sb = new System.Text.StringBuilder(Spine);
        sb.Append("\n- \"controlRefs\": optional ").Append(_profile.ControlFramework)
          .Append(" control ids the rule is evidence for (e.g. ").Append(_profile.ControlExample)
          .Append("). Use only ids valid in ").Append(_profile.ControlFramework)
          .Append("; if none clearly applies, omit controlRefs — do not invent a mapping.");
        if (!string.IsNullOrWhiteSpace(_profile.ControlCatalogueHint))
            sb.Append("\n  ").Append(_profile.ControlCatalogueHint);
        if (!string.IsNullOrWhiteSpace(_profile.ProjectContext))
            sb.Append("\n\nProject context: ").Append(_profile.ProjectContext);
        if (!string.IsNullOrWhiteSpace(_profile.StackConventions))
            sb.Append("\nStack conventions: ").Append(_profile.StackConventions);
        sb.Append("\n\nRespond with ONLY a JSON object: {\"rules\": [ ... ]}. No prose, no markdown fences.");
        return sb.ToString();
    }

    public IReadOnlyList<AdrRule> Extract(string adrId, string adrText)
    {
        var user = $"ADR id: {adrId}\n\n----- ADR TEXT -----\n{adrText}";
        var raw = _model.Complete(BuildSystemPrompt(), user);
        var json = ExtractJsonObject(raw);
        var parsed = JsonSerializer.Deserialize<ExtractionResult>(json, JsonOpts)
                     ?? throw new FormatException("Rule extraction returned no parseable object.");
        return parsed.Rules ?? Array.Empty<AdrRule>();
    }

    /// <summary>Tolerate a model that wraps JSON in prose or ```json fences: take the outermost {...}.</summary>
    internal static string ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new FormatException("No JSON object found in model output.");
        return raw.Substring(start, end - start + 1);
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record ExtractionResult
    {
        public IReadOnlyList<AdrRule>? Rules { get; init; }
    }
}
