using System.Text.RegularExpressions;
using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// Locates ADRs, their committed rule-sets, and the code files to check. The rule-set layout is one file
/// per ADR under the rules directory (<c>&lt;adrId&gt;.json</c>) so staleness and diffs are per-ADR and clean.
/// </summary>
public static class RuleStore
{
    private static readonly Regex AdrName = new(@"^(\d{3,})-.+\.md$", RegexOptions.IgnoreCase);

    /// <summary>ADR files in <paramref name="adrDir"/> whose name is <c>NNNN-*.md</c>, paired with the extracted ADR id. README and non-numbered files are ignored.</summary>
    public static IReadOnlyList<(string AdrId, AbsolutePath File)> AdrFiles(AbsolutePath adrDir)
    {
        if (!Directory.Exists(adrDir.Value))
            return Array.Empty<(string, AbsolutePath)>();

        var found = new List<(string, AbsolutePath)>();
        foreach (var path in Directory.EnumerateFiles(adrDir.Value, "*.md"))
        {
            var m = AdrName.Match(Path.GetFileName(path));
            if (m.Success)
                found.Add((m.Groups[1].Value, AbsolutePath.Create(path)));
        }
        return found.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    /// <summary>The committed rule-set path for one ADR.</summary>
    public static AbsolutePath RulesPath(AbsolutePath rulesDir, string adrId) => rulesDir / $"{adrId}.json";

    /// <summary>One-line summaries ("&lt;adrId&gt;: &lt;title&gt;") of the ADRs — the "known ADRs" context for reverse examination.</summary>
    public static IReadOnlyList<string> AdrSummaries(AbsolutePath adrDir)
        => AdrFiles(adrDir).Select(a => $"{a.AdrId}: {FirstHeading(a.File)}").ToList();

    private static string FirstHeading(AbsolutePath file)
    {
        try
        {
            foreach (var raw in File.ReadLines(file.Value))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                return line.TrimStart('#').Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* fall through */ }
        return "(untitled)";
    }

    /// <summary>All code files under <paramref name="root"/>, skipping <paramref name="ignoreDirs"/> anywhere in the path.</summary>
    public static IReadOnlyList<AbsolutePath> CodeFiles(AbsolutePath root, IReadOnlyList<string> ignoreDirs)
    {
        if (!Directory.Exists(root.Value))
            return Array.Empty<AbsolutePath>();

        var ignore = new HashSet<string>(ignoreDirs, StringComparer.OrdinalIgnoreCase);
        var files = new List<AbsolutePath>();
        Walk(root.Value, ignore, files);
        return files;
    }

    private static void Walk(string dir, HashSet<string> ignore, List<AbsolutePath> acc)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (ignore.Contains(Path.GetFileName(sub)))
                continue;
            Walk(sub, ignore, acc);
        }
        foreach (var file in Directory.EnumerateFiles(dir))
            acc.Add(AbsolutePath.Create(file));
    }
}
