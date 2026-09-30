using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamp.Conformance.Quality;

/// <summary>
/// The build identity every quality payload carries — reconciled by findings on <see cref="CommitSha"/>,
/// exactly like the /raw endpoints. Matches the pinned envelope (msg 03314e49).
/// </summary>
public sealed record QualityEnvelope(
    string Client,
    string Project,
    string Version,
    string? CommitSha = null,
    string? Branch = null,
    string? Component = "tamp-core",
    string? ComponentKind = "solution",
    string? Flavor = null);

/// <summary>
/// Serializes the four quality/SAST payloads to findings' pinned wire shapes (msg f30a7ea1 / 03314e49):
/// typed findings, quality-gate verdict, and analysis-coverage. (The scan-ran receipt keeps the existing
/// ScanRunReceipt path.) <c>source</c> is emitted as an opaque JSON *string*, findings stores it verbatim.
/// </summary>
public static class QualityEmit
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Shape 1 — <c>POST /ingest/findings</c>, typed-unified (one scanner per request).</summary>
    public static string FindingsBody(QualityEnvelope env, string scanner, IEnumerable<NormalizedFinding> findings)
    {
        var body = Envelope(env);
        body["scanner"] = scanner;
        body["findings"] = findings.Select(f => new Dictionary<string, object?>
        {
            ["ruleId"] = f.RuleId,
            ["type"] = Routing.ToWire(f.Type),
            ["severity"] = f.Severity.ToString(),           // 5-scale, e.g. "Medium"
            ["title"] = f.Title,
            ["filePath"] = f.FilePath,
            ["line"] = f.Line,
            ["source"] = SourceString(f.Source),            // opaque JSON string
        }).ToArray();
        return JsonSerializer.Serialize(body, Json);
    }

    /// <summary>Shape 3 — <c>POST /ingest/quality-gate</c>, attested verdict → the qualityGate gate.</summary>
    public static string QualityGateBody(QualityEnvelope env, QualityGateVerdict verdict)
    {
        var body = Envelope(env);
        body["status"] = verdict.Status;                    // mapped pass|fail|warn
        body["conditions"] = verdict.Conditions.Select(c => new Dictionary<string, object?>
        {
            ["metric"] = c.Metric,
            ["op"] = c.Op,
            ["threshold"] = c.Threshold,
            ["actual"] = c.Actual,
            ["status"] = c.Status,                          // raw Sonar level (OK|ERROR|WARN)
        }).ToArray();
        body["analysisId"] = verdict.AnalysisId;
        body["measures"] = verdict.Measures;
        return JsonSerializer.Serialize(body, Json);
    }

    /// <summary>Shape 4 — <c>POST /ingest/analysis-coverage</c> → the analysisCoverage gate.</summary>
    public static string AnalysisCoverageBody(QualityEnvelope env, AnalysisCoverageReport report)
    {
        var body = Envelope(env);
        body["languages"] = report.Languages.Select(l => new Dictionary<string, object?>
        {
            ["language"] = l.LanguageName,
            ["filesTotal"] = l.FilesTotal,
            ["filesAnalyzed"] = l.FilesAnalyzed,
            ["loc"] = l.Loc,
            ["percentAnalyzed"] = l.PercentAnalyzed,
            ["analyzedByTools"] = l.AnalyzedByTools,
            ["unanalyzedPaths"] = l.UnanalyzedPaths,
        }).ToArray();
        body["overall"] = new Dictionary<string, object?>
        {
            ["filesTotal"] = report.Overall.FilesTotal,
            ["filesAnalyzed"] = report.Overall.FilesAnalyzed,
            ["percentAnalyzed"] = report.Overall.PercentAnalyzed,
            ["languagesWithFootprintNoAnalyzer"] = report.Overall.LanguagesWithFootprintNoAnalyzer,
            ["excludes"] = report.Overall.Excludes,
        };
        return JsonSerializer.Serialize(body, Json);
    }

    /// <summary>Serialize the provenance blob to the opaque JSON string findings stores verbatim (null if none).</summary>
    public static string? SourceString(FindingSource? source)
    {
        if (source is null) return null;
        var blob = new Dictionary<string, object?>
        {
            ["tool"] = source.Tool,
            ["severityRaw"] = source.SeverityRaw,
            ["typeRaw"] = source.TypeRaw,
            ["effort"] = source.Effort,
            ["analysisId"] = source.AnalysisId,
        };
        return JsonSerializer.Serialize(blob, Json);
    }

    private static Dictionary<string, object?> Envelope(QualityEnvelope env) => new()
    {
        ["client"] = env.Client,
        ["project"] = env.Project,
        ["component"] = env.Component,
        ["componentKind"] = env.ComponentKind,
        ["flavor"] = env.Flavor,
        ["version"] = env.Version,
        ["commitSha"] = env.CommitSha,
        ["branch"] = env.Branch,
    };
}
