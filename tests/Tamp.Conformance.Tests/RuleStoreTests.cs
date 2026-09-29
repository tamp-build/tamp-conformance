using System.IO;
using Tamp;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class RuleStoreTests : IDisposable
{
    private readonly AbsolutePath _adrDir;

    public RuleStoreTests()
    {
        _adrDir = AbsolutePath.Create(Path.Combine(Path.GetTempPath(), $"tamp-adr-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_adrDir.Value);
    }

    public void Dispose() { try { Directory.Delete(_adrDir.Value, recursive: true); } catch { /* best effort */ } }

    private void WriteAdr(string name, string content) => File.WriteAllText(Path.Combine(_adrDir.Value, name), content);

    [Fact]
    public void AdrSummaries_Is_Title_Only()
    {
        WriteAdr("0019-agent-first.md", "# ADR 0019: Agent-first toolchain\n\n**Status:** Accepted — the `tamp mcp` control surface shipped.\n");
        var s = Assert.Single(RuleStore.AdrSummaries(_adrDir));
        Assert.Equal("0019: ADR 0019: Agent-first toolchain", s);
        Assert.DoesNotContain("mcp", s);   // title only — this is the gap AdrCoverageContext closes
    }

    [Fact]
    public void AdrCoverageContext_Includes_Body_So_Coverage_Is_Recognizable()
    {
        // The 0019/MCP false-positive case: the decision (mcp) is in the body/status, not the title.
        WriteAdr("0019-agent-first.md",
            "# ADR 0019: Agent-first toolchain\n\n**Status:** Accepted (2026-09-26) — delivered: the canonical BuildEvent model, typed results, and the `tamp mcp` control surface.\n\n## Context\nAgents need a machine surface.\n");
        var ctx = Assert.Single(RuleStore.AdrCoverageContext(_adrDir));
        Assert.StartsWith("0019: ADR 0019: Agent-first toolchain", ctx);
        Assert.Contains("tamp mcp", ctx);            // body pulled in → the coverage judge can now see it
        Assert.Contains("Context", ctx);
    }

    [Fact]
    public void AdrCoverageContext_Caps_The_Excerpt()
    {
        WriteAdr("0001-big.md", "# ADR 0001: Big\n\n" + new string('x', 5000) + "\n");
        var ctx = Assert.Single(RuleStore.AdrCoverageContext(_adrDir, maxExcerptChars: 200));
        Assert.True(ctx.Length < 400, $"excerpt should be bounded, was {ctx.Length}");
        Assert.EndsWith("…", ctx);
    }
}
