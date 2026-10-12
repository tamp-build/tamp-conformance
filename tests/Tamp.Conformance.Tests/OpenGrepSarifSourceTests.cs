using System;
using System.IO;
using System.Linq;
using Tamp.Conformance.Quality;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class OpenGrepSarifSourceTests
{
    // Builds a SARIF whose file URIs point at real paths under a temp repo root, so relativization is
    // exercised cross-platform (a hard-coded "/repo" breaks under Windows Path.GetFullPath).
    private static OpenGrepSarifSource Source(out string dir)
    {
        dir = Directory.CreateTempSubdirectory("og-sarif-").FullName;
        var root = dir; // local functions can't capture an out parameter
        string Uri(string rel) => new Uri(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))).AbsoluteUri;
        var sarif = $$"""
        {
          "version": "2.1.0",
          "runs": [{
            "tool": { "driver": { "name": "opengrep", "rules": [
              { "id": "csharp.lang.security.sqli", "properties": { "security-severity": "8.5" } },
              { "id": "generic.secrets.token", "properties": { "security-severity": "3.0" } },
              { "id": "no.score.rule", "properties": {} }
            ] } },
            "results": [
              { "ruleIndex": 0, "ruleId": "csharp.lang.security.sqli",
                "message": { "text": "Possible SQL injection\nvia string concat" },
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "{{Uri("src/Db.cs")}}" }, "region": { "startLine": 42 } } }] },
              { "ruleIndex": 1, "ruleId": "generic.secrets.token",
                "message": { "text": "Hardcoded token" },
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "{{Uri("src/Auth.cs")}}" }, "region": { "startLine": 7 } } }] },
              { "ruleIndex": 2, "ruleId": "no.score.rule", "level": "error",
                "message": { "text": "No security-severity; falls back to level" },
                "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "{{Uri("src/X.cs")}}" }, "region": { "startLine": 1 } } }] }
            ]
          }]
        }
        """;
        var p = Path.Combine(dir, "opengrep.sarif");
        File.WriteAllText(p, sarif);
        return OpenGrepSarifSource.FromFile(p, repoRoot: dir);
    }

    [Fact]
    public void Everything_Routes_To_Sast_As_Vulnerability()
    {
        var dir = "";
        try
        {
            var findings = Source(out dir).Read();
            Assert.Equal(3, findings.Count);
            Assert.All(findings, f => Assert.Equal(FindingType.Vulnerability, f.Type));
            Assert.All(findings, f => Assert.Equal(QualityBucket.Sast, f.Bucket));
            Assert.All(findings, f => Assert.Equal("opengrep", f.Source!.Tool));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SecuritySeverity_Maps_To_Normalized_Severity()
    {
        var dir = "";
        try
        {
            var f = Source(out dir).Read();
            Assert.Equal(NormalizedSeverity.High, f.Single(x => x.RuleId == "csharp.lang.security.sqli").Severity);   // 8.5 → High
            Assert.Equal(NormalizedSeverity.Low, f.Single(x => x.RuleId == "generic.secrets.token").Severity);        // 3.0 → Low
            Assert.Equal(NormalizedSeverity.High, f.Single(x => x.RuleId == "no.score.rule").Severity);               // no score → level "error" → High
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Location_Is_Relativized_And_Lined()
    {
        var dir = "";
        try
        {
            var f = Source(out dir).Read();
            var sqli = f.Single(x => x.RuleId == "csharp.lang.security.sqli");
            Assert.Equal("src/Db.cs", sqli.FilePath);
            Assert.Equal(42, sqli.Line);
            Assert.Equal("Possible SQL injection", sqli.Title); // first line of the message
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Missing_File_Yields_No_Findings()
    {
        var src = OpenGrepSarifSource.FromFile(Path.Combine(Path.GetTempPath(), "does-not-exist-og.sarif"));
        Assert.Empty(src.Read());
    }

    [Fact]
    public void Scanner_Name_Is_Opengrep()
    {
        var dir = "";
        try { Assert.Equal("opengrep", Source(out dir).Scanner); }
        finally { Directory.Delete(dir, true); }
    }

    // Exercises the full CVSS→severity + level→severity maps and the SarifReadHelpers location edge cases
    // (no-location result is skipped; a relative, non-file:// uri with no repo root passes through as-is).
    [Fact]
    public void Covers_Severity_Range_And_Location_Edge_Cases()
    {
        var dir = Directory.CreateTempSubdirectory("og-edge-").FullName;
        try
        {
            var sarif = """
            {
              "runs": [{
                "tool": { "driver": { "name": "opengrep", "rules": [
                  { "id": "crit", "properties": { "security-severity": "9.5" } },
                  { "id": "med",  "properties": { "security-severity": "5.0" } },
                  { "id": "zero", "properties": { "security-severity": "0" } }
                ] } },
                "results": [
                  { "ruleIndex": 0, "ruleId": "crit", "message": { "text": "crit" },
                    "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/A.cs" }, "region": { "startLine": 1 } } }] },
                  { "ruleIndex": 1, "ruleId": "med", "message": { "text": "med" },
                    "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/B.cs" } } }] },
                  { "ruleIndex": 2, "ruleId": "zero", "message": { "text": "zero" },
                    "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/C.cs" } } }] },
                  { "ruleId": "warnlevel", "level": "warning", "message": { "text": "w" },
                    "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/D.cs" } } }] },
                  { "ruleId": "notelevel", "level": "note", "message": { "text": "n" },
                    "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/E.cs" } } }] },
                  { "ruleId": "unknownlevel", "level": "info", "message": { "text": "i" },
                    "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "src/F.cs" } } }] },
                  { "ruleId": "noloc", "level": "error", "message": { "text": "skipped — no locations" } }
                ]
              }]
            }
            """;
            var p = Path.Combine(dir, "og.sarif");
            File.WriteAllText(p, sarif);
            var f = OpenGrepSarifSource.FromFile(p).Read(); // no repoRoot → relative uris pass through

            Assert.Equal(6, f.Count); // the no-location result is skipped
            Assert.Equal(NormalizedSeverity.Critical, f.Single(x => x.RuleId == "crit").Severity);  // 9.5
            Assert.Equal(NormalizedSeverity.Medium, f.Single(x => x.RuleId == "med").Severity);      // 5.0
            Assert.Equal(NormalizedSeverity.Info, f.Single(x => x.RuleId == "zero").Severity);       // 0 → Info
            Assert.Equal(NormalizedSeverity.Medium, f.Single(x => x.RuleId == "warnlevel").Severity);
            Assert.Equal(NormalizedSeverity.Low, f.Single(x => x.RuleId == "notelevel").Severity);
            Assert.Equal(NormalizedSeverity.Info, f.Single(x => x.RuleId == "unknownlevel").Severity);
            Assert.Equal("src/A.cs", f.Single(x => x.RuleId == "crit").FilePath); // relative, no-root passthrough
            Assert.Null(f.Single(x => x.RuleId == "med").Line);                   // no region → null line
        }
        finally { Directory.Delete(dir, true); }
    }
}
