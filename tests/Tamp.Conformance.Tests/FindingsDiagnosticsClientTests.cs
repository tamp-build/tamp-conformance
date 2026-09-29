using System.Net;
using System.Net.Http;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class FindingsDiagnosticsClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Url;
        public HttpMethod? Method;
        public string? Authorization;
        public string? ContentType;
        public string? Body;
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) { _json = json; _status = status; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(_status) { Content = new StringContent(_json) };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void Posts_Ndjson_To_Diagnostics_With_Bearer_And_Parses_Counts()
    {
        var handler = new CapturingHandler("""{"accepted":2,"skipped":5,"builds":["abc123"]}""");
        using var http = new HttpClient(handler);
        var client = new FindingsDiagnosticsClient(http);

        var ndjson = "{\"type\":\"diagnostic.emitted\",\"payload\":{\"$type\":\"diagnostic.emitted\",\"ruleId\":\"undocumented-decision:new-dependency\"}}\n";
        var result = client.PostNdjson("https://findings.example/", "prj_abc", ndjson);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://findings.example/ingest/diagnostics", handler.Url);   // separate from /ingest/conformance
        Assert.Equal("Bearer prj_abc", handler.Authorization);
        Assert.Equal("application/x-ndjson", handler.ContentType);
        Assert.Equal(2, result.Accepted);
        Assert.Equal(5, result.Skipped);
        Assert.Equal(new[] { "abc123" }, result.Builds);
    }

    [Fact]
    public void Non_Success_Throws_With_Body()
    {
        var handler = new CapturingHandler("""{"error":"bad token"}""", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var ex = Assert.Throws<HttpRequestException>(() => new FindingsDiagnosticsClient(http).PostNdjson("https://f", "nope", "x"));
        Assert.Contains("401", ex.Message);
    }
}
