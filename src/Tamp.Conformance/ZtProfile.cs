namespace Tamp.Conformance;

/// <summary>
/// A project's Zero Trust profile served by tamp-findings (<c>GET /projects/self/zt-profile</c>): the CISA
/// ZTMM model to score against (pillars → functions → stages) plus the project's operational mandate set.
/// findings is the system of record (see ADR 0003); the tooling fetches this, derives per-function /
/// per-mandate evidence, and posts it back — findings scores.
/// </summary>
public sealed record ZtProfile
{
    public string? SchemaVersion { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public required ZtModel Model { get; init; }

    /// <summary>The mandate-pack version this profile was served against — frozen into evidence for point-in-time.</summary>
    public string? MandatePackVersion { get; init; }

    /// <summary>Operational mandates only (encryption / MFA / IPv6 / logging-maturity); supply-chain stays findings-side.</summary>
    public IReadOnlyList<ZtMandate>? Mandates { get; init; }

    public string? AsOf { get; init; }
}

public sealed record ZtModel
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public IReadOnlyList<ZtPillar>? Pillars { get; init; }
}

public sealed record ZtPillar
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<ZtFunction>? Functions { get; init; }
}

public sealed record ZtFunction
{
    public required string Id { get; init; }
    public string? Name { get; init; }

    /// <summary>A cross-cutting capability (Visibility &amp; Analytics / Automation / Governance) folded per-pillar; scored like any function.</summary>
    public bool CrossCutting { get; init; }

    public IReadOnlyList<ZtStage>? Stages { get; init; }
}

public sealed record ZtStage
{
    public int Stage { get; init; }
    public string? Descriptor { get; init; }
}

public sealed record ZtMandate
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? ApplicabilityRule { get; init; }
    public string? DerivationRuleRef { get; init; }

    /// <summary>False when the crosswalked EO directive is rescinded (emit-but-flag-dormant); skip such mandates when scoring.</summary>
    public bool InForce { get; init; } = true;
}
