using System.Net.Http;
using System.Text;
using System.Text.Json;
using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// Provider-agnostic <see cref="ISemanticEvaluator"/>: judges a <see cref="RuleKind.Semantic"/> rule
/// against the in-scope code via any <see cref="IChatCompletion"/>. Two passes enforce the design's
/// safety rule: an evaluation pass returns a four-valued verdict, and — only on a claimed
/// <c>fail</c> — an <b>adversarial verify pass</b> confirms it. A violation that cannot be re-quoted (ADR
/// text + exact code line) is downgraded to <c>unknown</c>, never left as a false <c>fail</c>. The verdict
/// is non-deterministic, so it is emitted with <see cref="Provenance.ModelId"/> and frozen (the runner
/// snapshots it); it is never recomputed for the same evidence.
/// </summary>
public sealed class LlmSemanticEvaluator : ISemanticEvaluator
{
    private readonly IChatCompletion _model;
    private readonly int _maxFileChars;
    private readonly bool _verify;

    public LlmSemanticEvaluator(IChatCompletion model, int maxFileChars = 16000, bool verify = true)
    {
        _model = model;
        _maxFileChars = maxFileChars;
        _verify = verify;
    }

    private const string EvalSystem = """
        You check whether code honors ONE architectural rule from an ADR.
        Read the rule claim and the provided files, then respond with ONLY JSON:
        {"verdict":"pass|fail|unknown","file":"<path or null>","line":<int or null>,"codeEvidence":"<offending line or null>","adrQuote":"<the rule text you are enforcing or null>"}
        Rules:
        - "fail" ONLY if you can point to a specific offending file+line and quote it. Populate file, line, codeEvidence, adrQuote.
        - "unknown" if the provided files do not let you decide (missing context, ambiguous). Never guess.
        - "pass" if the code clearly honors the rule.
        No prose, no markdown fences.
        """;

    private const string VerifySystem = """
        You are an adversarial reviewer of a claimed architectural-conformance VIOLATION.
        Confirm the violation ONLY if the quoted code genuinely and unambiguously violates the quoted rule.
        Respond with ONLY JSON: {"confirmed": true|false, "reason": "<short>"}.
        If the evidence is weak, out of context, or does not clearly violate the rule, confirmed=false.
        """;

    public ConformanceResult Evaluate(AdrRuleSet set, AdrRule rule, IReadOnlyList<AbsolutePath> inScopeFiles)
    {
        ConformanceResult Result(string verdict, string method, string? evidence = null, string? file = null, int? line = null, string? adrQuote = null) => new()
        {
            AdrRef = set.Adr,
            RuleId = rule.Id,
            Verdict = verdict,
            AdrQuote = adrQuote ?? (verdict == ConformanceVerdict.Fail ? rule.Claim : null),
            CodeEvidence = evidence,
            File = file,
            Line = line,
            Method = method,
            Blocks = verdict != ConformanceVerdict.Pass,
            ControlRefs = rule.ControlRefs,
            ZtPillar = rule.ZtPillar,
            ZtFunction = rule.ZtFunction,
            ZtStage = rule.ZtStage,
            MandateId = rule.MandateId,
        };

        if (inScopeFiles.Count == 0)
            return Result(ConformanceVerdict.Unknown, ConformanceMethod.Semantic, "no in-scope files for this rule");

        string evalJson;
        try
        {
            evalJson = LlmRuleExtractor.ExtractJsonObject(_model.Complete(EvalSystem, BuildEvalPrompt(rule, inScopeFiles)));
        }
        catch (Exception ex) when (ex is FormatException or HttpRequestException)
        {
            return Result(ConformanceVerdict.Error, ConformanceMethod.Semantic, ex.Message);
        }

        var eval = JsonSerializer.Deserialize<EvalOut>(evalJson, LlmRuleExtractor.JsonOpts);
        if (eval is null || string.IsNullOrEmpty(eval.Verdict))
            return Result(ConformanceVerdict.Unknown, ConformanceMethod.Semantic, "model returned no verdict");

        var verdict = Normalize(eval.Verdict);

        // Only a claimed fail needs the adversarial pass; pass/unknown/error stand.
        if (verdict != ConformanceVerdict.Fail)
            return Result(verdict, ConformanceMethod.Semantic, eval.CodeEvidence, eval.File, eval.Line, eval.AdrQuote);

        // A fail that cannot quote both sides is not a fail — it is unknown.
        if (string.IsNullOrEmpty(eval.CodeEvidence) || string.IsNullOrEmpty(eval.AdrQuote))
            return Result(ConformanceVerdict.Unknown, ConformanceMethod.Verify, "claimed violation without quotable evidence");

        if (!_verify)
            return Result(ConformanceVerdict.Fail, ConformanceMethod.Semantic, eval.CodeEvidence, eval.File, eval.Line, eval.AdrQuote);

        // Adversarial verify pass decides fail vs unknown.
        var verifyPrompt = $"Rule: {rule.Claim}\nADR quote: {eval.AdrQuote}\nClaimed offending {eval.File}:{eval.Line}\nCode: {eval.CodeEvidence}";
        VerifyOut? verify;
        try
        {
            var verifyJson = LlmRuleExtractor.ExtractJsonObject(_model.Complete(VerifySystem, verifyPrompt));
            verify = JsonSerializer.Deserialize<VerifyOut>(verifyJson, LlmRuleExtractor.JsonOpts);
        }
        catch (FormatException)
        {
            verify = null;
        }

        return verify?.Confirmed == true
            ? Result(ConformanceVerdict.Fail, ConformanceMethod.Verify, eval.CodeEvidence, eval.File, eval.Line, eval.AdrQuote)
            : Result(ConformanceVerdict.Unknown, ConformanceMethod.Verify, $"verify did not confirm: {verify?.Reason ?? "unparseable verify"}");
    }

    private string BuildEvalPrompt(AdrRule rule, IReadOnlyList<AbsolutePath> files)
    {
        var sb = new StringBuilder();
        sb.Append("Rule claim: ").AppendLine(rule.Claim);
        sb.AppendLine("\n----- IN-SCOPE FILES -----");
        var budget = _maxFileChars;
        foreach (var f in files)
        {
            if (budget <= 0)
            {
                sb.AppendLine("\n[...additional files omitted for length...]");
                break;
            }
            string content;
            try { content = File.ReadAllText(f.Value); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (content.Length > budget)
                content = content.Substring(0, budget) + "\n[...truncated...]";
            budget -= content.Length;
            sb.Append("\n### ").AppendLine(f.Value);
            sb.AppendLine(content);
        }
        return sb.ToString();
    }

    private static string Normalize(string v) => v.Trim().ToLowerInvariant() switch
    {
        "pass" => ConformanceVerdict.Pass,
        "fail" => ConformanceVerdict.Fail,
        "error" => ConformanceVerdict.Error,
        _ => ConformanceVerdict.Unknown,
    };

    private sealed record EvalOut
    {
        public string? Verdict { get; init; }
        public string? File { get; init; }
        public int? Line { get; init; }
        public string? CodeEvidence { get; init; }
        public string? AdrQuote { get; init; }
    }

    private sealed record VerifyOut
    {
        public bool Confirmed { get; init; }
        public string? Reason { get; init; }
    }
}
