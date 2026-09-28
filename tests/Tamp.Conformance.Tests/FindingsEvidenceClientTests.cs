using System.Net;
using System.Net.Http;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class FindingsEvidenceClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Url;
        public string? Authorization;
        public string? ContentType;
        public string? Body;
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) { _json = json; _status = status; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(_status) { Content = new StringContent(_json) };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public void Posts_Ndjson_To_Ingest_Conformance_With_Bearer_And_Parses_Counts()
    {
        var handler = new CapturingHandler("""{"accepted":5,"skipped":2,"builds":["b1"]}""");
        using var http = new HttpClient(handler);
        var client = new FindingsEvidenceClient(http);

        var ndjson = "{\"type\":\"conformance.evaluated\",\"payload\":{\"$type\":\"conformance.evaluated\"}}\n{\"type\":\"target.finished\"}";
        var result = client.PostNdjson("https://findings.example/", "prj_abc", ndjson);

        Assert.Equal("https://findings.example/ingest/conformance", handler.Url);
        Assert.Equal("Bearer prj_abc", handler.Authorization);
        Assert.Equal("application/x-ndjson", handler.ContentType);
        Assert.Equal(ndjson, handler.Body);          // whole stream forwarded; findings filters
        Assert.Equal(5, result.Accepted);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(new[] { "b1" }, result.Builds);
    }

    [Fact]
    public void Surfaces_Error_Status()
    {
        var handler = new CapturingHandler("""{"error":"bad token"}""", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var client = new FindingsEvidenceClient(http);
        var ex = Assert.Throws<HttpRequestException>(() => client.PostNdjson("https://f", "nope", "{}"));
        Assert.Contains("401", ex.Message);
    }
}
