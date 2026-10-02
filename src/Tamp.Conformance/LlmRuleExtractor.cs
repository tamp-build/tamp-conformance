using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

        Classification order — deterministic is NOT the default, ROBUSTNESS is:
        1. Emit "deterministic" ONLY when a robust, low-false-positive regex exists (see below).
        2. Otherwise emit "semantic" — a brittle regex that an idiomatic rewrite would defeat is WORSE
           than a semantic rule, because it fails compliant code and trains people to ignore the gate.
        3. If neither is sound, ABSTAIN — emit nothing. Emitting nothing is correct; a shaky rule is not.

        Writing a ROBUST deterministic pattern (a pattern is a REGEX over source text — NEVER an English
        sentence or paraphrase; "requiredPattern":"beacon still on legacy emitter" is INVALID):
        - Anchor on the LOWEST-VARIANCE token that proves the claim — a string literal, a type/attribute/
          package name, an interface name. Do NOT match whole statements whose incidental syntax varies.
        - Account for how the language actually writes the thing, or your required-pattern will miss
          compliant code. In C#, the SAME construct has many spellings: target-typed `new("X")` vs
          `new Type("X")` vs fully-qualified `new Ns.Type("X")`; a class reaching a base through an
          INTERMEDIATE base (`class B : Mid` where `Mid : Base`) so a literal `: Base` is absent; `using`
          imports making a type unqualified; arbitrary whitespace/newlines. Prefer the invariant: to prove
          "declares an ActivitySource named Tamp.Build", require the literal "Tamp.Build" in scope — NOT
          `new\s+ActivitySource`. To prove "is a Tamp build", a `: Base` regex is fragile across
          intermediate bases — prefer semantic.
        - SCOPE precisely. Exclude build scripts, generated code, and build output unless the rule is about
          them: a rule about SHIPPED assemblies scopes to ["src/**/*.csproj"], not ["**/*.csproj"] (which
          would wrongly flag build/ and test projects). Default-exclude **/bin/**, **/obj/**, and build/**.
        - A requiredPattern must hold in THIS repo. A repo that DEFINES an artifact is not a CONSUMER of it
          (the repo that produces Tamp.Core has no PackageReference to Tamp.Core) — if the constraint only
          applies to downstream consumers, scope it out or make it semantic; do not require a self-dependency.
        - Only emit a rule for what is already TRUE of conforming code. Do NOT emit deterministic rules for
          aspirational/forward-looking consequences (a marker a future migration will add) — abstain or mark
          semantic, so unstarted work is not a standing failure.

        General:
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

    /// <summary>
    /// Extract the JSON object from a model's raw output, tolerant of the ways local/thinking models wrap it:
    /// inline reasoning blocks (<c>&lt;think&gt;…&lt;/think&gt;</c>, whose braces would otherwise fool the scan),
    /// markdown code fences, and surrounding prose. Strips reasoning, prefers a fenced object, then falls back to
    /// the outermost <c>{…}</c>.
    /// </summary>
    internal static string ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("No JSON object found in model output (empty).");

        // 1. Drop reasoning blocks some models emit inline — their braces would corrupt the brace scan.
        var cleaned = ThinkBlock.Replace(raw, string.Empty);

        // 2. Prefer the contents of a fenced code block if the model wrapped the JSON in one.
        var fence = FencedObject.Match(cleaned);
        if (fence.Success)
            return fence.Groups[1].Value;

        // 3. Fall back to the outermost {...}.
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new FormatException("No JSON object found in model output.");
        return cleaned.Substring(start, end - start + 1);
    }

    private static readonly Regex ThinkBlock =
        new("<think(?:ing)?>.*?</think(?:ing)?>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FencedObject =
        new(@"```(?:json)?\s*(\{.*\})\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
