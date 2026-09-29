using System.Text.RegularExpressions;
using Tamp;

namespace Tamp.Conformance;

/// <summary>The outcome of evaluating one <see cref="AdrRule"/> against the code — the shape emitted as a <c>conformance.evaluated</c> event.</summary>
public sealed record ConformanceResult
{
    public required string AdrRef { get; init; }
    public required string RuleId { get; init; }

    /// <summary>Four-valued; one of <see cref="ConformanceVerdict"/>.</summary>
    public required string Verdict { get; init; }

    public string? AdrQuote { get; init; }
    public string? CodeEvidence { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }

    /// <summary>One of <see cref="ConformanceMethod"/>.</summary>
    public required string Method { get; init; }

    public bool Blocks { get; init; }
    public IReadOnlyList<string>? ControlRefs { get; init; }

    // ZT / mandate overlay carried from the producing rule onto the verdict (see core ADR 0023; findings scores on these).
    public string? ZtPillar { get; init; }
    public string? ZtFunction { get; init; }
    public int? ZtStage { get; init; }
    public string? MandateId { get; init; }
}

/// <summary>
/// The deterministic (predicate) conformance evaluator — the <c>CheckAdrConformance</c> capability's
/// pure, reproducible, free-to-run path. Each rule yields a four-valued verdict (see
/// <see cref="ConformanceVerdict"/>): a matched forbidden pattern (or a missing required one) is
/// <c>fail</c> with the offending line as evidence; a rule whose scope matched no files is
/// <c>unknown</c> ("the check did not run") — never a silent pass; a regex/IO fault is <c>error</c>.
/// Results are also emitted on the canonical stream via <see cref="BuildEvents.Conformance"/> (a no-op
/// outside a build). The semantic (model-backed) path is separate — see <see cref="ISemanticEvaluator"/>.
/// </summary>
public static class ConformanceCheck
{
    /// <summary>
    /// Evaluate every <see cref="RuleKind.Deterministic"/> rule in <paramref name="set"/> against
    /// <paramref name="files"/> (candidate files already discovered by the caller), scoping each rule by
    /// its globs relative to <paramref name="repoRoot"/>.
    /// </summary>
    public static IReadOnlyList<ConformanceResult> CheckDeterministic(
        AdrRuleSet set,
        AbsolutePath repoRoot,
        IReadOnlyList<AbsolutePath> files,
        string? commitSha = null,
        bool blocks = true,
        bool emit = true)
    {
        var results = new List<ConformanceResult>();

        foreach (var rule in set.Rules)
        {
            if (rule.Kind != RuleKind.Deterministic)
                continue;

            var result = EvaluateOne(set, rule, repoRoot, files, blocks);
            results.Add(result);

            if (emit)
            {
                var provenance = new Provenance
                {
                    CommitSha = commitSha,
                    RulesSha = set.SourceSha,
                    Method = ConformanceMethod.Deterministic,
                };
                BuildEvents.Conformance(
                    adrRef: result.AdrRef,
                    ruleId: result.RuleId,
                    verdict: result.Verdict,
                    method: result.Method,
                    adrQuote: result.AdrQuote,
                    codeEvidence: result.CodeEvidence,
                    file: result.File,
                    line: result.Line,
                    blocks: result.Blocks,
                    provenance: provenance,
                    controlRefs: result.ControlRefs,
                    ztPillar: result.ZtPillar,
                    ztFunction: result.ZtFunction,
                    ztStage: result.ZtStage,
                    mandateId: result.MandateId);
            }
        }

        return results;
    }

    private static ConformanceResult EvaluateOne(
        AdrRuleSet set, AdrRule rule, AbsolutePath repoRoot, IReadOnlyList<AbsolutePath> files, bool blocks)
    {
        ConformanceResult Result(string verdict, string? evidence = null, string? file = null, int? line = null) => new()
        {
            AdrRef = set.Adr,
            RuleId = rule.Id,
            Verdict = verdict,
            AdrQuote = verdict == ConformanceVerdict.Fail ? rule.Claim : null,
            CodeEvidence = evidence,
            File = file,
            Line = line,
            Method = ConformanceMethod.Deterministic,
            Blocks = blocks && verdict != ConformanceVerdict.Pass,
            ControlRefs = rule.ControlRefs,
            ZtPillar = rule.ZtPillar,
            ZtFunction = rule.ZtFunction,
            ZtStage = rule.ZtStage,
            MandateId = rule.MandateId,
        };

        // A deterministic rule with no predicate is not deterministically checkable — never a pass.
        if (string.IsNullOrEmpty(rule.ForbiddenPattern) && string.IsNullOrEmpty(rule.RequiredPattern))
            return Result(ConformanceVerdict.Unknown);

        var inScope = InScope(rule, repoRoot, files);
        if (inScope.Count == 0)
            return Result(ConformanceVerdict.Unknown);   // the check did not run — not a pass

        try
        {
            if (!string.IsNullOrEmpty(rule.ForbiddenPattern))
            {
                var forbidden = new Regex(rule.ForbiddenPattern);
                foreach (var f in inScope)
                {
                    var lines = File.ReadAllLines(f.Value);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (forbidden.IsMatch(lines[i]))
                            return Result(ConformanceVerdict.Fail, lines[i].Trim(), Rel(repoRoot, f), i + 1);
                    }
                }
                return Result(ConformanceVerdict.Pass);
            }

            // RequiredPattern: must appear in at least one in-scope file.
            var required = new Regex(rule.RequiredPattern!);
            foreach (var f in inScope)
            {
                var lines = File.ReadAllLines(f.Value);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (required.IsMatch(lines[i]))
                        return Result(ConformanceVerdict.Pass);
                }
            }
            return Result(ConformanceVerdict.Fail, $"required pattern /{rule.RequiredPattern}/ not found in scope");
        }
        catch (Exception ex) when (ex is RegexParseException or IOException or UnauthorizedAccessException)
        {
            return Result(ConformanceVerdict.Error, ex.Message);
        }
    }

    /// <summary>The subset of <paramref name="files"/> a rule's <see cref="AdrRule.Scope"/> globs match (all files when scope is empty). Public so the semantic path can scope identically.</summary>
    public static IReadOnlyList<AbsolutePath> FilesInScope(AdrRule rule, AbsolutePath repoRoot, IReadOnlyList<AbsolutePath> files) => InScope(rule, repoRoot, files);

    private static List<AbsolutePath> InScope(AdrRule rule, AbsolutePath repoRoot, IReadOnlyList<AbsolutePath> files)
    {
        if (rule.Scope is null || rule.Scope.Count == 0)
            return files.ToList();

        var matchers = rule.Scope.Select(GlobToRegex).ToList();
        return files.Where(f =>
        {
            var rel = Rel(repoRoot, f);
            return matchers.Any(m => m.IsMatch(rel));
        }).ToList();
    }

    private static string Rel(AbsolutePath root, AbsolutePath file)
        => Path.GetRelativePath(root.Value, file.Value).Replace('\\', '/');

    /// <summary>Minimal glob → regex: <c>**</c> any path segments, <c>*</c> within a segment, <c>?</c> one char.</summary>
    private static Regex GlobToRegex(string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
                if (i + 1 < normalized.Length && normalized[i + 1] == '/')
                    i++;   // consume the slash after ** so "**/x" also matches a top-level "x"
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString());
    }
}
