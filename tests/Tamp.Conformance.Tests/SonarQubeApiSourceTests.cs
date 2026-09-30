using Tamp.Conformance.Quality;
using Xunit;

namespace Tamp.Conformance.Tests;

public class SonarQubeApiSourceTests
{
    // Shaped like a real issues/search response (component carries the "projectKey:" prefix).
    private const string Issues = """
    {
      "issues": [
        { "rule": "csharpsquid:S4036", "severity": "MINOR", "type": "VULNERABILITY",
          "component": "tamp-build_tamp:src/Tamp.Core/WorkerIdResolver.cs", "line": 74,
          "message": "Use an absolute path for this command.", "effort": "15min",
          "impacts": [ { "softwareQuality": "SECURITY", "severity": "LOW" } ] },
        { "rule": "csharpsquid:S6966", "severity": "MAJOR", "type": "CODE_SMELL",
          "component": "tamp-build_tamp:tools/findings-ingest/Program.cs", "line": 138,
          "message": "Await LoadFromFileAsync instead.", "effort": "5min" },
        { "rule": "csharpsquid:S3903", "severity": "MAJOR", "type": "BUG",
          "component": "tamp-build_tamp:tools/findings-ingest/Program.cs", "line": 510,
          "message": "Move 'UsageChat' into a named namespace.", "effort": "5min" }
      ]
    }
    """;

    [Fact]
    public void Reads_issues_with_type_severity_path_and_provenance()
    {
        var src = new SonarQubeApiSource(Issues, analysisId: "AY9abc");
        var findings = src.Read();

        Assert.Equal(3, findings.Count);

        var vuln = Assert.Single(findings, f => f.RuleId == "csharpsquid:S4036");
        Assert.Equal(FindingType.Vulnerability, vuln.Type);
        Assert.Equal(QualityBucket.Sast, vuln.Bucket);
        Assert.Equal(NormalizedSeverity.Low, vuln.Severity);             // MINOR → Low
        Assert.Equal("src/Tamp.Core/WorkerIdResolver.cs", vuln.FilePath); // projectKey prefix stripped
        Assert.Equal(74, vuln.Line);
        Assert.Equal("MINOR", vuln.Source!.SeverityRaw);
        Assert.Equal("AY9abc", vuln.Source!.AnalysisId);

        Assert.Equal(QualityBucket.Quality, Assert.Single(findings, f => f.RuleId == "csharpsquid:S6966").Bucket);
        Assert.Equal(FindingType.Bug, Assert.Single(findings, f => f.RuleId == "csharpsquid:S3903").Type);
    }

    [Fact]
    public void Sonarqube_and_roslyn_overlap_dedups_to_one()
    {
        // Same C# code smell seen by both the server-side SonarAnalyzer (SonarQube) and local Roslyn.
        var sq = new SonarQubeApiSource("""
        { "issues": [ { "rule": "csharpsquid:S2325", "severity": "MINOR", "type": "CODE_SMELL",
          "component": "tamp-build_tamp:build/Build.cs", "line": 44, "message": "Make it static." } ] }
        """);
        var roslyn = new StubSource("roslyn",
            new NormalizedFinding("csharpsquid:S2325", FindingType.CodeSmell, NormalizedSeverity.Low, "Make it static.", "build/Build.cs", 44));

        var result = new QualityAdapter(sq, roslyn).Run();

        Assert.Single(result.All);
        Assert.Equal(1, result.DuplicatesRemoved);
        Assert.Single(result.Quality);
    }

    private sealed class StubSource(string scanner, params NormalizedFinding[] findings) : IQualitySource
    {
        public string Scanner { get; } = scanner;
        public IReadOnlyList<NormalizedFinding> Read() => findings;
    }
}

public class SonarQualityGateTests
{
    // The real tamp-core measures/component response (alert_status ERROR on new_reliability_rating).
    private const string Measures = """
    {
      "component": { "key": "tamp-build_tamp", "measures": [
        { "metric": "alert_status", "value": "ERROR" },
        { "metric": "bugs", "value": "2" },
        { "metric": "code_smells", "value": "45" },
        { "metric": "ncloc", "value": "12306" },
        { "metric": "vulnerabilities", "value": "11" },
        { "metric": "security_hotspots", "value": "0" },
        { "metric": "sqale_index", "value": "240" },
        { "metric": "quality_gate_details", "value": "{\"level\":\"ERROR\",\"conditions\":[{\"metric\":\"new_reliability_rating\",\"op\":\"GT\",\"period\":1,\"error\":\"1\",\"actual\":\"3\",\"level\":\"ERROR\"},{\"metric\":\"new_security_rating\",\"op\":\"GT\",\"period\":1,\"error\":\"1\",\"actual\":\"1\",\"level\":\"OK\"}],\"ignoredConditions\":false}" }
      ] }
    }
    """;

    [Fact]
    public void Parses_status_conditions_and_measures()
    {
        var v = SonarQualityGate.Parse(Measures, analysisId: "AY9abc");

        Assert.Equal("fail", v.Status);
        Assert.Equal("sonarqube", v.Source);
        Assert.Equal("AY9abc", v.AnalysisId);

        var failing = Assert.Single(v.Conditions, c => c.Status == "ERROR"); // raw Sonar level, verbatim
        Assert.Equal("new_reliability_rating", failing.Metric);
        Assert.Equal("GT", failing.Op);
        Assert.Equal("1", failing.Threshold);
        Assert.Equal("3", failing.Actual);

        Assert.Equal(11, v.Measures["vulnerabilities"]);
        Assert.Equal(45, v.Measures["code_smells"]);
        Assert.Equal(12306, v.Measures["ncloc"]);
    }
}
