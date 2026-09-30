namespace Tamp.Conformance.Quality;

/// <summary>
/// Aggregates findings from one or more <see cref="IQualitySource"/>: reads all, dedups by
/// (ruleId, filePath, line), and routes each into the quality or SAST report by finding TYPE.
/// The result is what the producer emits to findings (typed-unified) — findings then applies policy.
/// </summary>
public sealed class QualityAdapter
{
    private readonly IReadOnlyList<IQualitySource> _sources;

    public QualityAdapter(IEnumerable<IQualitySource> sources) => _sources = sources.ToList();

    public QualityAdapter(params IQualitySource[] sources) => _sources = sources.ToList();

    /// <summary>Read every source, dedup, and route.</summary>
    public AdapterResult Run()
    {
        var seen = new Dictionary<(string, string, int), NormalizedFinding>();
        var producedBy = new Dictionary<(string, string, int), SortedSet<string>>();
        var duplicates = 0;

        foreach (var source in _sources)
        foreach (var f in source.Read())
        {
            var key = (f.RuleId, f.FilePath, f.Line ?? -1);
            if (seen.TryGetValue(key, out _))
            {
                duplicates++;
                producedBy[key].Add(source.Scanner);
                continue;
            }
            seen[key] = f;
            producedBy[key] = new SortedSet<string>(StringComparer.Ordinal) { source.Scanner };
        }

        var all = seen.Values.OrderBy(f => f.FilePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? -1)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToList();

        return new AdapterResult(
            All: all,
            Quality: all.Where(f => f.Bucket == QualityBucket.Quality).ToList(),
            Sast: all.Where(f => f.Bucket == QualityBucket.Sast).ToList(),
            DuplicatesRemoved: duplicates,
            Scanners: _sources.Select(s => s.Scanner).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList());
    }
}

/// <summary>The deduped, routed output of a <see cref="QualityAdapter"/> run.</summary>
public sealed record AdapterResult(
    IReadOnlyList<NormalizedFinding> All,
    IReadOnlyList<NormalizedFinding> Quality,
    IReadOnlyList<NormalizedFinding> Sast,
    int DuplicatesRemoved,
    IReadOnlyList<string> Scanners)
{
    /// <summary>Count of findings of each type (across both buckets).</summary>
    public IReadOnlyDictionary<FindingType, int> CountByType =>
        All.GroupBy(f => f.Type).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Count of findings of each severity (across both buckets).</summary>
    public IReadOnlyDictionary<NormalizedSeverity, int> CountBySeverity =>
        All.GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.Count());
}
