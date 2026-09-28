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
}
