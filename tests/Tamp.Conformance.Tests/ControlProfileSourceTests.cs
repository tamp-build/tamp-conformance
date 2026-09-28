using System.Net;
using System.Net.Http;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class ControlProfileSourceTests
{
    private sealed class FakeClient : IComplianceProfileClient
    {
        public string? SeenEndpoint;
        public string? SeenToken;
        private readonly ComplianceProfile _profile;
        public FakeClient(ComplianceProfile profile) => _profile = profile;
        public ComplianceProfile Get(string endpoint, string ingestToken)
        {
            SeenEndpoint = endpoint; SeenToken = ingestToken;
            return _profile;
        }
    }

    // ---- mutual exclusivity (fail-closed) ----

    [Fact]
    public void FromConfig_Throws_When_Both_Tracks_Set()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ControlProfileSource.FromConfig(manualFramework: "SOC 2", findingsEndpoint: "https://f", ingestToken: "t"));
        Assert.Contains("EITHER", ex.Message);
    }

    [Fact]
    public void FromConfig_Throws_When_Neither_Track_Set()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ControlProfileSource.FromConfig(manualFramework: null, findingsEndpoint: null, ingestToken: null));
        Assert.Contains("unset", ex.Message);
    }

    [Fact]
    public void FromConfig_Throws_When_Findings_Missing_Token()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ControlProfileSource.FromConfig(manualFramework: null, findingsEndpoint: "https://f", ingestToken: null));
    }

    // ---- manual track ----

    [Fact]
    public void Manual_Resolves_The_Given_Framework()
    {
        var resolved = ControlProfileSource.Manual("SOC 2", "[\"CC8.1\"]", "(Common: CC6.x, CC7.x, CC8.1)").Resolve();
        Assert.Equal("SOC 2", resolved.Framework);
        Assert.Equal("[\"CC8.1\"]", resolved.Example);
        Assert.Contains("CC8.1", resolved.CatalogueHint);
    }

    // ---- findings track ----

    [Fact]
    public void FromFindings_Resolves_Framework_And_Controls_Via_The_Client()
    {
        var client = new FakeClient(new ComplianceProfile
        {
            ProjectId = "p1",
            Framework = new FrameworkInfo { Id = "iso-27001", Name = "ISO 27001", Version = "2022" },
            Controls = new[] { new ControlInfo { Id = "A.8.9", Title = "Configuration management", Family = "A.8" } },
        });

        var resolved = ControlProfileSource.FromFindings("https://findings.example/", "ingest-tok", client).Resolve();

        Assert.Equal("ISO 27001", resolved.Framework);
        Assert.Equal("iso-27001", resolved.FrameworkId);
        Assert.Equal("[\"A.8.9\"]", resolved.Example);        // example seeded from the project's first control
        Assert.Contains("A.8.9", resolved.CatalogueHint);
        Assert.Equal("https://findings.example/", client.SeenEndpoint);
        Assert.Equal("ingest-tok", client.SeenToken);          // ingest token forwarded
    }

    [Fact]
    public void Resolved_Profile_Merges_Into_ExtractionProfile_Keeping_Project_Context()
    {
        var resolved = ControlProfileSource.Manual("SOC 2", "[\"CC8.1\"]").Resolve();
        var baseProfile = new ExtractionProfile { ProjectContext = "Terraform monorepo", StackConventions = "modules/**" };

        var merged = resolved.ToExtractionProfile(baseProfile);

        Assert.Equal("SOC 2", merged.ControlFramework);
        Assert.Equal("[\"CC8.1\"]", merged.ControlExample);
        Assert.Equal("Terraform monorepo", merged.ProjectContext);   // project context preserved
    }

    // ---- prompt reflects the profile ----

    [Fact]
    public void Extractor_Prompt_Uses_The_Resolved_Framework_And_Context()
    {
        var profile = ControlProfileSource.Manual("SOC 2", "[\"CC8.1\"]", "(Common: CC8.1)").Resolve()
            .ToExtractionProfile(new ExtractionProfile { ProjectContext = "This is a Terraform/IaC repo." });
        var prompt = new LlmRuleExtractor(new StubModel(), profile).BuildSystemPrompt();

        Assert.Contains("SOC 2 control ids", prompt);
        Assert.Contains("[\"CC8.1\"]", prompt);
        Assert.Contains("(Common: CC8.1)", prompt);
        Assert.Contains("Terraform/IaC", prompt);
        Assert.DoesNotContain("NIST 800-53", prompt);            // no leftover default
    }

    // ---- HTTP client shape ----

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

    [Fact]
    public void FindingsClient_Gets_Self_Endpoint_With_Bearer_And_Parses()
    {
        var handler = new CapturingHandler("""{"projectId":"p1","framework":{"id":"soc2","name":"SOC 2","version":"2017"},"controls":[{"id":"CC8.1","title":"Change mgmt","family":"CC8"}]}""");
        using var http = new HttpClient(handler);
        var client = new FindingsComplianceProfileClient(http);

        var profile = client.Get("https://findings.example/", "tok123");

        Assert.Equal("https://findings.example/api/v1/projects/self/compliance-profile", handler.Url);
        Assert.Equal("Bearer tok123", handler.Authorization);
        Assert.Equal("SOC 2", profile.Framework.Name);
        Assert.Equal("CC8.1", profile.Controls![0].Id);
    }

    [Fact]
    public void FindingsClient_Surfaces_Error_Status()
    {
        var handler = new CapturingHandler("""{"error":"forbidden"}""", HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler);
        var client = new FindingsComplianceProfileClient(http);
        var ex = Assert.Throws<HttpRequestException>(() => client.Get("https://f", "bad"));
        Assert.Contains("403", ex.Message);
    }

    private sealed class StubModel : IChatCompletion
    {
        public string ModelId => "stub/model";
        public string Complete(string system, string user) => """{"rules":[]}""";
    }
}
