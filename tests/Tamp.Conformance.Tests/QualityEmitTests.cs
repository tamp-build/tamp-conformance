using System.Text.Json;
using Tamp.Conformance.Quality;
using Xunit;

namespace Tamp.Conformance.Tests;

public class QualityEmitTests
{
    private static readonly QualityEnvelope Env = new(
        Client: "Tamp", Project: "tamp-core", Version: "1.17.3", CommitSha: "6e9ed40", Branch: "main");

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Findings_body_uses_type_wire_vocab_and_source_as_opaque_string()
    {
        var f = new NormalizedFinding(
            "csharpsquid:S4036", FindingType.Vulnerability, NormalizedSeverity.Low,
            "Use an absolute path for this command.", "src/Tamp.Core/WorkerIdResolver.cs", 74,
            Source: new FindingSource("sonarqube", SeverityRaw: "MINOR", TypeRaw: "VULNERABILITY", Effort: "15min", AnalysisId: "AXfoo"));

        var root = Parse(QualityEmit.FindingsBody(Env, "SonarQube", new[] { f }));

        Assert.Equal("Tamp", root.GetProperty("client").GetString());
        Assert.Equal("SonarQube", root.GetProperty("scanner").GetString());

        var finding = root.GetProperty("findings")[0];
        Assert.Equal("csharpsquid:S4036", finding.GetProperty("ruleId").GetString());
        Assert.Equal("vulnerability", finding.GetProperty("type").GetString());   // wire vocab
        Assert.Equal("Low", finding.GetProperty("severity").GetString());          // 5-scale
        Assert.Equal(74, finding.GetProperty("line").GetInt32());

        // source is an opaque JSON *string*, not a nested object
        var source = finding.GetProperty("source");
        Assert.Equal(JsonValueKind.String, source.ValueKind);
        var blob = JsonDocument.Parse(source.GetString()!).RootElement;
        Assert.Equal("MINOR", blob.GetProperty("severityRaw").GetString());
        Assert.Equal("AXfoo", blob.GetProperty("analysisId").GetString());
    }

    [Fact]
    public void QualityGate_body_maps_top_status_but_keeps_conditions_raw()
    {
        var verdict = new QualityGateVerdict(
            "sonarqube", "fail", "AXfoo",
            new[] { new GateCondition("new_reliability_rating", "GT", "1", "3", "ERROR") },
            new Dictionary<string, double> { ["vulnerabilities"] = 11, ["ncloc"] = 12306 });

        var root = Parse(QualityEmit.QualityGateBody(Env, verdict));

        Assert.Equal("fail", root.GetProperty("status").GetString());              // mapped
        var cond = root.GetProperty("conditions")[0];
        Assert.Equal("new_reliability_rating", cond.GetProperty("metric").GetString());
        Assert.Equal("ERROR", cond.GetProperty("status").GetString());             // raw level
        Assert.Equal("AXfoo", root.GetProperty("analysisId").GetString());
        Assert.Equal(11, root.GetProperty("measures").GetProperty("vulnerabilities").GetInt32());
    }

    [Fact]
    public void AnalysisCoverage_body_has_languages_and_overall_gap_list()
    {
        var report = new AnalysisCoverageReport(
            Languages: new[]
            {
                new LanguageCoverage("csharp", 420, 420, 12306, 100.0, new[] { "sonarqube", "roslyn" }, Array.Empty<string>()),
                new LanguageCoverage("typescript", 30, 0, 1500, 0.0, Array.Empty<string>(), new[] { "web/src/app.tsx" }),
            },
            Overall: new OverallCoverage(450, 420, 93.3, new[] { "typescript" }, new[] { "bin/**", "obj/**" }));

        var root = Parse(QualityEmit.AnalysisCoverageBody(Env, report));

        var langs = root.GetProperty("languages");
        Assert.Equal(2, langs.GetArrayLength());
        Assert.Equal("csharp", langs[0].GetProperty("language").GetString());
        Assert.Equal(100.0, langs[0].GetProperty("percentAnalyzed").GetDouble());
        Assert.Equal(2, langs[0].GetProperty("analyzedByTools").GetArrayLength());

        var overall = root.GetProperty("overall");
        var gaps = overall.GetProperty("languagesWithFootprintNoAnalyzer");
        Assert.Equal("typescript", gaps[0].GetString());
    }

    [Fact]
    public void Envelope_carries_component_and_commit()
    {
        var root = Parse(QualityEmit.FindingsBody(Env, "SonarQube", Array.Empty<NormalizedFinding>()));
        Assert.Equal("tamp-core", root.GetProperty("component").GetString());
        Assert.Equal("solution", root.GetProperty("componentKind").GetString());
        Assert.Equal("6e9ed40", root.GetProperty("commitSha").GetString());
    }
}
