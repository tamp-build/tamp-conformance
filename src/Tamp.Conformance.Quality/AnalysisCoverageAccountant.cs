namespace Tamp.Conformance.Quality;

/// <summary>What one tool analyzed — the set of files it actually looked at (repo-relative, any slash).</summary>
/// <param name="Tool">The tool's wire name (e.g. "sonarqube", "roslyn", "eslint").</param>
/// <param name="AnalyzedFiles">Files the tool analyzed. A clean file has no findings, so this set — not the findings — is what proves coverage.</param>
public sealed record CoverageContribution(string Tool, IReadOnlyCollection<string> AnalyzedFiles);

/// <summary>Per-language coverage: how much of the tree's footprint for a language was actually analyzed.</summary>
public sealed record LanguageCoverage(
    string LanguageName,
    int FilesTotal,
    int FilesAnalyzed,
    long Loc,
    double PercentAnalyzed,
    IReadOnlyList<string> AnalyzedByTools,
    IReadOnlyList<string> UnanalyzedPaths);

/// <summary>Whole-repo roll-up plus the headline gap: languages with a footprint but no analyzer at all.</summary>
public sealed record OverallCoverage(
    int FilesTotal,
    int FilesAnalyzed,
    double PercentAnalyzed,
    IReadOnlyList<string> LanguagesWithFootprintNoAnalyzer,
    IReadOnlyList<string> Excludes);

/// <summary>The analysis-coverage evidence — per-language rows + overall. Posted to /ingest/analysis-coverage.</summary>
public sealed record AnalysisCoverageReport(
    IReadOnlyList<LanguageCoverage> Languages,
    OverallCoverage Overall);

/// <summary>
/// Computes analysis-coverage completeness — the piece only the producer can produce, because only it
/// has the full checkout. Inventories the tree by language (the denominator), attributes what each
/// tool analyzed (the numerator), and reports the delta so "0 findings" can't mask "never analyzed"
/// and a language with a footprint but no analyzer surfaces as a visible gap.
/// </summary>
public sealed class AnalysisCoverageAccountant
{
    private static readonly string[] DefaultExcludeDirs =
        { "bin", "obj", "node_modules", ".git", ".vs", "artifacts", "dist", "out", "packages", "TestResults", ".idea" };
    private static readonly string[] DefaultExcludeSuffixes =
        { ".g.cs", ".designer.cs", ".generated.cs", ".g.i.cs" };

    private readonly string _repoRoot;
    private readonly HashSet<string> _excludeDirs;
    private readonly string[] _excludeSuffixes;
    private readonly int _footprintThreshold;
    private readonly int _unanalyzedSample;

    /// <param name="repoRoot">Repository root to inventory.</param>
    /// <param name="excludeDirNames">Directory names to skip (default: build/vendor/generated dirs).</param>
    /// <param name="excludeFileSuffixes">Filename suffixes to skip (default: generated *.g.cs / *.Designer.cs).</param>
    /// <param name="footprintThreshold">A language needs at least this many files to count as a "footprint" for the no-analyzer gap.</param>
    /// <param name="unanalyzedSampleSize">Cap on the sample of unanalyzed paths reported per language.</param>
    public AnalysisCoverageAccountant(
        string repoRoot,
        IEnumerable<string>? excludeDirNames = null,
        IEnumerable<string>? excludeFileSuffixes = null,
        int footprintThreshold = 1,
        int unanalyzedSampleSize = 20)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _excludeDirs = new HashSet<string>(excludeDirNames ?? DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);
        _excludeSuffixes = (excludeFileSuffixes ?? DefaultExcludeSuffixes).Select(s => s.ToLowerInvariant()).ToArray();
        _footprintThreshold = Math.Max(1, footprintThreshold);
        _unanalyzedSample = Math.Max(0, unanalyzedSampleSize);
    }

    /// <summary>Inventory the tree and attribute the given tool contributions.</summary>
    public AnalysisCoverageReport Compute(IEnumerable<CoverageContribution> contributions)
    {
        // File → tools that analyzed it (normalized rel paths, case-insensitive).
        var analyzedBy = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in contributions)
        foreach (var f in c.AnalyzedFiles)
        {
            var rel = NormalizeRel(f);
            if (!analyzedBy.TryGetValue(rel, out var tools)) analyzedBy[rel] = tools = new SortedSet<string>(StringComparer.Ordinal);
            tools.Add(c.Tool);
        }

        // Inventory by language.
        var byLang = new Dictionary<string, LangAccum>(StringComparer.Ordinal);
        foreach (var abs in EnumerateSourceFiles())
        {
            var rel = NormalizeRel(abs);
            var lang = Language.ForPath(rel);
            if (lang is null) continue;
            if (!byLang.TryGetValue(lang, out var acc)) byLang[lang] = acc = new LangAccum();

            acc.Total++;
            acc.Loc += CountLines(abs);
            if (analyzedBy.TryGetValue(rel, out var tools))
            {
                acc.Analyzed++;
                foreach (var t in tools) acc.Tools.Add(t);
            }
            else if (acc.Unanalyzed.Count < _unanalyzedSample)
            {
                acc.Unanalyzed.Add(rel);
            }
        }

        var languages = byLang
            .OrderByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new LanguageCoverage(
                LanguageName: kv.Key,
                FilesTotal: kv.Value.Total,
                FilesAnalyzed: kv.Value.Analyzed,
                Loc: kv.Value.Loc,
                PercentAnalyzed: Pct(kv.Value.Analyzed, kv.Value.Total),
                AnalyzedByTools: kv.Value.Tools.ToList(),
                UnanalyzedPaths: kv.Value.Unanalyzed))
            .ToList();

        var totalFiles = languages.Sum(l => l.FilesTotal);
        var totalAnalyzed = languages.Sum(l => l.FilesAnalyzed);
        var noAnalyzer = languages
            .Where(l => l.FilesTotal >= _footprintThreshold && l.FilesAnalyzed == 0)
            .Select(l => l.LanguageName)
            .ToList();

        var overall = new OverallCoverage(
            FilesTotal: totalFiles,
            FilesAnalyzed: totalAnalyzed,
            PercentAnalyzed: Pct(totalAnalyzed, totalFiles),
            LanguagesWithFootprintNoAnalyzer: noAnalyzer,
            Excludes: _excludeDirs.OrderBy(d => d, StringComparer.Ordinal).Select(d => d + "/**").ToList());

        return new AnalysisCoverageReport(languages, overall);
    }

    private IEnumerable<string> EnumerateSourceFiles()
    {
        var stack = new Stack<string>();
        stack.Push(_repoRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subdirs)
                if (!_excludeDirs.Contains(Path.GetFileName(sub)))
                    stack.Push(sub);

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files)
                if (!IsExcludedFile(f))
                    yield return f;
        }
    }

    private bool IsExcludedFile(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        foreach (var suffix in _excludeSuffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return true;
        return false;
    }

    private string NormalizeRel(string path)
    {
        var p = path.Replace('\\', '/');
        if (Path.IsPathRooted(path))
        {
            var full = Path.GetFullPath(path).Replace('\\', '/');
            var root = _repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full[root.Length..];
            return full;
        }
        return p.TrimStart('.', '/');
    }

    private static long CountLines(string path)
    {
        try
        {
            long n = 0;
            foreach (var _ in File.ReadLines(path)) n++;
            return n;
        }
        catch { return 0; }
    }

    private static double Pct(int part, int total) =>
        total == 0 ? 100.0 : Math.Round(part * 100.0 / total, 1);

    private sealed class LangAccum
    {
        public int Total;
        public int Analyzed;
        public long Loc;
        public SortedSet<string> Tools { get; } = new(StringComparer.Ordinal);
        public List<string> Unanalyzed { get; } = new();
    }
}
