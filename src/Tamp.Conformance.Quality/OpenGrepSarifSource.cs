using System.Text.Json;

namespace Tamp.Conformance.Quality;

/// <summary>
/// Reads opengrep (Semgrep-fork) SARIF — the <c>opengrep.sarif</c> the security pipeline emits — and
/// normalizes it into the SAST bucket (#16). opengrep is a security scanner: its findings route to
/// <see cref="FindingType.Vulnerability"/>. Severity comes from the SARIF rule's
/// <c>properties.security-severity</c> (a CVSS-style 0–10 score, when present) and falls back to the
/// result <c>level</c>.
/// </summary>
/// <remarks>
/// Standalone (not sharing internals with <see cref="RoslynSarifSource"/>) so it can't regress the Roslyn
/// path; the small SARIF location/message helpers are duplicated deliberately. Routing stays type-driven:
/// everything opengrep emits is a security finding, so it lands in SAST via <see cref="FindingType.Vulnerability"/>.
/// </remarks>
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
                    var raw = ss.ValueKind == JsonValueKind.String ? ss.GetString() : (ss.ValueKind == JsonValueKind.Number ? ss.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
                    if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) sev = d;
                }
                secSevByIndex.Add(sev);
                var id = Str(r, "id");
                if (!string.IsNullOrEmpty(id)) secSevById[id!] = sev;
            }
        }

        if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return;
        foreach (var res in results.EnumerateArray())
        {
            var ruleId = Str(res, "ruleId") ?? "";
            double? secSev = null;
            if (res.TryGetProperty("ruleIndex", out var ri) && ri.TryGetInt32(out var idx) && idx >= 0 && idx < secSevByIndex.Count)
                secSev = secSevByIndex[idx];
            else if (ruleId.Length > 0 && secSevById.TryGetValue(ruleId, out var s))
                secSev = s;

            var (file, line) = Location(res);
            if (file is null) continue;

            var severity = secSev is { } cvss ? CvssToSeverity(cvss) : LevelToSeverity(Str(res, "level"));
            var message = Message(res);
            into.Add(new NormalizedFinding(
                RuleId: ruleId,
                Type: FindingType.Vulnerability, // opengrep is a SAST scanner → SAST bucket, type-driven
                Severity: severity,
                Title: FirstLine(message),
                FilePath: file,
                Line: line,
                Description: message,
                Source: new FindingSource(
                    Tool: "opengrep",
                    SeverityRaw: secSev?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Str(res, "level"),
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

    private (string? File, int? Line) Location(JsonElement res)
    {
        if (!res.TryGetProperty("locations", out var locs) || locs.ValueKind != JsonValueKind.Array) return (null, null);
        foreach (var loc in locs.EnumerateArray())
        {
            if (!loc.TryGetProperty("physicalLocation", out var pl)) continue;
            string? uri = pl.TryGetProperty("artifactLocation", out var al) ? Str(al, "uri") : null;
            if (uri is null) continue;
            int? line = null;
            if (pl.TryGetProperty("region", out var region) && region.TryGetProperty("startLine", out var sl) && sl.TryGetInt32(out var l)) line = l;
            return (Relativize(uri), line);
        }
        return (null, null);
    }

    private string Relativize(string uri)
    {
        // Uri.LocalPath handles both file:///C:/x (Windows) and file:///home/x (Unix); manual stripping drops
        // the leading slash on Unix absolute paths.
        string path;
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile)
            path = u.LocalPath;
        else
            path = Uri.UnescapeDataString(uri);
        path = path.Replace('\\', '/');
        if (_repoRoot is not null)
        {
            var root = _repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                path = path[root.Length..];
        }
        return path;
    }

    private static string Message(JsonElement res) =>
        res.TryGetProperty("message", out var m) && m.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
