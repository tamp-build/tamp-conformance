using System.Text.Json;
using System.Text.Json.Serialization;
using Tamp;

namespace Tamp.Conformance;

/// <summary>
/// Loads and saves <see cref="AdrRuleSet"/>s (the committed <c>adr-rules.json</c> "lockfile for
/// architectural intent") and answers the staleness question the CI gate turns on: does the rule-set's
/// recorded <see cref="AdrRuleSet.SourceSha"/> still match the live ADR file? A mismatch means the ADR
/// changed without its rules being refreshed — the gate fails closed on that, closing the loophole
/// where an edited decision silently stops being enforced.
/// </summary>
public static class RuleLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Serialize a rule-set to its committed JSON form.</summary>
    public static string Serialize(AdrRuleSet set) => JsonSerializer.Serialize(set, Json);

    /// <summary>Parse a rule-set from its committed JSON form. Throws on malformed input.</summary>
    public static AdrRuleSet Deserialize(string json)
        => JsonSerializer.Deserialize<AdrRuleSet>(json, Json)
           ?? throw new FormatException("adr-rules.json deserialized to null.");

    /// <summary>Load a rule-set from a file.</summary>
    public static AdrRuleSet Load(AbsolutePath rulesFile) => Deserialize(File.ReadAllText(rulesFile.Value));

    /// <summary>Write a rule-set to a file (creating parent directories).</summary>
    public static void Save(AdrRuleSet set, AbsolutePath rulesFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(rulesFile.Value)!);
        File.WriteAllText(rulesFile.Value, Serialize(set));
    }

    /// <summary>
    /// True when <paramref name="set"/>'s recorded source hash no longer matches the current content of
    /// <paramref name="adrFile"/> — i.e. the ADR changed and the rules were not refreshed.
    /// </summary>
    public static bool IsStale(AdrRuleSet set, AbsolutePath adrFile) => set.SourceSha != adrFile.Sha256();
}
