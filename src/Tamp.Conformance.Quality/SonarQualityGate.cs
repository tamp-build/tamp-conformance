using System.Text.Json;

namespace Tamp.Conformance.Quality;

/// <summary>One condition of a quality gate, as the source evaluated it.</summary>
public sealed record GateCondition(string Metric, string Op, string Threshold, string Actual, string Status);

/// <summary>
/// A quality-gate verdict — an attested external control result. The tool/CI already rendered the
/// decision; the producer only reports it (findings decides whether it blocks). Maps to Shape 3
/// (POST /ingest/quality-gate → the qualityGate gate).
/// </summary>
public sealed record QualityGateVerdict(
    string Source,
    string Status,
    string? AnalysisId,
    IReadOnlyList<GateCondition> Conditions,
    IReadOnlyDictionary<string, double> Measures);

/// <summary>Parses a SonarQube <c>measures/component</c> response into a <see cref="QualityGateVerdict"/>.</summary>
public static class SonarQualityGate
{
    private static readonly string[] MeasureMetrics =
        { "bugs", "vulnerabilities", "code_smells", "security_hotspots", "ncloc", "sqale_index" };

    /// <param name="measuresJson">The <c>measures/component</c> response (must include alert_status + quality_gate_details).</param>
    /// <param name="analysisId">The analysis this was pinned to.</param>
    public static QualityGateVerdict Parse(string measuresJson, string? analysisId = null)
    {
        using var doc = JsonDocument.Parse(measuresJson);
        var measures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("component", out var comp) &&
            comp.TryGetProperty("measures", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var metric = m.TryGetProperty("metric", out var mk) ? mk.GetString() : null;
                var value = m.TryGetProperty("value", out var mv) ? mv.GetString() : null;
                if (metric is not null && value is not null) measures[metric] = value;
            }
        }

        var status = MapStatus(measures.GetValueOrDefault("alert_status"));
        var conditions = ParseConditions(measures.GetValueOrDefault("quality_gate_details"));

        var numeric = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var metric in MeasureMetrics)
            if (measures.TryGetValue(metric, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d))
                numeric[metric] = d;

        return new QualityGateVerdict("sonarqube", status, analysisId, conditions, numeric);
    }

    private static string MapStatus(string? alertStatus) => (alertStatus ?? "").ToUpperInvariant() switch
    {
        "OK" => "pass",
        "ERROR" => "fail",
        "WARN" => "warn",
        _ => "unknown",
    };

    private static IReadOnlyList<GateCondition> ParseConditions(string? qualityGateDetails)
    {
        var list = new List<GateCondition>();
        if (string.IsNullOrWhiteSpace(qualityGateDetails)) return list;
        JsonDocument details;
        try { details = JsonDocument.Parse(qualityGateDetails); }
        catch { return list; }
        using (details)
        {
            if (!details.RootElement.TryGetProperty("conditions", out var conds) || conds.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var c in conds.EnumerateArray())
            {
                list.Add(new GateCondition(
                    Metric: Str(c, "metric") ?? "",
                    Op: Str(c, "op") ?? "",
                    Threshold: Str(c, "error") ?? "",
                    Actual: Str(c, "actual") ?? "",
                    Status: MapConditionLevel(Str(c, "level"))));
            }
        }
        return list;
    }

    private static string MapConditionLevel(string? level) => (level ?? "").ToUpperInvariant() switch
    {
        "OK" => "pass",
        "ERROR" => "fail",
        "WARN" => "warn",
        _ => "unknown",
    };

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
