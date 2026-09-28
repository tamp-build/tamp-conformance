using System.IO;
using Tamp;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class RuleLoaderTests
{
    private static AbsolutePath TempFile(string content, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tamp-conf-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, content);
        return AbsolutePath.Create(path);
    }

    [Fact]
    public void RuleSet_RoundTrips_Through_Json_CamelCase_And_Enum()
    {
        var set = new AdrRuleSet
        {
            Adr = "0018",
            SourceSha = "sha256:abc",
            ExtractedBy = "claude-opus-4-8",
            Rules = new[]
            {
                new AdrRule
                {
                    Id = "0018-r1", Claim = "diagnostics are additive-only", Kind = RuleKind.Deterministic,
                    ForbiddenPattern = "renamed", Scope = new[] { "src/**/*.cs" }, ControlRefs = new[] { "CM-6" },
                },
            },
        };

        var json = RuleLoader.Serialize(set);
        Assert.Contains("\"adr\"", json);          // camelCase
        Assert.Contains("\"deterministic\"", json); // enum as camelCase string

        var back = RuleLoader.Deserialize(json);
        Assert.Equal(set.Adr, back.Adr);
        Assert.Equal(set.SourceSha, back.SourceSha);
        Assert.Single(back.Rules);
        Assert.Equal(RuleKind.Deterministic, back.Rules[0].Kind);
        Assert.Equal(new[] { "CM-6" }, back.Rules[0].ControlRefs);
    }

    [Fact]
    public void IsStale_Is_False_When_Sha_Matches_And_True_When_Adr_Changes()
    {
        var adr = TempFile("# ADR 0018\nadditive-only.\n", ".md");
        var set = RuleGeneration.Seed("0018", adr);

        Assert.False(RuleLoader.IsStale(set, adr));       // fresh: sha matches

        File.WriteAllText(adr.Value, "# ADR 0018\nCHANGED.\n");
        Assert.True(RuleLoader.IsStale(set, adr));         // ADR edited without refreshing rules → stale
    }

    [Fact]
    public void Save_Then_Load_Preserves_The_RuleSet()
    {
        var set = new AdrRuleSet { Adr = "0022", SourceSha = "sha256:def", Rules = Array.Empty<AdrRule>() };
        var file = AbsolutePath.Create(Path.Combine(Path.GetTempPath(), $"tamp-conf-{Guid.NewGuid():N}", "adr-rules.json"));

        RuleLoader.Save(set, file);
        var back = RuleLoader.Load(file);

        Assert.Equal("0022", back.Adr);
        Assert.Empty(back.Rules);
    }
}
