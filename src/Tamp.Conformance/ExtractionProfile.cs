namespace Tamp.Conformance;

/// <summary>
/// The per-project context injected into the rule-extraction prompt. The prompt's <b>integrity</b> clauses
/// (the deterministic/semantic split, calibrated abstention, strict JSON) are fixed and never tunable;
/// only this project context is injectable — because every project differs in its compliance framework,
/// stack, and conventions. <see cref="ControlFramework"/> is typically resolved from a
/// <see cref="ControlProfileSource"/> (manual, or fetched from tamp-findings for the exact project the
/// evidence lands in).
/// </summary>
public sealed record ExtractionProfile
{
    /// <summary>The control framework the rules map to, e.g. <c>NIST 800-53</c>, <c>SOC 2</c>, <c>ISO 27001</c>.</summary>
    public string ControlFramework { get; init; } = "NIST 800-53";

    /// <summary>An example control-id list in the framework, e.g. <c>["CM-6"]</c> or <c>["CC8.1"]</c>.</summary>
    public string ControlExample { get; init; } = "[\"CM-6\"]";

    /// <summary>Optional one-line hint listing common/applicable control ids to steer mapping.</summary>
    public string? ControlCatalogueHint { get; init; }

    /// <summary>Optional project description, e.g. "This is a Terraform/IaC monorepo; there is no compiled code."</summary>
    public string? ProjectContext { get; init; }

    /// <summary>Optional stack/naming conventions, e.g. "Packages are Tamp.*; the core library is src/Tamp.Core."</summary>
    public string? StackConventions { get; init; }

    /// <summary>Today's default behavior (NIST 800-53), so existing callers are unchanged.</summary>
    public static ExtractionProfile Default { get; } = new();
}
