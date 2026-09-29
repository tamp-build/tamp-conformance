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

            RunSet(set, codeFiles, options, semantic, results);
        }

        var passed = !options.Enforcing || results.All(r => r.Verdict == ConformanceVerdict.Pass);
        return new ConformanceRunResult { Results = results, Passed = passed };
    }

    /// <summary>
    /// Check code against a rule-set <b>fetched from tamp-findings</b> (the authoritative store, ADR 0003) —
    /// the integrated gate. Same read-only, four-valued, emitting behavior as <see cref="Check"/>, but the
    /// rules come from the flat active set findings serves (grouped back into per-ADR sets), so there is no
    /// local rule file to read and no ADR-file staleness gate: the fetched set <i>is</i> the truth. Use this
    /// wherever findings is wired; <see cref="Check"/> stays for standalone/offline runs against committed rules.
    /// </summary>
    public static ConformanceRunResult CheckFetched(
        IReadOnlyList<AdrRuleWithRef> fetched, ConformanceOptions options, ISemanticEvaluator? semantic = null)
    {
        var results = new List<ConformanceResult>();
        var codeFiles = RuleStore.CodeFiles(options.RepoRoot, options.IgnoreDirs);

        foreach (var group in fetched.GroupBy(r => r.AdrRef).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rules = group.Select(r => r.Rule).ToList();
            var set = new AdrRuleSet
            {
                Adr = group.Key,
                // No ADR-file hash on the wire; stamp a deterministic content hash of the fetched rules so
                // provenance.rulesSha still pins which interpretation ran (findings is authoritative anyway).
                SourceSha = "findings:" + AbsolutePath.Sha256Of(string.Join("\n", rules.Select(RuleFingerprint))),
                ExtractedBy = "tamp-findings",
                Rules = rules,
            };
            RunSet(set, codeFiles, options, semantic, results);
        }

        var passed = !options.Enforcing || results.All(r => r.Verdict == ConformanceVerdict.Pass);
        return new ConformanceRunResult { Results = results, Passed = passed };
    }

    private static string RuleFingerprint(AdrRule r)
        => string.Join("|", r.Id, r.Kind, r.ForbiddenPattern, r.RequiredPattern,
            r.Scope is null ? "" : string.Join(",", r.Scope), r.Claim);

    /// <summary>Run one rule-set (deterministic + semantic) against the code, appending verdicts and emitting each.</summary>
    private static void RunSet(
        AdrRuleSet set, IReadOnlyList<AbsolutePath> codeFiles, ConformanceOptions options,
        ISemanticEvaluator? semantic, List<ConformanceResult> results)
    {
        // Deterministic rules — pure, reproducible, emitted.
        results.AddRange(ConformanceCheck.CheckDeterministic(set, options.RepoRoot, codeFiles, options.CommitSha, options.Enforcing, emit: true));

        // Semantic rules — routed to the model-backed evaluator, or unknown when none is wired.
        foreach (var rule in set.Rules.Where(r => r.Kind == RuleKind.Semantic))
        {
            var inScope = ConformanceCheck.FilesInScope(rule, options.RepoRoot, codeFiles);
            ConformanceResult r;
            if (semantic is null)
            {
                r = Meta(set.Adr, rule.Id, ConformanceVerdict.Unknown, "Semantic rule but no evaluator configured.", options, rule.ControlRefs, rule);
            }
            else
            {
                r = semantic.Evaluate(set, rule, inScope);
                r = r with { Blocks = options.Enforcing && r.Verdict != ConformanceVerdict.Pass };
                Emit(r, set, options);
            }
            results.Add(r);
        }
    }

    private static ConformanceResult Meta(
        string adrId, string ruleId, string verdict, string reason, ConformanceOptions options, IReadOnlyList<string>? controlRefs = null, AdrRule? rule = null)
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
            ZtPillar = rule?.ZtPillar,
            ZtFunction = rule?.ZtFunction,
            ZtStage = rule?.ZtStage,
            MandateId = rule?.MandateId,
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
            blocks: r.Blocks, provenance: provenance, controlRefs: r.ControlRefs,
            ztPillar: r.ZtPillar, ztFunction: r.ZtFunction, ztStage: r.ZtStage, mandateId: r.MandateId);
    }
}
