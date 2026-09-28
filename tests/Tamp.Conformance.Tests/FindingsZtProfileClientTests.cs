using System.Net;
using System.Net.Http;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class FindingsZtProfileClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Url;
        public string? Authorization;
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) { _json = json; _status = status; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(_status) { Content = new StringContent(_json) };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private const string Body = """
        {"schemaVersion":"1.0","projectId":"p1","projectName":"Demo",
         "model":{"name":"CISA ZTMM","version":"2.0","pillars":[
           {"id":"identity","name":"Identity","functions":[
             {"id":"authentication","name":"Authentication","crossCutting":false,"stages":[
               {"stage":1,"descriptor":"passwords"},{"stage":4,"descriptor":"phishing-resistant"}]},
             {"id":"visibility","name":"Visibility & Analytics","crossCutting":true,"stages":[{"stage":1,"descriptor":"none"}]}]}]},
         "mandatePackVersion":"2026-09-28",
         "mandates":[
           {"id":"mfa","title":"MFA","applicabilityRule":"has-users","derivationRuleRef":"mfa","inForce":true},
           {"id":"machine-readable-attestation","title":"Machine-readable attestation","inForce":false}],
         "asOf":"2026-09-28T00:00:00Z"}
        """;

    [Fact]
    public void Gets_ZtProfile_Endpoint_With_Bearer_And_Parses_Model_And_Mandates()
    {
        var handler = new CapturingHandler(Body);
        using var http = new HttpClient(handler);
        var profile = new FindingsZtProfileClient(http).Get("https://findings.example/", "prj_abc");

        Assert.NotNull(profile);
        Assert.Equal("https://findings.example/projects/self/zt-profile", handler.Url);
        Assert.Equal("Bearer prj_abc", handler.Authorization);
        Assert.Equal("CISA ZTMM", profile!.Model.Name);
        Assert.Equal("2.0", profile.Model.Version);

        var identity = Assert.Single(profile.Model.Pillars!);
        Assert.Equal("identity", identity.Id);
        Assert.Equal(2, identity.Functions!.Count);
        Assert.True(identity.Functions!.Single(f => f.Id == "visibility").CrossCutting);   // cross-cutting flagged
        Assert.Equal(4, identity.Functions!.Single(f => f.Id == "authentication").Stages!.Max(s => s.Stage));

        Assert.Equal("2026-09-28", profile.MandatePackVersion);
        Assert.Equal(2, profile.Mandates!.Count);
        Assert.False(profile.Mandates!.Single(m => m.Id == "machine-readable-attestation").InForce);  // dormant (rescinded)
        Assert.True(profile.Mandates!.Single(m => m.Id == "mfa").InForce);
    }

    [Fact]
    public void Not_Zt_Scored_Returns_Null_On_404()
    {
        var handler = new CapturingHandler("""{"error":"not zt-scored"}""", HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);
        Assert.Null(new FindingsZtProfileClient(http).Get("https://f", "prj_x"));
    }

    [Fact]
    public void Other_Errors_Throw()
    {
        var handler = new CapturingHandler("""{"error":"bad token"}""", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        Assert.Throws<HttpRequestException>(() => new FindingsZtProfileClient(http).Get("https://f", "nope"));
    }
}
