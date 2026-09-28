using Tamp;

namespace Tamp.Conformance;

/// <summary>The aggregate outcome of a conformance check.</summary>
public sealed record ConformanceRunResult
{
    public required IReadOnlyList<ConformanceResult> Results { get; init; }

    /// <summary>True when nothing blocks (every verdict is <c>pass</c>, or the run is advisory).</summary>
    public bool Passed { get; init; }

    public int Fails => Results.Count(r => r.Verdict == ConformanceVerdict.Fail);
    public int Unknowns => Results.Count(r => r.Verdict == ConformanceVerdict.Unknown);
    public int Errors => Results.Count(r => r.Verdict == ConformanceVerdict.Error);
}

/// <summary>
/// Orchestrates the two capabilities over a repo's ADRs. <see cref="GenerateRules"/> is the write path —
/// it writes committed rule-sets into the working tree and touches nothing in git (branch protection is
/// respected by construction; the surrounding workflow commits via a PR). <see cref="Check"/> is the
/// read-only gate: it never writes, fails closed on stale/missing rules, runs the deterministic path, and
/// routes semantic rules to an <see cref="ISemanticEvaluator"/> when one is supplied (else they are
/// <c>unknown</c> — a semantic rule with no evaluator cannot be a silent pass).
/// </summary>
public static class ConformanceRunner
{
    /// <summary>Generate/refresh a rule-set per ADR and write it under the rules directory. Working-tree only.</summary>
    public static void GenerateRules(IRuleExtractor extractor, ConformanceOptions options, string? extractedBy = null)
    {
        foreach (var (adrId, file) in RuleStore.AdrFiles(options.ResolvedAdrDir))
        {
            var set = RuleGeneration.Generate(adrId, file, extractor, extractedBy);
            RuleLoader.Save(set, RuleStore.RulesPath(options.ResolvedRulesDir, adrId));
        }
    }

    /// <summary>Check code against the committed rules. Read-only; emits <c>conformance.evaluated</c> for every verdict.</summary>
    public static ConformanceRunResult Check(ConformanceOptions options, ISemanticEvaluator? semantic = null)
    {
        var results = new List<ConformanceResult>();
        var codeFiles = RuleStore.CodeFiles(options.RepoRoot, options.IgnoreDirs);

        foreach (var (adrId, adrFile) in RuleStore.AdrFiles(options.ResolvedAdrDir))
        {
            var rulesPath = RuleStore.RulesPath(options.ResolvedRulesDir, adrId);

            if (!File.Exists(rulesPath.Value))
            {
                results.Add(Meta(adrId, "rules-missing", ConformanceVerdict.Unknown,
                    $"No rule-set for ADR {adrId} — run AdrRules to generate one.", options));
                continue;
            }

            var set = RuleLoader.Load(rulesPath);

            if (RuleLoader.IsStale(set, adrFile))
            {
                results.Add(Meta(adrId, "rules-stale", ConformanceVerdict.Unknown,
                    $"ADR {adrId} changed since its rules were generated — run AdrRules to refresh, then commit.", options));
                continue;   // don't check against stale rules
            }

            // Deterministic rules — pure, reproducible, emitted.
            results.AddRange(ConformanceCheck.CheckDeterministic(set, options.RepoRoot, codeFiles, options.CommitSha, options.Enforcing, emit: true));

            // Semantic rules — routed to the model-backed evaluator, or unknown when none is wired.
            foreach (var rule in set.Rules.Where(r => r.Kind == RuleKind.Semantic))
            {
                var inScope = ConformanceCheck.FilesInScope(rule, options.RepoRoot, codeFiles);
                ConformanceResult r;
                if (semantic is null)
                {
                    r = Meta(adrId, rule.Id, ConformanceVerdict.Unknown, "Semantic rule but no evaluator configured.", options, rule.ControlRefs);
                }
                else
                {
                    r = semantic.Evaluate(set, rule, inScope);
                    Emit(r, set, options);
                }
                results.Add(r);
            }
        }

        var passed = !options.Enforcing || results.All(r => r.Verdict == ConformanceVerdict.Pass);
        return new ConformanceRunResult { Results = results, Passed = passed };
    }

    private static ConformanceResult Meta(
        string adrId, string ruleId, string verdict, string reason, ConformanceOptions options, IReadOnlyList<string>? controlRefs = null)
    {
        var r = new ConformanceResult
        {
            AdrRef = adrId,
            RuleId = ruleId,
            Verdict = verdict,
            CodeEvidence = reason,
            Method = ConformanceMethod.Deterministic,
            Blocks = options.Enforcing && verdict != ConformanceVerdict.Pass,
            ControlRefs = controlRefs,
        };
        Emit(r, null, options);
        return r;
    }

    private static void Emit(ConformanceResult r, AdrRuleSet? set, ConformanceOptions options)
    {
        var provenance = new Provenance
        {
            CommitSha = options.CommitSha,
            RulesSha = set?.SourceSha,
            Method = r.Method,
        };
        BuildEvents.Conformance(
            adrRef: r.AdrRef, ruleId: r.RuleId, verdict: r.Verdict, method: r.Method,
            adrQuote: r.AdrQuote, codeEvidence: r.CodeEvidence, file: r.File, line: r.Line,
            blocks: r.Blocks, provenance: provenance, controlRefs: r.ControlRefs);
    }
}
