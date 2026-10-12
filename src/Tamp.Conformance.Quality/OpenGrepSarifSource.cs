using System.Globalization;
using System.Text.Json;

namespace Tamp.Conformance.Quality;

/// <summary>
/// Reads opengrep (Semgrep-fork) SARIF — the <c>opengrep.sarif</c> the security pipeline emits — and
/// normalizes it into the SAST bucket (#16). opengrep is a security scanner: its findings route to
/// <see cref="FindingType.Vulnerability"/>. Severity comes from the SARIF rule's
/// <c>properties.security-severity</c> (a CVSS-style 0–10 score, when present) and falls back to the
/// result <c>level</c>. SARIF primitives are shared via <see cref="SarifReadHelpers"/>.
/// </summary>
public sealed class OpenGrepSarifSource : IQualitySource
{
    private readonly IReadOnlyList<string> _paths;
    private readonly string? _repoRoot;

    /// <param name="sarifPaths">SARIF files to read.</param>
    /// <param name="repoRoot">If given, file paths are made relative to it (forward-slashed).</param>
    public OpenGrepSarifSource(IEnumerable<string> sarifPaths, string? repoRoot = null)
    {
        _paths = sarifPaths.ToList();
        _repoRoot = repoRoot is null ? null : Path.GetFullPath(repoRoot);
    }

    /// <summary>Read a single opengrep SARIF file if it exists (the common one-file case).</summary>
    public static OpenGrepSarifSource FromFile(string sarifPath, string? repoRoot = null) =>
        new(File.Exists(sarifPath) ? new[] { sarifPath } : Array.Empty<string>(), repoRoot);

    /// <inheritdoc/>
    public string Scanner => "opengrep";

    /// <inheritdoc/>
    public IReadOnlyList<NormalizedFinding> Read()
    {
        var findings = new List<NormalizedFinding>();
        foreach (var path in _paths)
        {
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) continue;
            foreach (var run in runs.EnumerateArray()) ReadRun(run, findings);
        }
        return findings;
    }

    private void ReadRun(JsonElement run, List<NormalizedFinding> into)
    {
        // Index the rule table for security-severity lookup (opengrep puts the CVSS score on the rule).
        var secSevByIndex = new List<double?>();
        var secSevById = new Dictionary<string, double?>(StringComparer.Ordinal);
        if (run.TryGetProperty("tool", out var tool) &&
            tool.TryGetProperty("driver", out var driver) &&
            driver.TryGetProperty("rules", out var ruleArr) && ruleArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in ruleArr.EnumerateArray())
            {
                double? sev = null;
                if (r.TryGetProperty("properties", out var rp) && rp.TryGetProperty("security-severity", out var ss))
                {
                    var raw = ss.ValueKind == JsonValueKind.String ? ss.GetString()
                        : ss.ValueKind == JsonValueKind.Number ? ss.GetDouble().ToString(CultureInfo.InvariantCulture) : null;
                    if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) sev = d;
                }
                secSevByIndex.Add(sev);
                var id = SarifReadHelpers.Str(r, "id");
                if (!string.IsNullOrEmpty(id)) secSevById[id!] = sev;
            }
        }

        if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return;
        foreach (var res in results.EnumerateArray())
        {
            var ruleId = SarifReadHelpers.Str(res, "ruleId") ?? "";
            double? secSev = null;
            if (res.TryGetProperty("ruleIndex", out var ri) && ri.TryGetInt32(out var idx) && idx >= 0 && idx < secSevByIndex.Count)
                secSev = secSevByIndex[idx];
            else if (ruleId.Length > 0 && secSevById.TryGetValue(ruleId, out var s))
                secSev = s;

            var (file, line) = SarifReadHelpers.Location(res, _repoRoot);
            if (file is null) continue;

            var severity = secSev is { } cvss ? CvssToSeverity(cvss) : LevelToSeverity(SarifReadHelpers.Str(res, "level"));
            var message = SarifReadHelpers.Message(res);
            into.Add(new NormalizedFinding(
                RuleId: ruleId,
                Type: FindingType.Vulnerability, // opengrep is a SAST scanner → SAST bucket, type-driven
                Severity: severity,
                Title: SarifReadHelpers.FirstLine(message),
                FilePath: file,
                Line: line,
                Description: message,
                Source: new FindingSource(
                    Tool: "opengrep",
                    SeverityRaw: secSev?.ToString(CultureInfo.InvariantCulture) ?? SarifReadHelpers.Str(res, "level"),
                    TypeRaw: "security-severity")));
        }
    }

    private static NormalizedSeverity CvssToSeverity(double cvss) => cvss switch
    {
        >= 9.0 => NormalizedSeverity.Critical,
        >= 7.0 => NormalizedSeverity.High,
        >= 4.0 => NormalizedSeverity.Medium,
        > 0.0 => NormalizedSeverity.Low,
        _ => NormalizedSeverity.Info,
    };

    private static NormalizedSeverity LevelToSeverity(string? level) => (level ?? "").ToLowerInvariant() switch
    {
        "error" => NormalizedSeverity.High,
        "warning" => NormalizedSeverity.Medium,
        "note" => NormalizedSeverity.Low,
        _ => NormalizedSeverity.Info,
    };
}
