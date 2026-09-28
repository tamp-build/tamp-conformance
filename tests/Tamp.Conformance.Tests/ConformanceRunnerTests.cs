using System.IO;
using Tamp;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class ConformanceRunnerTests : IDisposable
{
    private readonly AbsolutePath _root;
    private readonly ConformanceOptions _options;

    public ConformanceRunnerTests()
    {
        _root = AbsolutePath.Create(Path.Combine(Path.GetTempPath(), $"tamp-conf-run-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_root.Value);
        _options = new ConformanceOptions { RepoRoot = _root, CommitSha = "abc123" };
    }

    public void Dispose() { try { Directory.Delete(_root.Value, recursive: true); } catch { /* best effort */ } }

    private AbsolutePath Write(string rel, string content)
    {
        var full = Path.Combine(_root.Value, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return AbsolutePath.Create(full);
    }

    // Fake extractor: emits one deterministic rule forbidding a floating version in csproj.
    private sealed class FakeExtractor : IRuleExtractor
    {
        public IReadOnlyList<AdrRule> Extract(string adrId, string adrText) => new[]
        {
            new AdrRule { Id = $"{adrId}-r1", Claim = "no floating versions", Kind = RuleKind.Deterministic, ForbiddenPattern = "Version=\"\\*\"", Scope = new[] { "**/*.csproj" }, ControlRefs = new[] { "CM-6" } },
        };
    }

    private sealed class FakeSemantic : ISemanticEvaluator
    {
        private readonly string _verdict;
        public FakeSemantic(string verdict) => _verdict = verdict;
        public ConformanceResult Evaluate(AdrRuleSet set, AdrRule rule, IReadOnlyList<AbsolutePath> inScopeFiles) => new()
        {
            AdrRef = set.Adr, RuleId = rule.Id, Verdict = _verdict, Method = ConformanceMethod.Semantic, Blocks = _verdict != ConformanceVerdict.Pass,
        };
    }

    [Fact]
    public void GenerateRules_Writes_A_RuleSet_Per_Adr_Into_The_Working_Tree()
    {
        Write("docs/adr/0001-first.md", "# ADR 0001\nno floating versions.\n");
        Write("docs/adr/0002-second.md", "# ADR 0002\nanother.\n");
        Write("docs/adr/README.md", "index — must be ignored");

        ConformanceRunner.GenerateRules(new FakeExtractor(), _options, extractedBy: "fake");

        Assert.True(File.Exists(RuleStore.RulesPath(_options.ResolvedRulesDir, "0001").Value));
        Assert.True(File.Exists(RuleStore.RulesPath(_options.ResolvedRulesDir, "0002").Value));
        Assert.False(File.Exists(RuleStore.RulesPath(_options.ResolvedRulesDir, "README").Value));
    }

    [Fact]
    public void Check_Fails_When_Code_Violates_A_Generated_Rule()
    {
        Write("docs/adr/0001-pins.md", "# ADR 0001\nexact pins only.\n");
        Write("src/App.csproj", "<Project>\n  <PackageReference Include=\"X\" Version=\"*\" />\n</Project>\n");
        ConformanceRunner.GenerateRules(new FakeExtractor(), _options);

        var result = ConformanceRunner.Check(_options);

        Assert.False(result.Passed);
        Assert.Equal(1, result.Fails);
        Assert.Contains(result.Results, r => r.Verdict == ConformanceVerdict.Fail && r.RuleId == "0001-r1");
    }

    [Fact]
    public void Check_Passes_When_Code_Honors_The_Rule()
    {
        Write("docs/adr/0001-pins.md", "# ADR 0001\nexact pins only.\n");
        Write("src/App.csproj", "<Project>\n  <PackageReference Include=\"X\" Version=\"1.2.3\" />\n</Project>\n");
        ConformanceRunner.GenerateRules(new FakeExtractor(), _options);

        Assert.True(ConformanceRunner.Check(_options).Passed);
    }

    [Fact]
    public void Missing_Rules_For_An_Adr_Is_Unknown_And_Blocks()
    {
        Write("docs/adr/0001-pins.md", "# ADR 0001\nexact pins.\n");   // no rules generated

        var result = ConformanceRunner.Check(_options);

        Assert.False(result.Passed);
        Assert.Contains(result.Results, r => r.RuleId == "rules-missing" && r.Verdict == ConformanceVerdict.Unknown);
    }

    [Fact]
    public void Stale_Rules_Are_Unknown_And_Block_And_Skip_Checking()
    {
        var adr = Write("docs/adr/0001-pins.md", "# ADR 0001\nexact pins.\n");
        ConformanceRunner.GenerateRules(new FakeExtractor(), _options);
        File.WriteAllText(adr.Value, "# ADR 0001\nCHANGED — pins plus something.\n");   // ADR edited, rules not refreshed

        var result = ConformanceRunner.Check(_options);

        Assert.False(result.Passed);
        Assert.Contains(result.Results, r => r.RuleId == "rules-stale" && r.Verdict == ConformanceVerdict.Unknown);
    }

    [Fact]
    public void Advisory_Mode_Never_Blocks_Even_On_A_Violation()
    {
        Write("docs/adr/0001-pins.md", "# ADR 0001\nexact pins.\n");
        Write("src/App.csproj", "<Project><PackageReference Version=\"*\" /></Project>");
        var advisory = _options with { Enforcing = false };
        ConformanceRunner.GenerateRules(new FakeExtractor(), advisory);

        var result = ConformanceRunner.Check(advisory);

        Assert.True(result.Passed);                 // advisory: reports, never blocks
        Assert.Equal(1, result.Fails);              // ...but the fail is still recorded
    }

    [Fact]
    public void Semantic_Rule_Uses_The_Evaluator_When_Present_Else_Unknown()
    {
        Write("docs/adr/0001-native.md", "# ADR 0001\nnative UI only.\n");
        var rulesPath = RuleStore.RulesPath(_options.ResolvedRulesDir, "0001");
        var set = new AdrRuleSet
        {
            Adr = "0001",
            SourceSha = AbsolutePath.Create(Path.Combine(_root.Value, "docs/adr/0001-native.md".Replace('/', Path.DirectorySeparatorChar))).Sha256(),
            Rules = new[] { new AdrRule { Id = "0001-r1", Claim = "native UI only", Kind = RuleKind.Semantic } },
        };
        RuleLoader.Save(set, rulesPath);

        Assert.Contains(ConformanceRunner.Check(_options).Results, r => r.RuleId == "0001-r1" && r.Verdict == ConformanceVerdict.Unknown);
        Assert.True(ConformanceRunner.Check(_options, new FakeSemantic(ConformanceVerdict.Pass)).Passed);
    }
}
