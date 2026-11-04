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

    // ---- regex hardening (#26): identical extraction, linear time, timeouts are failures not empties ----

    [Theory]
    [InlineData("{\"rules\":[]}", "{\"rules\":[]}")]
    [InlineData("prose {\"a\":{\"b\":1}} trailing", "{\"a\":{\"b\":1}}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("```JSON  {\"a\":{\"b\":2}}  ```", "{\"a\":{\"b\":2}}")]
    [InlineData("```\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("<THINK>{x}</THINK>\n{\"a\":1}", "{\"a\":1}")]
    [InlineData("<think>{x}</think> mid <thinking>{y}</thinking> {\"a\":1}", "{\"a\":1}")]
    // Greedy fence capture spans to the last `}` before the last fence — pinned so the engine swap can't drift it.
    [InlineData("```json\n{\"a\":1}\n```\ntext\n```json\n{\"b\":2}\n```", "{\"a\":1}\n```\ntext\n```json\n{\"b\":2}")]
    // An unclosed think tag is left alone, exactly as before.
    [InlineData("<think>{x} {\"a\":1}", "{x} {\"a\":1}")]
    public void ExtractJsonObject_Output_Is_Unchanged_For_Normal_Responses(string raw, string expected)
    {
        Assert.Equal(expected, LlmRuleExtractor.ExtractJsonObject(raw));
    }

    [Fact]
    public void Pathological_Model_Output_Parses_Without_Hanging()
    {
        // ~1.8 MB of unterminated fences and unclosed think tags ending in a real answer. Quadratic under the
        // backtracking engine (measured ~7.7 s here — past the 2 s timeout), linear under NonBacktracking
        // (~12 ms); the fallback still finds the trailing object.
        var junk = string.Concat(Enumerable.Repeat("```json {\"x\": <think> ", 80_000));
        var raw = junk + "\n" + GoodJson;

        var json = LlmRuleExtractor.ExtractJsonObject(raw);

        Assert.StartsWith("{\"x\":", json);
        Assert.EndsWith("]}", json);
    }

    [Fact]
    public void Parse_Timeout_Surfaces_As_A_Distinguishable_Failure_Not_Empty_Rules()
    {
        // A valid response large enough that a 1-tick budget cannot cover it. It would extract cleanly given
        // time — so an empty result here would be the silent "conformance run looked clean" failure mode.
        var raw = "<think>" + new string('r', 4_000_000) + "</think>\n" + GoodJson;
        var extractor = new LlmRuleExtractor(new FakeModel(raw)) { MatchTimeout = TimeSpan.FromTicks(1) };

        var ex = Assert.Throws<ModelOutputParseTimeoutException>(() => extractor.Extract("0018", "x"));

        Assert.Equal(raw.Length, ex.ResponseLength);
        Assert.Contains(raw.Length.ToString("N0"), ex.Message);
        Assert.IsType<System.Text.RegularExpressions.RegexMatchTimeoutException>(ex.InnerException);
        Assert.IsAssignableFrom<FormatException>(ex);   // existing "unparseable output" handling still applies
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
