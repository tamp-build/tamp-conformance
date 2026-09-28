using System.IO;
using Tamp;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class LlmSemanticEvaluatorTests : IDisposable
{
    private readonly AbsolutePath _root;
    private readonly AbsolutePath _file;

    public LlmSemanticEvaluatorTests()
    {
        _root = AbsolutePath.Create(Path.Combine(Path.GetTempPath(), $"tamp-conf-sem-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_root.Value);
        var f = Path.Combine(_root.Value, "Ui.cs");
        File.WriteAllText(f, "class Ui { /* a WebView-based UI */ }");
        _file = AbsolutePath.Create(f);
    }

    public void Dispose() { try { Directory.Delete(_root.Value, recursive: true); } catch { /* best effort */ } }

    /// <summary>Fake model: returns an eval response, and a verify response when the system prompt is the adversarial one.</summary>
    private sealed class FakeModel : IChatCompletion
    {
        private readonly string _eval;
        private readonly string _verify;
        public FakeModel(string eval, string verify = """{"confirmed":false,"reason":"n/a"}""") { _eval = eval; _verify = verify; }
        public string ModelId => "fake/model";
        public string Complete(string system, string user) => system.Contains("adversarial") ? _verify : _eval;
    }

    private static readonly AdrRuleSet Set = new()
    {
        Adr = "0001", SourceSha = "sha256:x",
        Rules = new[] { new AdrRule { Id = "0001-r1", Claim = "the UI is native, not a web app", Kind = RuleKind.Semantic, ControlRefs = new[] { "SA-8" } } },
    };

    private ConformanceResult Eval(FakeModel model, bool verify = true)
        => new LlmSemanticEvaluator(model, verify: verify).Evaluate(Set, Set.Rules[0], new[] { _file });

    [Fact]
    public void Pass_Verdict_Stands_Without_Verify()
    {
        var r = Eval(new FakeModel("""{"verdict":"pass"}"""));
        Assert.Equal(ConformanceVerdict.Pass, r.Verdict);
        Assert.False(r.Blocks);
    }

    [Fact]
    public void Fail_With_Evidence_Confirmed_By_Verify_Is_Fail()
    {
        var eval = """{"verdict":"fail","file":"Ui.cs","line":1,"codeEvidence":"a WebView-based UI","adrQuote":"the UI is native, not a web app"}""";
        var r = Eval(new FakeModel(eval, """{"confirmed":true,"reason":"WebView is a web UI"}"""));

        Assert.Equal(ConformanceVerdict.Fail, r.Verdict);
        Assert.Equal(ConformanceMethod.Verify, r.Method);   // verified
        Assert.Equal("Ui.cs", r.File);
        Assert.Equal(new[] { "SA-8" }, r.ControlRefs);
    }

    [Fact]
    public void Fail_Not_Confirmed_By_Verify_Downgrades_To_Unknown()
    {
        var eval = """{"verdict":"fail","file":"Ui.cs","line":1,"codeEvidence":"a WebView-based UI","adrQuote":"the UI is native, not a web app"}""";
        var r = Eval(new FakeModel(eval, """{"confirmed":false,"reason":"comment only, not real UI code"}"""));

        Assert.Equal(ConformanceVerdict.Unknown, r.Verdict);   // adversarial pass rescued a false positive
        Assert.Equal(ConformanceMethod.Verify, r.Method);
    }

    [Fact]
    public void Fail_Without_Quotable_Evidence_Is_Unknown_Not_Fail()
    {
        var r = Eval(new FakeModel("""{"verdict":"fail","codeEvidence":null,"adrQuote":null}"""));
        Assert.Equal(ConformanceVerdict.Unknown, r.Verdict);   // can't quote both sides -> not a fail
    }

    [Fact]
    public void Unknown_Eval_Stays_Unknown()
    {
        var r = Eval(new FakeModel("""{"verdict":"unknown"}"""));
        Assert.Equal(ConformanceVerdict.Unknown, r.Verdict);
    }

    [Fact]
    public void No_In_Scope_Files_Is_Unknown()
    {
        var r = new LlmSemanticEvaluator(new FakeModel("""{"verdict":"pass"}""")).Evaluate(Set, Set.Rules[0], Array.Empty<AbsolutePath>());
        Assert.Equal(ConformanceVerdict.Unknown, r.Verdict);
    }

    [Fact]
    public void Verify_Disabled_Keeps_A_Well_Evidenced_Fail()
    {
        var eval = """{"verdict":"fail","file":"Ui.cs","line":1,"codeEvidence":"a WebView-based UI","adrQuote":"the UI is native, not a web app"}""";
        var r = Eval(new FakeModel(eval), verify: false);
        Assert.Equal(ConformanceVerdict.Fail, r.Verdict);
        Assert.Equal(ConformanceMethod.Semantic, r.Method);
    }
}
