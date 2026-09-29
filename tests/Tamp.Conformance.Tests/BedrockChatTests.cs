using System.Net;
using System.Net.Http;
using Tamp.Conformance;
using Tamp.Conformance.Bedrock;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class BedrockChatTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Url;
        public HttpMethod? Method;
        public string? Authorization;
        public string? AmzDate;
        public string? Body;
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) { _json = json; _status = status; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Method = request.Method;
            Authorization = request.Headers.TryGetValues("Authorization", out var a) ? string.Join("", a) : null;
            AmzDate = request.Headers.TryGetValues("x-amz-date", out var d) ? string.Join("", d) : null;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(_status) { Content = new StringContent(_json) };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private static AwsSigV4.Credentials Creds() => new()
    {
        AccessKeyId = "AKIDEXAMPLE",
        SecretAccessKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
        Region = "us-east-1",
    };

    [Fact]
    public void Complete_Posts_Signed_InvokeModel_And_Parses_Text()
    {
        var handler = new CapturingHandler("""{"content":[{"type":"text","text":"hello from claude"}]}""");
        using var http = new HttpClient(handler);
        var config = new ModelConfig { ModelId = "anthropic.claude-3-5-sonnet-20240620-v1:0", MaxTokens = 512 };
        var chat = new BedrockChat(Creds(), config, http, clock: () => new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

        var text = chat.Complete("you are a rule extractor", "extract rules");

        Assert.Equal("hello from claude", text);
        Assert.Equal(HttpMethod.Post, handler.Method);
        // model id is a path segment; the ":0" version suffix must be percent-encoded
        Assert.Equal("https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude-3-5-sonnet-20240620-v1%3A0/invoke", handler.Url);
        Assert.Contains("\"anthropic_version\":\"bedrock-2023-05-31\"", handler.Body);
        Assert.Contains("\"max_tokens\":512", handler.Body);
        Assert.Contains("extract rules", handler.Body);
        Assert.DoesNotContain("temperature", handler.Body);                 // null by default → dropped (opus-style models reject it)
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", handler.Authorization);
        Assert.Contains("/us-east-1/bedrock/aws4_request", handler.Authorization);
        Assert.Contains("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date", handler.Authorization);
        Assert.Equal("20260929T120000Z", handler.AmzDate);
        Assert.Equal("bedrock/anthropic.claude-3-5-sonnet-20240620-v1:0", chat.ModelId);
    }

    [Fact]
    public void Endpoint_Override_Is_Honored_For_Govcloud()
    {
        var handler = new CapturingHandler("""{"content":[]}""");
        using var http = new HttpClient(handler);
        var config = new ModelConfig { ModelId = "anthropic.claude-3-haiku-20240307-v1:0", MaxTokens = 100, Endpoint = "https://bedrock-runtime.us-gov-west-1.amazonaws.com" };
        new BedrockChat(Creds() with { Region = "us-gov-west-1" }, config, http).Complete("s", "u");
        Assert.StartsWith("https://bedrock-runtime.us-gov-west-1.amazonaws.com/model/", handler.Url);
    }

    [Fact]
    public void Session_Token_Adds_Security_Token_To_Signed_Headers()
    {
        var handler = new CapturingHandler("""{"content":[]}""");
        using var http = new HttpClient(handler);
        var config = new ModelConfig { ModelId = "m", MaxTokens = 10 };
        new BedrockChat(Creds() with { SessionToken = "FQoGZ...session" }, config, http).Complete("s", "u");
        Assert.Contains("x-amz-security-token", handler.Authorization);      // temporary creds signed correctly
    }
}
