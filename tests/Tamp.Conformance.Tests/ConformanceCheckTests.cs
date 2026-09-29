using System.IO;
using Tamp;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class ConformanceCheckTests
{
    private sealed class Repo : IDisposable
    {
        public AbsolutePath Root { get; }
        public Repo() { Root = AbsolutePath.Create(Path.Combine(Path.GetTempPath(), $"tamp-conf-{Guid.NewGuid():N}")); Directory.CreateDirectory(Root.Value); }
        public AbsolutePath Write(string rel, string content)
        {
            var full = Path.Combine(Root.Value, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return AbsolutePath.Create(full);
        }
        public void Dispose() { try { Directory.Delete(Root.Value, recursive: true); } catch { /* best effort */ } }
    }

    private static AdrRuleSet OneRule(AdrRule rule) => new() { Adr = "0018", SourceSha = "sha256:test", Rules = new[] { rule } };

    [Fact]
    public void ForbiddenPattern_Present_Is_Fail_With_Evidence_And_Line()
    {
        using var repo = new Repo();
        var f = repo.Write("src/Foo.csproj", "<Project>\n  <PackageReference Include=\"X\" Version=\"*\" />\n</Project>\n");
        var set = OneRule(new AdrRule { Id = "0018-r1", Claim = "no floating versions", ForbiddenPattern = "Version=\"\\*\"", Scope = new[] { "**/*.csproj" } });

        var r = ConformanceCheck.CheckDeterministic(set, repo.Root, new[] { f }, emit: false)[0];

        Assert.Equal(ConformanceVerdict.Fail, r.Verdict);
        Assert.Equal("no floating versions", r.AdrQuote);      // structured reason on fail
        Assert.Contains("Version=\"*\"", r.CodeEvidence);
        Assert.Equal("src/Foo.csproj", r.File);
        Assert.Equal(2, r.Line);
        Assert.True(r.Blocks);
    }

    [Fact]
    public void ForbiddenPattern_Absent_Is_Pass_And_Does_Not_Block()
    {
        using var repo = new Repo();
        var f = repo.Write("src/Foo.csproj", "<Project>\n  <PackageReference Include=\"X\" Version=\"1.2.3\" />\n</Project>\n");
        var set = OneRule(new AdrRule { Id = "0018-r1", Claim = "no floating versions", ForbiddenPattern = "Version=\"\\*\"", Scope = new[] { "**/*.csproj" } });

        var r = ConformanceCheck.CheckDeterministic(set, repo.Root, new[] { f }, emit: false)[0];

        Assert.Equal(ConformanceVerdict.Pass, r.Verdict);
        Assert.Null(r.AdrQuote);
        Assert.False(r.Blocks);
    }

    [Fact]
    public void Scope_Matching_No_Files_Is_Unknown_Not_Pass()
    {
        using var repo = new Repo();
        var f = repo.Write("docs/notes.md", "nothing to see");
        var set = OneRule(new AdrRule { Id = "0018-r1", Claim = "x", ForbiddenPattern = "anything", Scope = new[] { "**/*.csproj" } });

        var r = ConformanceCheck.CheckDeterministic(set, repo.Root, new[] { f }, emit: false)[0];

        Assert.Equal(ConformanceVerdict.Unknown, r.Verdict);   // the check did not run — must NOT read as pass
        Assert.True(r.Blocks);                                 // unknown blocks in enforcing mode
    }

    [Fact]
    public void RequiredPattern_Missing_Is_Fail()
    {
        using var repo = new Repo();
        var f = repo.Write(".github/workflows/ci.yml", "on: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n");
        var set = OneRule(new AdrRule { Id = "0018-r3", Claim = "CI runs on macOS too", RequiredPattern = "macos-latest", Scope = new[] { ".github/**/*.yml" } });

        var r = ConformanceCheck.CheckDeterministic(set, repo.Root, new[] { f }, emit: false)[0];

        Assert.Equal(ConformanceVerdict.Fail, r.Verdict);
    }

    [Fact]
    public void Deterministic_Rule_With_No_Predicate_Is_Unknown()
    {
        using var repo = new Repo();
        var f = repo.Write("src/Foo.cs", "class Foo {}");
        var set = OneRule(new AdrRule { Id = "0018-r9", Claim = "vibes", Kind = RuleKind.Deterministic });

        var r = ConformanceCheck.CheckDeterministic(set, repo.Root, new[] { f }, emit: false)[0];

        Assert.Equal(ConformanceVerdict.Unknown, r.Verdict);
    }

    [Fact]
    public void Zt_Overlay_Carries_From_Rule_Onto_The_Verdict()
    {
        using var repo = new Repo();
        var f = repo.Write("src/Secret.cs", "public string Reveal() => _value;\n");
        var set = OneRule(new AdrRule
        {
            Id = "0005-r1", Claim = "secrets never leak their raw value", ForbiddenPattern = "=> _value",
            Scope = new[] { "**/Secret.cs" }, ControlRefs = new[] { "SC-28" },
            ZtPillar = "Data", ZtFunction = "Data Encryption", ZtStage = 3, MandateId = "encrypt-at-rest",
        });

        var r = ConformanceCheck.CheckDeterministic(set, repo.Root, new[] { f }, emit: false)[0];

        Assert.Equal(ConformanceVerdict.Fail, r.Verdict);
        Assert.Equal("Data", r.ZtPillar);                 // overlay rides through to the evidence
        Assert.Equal("Data Encryption", r.ZtFunction);
        Assert.Equal(3, r.ZtStage);
        Assert.Equal("encrypt-at-rest", r.MandateId);
    }

    [Fact]
    public void Semantic_Rules_Are_Skipped_By_The_Deterministic_Pass()
    {
        using var repo = new Repo();
        var f = repo.Write("src/Foo.cs", "class Foo {}");
        var set = OneRule(new AdrRule { Id = "0018-r5", Claim = "the UI is native, not a web app", Kind = RuleKind.Semantic });

        var results = ConformanceCheck.CheckDeterministic(set, repo.Root, new[] { f }, emit: false);

        Assert.Empty(results);   // semantic rules need the model-backed evaluator, not this path
    }
}
