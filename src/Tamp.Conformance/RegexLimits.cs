using System.Text.RegularExpressions;

namespace Tamp.Conformance;

/// <summary>
/// The single source of truth for regex execution limits in Tamp.Conformance (Sonar S6444, ReDoS hardening).
/// Every <see cref="Regex"/> this package constructs passes <see cref="MatchTimeout"/>, so a pathological
/// pattern or input turns into a <see cref="RegexMatchTimeoutException"/> instead of a hang. Each call site
/// decides what that exception means for its verdict — none of them may read it as "no match".
/// </summary>
internal static class RegexLimits
{
    /// <summary>
    /// Upper bound on a single match/replace operation. Generous for real inputs (a model response, one
    /// source line, one relative path all complete in microseconds), small enough that a runaway pattern
    /// fails the operation quickly rather than stalling a build agent.
    /// </summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
}
