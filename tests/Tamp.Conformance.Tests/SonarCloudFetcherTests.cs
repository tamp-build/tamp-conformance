using System.Net;
using System.Net.Http;
using System.Text.Json;
using Tamp.Conformance.Quality;
using Xunit;

namespace Tamp.Conformance.Tests;

public class SonarCloudFetcherTests
{
    // Routes canned responses by URL substring and records the auth header it saw.
    private sealed class StubHandler : HttpMessageHandler
    {
        public string? AuthScheme;
        public string? AuthParam;
        public readonly List<string> Urls = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            AuthScheme = request.Headers.Authorization?.Scheme;
            AuthParam = request.Headers.Authorization?.Parameter;

            string body = url switch
            {
                _ when url.Contains("/api/measures/component") => """{"component":{"key":"tamp-build_tamp","measures":[{"metric":"alert_status","value":"ERROR"}]}}""",
                _ when url.Contains("/api/issues/search") => """{"total":2,"issues":[{"rule":"csharpsquid:S4036","severity":"MINOR","type":"VULNERABILITY","component":"tamp-build_tamp:src/A.cs","line":1,"message":"x"},{"rule":"csharpsquid:S2325","severity":"MAJOR","type":"CODE_SMELL","component":"tamp-build_tamp:src/B.cs","line":2,"message":"y"}]}""",
                _ when url.Contains("/api/hotspots/search") => """{"hotspots":[]}""",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Fetches_all_three_endpoints_with_bearer_auth_and_merges_issues()
    {
        var stub = new StubHandler();
        using var http = new HttpClient(stub);
        var fetcher = new SonarCloudFetcher(http, token: "tok123");

        var snap = await fetcher.FetchAsync("tamp-build_tamp", branch: "main");

        // Bearer auth on the requests
        Assert.Equal("Bearer", stub.AuthScheme);
        Assert.Equal("tok123", stub.AuthParam);

        // Hit all three endpoints, project key + branch encoded
        Assert.Contains(stub.Urls, u => u.Contains("/api/measures/component") && u.Contains("component=tamp-build_tamp") && u.Contains("branch=main"));
        Assert.Contains(stub.Urls, u => u.Contains("/api/issues/search") && u.Contains("componentKeys=tamp-build_tamp"));
        Assert.Contains(stub.Urls, u => u.Contains("/api/hotspots/search") && u.Contains("projectKey=tamp-build_tamp"));

        // Merged issues parse + route through the source
        var findings = snap.ToSource("A1").Read();
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.RuleId == "csharpsquid:S4036" && f.Bucket == QualityBucket.Sast);
        Assert.Contains(findings, f => f.RuleId == "csharpsquid:S2325" && f.Bucket == QualityBucket.Quality);

        // Gate verdict parses from the same snapshot
        Assert.Equal("fail", snap.ToGateVerdict("A1").Status);
    }

    [Fact]
    public async Task Merged_issues_json_is_wellformed_issues_array()
    {
        var stub = new StubHandler();
        using var http = new HttpClient(stub);
        var snap = await new SonarCloudFetcher(http, "t").FetchAsync("k");
        using var doc = JsonDocument.Parse(snap.IssuesJson);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("issues").ValueKind);
        Assert.Equal(2, doc.RootElement.GetProperty("issues").GetArrayLength());
    }
}
