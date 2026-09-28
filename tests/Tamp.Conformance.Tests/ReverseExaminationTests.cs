using System.IO;
using Tamp;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class ReverseExaminationTests : IDisposable
{
    private readonly AbsolutePath _root;
    public ReverseExaminationTests()
    {
        _root = AbsolutePath.Create(Path.Combine(Path.GetTempPath(), $"tamp-conf-rev-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_root.Value);
    }
    public void Dispose() { try { Directory.Delete(_root.Value, recursive: true); } catch { /* best effort */ } }

    private AbsolutePath Write(string rel, string content)
    {
        var full = Path.Combine(_root.Value, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return AbsolutePath.Create(full);
    }

    private sealed class FakeModel : IChatCompletion
    {
        private readonly string _response;
        public FakeModel(string response) => _response = response;
        public string ModelId => "fake/model";
        public string Complete(string system, string user) => _response;
    }

    // ---- deterministic triggers ----

    [Fact]
    public void Triggers_Flags_Signal_Matches_With_File_And_Line()
    {
        var f = Write("src/App.csproj", "<Project>\n  <PackageReference Include=\"Newtonsoft.Json\" />\n</Project>\n");
        var signals = new Dictionary<string, string> { ["new-dependency"] = "<PackageReference\\s+Include=" };

        var candidates = ReverseExamination.Triggers(_root, new[] { f }, signals);

        var c = Assert.Single(candidates);
        Assert.Equal("new-dependency", c.Kind);
        Assert.Equal("src/App.csproj", c.File);
        Assert.Equal(2, c.Line);
    }

    // ---- detector parsing ----

    [Fact]
    public void Detector_Returns_Only_Model_Selected_Decisions()
    {
        var detector = new LlmDecisionDetector(new FakeModel(
            """{"decisions":[{"kind":"new-dependency","summary":"adds a JSON lib with no ADR","file":"src/App.csproj","line":2}]}"""));
        var candidates = new[] { new DecisionCandidate { Kind = "new-dependency", Detail = "Newtonsoft", File = "src/App.csproj", Line = 2 } };

        var decisions = detector.Detect(candidates, new[] { "0018: Diagnostics emission contract" });

        var d = Assert.Single(decisions);
        Assert.Equal("new-dependency", d.Kind);
        Assert.Contains("JSON lib", d.Summary);
    }

    [Fact]
    public void Detector_Abstains_Empty_Is_Valid()
    {
        var detector = new LlmDecisionDetector(new FakeModel("""{"decisions":[]}"""));
        var candidates = new[] { new DecisionCandidate { Kind = "x", Detail = "routine", File = "a", Line = 1 } };
        Assert.Empty(detector.Detect(candidates, Array.Empty<string>()));
    }

    [Fact]
    public void Detector_Short_Circuits_With_No_Candidates()
    {
        // No model call needed when there are no candidates.
        var decisions = new LlmDecisionDetector(new FakeModel("SHOULD NOT BE PARSED")).Detect(Array.Empty<DecisionCandidate>(), Array.Empty<string>());
        Assert.Empty(decisions);
    }

    // ---- end-to-end orchestration ----

    [Fact]
    public void Detect_Runs_Triggers_Then_Detector()
    {
        Write("src/App.csproj", "<Project>\n  <PackageReference Include=\"Grpc\" />\n</Project>\n");
        var files = RuleStore.CodeFiles(_root, Array.Empty<string>());
        var signals = new Dictionary<string, string> { ["new-dependency"] = "<PackageReference\\s+Include=" };
        var detector = new LlmDecisionDetector(new FakeModel(
            """{"decisions":[{"kind":"new-dependency","summary":"introduces gRPC transport with no ADR","file":"src/App.csproj","line":2}]}"""));

        var decisions = ReverseExamination.Detect(_root, files, signals, detector, new[] { "0018: Diagnostics" }, emit: false);

        Assert.Single(decisions);
        Assert.Contains("gRPC", decisions[0].Summary);
    }

    [Fact]
    public void Detect_No_Triggers_Skips_The_Model()
    {
        Write("docs/notes.md", "nothing architectural here");
        var files = RuleStore.CodeFiles(_root, Array.Empty<string>());
        var signals = new Dictionary<string, string> { ["new-dependency"] = "<PackageReference\\s+Include=" };
        var detector = new LlmDecisionDetector(new FakeModel("SHOULD NOT BE PARSED"));

        Assert.Empty(ReverseExamination.Detect(_root, files, signals, detector, Array.Empty<string>(), emit: false));
    }

    // ---- ADR summaries helper ----

    [Fact]
    public void AdrSummaries_Reads_The_First_Heading()
    {
        Write("docs/adr/0018-diagnostics.md", "# ADR 0018: Diagnostics emission contract\n\nbody");
        Write("docs/adr/0020-components.md", "# ADR 0020: Tamp.Components\n");
        Write("docs/adr/README.md", "index");

        var summaries = RuleStore.AdrSummaries(AbsolutePath.Create(Path.Combine(_root.Value, "docs", "adr")));

        Assert.Equal(2, summaries.Count);
        Assert.Contains("0018: ADR 0018: Diagnostics emission contract", summaries);
        Assert.Contains("0020: ADR 0020: Tamp.Components", summaries);
    }
}
