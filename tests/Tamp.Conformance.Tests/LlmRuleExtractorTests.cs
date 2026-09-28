using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class LlmRuleExtractorTests
{
    private sealed class FakeModel : IChatCompletion
    {
        private readonly string _response;
        public FakeModel(string response) => _response = response;
        public string ModelId => "fake/model";
        public string Complete(string system, string user) => _response;
    }

    private const string GoodJson = """
        {"rules":[
          {"id":"0018-r1","claim":"no floating package versions","kind":"deterministic","forbiddenPattern":"Version=\"\\*\"","scope":["**/*.csproj"],"controlRefs":["CM-6"]},
          {"id":"0018-r2","claim":"the UI is native, not a web app","kind":"semantic"}
        ]}
        """;

    [Fact]
    public void Parses_Deterministic_And_Semantic_Rules()
    {
        var rules = new LlmRuleExtractor(new FakeModel(GoodJson)).Extract("0018", "irrelevant");

        Assert.Equal(2, rules.Count);
        Assert.Equal("0018-r1", rules[0].Id);
        Assert.Equal(RuleKind.Deterministic, rules[0].Kind);
        Assert.Equal("Version=\"\\*\"", rules[0].ForbiddenPattern);
        Assert.Equal(new[] { "**/*.csproj" }, rules[0].Scope);
        Assert.Equal(new[] { "CM-6" }, rules[0].ControlRefs);
        Assert.Equal(RuleKind.Semantic, rules[1].Kind);
        Assert.Null(rules[1].ForbiddenPattern);
    }

    [Fact]
    public void Tolerates_Markdown_Fenced_Output()
    {
        var fenced = "Here are the rules:\n```json\n" + GoodJson + "\n```\n";
        var rules = new LlmRuleExtractor(new FakeModel(fenced)).Extract("0018", "x");
        Assert.Equal(2, rules.Count);
    }

    [Fact]
    public void Empty_Rules_Array_Is_Valid_Abstention()
    {
        var rules = new LlmRuleExtractor(new FakeModel("""{"rules":[]}""")).Extract("0099", "x");
        Assert.Empty(rules);
    }

    [Fact]
    public void No_Json_Throws_Format()
    {
        Assert.Throws<FormatException>(() => new LlmRuleExtractor(new FakeModel("I could not do that.")).Extract("0018", "x"));
    }

    [Fact]
    public void Extracted_Rules_Feed_Straight_Into_A_RuleSet()
    {
        var rules = new LlmRuleExtractor(new FakeModel(GoodJson)).Extract("0018", "x");
        var set = new AdrRuleSet { Adr = "0018", SourceSha = "sha256:abc", ExtractedBy = "fake/model", Rules = rules };

        var roundTripped = RuleLoader.Deserialize(RuleLoader.Serialize(set));
        Assert.Equal(2, roundTripped.Rules.Count);
        Assert.Equal(RuleKind.Deterministic, roundTripped.Rules[0].Kind);
    }
}
