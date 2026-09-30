using Tamp.Conformance.Quality;
using Xunit;

namespace Tamp.Conformance.Tests;

public class AnalysisCoverageAccountantTests : IDisposable
{
    private readonly string _root;

    public AnalysisCoverageAccountantTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"coverage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Write("a.cs", "class A {}\n");
        Write("src/b.cs", "class B {}\nclass B2 {}\n");
        Write("web/app.ts", "export const x = 1;\n");           // typescript — no analyzer in the test
        Write("obj/generated.cs", "// build output\n");          // excluded dir
        Write("legacy.Designer.cs", "// designer\n");            // excluded suffix
        Write("README.md", "# not source\n");                   // not a source language
    }

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Attributes_analyzed_files_and_surfaces_unanalyzed_language()
    {
        var accountant = new AnalysisCoverageAccountant(_root);
        var report = accountant.Compute(new[]
        {
            new CoverageContribution("roslyn", new[] { "a.cs", "src/b.cs" }),
        });

        var cs = Assert.Single(report.Languages, l => l.LanguageName == "csharp");
        Assert.Equal(2, cs.FilesTotal);           // a.cs + src/b.cs (obj/ + *.Designer.cs excluded)
        Assert.Equal(2, cs.FilesAnalyzed);
        Assert.Equal(100.0, cs.PercentAnalyzed);
        Assert.Equal(new[] { "roslyn" }, cs.AnalyzedByTools);
        Assert.Equal(3, cs.Loc);                  // 1 + 2 lines

        var ts = Assert.Single(report.Languages, l => l.LanguageName == "typescript");
        Assert.Equal(1, ts.FilesTotal);
        Assert.Equal(0, ts.FilesAnalyzed);
        Assert.Equal(0.0, ts.PercentAnalyzed);
        Assert.Contains("web/app.ts", ts.UnanalyzedPaths);
    }

    [Fact]
    public void Overall_flags_language_with_footprint_but_no_analyzer()
    {
        var accountant = new AnalysisCoverageAccountant(_root);
        var report = accountant.Compute(new[]
        {
            new CoverageContribution("roslyn", new[] { "a.cs", "src/b.cs" }),
        });

        Assert.Equal(3, report.Overall.FilesTotal);       // 2 cs + 1 ts
        Assert.Equal(2, report.Overall.FilesAnalyzed);
        Assert.Contains("typescript", report.Overall.LanguagesWithFootprintNoAnalyzer);
        Assert.DoesNotContain("csharp", report.Overall.LanguagesWithFootprintNoAnalyzer);
    }

    [Fact]
    public void Absolute_contribution_paths_are_relativized_to_repo_root()
    {
        var accountant = new AnalysisCoverageAccountant(_root);
        var absA = Path.Combine(_root, "a.cs");
        var report = accountant.Compute(new[]
        {
            new CoverageContribution("sonarqube", new[] { absA }),
        });

        var cs = Assert.Single(report.Languages, l => l.LanguageName == "csharp");
        Assert.Equal(1, cs.FilesAnalyzed);        // a.cs matched via its absolute path
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
