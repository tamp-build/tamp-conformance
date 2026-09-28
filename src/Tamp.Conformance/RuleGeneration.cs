using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// The <c>AdrRules</c> capability — turn an ADR into a committed <see cref="AdrRuleSet"/>. The
/// deterministic scaffolding (reading the ADR, hashing it into <see cref="AdrRuleSet.SourceSha"/>,
/// seeding an empty set) is self-contained; the prose→rules extraction is a frontier-model step
/// supplied via <see cref="IRuleExtractor"/>. Runs rarely (on ADR change), writes source a human
/// reviews — it is a generator, never the CI gate.
/// </summary>
public static class RuleGeneration
{
    /// <summary>
    /// Seed an empty rule-set stamped with the ADR's current content hash — a valid starting point a
    /// human or agent then fills. No model required.
    /// </summary>
    public static AdrRuleSet Seed(string adrId, AbsolutePath adrFile) => new()
    {
        Adr = adrId,
        SourceSha = adrFile.Sha256(),
        ExtractedBy = "manual",
        Rules = Array.Empty<AdrRule>(),
    };

    /// <summary>
    /// Generate a rule-set from an ADR using the host-provided <paramref name="extractor"/>, stamping the
    /// current source hash so staleness is detectable.
    /// </summary>
    public static AdrRuleSet Generate(string adrId, AbsolutePath adrFile, IRuleExtractor extractor, string? extractedBy = null)
    {
        var text = File.ReadAllText(adrFile.Value);
        var rules = extractor.Extract(adrId, text);
        return new AdrRuleSet
        {
            Adr = adrId,
            SourceSha = adrFile.Sha256(),
            ExtractedBy = extractedBy ?? extractor.GetType().Name,
            Rules = rules,
        };
    }
}
