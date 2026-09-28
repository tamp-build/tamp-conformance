namespace Tamp.Conformance;

/// <summary>
/// Where the control framework (and control catalogue) for rule extraction comes from — one of two
/// <b>mutually exclusive</b> tracks:
/// <list type="bullet">
/// <item><b>Manual</b> — the framework is specified in config. For standalone use / exporting evidence
///   somewhere other than tamp-findings.</item>
/// <item><b>FromFindings</b> — the framework is fetched from tamp-findings using the project's ingest
///   token, so it matches the exact project the evidence lands in (no drift). The integrated norm.</item>
/// </list>
/// <see cref="FromConfig"/> enforces the exclusivity fail-closed: exactly one track must be configured.
/// </summary>
public sealed class ControlProfileSource
{
    private readonly ManualSpec? _manual;
    private readonly FindingsSpec? _findings;

    private ControlProfileSource(ManualSpec? manual, FindingsSpec? findings)
    {
        _manual = manual;
        _findings = findings;
    }

    /// <summary>Specify the framework directly (standalone / export-elsewhere track).</summary>
    public static ControlProfileSource Manual(string framework, string? controlExample = null, string? catalogueHint = null)
        => new(new ManualSpec(framework, controlExample, catalogueHint), null);

    /// <summary>Fetch the framework from tamp-findings via the project's ingest token (integrated track).</summary>
    public static ControlProfileSource FromFindings(string endpoint, string ingestToken, IComplianceProfileClient? client = null)
        => new(null, new FindingsSpec(endpoint, ingestToken, client ?? new FindingsComplianceProfileClient()));

    /// <summary>
    /// Build from (env-style) config, enforcing mutual exclusivity: set EITHER a manual framework OR a
    /// findings endpoint+token — never both, never neither. Throws otherwise.
    /// </summary>
    public static ControlProfileSource FromConfig(
        string? manualFramework, string? findingsEndpoint, string? ingestToken, IComplianceProfileClient? client = null)
    {
        var hasManual = !string.IsNullOrWhiteSpace(manualFramework);
        var hasFindings = !string.IsNullOrWhiteSpace(findingsEndpoint) || !string.IsNullOrWhiteSpace(ingestToken);

        if (hasManual && hasFindings)
            throw new InvalidOperationException(
                "Control framework is over-specified: set EITHER a manual framework OR a findings endpoint+token, not both.");
        if (!hasManual && !hasFindings)
            throw new InvalidOperationException(
                "Control framework is unset: set a manual framework, or a findings endpoint + ingest token.");

        if (hasManual)
            return Manual(manualFramework!);

        if (string.IsNullOrWhiteSpace(findingsEndpoint) || string.IsNullOrWhiteSpace(ingestToken))
            throw new InvalidOperationException("FromFindings requires BOTH a findings endpoint and an ingest token.");
        return FromFindings(findingsEndpoint, ingestToken, client);
    }

    /// <summary>Resolve to the concrete framework + controls, fetching from findings if that is the source.</summary>
    public ResolvedControlProfile Resolve()
    {
        if (_manual is { } m)
            return new ResolvedControlProfile
            {
                Framework = m.Framework,
                Example = m.ControlExample ?? "[\"CTRL-1\"]",
                CatalogueHint = m.CatalogueHint,
            };

        var f = _findings!;
        var profile = f.Client.Get(f.Endpoint, f.IngestToken);
        var hint = profile.Controls is { Count: > 0 }
            ? "(Applicable: " + string.Join(", ", profile.Controls.Take(12).Select(c => c.Id)) + ")"
            : null;
        return new ResolvedControlProfile
        {
            Framework = profile.Framework.Name,
            FrameworkId = profile.Framework.Id,
            FrameworkVersion = profile.Framework.Version,
            Example = profile.Controls is { Count: > 0 } ? $"[\"{profile.Controls[0].Id}\"]" : "[\"CTRL-1\"]",
            CatalogueHint = hint,
        };
    }

    private sealed record ManualSpec(string Framework, string? ControlExample, string? CatalogueHint);
    private sealed record FindingsSpec(string Endpoint, string IngestToken, IComplianceProfileClient Client);
}

/// <summary>The resolved framework used to build an <see cref="ExtractionProfile"/> and stamp the committed rules.</summary>
public sealed record ResolvedControlProfile
{
    public required string Framework { get; init; }
    public string? FrameworkId { get; init; }
    public string? FrameworkVersion { get; init; }
    public required string Example { get; init; }
    public string? CatalogueHint { get; init; }

    /// <summary>Merge the resolved framework into an extraction profile (keeping any project/stack context on <paramref name="baseProfile"/>).</summary>
    public ExtractionProfile ToExtractionProfile(ExtractionProfile? baseProfile = null)
    {
        var b = baseProfile ?? new ExtractionProfile();
        return b with { ControlFramework = Framework, ControlExample = Example, ControlCatalogueHint = CatalogueHint ?? b.ControlCatalogueHint };
    }
}
