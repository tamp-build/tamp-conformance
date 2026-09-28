namespace Tamp.Conformance;

/// <summary>
/// Fetches a project's compliance profile from tamp-findings, authenticated by the project's ingest token
/// (the same token the evidence flows through — so the framework used for extraction is, by construction,
/// the framework the evidence will be attested under). The contract mirrors
/// <c>GET /api/v1/projects/self/compliance-profile</c>. Kept an interface so <see cref="ControlProfileSource"/>
/// is testable without a live findings instance.
/// </summary>
public interface IComplianceProfileClient
{
    ComplianceProfile Get(string endpoint, string ingestToken);
}

/// <summary>The project compliance profile returned by tamp-findings (subset consumed by conformance).</summary>
public sealed record ComplianceProfile
{
    public string? ProjectId { get; init; }
    public required FrameworkInfo Framework { get; init; }
    public IReadOnlyList<ControlInfo>? Controls { get; init; }
}

public sealed record FrameworkInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
}

public sealed record ControlInfo
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Family { get; init; }
}
