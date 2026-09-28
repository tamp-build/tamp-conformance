using System.Text.RegularExpressions;
using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// The reverse-examination capability — decisions the code made that no ADR records (a change-control
/// drift signal, CM-3). <b>Advisory only, always</b>: it suggests "write an ADR," it never gates. The
/// unbounded "what undocumented decisions exist?" question is tamed by cheap deterministic
/// <see cref="Triggers"/> (a new dependency, a new project, …); only a triggered candidate is handed to
/// the model-backed <see cref="IDecisionDetector"/> for the ADR-worthiness judgement.
/// </summary>
public static class ReverseExamination
{
    /// <summary>
    /// Scan candidate files for deterministic decision signals. Each entry in
    /// <paramref name="signals"/> maps a candidate kind (e.g. <c>"new-dependency"</c>) to a regex whose
    /// matches are surfaced as <see cref="DecisionCandidate"/>s. Pure and free; the model only sees hits.
    /// </summary>
    public static IReadOnlyList<DecisionCandidate> Triggers(
        AbsolutePath repoRoot,
        IReadOnlyList<AbsolutePath> files,
        IReadOnlyDictionary<string, string> signals)
    {
        var candidates = new List<DecisionCandidate>();
        var compiled = signals.ToDictionary(kv => kv.Key, kv => new Regex(kv.Value));

        foreach (var f in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(f.Value); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            var rel = Path.GetRelativePath(repoRoot.Value, f.Value).Replace('\\', '/');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (kind, rx) in compiled)
                {
                    if (rx.IsMatch(lines[i]))
                        candidates.Add(new DecisionCandidate { Kind = kind, Detail = lines[i].Trim(), File = rel, Line = i + 1 });
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// End-to-end reverse examination: run the deterministic <see cref="Triggers"/>, then hand any hits to the
    /// model-backed <paramref name="detector"/> to judge which are ADR-worthy and uncovered. <b>Advisory
    /// only</b> — surfaces each as a SARIF <c>note</c> diagnostic (<c>undocumented-decision:&lt;kind&gt;</c>);
    /// never blocks. Returns the (possibly empty) list of undocumented decisions.
    /// </summary>
    public static IReadOnlyList<UndocumentedDecision> Detect(
        AbsolutePath repoRoot,
        IReadOnlyList<AbsolutePath> files,
        IReadOnlyDictionary<string, string> signals,
        IDecisionDetector detector,
        IReadOnlyList<string> knownAdrSummaries,
        bool emit = true)
    {
        var candidates = Triggers(repoRoot, files, signals);
        if (candidates.Count == 0)
            return Array.Empty<UndocumentedDecision>();

        var decisions = detector.Detect(candidates, knownAdrSummaries);

        if (emit)
            foreach (var d in decisions)
                BuildEvents.Diagnostic($"undocumented-decision:{d.Kind}", "note", d.Summary, d.File, d.Line);

        return decisions;
    }
}
