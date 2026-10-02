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
    public void Strips_Think_Blocks_Whose_Braces_Would_Fool_The_Scan()
    {
        // A thinking model emits reasoning (with braces!) before the answer — the naive outermost-{...} would grab it.
        var thinky = "<think>I should emit {rules} with an array. Let me consider {this} and {that}.</think>\n" + GoodJson;
        var rules = new LlmRuleExtractor(new FakeModel(thinky)).Extract("0018", "x");
        Assert.Equal(2, rules.Count);
        Assert.Equal("0018-r1", rules[0].Id);
    }

    [Fact]
    public void Strips_Think_Blocks_Even_Inside_A_Fence()
    {
        var thinkyFenced = "<thinking>plan: {a:1}</thinking>\n```json\n" + GoodJson + "\n```";
        var rules = new LlmRuleExtractor(new FakeModel(thinkyFenced)).Extract("0018", "x");
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
    public void System_Prompt_Carries_The_Robustness_Guidance()
    {
        // Regression guard for the anti-brittleness spine (the fix for tamp-core's 10 rule-quality
        // conformance fails: over-literal regexes, wrong scope, prose-as-pattern, aspirational markers).
        var prompt = new LlmRuleExtractor(new FakeModel("{\"rules\":[]}")).BuildSystemPrompt();

        // Deterministic is gated on robustness, not the default.
        Assert.Contains("ROBUSTNESS", prompt);
        // Patterns are regexes, never English prose.
        Assert.Contains("NEVER an English", prompt);
        // Account for C# idiom variance (target-typed new, intermediate bases).
        Assert.Contains("target-typed", prompt);
        Assert.Contains("INTERMEDIATE base", prompt);
        // Scope precisely / exclude build+generated.
        Assert.Contains("src/**", prompt);
        // Defining repo is not a consumer.
        Assert.Contains("not a CONSUMER", prompt);
        // No aspirational/forward-looking deterministic rules.
        Assert.Contains("aspirational", prompt);
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
