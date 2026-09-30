using System.Text.Json;

namespace Tamp.Conformance.Quality;

/// <summary>
/// Normalizes SonarQube / SonarCloud API results into <see cref="NormalizedFinding"/>. Reads the
/// <c>issues/search</c> response (bugs, code smells, vulnerabilities) and, optionally, the separate
/// <c>hotspots/search</c> response (security hotspots). Type + severity come straight from the API
/// fields via <see cref="SonarConventions"/>; the native values + MQR impacts are preserved in
/// <see cref="FindingSource"/>. This is a read-only projection of an already-completed analysis —
/// the caller fetches the JSON (pinned to an analysisId) and hands it in.
/// </summary>
public sealed class SonarQubeApiSource : IQualitySource
{
    private readonly string _issuesJson;
    private readonly string? _hotspotsJson;
    private readonly string? _analysisId;

    /// <param name="issuesJson">The <c>issues/search</c> response body.</param>
    /// <param name="hotspotsJson">The <c>hotspots/search</c> response body, if fetched.</param>
    /// <param name="analysisId">The analysis this data was pinned to, recorded as provenance.</param>
    public SonarQubeApiSource(string issuesJson, string? hotspotsJson = null, string? analysisId = null)
    {
        _issuesJson = issuesJson;
        _hotspotsJson = hotspotsJson;
        _analysisId = analysisId;
    }

    /// <summary>Read from files on disk (issues + optional hotspots).</summary>
    public static SonarQubeApiSource FromFiles(string issuesPath, string? hotspotsPath = null, string? analysisId = null) =>
        new(File.ReadAllText(issuesPath),
            hotspotsPath is not null && File.Exists(hotspotsPath) ? File.ReadAllText(hotspotsPath) : null,
            analysisId);

    /// <inheritdoc/>
    public string Scanner => "sonarqube";

    /// <inheritdoc/>
    public IReadOnlyList<NormalizedFinding> Read()
    {
        var findings = new List<NormalizedFinding>();
        ReadIssues(findings);
        if (_hotspotsJson is not null) ReadHotspots(findings);
        return findings;
    }

    private void ReadIssues(List<NormalizedFinding> into)
    {
        using var doc = JsonDocument.Parse(_issuesJson);
        if (!doc.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array) return;
        foreach (var i in issues.EnumerateArray())
        {
            var rule = Str(i, "rule") ?? "";
            var type = SonarConventions.MapType(Str(i, "type"));
            var severityRaw = Str(i, "severity");
            var (file, _) = SplitComponent(Str(i, "component"));
            if (file is null) continue;
            var line = Int(i, "line");
            var message = Str(i, "message") ?? "";

            into.Add(new NormalizedFinding(
                RuleId: rule,
                Type: type,
                Severity: SonarConventions.MapSeverity(severityRaw),
                Title: message,
                FilePath: file,
                Line: line,
                Description: message,
                SubCategory: RuleRepository(rule),
                Source: new FindingSource(
                    Tool: "sonarqube",
                    SeverityRaw: severityRaw,
                    TypeRaw: Str(i, "type"),
                    Effort: Str(i, "effort"),
                    AnalysisId: _analysisId)));
        }
    }

    private void ReadHotspots(List<NormalizedFinding> into)
    {
        using var doc = JsonDocument.Parse(_hotspotsJson!);
        if (!doc.RootElement.TryGetProperty("hotspots", out var hs) || hs.ValueKind != JsonValueKind.Array) return;
        foreach (var h in hs.EnumerateArray())
        {
            var rule = Str(h, "ruleKey") ?? Str(h, "rule") ?? "";
            var (file, _) = SplitComponent(Str(h, "component"));
            if (file is null) continue;
            var message = Str(h, "message") ?? "";
            // vulnerabilityProbability HIGH/MEDIUM/LOW → severity; hotspots are always SecurityHotspot.
            var prob = Str(h, "vulnerabilityProbability");
            into.Add(new NormalizedFinding(
                RuleId: rule,
                Type: FindingType.SecurityHotspot,
                Severity: ProbabilityToSeverity(prob),
                Title: message,
                FilePath: file,
                Line: Int(h, "line"),
                Description: message,
                SubCategory: Str(h, "securityCategory"),
                Source: new FindingSource(Tool: "sonarqube", SeverityRaw: prob, TypeRaw: "SECURITY_HOTSPOT", AnalysisId: _analysisId)));
        }
    }

    // "tamp-build_tamp:src/Tamp.Core/WorkerIdResolver.cs" → "src/Tamp.Core/WorkerIdResolver.cs"
    private static (string? File, string? ProjectKey) SplitComponent(string? component)
    {
        if (string.IsNullOrEmpty(component)) return (null, null);
        var idx = component.IndexOf(':');
        return idx < 0 ? (component, null) : (component[(idx + 1)..], component[..idx]);
    }

    // "csharpsquid:S4036" → "csharpsquid"
    private static string? RuleRepository(string rule)
    {
        var idx = rule.IndexOf(':');
        return idx < 0 ? null : rule[..idx];
    }

    private static NormalizedSeverity ProbabilityToSeverity(string? p) => (p ?? "").ToLowerInvariant() switch
    {
        "high" => NormalizedSeverity.High,
        "medium" => NormalizedSeverity.Medium,
        "low" => NormalizedSeverity.Low,
        _ => NormalizedSeverity.Info,
    };

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.TryGetInt32(out var v) ? v : null;
}
