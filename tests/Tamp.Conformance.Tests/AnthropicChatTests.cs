using System.Net;
using System.Net.Http;
using Tamp.Conformance;
using Tamp.Conformance.Anthropic;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class AnthropicChatTests
{
    /// <summary>Captures the outgoing request and returns a canned response — no network.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Url;
        public string? ApiKey;
        public string? Version;
        public string? Body;

        private readonly string _json;
        private readonly HttpStatusCode _status;
        public CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) { _json = json; _status = status; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            ApiKey = request.Headers.TryGetValues("x-api-key", out var k) ? string.Concat(k) : null;
            Version = request.Headers.TryGetValues("anthropic-version", out var v) ? string.Concat(v) : null;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(_status) { Content = new StringContent(_json) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void Complete_Posts_To_Messages_Endpoint_With_Auth_And_Concatenates_Text_Blocks()
    {
        var handler = new CapturingHandler("""{"content":[{"type":"text","text":"hello "},{"type":"text","text":"world"}]}""");
        using var http = new HttpClient(handler);
        var chat = new AnthropicChat("sk-test-key", new ModelConfig { ModelId = "claude-opus-4-8", Endpoint = "https://gw.example.com/" }, http);

        var text = chat.Complete("sys", "usr");

        Assert.Equal("hello world", text);
        Assert.Equal("https://gw.example.com/v1/messages", handler.Url);   // endpoint override honored, /v1/messages appended
        Assert.Equal("sk-test-key", handler.ApiKey);
        Assert.Equal("2023-06-01", handler.Version);
        Assert.Contains("\"model\":\"claude-opus-4-8\"", handler.Body);
        Assert.Contains("\"max_tokens\":", handler.Body);                  // snake_case wire shape
        Assert.Equal("anthropic/claude-opus-4-8", chat.ModelId);          // provenance model id
    }

    [Fact]
    public void Non_Success_Surfaces_The_Api_Error_Body()
    {
        var handler = new CapturingHandler("""{"type":"error","error":{"message":"credit balance too low"}}""", HttpStatusCode.BadRequest);
        using var http = new HttpClient(handler);
        var chat = new AnthropicChat("k", new ModelConfig { ModelId = "claude-opus-4-8" }, http);

        var ex = Assert.Throws<HttpRequestException>(() => chat.Complete("s", "u"));
        Assert.Contains("credit balance too low", ex.Message);
    }
}
