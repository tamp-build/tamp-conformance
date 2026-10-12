using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tamp.Conformance;
using Tamp.Conformance.Anthropic;
using Tamp.Conformance.Bedrock;
using Tamp.Conformance.OpenAiCompatible;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class TokenUsageTests
{
    private sealed class Canned : HttpMessageHandler
    {
        private readonly string _json;
        public Canned(string json) => _json = json;
        // Adapters call HttpClient.Send (synchronous), so Send must be overridden, not just SendAsync.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
            => new(HttpStatusCode.OK) { Content = new StringContent(_json) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Send(request, ct));
    }

    private static HttpClient Http(string json) => new(new Canned(json));

    // ---- adapters surface provider usage (#12 scope 1) ----

    [Fact]
    public void Anthropic_Surfaces_Input_Output_And_Cache_Tokens()
    {
        using var http = Http("""{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1200,"output_tokens":340,"cache_read_input_tokens":800,"cache_creation_input_tokens":50}}""");
        var chat = new AnthropicChat("k", new ModelConfig { ModelId = "claude-opus-4-8" }, http);
        var r = chat.CompleteWithUsage("s", "u");
        Assert.Equal("ok", r.Text);
        Assert.Equal(1200, r.Usage.InputTokens);
        Assert.Equal(340, r.Usage.OutputTokens);
        Assert.Equal(800, r.Usage.CacheReadTokens);
        Assert.Equal(50, r.Usage.CacheWriteTokens);
        Assert.Equal(1540, r.Usage.TotalTokens);
    }

    [Fact]
    public void OpenAiCompatible_Surfaces_Prompt_And_Completion_Tokens()
    {
        using var http = Http("""{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":90,"completion_tokens":12,"total_tokens":102}}""");
        var chat = new OpenAiCompatibleChat("k", new ModelConfig { ModelId = "gpt-x" }, http);
        var r = chat.CompleteWithUsage("s", "u");
        Assert.Equal(90, r.Usage.InputTokens);
        Assert.Equal(12, r.Usage.OutputTokens);
    }

    [Fact]
    public void Bedrock_Surfaces_Input_Output_Tokens()
    {
        using var http = Http("""{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":77,"output_tokens":8}}""");
        var chat = new BedrockChat(
            new AwsSigV4.Credentials { AccessKeyId = "a", SecretAccessKey = "b", Region = "us-east-1" },
            new ModelConfig { ModelId = "anthropic.claude-3-5-sonnet-20240620-v1:0" }, http);
        var r = chat.CompleteWithUsage("s", "u");
        Assert.Equal(77, r.Usage.InputTokens);
        Assert.Equal(8, r.Usage.OutputTokens);
    }

    [Fact]
    public void Missing_Usage_Yields_None_Not_An_Error()
    {
        using var http = Http("""{"choices":[{"message":{"content":"ok"}}]}""");
        var chat = new OpenAiCompatibleChat("k", new ModelConfig { ModelId = "gpt-x" }, http);
        Assert.Equal(ModelUsage.None, chat.CompleteWithUsage("s", "u").Usage);
    }

    [Fact]
    public void Default_Interface_Method_Keeps_Legacy_Implementors_Working()
    {
        IChatCompletion legacy = new TextOnlyChat();
        var r = legacy.CompleteWithUsage("s", "u"); // uses the default → text + None usage
        Assert.Equal("legacy-text", r.Text);
        Assert.Equal(ModelUsage.None, r.Usage);
    }

    private sealed class TextOnlyChat : IChatCompletion
    {
        public string Complete(string system, string user) => "legacy-text";
        public string ModelId => "fake/legacy";
    }

    // ---- aggregation (#12 scope 2) ----

    [Fact]
    public void Accumulator_Rolls_Up_Per_Capability_And_Model()
    {
        var acc = new UsageAccumulator();
        acc.Record("generate", "anthropic/claude-opus-4-8", new ModelUsage(100, 50), latencyMs: 1200);
        acc.Record("generate", "anthropic/claude-opus-4-8", new ModelUsage(10, 5), latencyMs: 300);
        acc.Record("evaluate", "anthropic/claude-opus-4-8", new ModelUsage(1, 1));

        var totals = acc.Totals;
        Assert.Equal(2, totals.Count);
        var gen = totals.Single(t => t.Capability == "generate");
        Assert.Equal(2, gen.Calls);
        Assert.Equal(110, gen.Usage.InputTokens);
        Assert.Equal(55, gen.Usage.OutputTokens);
        Assert.Equal(1500, gen.LatencyMs);
        Assert.Equal(1, totals.Single(t => t.Capability == "evaluate").Calls);
    }

    // ---- cost (#12 scope 3) — tokens are facts, price is config/policy ----

    [Fact]
    public void Pricing_Estimates_Known_Model()
    {
        // opus 4-8 default = $15/M in, $75/M out.
        var cost = ModelPricing.Default.EstimateUsd("anthropic/claude-opus-4-8", new ModelUsage(1_000, 500));
        Assert.Equal(0.0525m, cost); // 1000/1e6*15 + 500/1e6*75
    }

    [Fact]
    public void Pricing_Unknown_Model_Is_Null_Never_Guessed()
    {
        Assert.Null(ModelPricing.Default.EstimateUsd("mystery/model-z", new ModelUsage(1000, 1000)));
    }

    [Fact]
    public void Pricing_Local_Model_Counts_Tokens_At_Zero_Dollars()
    {
        var local = new ModelPricing(new Dictionary<string, ModelPrice> { ["local/ollama-qwen"] = new(0m, 0m) }, asOf: "2026-10-12");
        var usage = new ModelUsage(5000, 2000);
        Assert.Equal(0m, local.EstimateUsd("local/ollama-qwen", usage)); // $0 but still counted (tokens known)
        Assert.Equal(7000, usage.TotalTokens);
    }
}
