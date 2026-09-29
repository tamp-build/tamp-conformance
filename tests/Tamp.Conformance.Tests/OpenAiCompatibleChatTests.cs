using System.Net;
using System.Net.Http;
using Tamp.Conformance;
using Tamp.Conformance.OpenAiCompatible;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class OpenAiCompatibleChatTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Url;
        public string? Authorization;
        public string? ApiKeyHeader;
        public string? Body;

        private readonly string _json;
        private readonly HttpStatusCode _status;
        public CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) { _json = json; _status = status; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.TryGetValues("Authorization", out var a) ? string.Concat(a) : null;
            ApiKeyHeader = request.Headers.TryGetValues("api-key", out var k) ? string.Concat(k) : null;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(_status) { Content = new StringContent(_json) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private const string OkResponse = """{"choices":[{"message":{"role":"assistant","content":"the answer"}}]}""";

    [Fact]
    public void Bearer_Default_Posts_To_ChatCompletions_And_Reads_Choice_Content()
    {
        var handler = new CapturingHandler(OkResponse);
        using var http = new HttpClient(handler);
        var chat = new OpenAiCompatibleChat("sk-x", new ModelConfig { ModelId = "gpt-x", Endpoint = "https://api.example.com/v1/" }, http);

        var text = chat.Complete("sys", "usr");

        Assert.Equal("the answer", text);
        Assert.Equal("https://api.example.com/v1/chat/completions", handler.Url);
        Assert.Equal("Bearer sk-x", handler.Authorization);
        Assert.Contains("\"model\":\"gpt-x\"", handler.Body);
        Assert.Contains("\"role\":\"system\"", handler.Body);
        Assert.Equal("openai/gpt-x", chat.ModelId);
    }

    [Fact]
    public void Azure_Style_Uses_Raw_ApiKey_Header()
    {
        var handler = new CapturingHandler(OkResponse);
        using var http = new HttpClient(handler);
        var chat = new OpenAiCompatibleChat("azkey", new ModelConfig { ModelId = "gpt-4o", Endpoint = "https://x.openai.azure.com/openai/v1" }, http,
            authHeader: "api-key", authPrefix: "", providerLabel: "azure");

        chat.Complete("s", "u");

        Assert.Equal("azkey", handler.ApiKeyHeader);
        Assert.Null(handler.Authorization);
        Assert.Equal("azure/gpt-4o", chat.ModelId);
    }

    [Fact]
    public void Tolerates_Extra_Reasoning_Content_Sibling_Poolside()
    {
        var handler = new CapturingHandler("""{"choices":[{"message":{"role":"assistant","reasoning_content":"thinking...","content":"final"}}]}""");
        using var http = new HttpClient(handler);
        var chat = OpenAiCompatibleChat.ForPoolside("pk", new ModelConfig { ModelId = "laguna-s-2.1" }, http);

        Assert.Equal("final", chat.Complete("s", "u"));
        Assert.Equal("https://inference.poolside.ai/v1/chat/completions", handler.Url);   // default hosted endpoint
        Assert.Equal("poolside/laguna-s-2.1", chat.ModelId);
    }

    [Fact]
    public void Truncation_Finish_Reason_Length_Throws_A_Clear_Error()
    {
        var handler = new CapturingHandler("""{"choices":[{"message":{"content":"{\"rules\":[{\"id\":"},"finish_reason":"length"}]}""");
        using var http = new HttpClient(handler);
        var chat = new OpenAiCompatibleChat("k", new ModelConfig { ModelId = "qwen3:14b", MaxTokens = 8192 }, http, providerLabel: "ollama");

        var ex = Assert.Throws<FormatException>(() => chat.Complete("s", "u"));
        Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_Success_Surfaces_Error_Body()
    {
        var handler = new CapturingHandler("""{"error":{"message":"model not found"}}""", HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);
        var chat = new OpenAiCompatibleChat("k", new ModelConfig { ModelId = "nope" }, http);

        var ex = Assert.Throws<HttpRequestException>(() => chat.Complete("s", "u"));
        Assert.Contains("model not found", ex.Message);
    }

    [Fact]
    public void Extracts_Rules_Through_The_Compatible_Adapter()
    {
        var handler = new CapturingHandler("""{"choices":[{"message":{"content":"{\"rules\":[{\"id\":\"0018-r1\",\"claim\":\"c\",\"kind\":\"deterministic\",\"forbiddenPattern\":\"x\"}]}"}}]}""");
        using var http = new HttpClient(handler);
        var chat = new OpenAiCompatibleChat("k", new ModelConfig { ModelId = "m" }, http);

        var rules = new LlmRuleExtractor(chat).Extract("0018", "adr text");

        Assert.Single(rules);
        Assert.Equal("0018-r1", rules[0].Id);
    }
}
