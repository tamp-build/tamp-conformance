using Tamp.Conformance.Quality;
using Xunit;

namespace Tamp.Conformance.Tests;

public class SonarConventionsTests
{
    [Theory]
    [InlineData("Minor Code Smell", FindingType.CodeSmell, NormalizedSeverity.Low)]
    [InlineData("Major Bug", FindingType.Bug, NormalizedSeverity.Medium)]
    [InlineData("Blocker Vulnerability", FindingType.Vulnerability, NormalizedSeverity.Critical)]
    [InlineData("Critical Security Hotspot", FindingType.SecurityHotspot, NormalizedSeverity.High)]
    [InlineData("Info Code Smell", FindingType.CodeSmell, NormalizedSeverity.Info)]
    public void TryParseCategory_parses_type_and_severity(string category, FindingType type, NormalizedSeverity sev)
    {
        Assert.True(SonarConventions.TryParseCategory(category, out var t, out var s));
        Assert.Equal(type, t);
        Assert.Equal(sev, s);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Naming")]              // a non-Sonar Roslyn category — no recognizable type
    public void TryParseCategory_rejects_non_sonar_categories(string? category)
    {
        Assert.False(SonarConventions.TryParseCategory(category, out _, out _));
    }

    [Theory]
    [InlineData(FindingType.Bug, QualityBucket.Quality)]
    [InlineData(FindingType.CodeSmell, QualityBucket.Quality)]
    [InlineData(FindingType.Vulnerability, QualityBucket.Sast)]
    [InlineData(FindingType.SecurityHotspot, QualityBucket.Sast)]
    public void Routing_is_by_type(FindingType type, QualityBucket bucket)
    {
        Assert.Equal(bucket, Routing.BucketFor(type));
    }
}

public class RoslynSarifSourceTests
{
    // File URIs are built from a real host-OS root so relativization is exercised portably
    // (Windows drive paths and Unix absolute paths both go through the same code path).
    private static string Uri3(string root, string rel) =>
        new Uri(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))).AbsoluteUri;

    private static string BuildSarif(string root) => $$"""
    {
      "runs": [{
        "tool": { "driver": { "rules": [
          { "id": "S4036", "properties": { "category": "Minor Vulnerability" } },
          { "id": "S2325", "properties": { "category": "Minor Code Smell" } },
          { "id": "CA1822", "properties": { "category": "Performance" } }
        ] } },
        "results": [
          { "ruleId": "S4036", "ruleIndex": 0, "level": "warning",
            "message": { "text": "Use an absolute path for this command." },
            "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "{{Uri3(root, "src/Tamp.Core/WorkerIdResolver.cs")}}" }, "region": { "startLine": 74 } } }] },
          { "ruleId": "S2325", "ruleIndex": 1, "level": "warning",
            "message": { "text": "Make 'Artifacts' a static property." },
            "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "{{Uri3(root, "build/Build.cs")}}" }, "region": { "startLine": 44 } } }] },
          { "ruleId": "CA1822", "ruleIndex": 2, "level": "warning",
            "message": { "text": "Member can be static." },
            "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "{{Uri3(root, "build/Build.cs")}}" }, "region": { "startLine": 10 } } }] }
        ]
      }]
    }
    """;

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"quality-test-{Guid.NewGuid():N}.sarif");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Reads_sonar_findings_with_type_severity_and_relative_path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}");
        var path = WriteTemp(BuildSarif(root));
        try
        {
            var src = new RoslynSarifSource(new[] { path }, repoRoot: root);
            var findings = src.Read();

            // onlySonarRules default → CA1822 (no Sonar type) is dropped.
            Assert.Equal(2, findings.Count);

            var vuln = Assert.Single(findings, f => f.RuleId == "S4036");
            Assert.Equal(FindingType.Vulnerability, vuln.Type);
            Assert.Equal(QualityBucket.Sast, vuln.Bucket);
            Assert.Equal(NormalizedSeverity.Low, vuln.Severity);
            Assert.Equal("src/Tamp.Core/WorkerIdResolver.cs", vuln.FilePath); // relativized + forward-slashed
            Assert.Equal(74, vuln.Line);

            var smell = Assert.Single(findings, f => f.RuleId == "S2325");
            Assert.Equal(FindingType.CodeSmell, smell.Type);
            Assert.Equal(QualityBucket.Quality, smell.Bucket);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Non_sonar_rules_included_as_code_smell_when_requested()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}");
        var path = WriteTemp(BuildSarif(root));
        try
        {
            var src = new RoslynSarifSource(new[] { path }, repoRoot: root, onlySonarRules: false);
            var findings = src.Read();
            Assert.Equal(3, findings.Count);
            var ca = Assert.Single(findings, f => f.RuleId == "CA1822");
            Assert.Equal(FindingType.CodeSmell, ca.Type);      // never routed to SAST
            Assert.Equal(QualityBucket.Quality, ca.Bucket);
        }
        finally { File.Delete(path); }
    }
}

public class QualityAdapterTests
{
    private sealed class FakeSource(string scanner, params NormalizedFinding[] findings) : IQualitySource
    {
        public string Scanner { get; } = scanner;
        public IReadOnlyList<NormalizedFinding> Read() => findings;
    }

    private static NormalizedFinding F(string rule, FindingType type, string file, int line) =>
        new(rule, type, NormalizedSeverity.Medium, rule, file, line);

    [Fact]
    public void Dedups_same_finding_across_sources_by_rule_file_line()
    {
        // Same C# finding seen by both SonarQube (server-side SonarAnalyzer) and local Roslyn.
        var dup = F("S2325", FindingType.CodeSmell, "build/Build.cs", 44);
        var adapter = new QualityAdapter(
            new FakeSource("sonarqube", dup),
            new FakeSource("roslyn", dup with { }));

        var r = adapter.Run();

        Assert.Single(r.All);
        Assert.Equal(1, r.DuplicatesRemoved);
    }

    [Fact]
    public void Routes_by_type_across_both_buckets()
    {
        var adapter = new QualityAdapter(new FakeSource("sonarqube",
            F("S4036", FindingType.Vulnerability, "a.cs", 1),
            F("S1234", FindingType.SecurityHotspot, "b.cs", 2),
            F("S2325", FindingType.CodeSmell, "c.cs", 3),
            F("S3903", FindingType.Bug, "d.cs", 4)));

        var r = adapter.Run();

        Assert.Equal(2, r.Sast.Count);       // vuln + hotspot
        Assert.Equal(2, r.Quality.Count);    // smell + bug
        Assert.Equal(4, r.All.Count);
        Assert.Equal(0, r.DuplicatesRemoved);
    }
}
