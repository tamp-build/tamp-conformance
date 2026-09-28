using System.Net;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class FindingsAdrRulesClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Url;
        public HttpMethod? Method;
        public string? Authorization;
        public string? Body;
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) { _json = json; _status = status; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return new HttpResponseMessage(_status) { Content = new StringContent(_json) };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private static AdrRuleWithRef DetRule() => new()
    {
        AdrRef = "0018",
        Rule = new AdrRule
        {
            Id = "0018-r1", Claim = "no telemetry lib in core", Kind = RuleKind.Deterministic,
            ForbiddenPattern = "OpenTelemetry", Scope = new[] { "**/Tamp.Core/**/*.csproj" }, ControlRefs = new[] { "CM-6" },
            ZtPillar = "Applications & Workloads", ZtFunction = "Application Access", ZtStage = 3, ReviewStatus = "Reviewed",
        },
    };

    [Fact]
    public void Push_Posts_Generation_With_Bearer_And_Parses_Counts()
    {
        var handler = new CapturingHandler("""{"upserted":2,"retired":1,"active":5}""");
        using var http = new HttpClient(handler);
        var client = new FindingsAdrRulesClient(http);

        var result = client.PushGeneration("https://findings.example/", "prj_abc", "anthropic/claude-opus-4-8", new[] { DetRule() });

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://findings.example/projects/self/adr-rules", handler.Url);
        Assert.Equal("Bearer prj_abc", handler.Authorization);
        Assert.Contains("\"extractionModelId\":\"anthropic/claude-opus-4-8\"", handler.Body);
        Assert.Contains("\"ruleId\":\"0018-r1\"", handler.Body);
        Assert.Contains("\"ztStage\":3", handler.Body);
        Assert.Contains("checkSpec", handler.Body);                 // our check def carried opaque
        Assert.Contains("OpenTelemetry", handler.Body);             // inside the checkSpec string
        Assert.Equal(2, result.Upserted);
        Assert.Equal(1, result.Retired);
        Assert.Equal(5, result.Active);
    }

    [Fact]
    public void Fetch_Gets_Active_Set_And_Maps_Back_To_Local_Rules()
    {
        var checkSpec = JsonSerializer.Serialize(new { forbiddenPattern = "OpenTelemetry", scope = new[] { "**/*.csproj" } });
        var body = JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            projectId = "p1",
            rules = new[]
            {
                new { adrRef = "0018", ruleId = "0018-r1", intent = "no telemetry lib", method = "deterministic",
                      checkSpec, controlRefs = new[] { "CM-6" }, ztPillar = "Data", ztFunction = "Data Encryption",
                      ztStage = 2, mandateId = (string?)null, rulesSha = "sha256:x", reviewStatus = "Reviewed" },
            },
        });
        var handler = new CapturingHandler(body);
        using var http = new HttpClient(handler);

        var rules = new FindingsAdrRulesClient(http).FetchActive("https://findings.example/", "prj_abc");

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("https://findings.example/projects/self/adr-ruleset", handler.Url);
        var r = Assert.Single(rules);
        Assert.Equal("0018", r.AdrRef);
        Assert.Equal("0018-r1", r.Rule.Id);
        Assert.Equal(RuleKind.Deterministic, r.Rule.Kind);
        Assert.Equal("OpenTelemetry", r.Rule.ForbiddenPattern);     // checkSpec parsed back
        Assert.Equal(new[] { "**/*.csproj" }, r.Rule.Scope);
        Assert.Equal("Data", r.Rule.ZtPillar);
        Assert.Equal(2, r.Rule.ZtStage);
        Assert.Equal("Reviewed", r.Rule.ReviewStatus);
    }

    [Fact]
    public void Fetch_Not_Found_Returns_Empty()
    {
        var handler = new CapturingHandler("""{"error":"no ruleset"}""", HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);
        Assert.Empty(new FindingsAdrRulesClient(http).FetchActive("https://f", "prj_x"));
    }

    [Fact]
    public void Rule_Survives_A_Wire_Round_Trip()
    {
        // ToWire then FromWire preserves the check + annotations (checkSpec is opaque to findings but symmetric for us).
        var original = DetRule();
        var wire = RuleWire.ToWire(original);
        var back = RuleWire.FromWire(wire);

        Assert.Equal(original.AdrRef, back.AdrRef);
        Assert.Equal(original.Rule.Id, back.Rule.Id);
        Assert.Equal(original.Rule.ForbiddenPattern, back.Rule.ForbiddenPattern);
        Assert.Equal(original.Rule.Scope, back.Rule.Scope);
        Assert.Equal(original.Rule.ZtPillar, back.Rule.ZtPillar);
        Assert.Equal(original.Rule.ZtStage, back.Rule.ZtStage);
        Assert.Equal(RuleKind.Deterministic, back.Rule.Kind);
    }
}
