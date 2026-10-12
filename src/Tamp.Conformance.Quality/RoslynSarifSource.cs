using System.Text.Json;

namespace Tamp.Conformance.Quality;

/// <summary>
/// Reads SonarAnalyzer (and general Roslyn) SARIF logs — the per-project <c>*.sarif</c> the
/// <c>SecurityScanRoslyn</c> target emits — and normalizes them. A finding's type + severity come
/// from the SARIF rule's <c>properties.category</c> ("&lt;Severity&gt; &lt;Type&gt;", e.g. "Minor Code Smell"),
/// which the result references by <c>ruleIndex</c> into <c>tool.driver.rules[]</c>. SARIF primitives are
/// shared via <see cref="SarifReadHelpers"/>.
/// </summary>
public sealed class RoslynSarifSource : IQualitySource
{
    private readonly IReadOnlyList<string> _paths;
    private readonly string? _repoRoot;
    private readonly bool _onlySonarRules;

    /// <param name="sarifPaths">SARIF files to read.</param>
    /// <param name="repoRoot">If given, file paths are made relative to it (forward-slashed).</param>
    /// <param name="onlySonarRules">
    /// When true (default), only results whose rule carries a parseable Sonar category are emitted, so a
    /// non-Sonar Roslyn/Roslynator rule (CA*, RCS*) with no type is never mis-routed into SAST.
    /// </param>
    public RoslynSarifSource(IEnumerable<string> sarifPaths, string? repoRoot = null, bool onlySonarRules = true)
    {
        _paths = sarifPaths.ToList();
        _repoRoot = repoRoot is null ? null : Path.GetFullPath(repoRoot);
        _onlySonarRules = onlySonarRules;
    }

    /// <summary>Read every <c>*.sarif</c> under <paramref name="dir"/> (recursively).</summary>
    public static RoslynSarifSource FromDirectory(string dir, string? repoRoot = null, bool onlySonarRules = true) =>
        new(Directory.Exists(dir) ? Directory.GetFiles(dir, "*.sarif", SearchOption.AllDirectories) : Array.Empty<string>(),
            repoRoot, onlySonarRules);

    /// <inheritdoc/>
    public string Scanner => "roslyn";

    /// <inheritdoc/>
    public IReadOnlyList<NormalizedFinding> Read()
    {
        var findings = new List<NormalizedFinding>();
        foreach (var path in _paths)
        {
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) continue;
            foreach (var run in runs.EnumerateArray())
                ReadRun(run, findings);
        }
        return findings;
    }

    private void ReadRun(JsonElement run, List<NormalizedFinding> into)
    {
        // Index the rule table once: ruleIndex -> (id, category).
        var rules = new List<(string Id, string? Category)>();
        var byId = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (run.TryGetProperty("tool", out var tool) &&
            tool.TryGetProperty("driver", out var driver) &&
            driver.TryGetProperty("rules", out var ruleArr) && ruleArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in ruleArr.EnumerateArray())
            {
                var id = SarifReadHelpers.Str(r, "id") ?? "";
                string? category = null;
                if (r.TryGetProperty("properties", out var rp)) category = SarifReadHelpers.Str(rp, "category");
                rules.Add((id, category));
                if (id.Length > 0) byId[id] = category;
            }
        }

        if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return;
        foreach (var res in results.EnumerateArray())
        {
            var ruleId = SarifReadHelpers.Str(res, "ruleId") ?? "";
            // Prefer ruleIndex; fall back to id lookup.
            string? category = null;
            if (res.TryGetProperty("ruleIndex", out var ri) && ri.TryGetInt32(out var idx) && idx >= 0 && idx < rules.Count)
            {
                category = rules[idx].Category;
                if (ruleId.Length == 0) ruleId = rules[idx].Id;
            }
            else if (ruleId.Length > 0 && byId.TryGetValue(ruleId, out var c))
            {
                category = c;
            }

            if (!SonarConventions.TryParseCategory(category, out var type, out var severity))
            {
                if (_onlySonarRules) continue;
                type = FindingType.CodeSmell;
                severity = LevelToSeverity(SarifReadHelpers.Str(res, "level"));
            }

            var (file, line) = SarifReadHelpers.Location(res, _repoRoot);
            if (file is null) continue;

            var message = SarifReadHelpers.Message(res);
            into.Add(new NormalizedFinding(
                RuleId: ruleId,
                Type: type,
                Severity: severity,
                Title: SarifReadHelpers.FirstLine(message),
                FilePath: file,
                Line: line,
                Description: message,
                Snippet: null,
                SubCategory: category,
                Source: new FindingSource(Tool: "roslyn", TypeRaw: category)));
        }
    }

    private static NormalizedSeverity LevelToSeverity(string? level) => (level ?? "").ToLowerInvariant() switch
    {
        "error" => NormalizedSeverity.High,
        "warning" => NormalizedSeverity.Medium,
        "note" => NormalizedSeverity.Low,
        _ => NormalizedSeverity.Info,
    };
}
