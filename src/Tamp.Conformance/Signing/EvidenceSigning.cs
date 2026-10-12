using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tamp.Conformance.Signing;

/// <summary>
/// One stamped evidence item in a per-run manifest: the sha256 of the posted payload plus the
/// provenance stamp (<see cref="ToolCode"/> + <see cref="ToolVersion"/> + claimed <see cref="ControlRefs"/>)
/// the control-driven tool framework already uses. A signed manifest of these is what makes a run's
/// evidence <i>authentic</i> (ADR 0005), closing the bearer-token forgery gap (TFND-210).
/// </summary>
/// <remarks>
/// <para><b>Fact, not policy.</b> An item records what was observed/posted; findings decides whether a
/// <c>(control, toolCode, version)</c> is acceptable and whether a trust tier accepts the evidence.</para>
/// </remarks>
public sealed record EvidenceItem
{
    /// <summary>The evidence axis / slot family (e.g. <c>sbom</c>, <c>sast</c>, <c>test-results</c>, <c>conformance</c>, <c>coverage</c>).</summary>
    public required string Axis { get; init; }
    /// <summary>Identifier of the specific slot within the axis (e.g. a trx assembly name) — the replace-on-slot key.</summary>
    public required string ArtifactId { get; init; }
    /// <summary>Lowercase hex SHA-256 of the exact payload posted for this item.</summary>
    public required string Sha256 { get; init; }
    /// <summary>Producing tool code (e.g. <c>adr</c>, <c>sbom</c>, <c>grype</c>). Part of the provenance stamp.</summary>
    public string? ToolCode { get; init; }
    /// <summary>Producing tool version.</summary>
    public string? ToolVersion { get; init; }
    /// <summary>Controls this item claims to be evidence for (findings judges the claim).</summary>
    public IReadOnlyList<string>? ControlRefs { get; init; }
}

/// <summary>
/// The per-run evidence manifest — assembled into an <b>in-toto v1 Statement</b> (ADR 0005): subject is the
/// build's <b>commit SHA</b> (findings' <c>BuildResolver</c> reconciles on the commit, not the version string),
/// predicate is the re-signable inventory of <see cref="EvidenceItem"/>s. A signer wraps this Statement in a
/// single DSSE envelope; the latest signed manifest wins per <c>(axis, artifactId)</c> slot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire-format note.</b> The exact subject-digest key and predicate shape are the contract with findings'
/// verifier (TFND-210, not yet live), so <see cref="PredicateType"/> is versioned and the shape is pinned here
/// in one place. This type is deterministic (stable field order) so the signed bytes are reproducible.
/// </para>
/// </remarks>
public sealed record EvidenceManifest
{
    /// <summary>in-toto Statement type URI.</summary>
    public const string StatementType = "https://in-toto.io/Statement/v1";
    /// <summary>tamp evidence predicate type URI (versioned — bump when the predicate shape changes).</summary>
    public const string PredicateType = "https://tamp.build/evidence/v1";

    /// <summary>The build's commit SHA — the manifest subject and findings' reconciliation key.</summary>
    public required string CommitSha { get; init; }
    public string? Client { get; init; }
    public string? Project { get; init; }
    public string? Component { get; init; }
    public string? Version { get; init; }
    public string? Branch { get; init; }
    /// <summary>The stamped evidence items. Emitted in a stable order (axis, then artifactId).</summary>
    public required IReadOnlyList<EvidenceItem> Items { get; init; }

    /// <summary>
    /// Serialize to the canonical in-toto v1 Statement JSON that a signer wraps in DSSE. Deterministic:
    /// items are ordered by <c>(axis, artifactId)</c> and object keys are written in a fixed order, so the
    /// same manifest always yields the same bytes (required for reproducible signatures + findings' re-verify).
    /// </summary>
    public string ToStatementJson()
    {
        var ordered = Items
            .OrderBy(i => i.Axis, StringComparer.Ordinal)
            .ThenBy(i => i.ArtifactId, StringComparer.Ordinal);

        var items = new JsonArray();
        foreach (var i in ordered)
        {
            var obj = new JsonObject
            {
                ["axis"] = i.Axis,
                ["artifactId"] = i.ArtifactId,
                ["sha256"] = i.Sha256,
            };
            if (!string.IsNullOrEmpty(i.ToolCode)) obj["toolCode"] = i.ToolCode;
            if (!string.IsNullOrEmpty(i.ToolVersion)) obj["toolVersion"] = i.ToolVersion;
            if (i.ControlRefs is { Count: > 0 })
            {
                var refs = new JsonArray();
                foreach (var c in i.ControlRefs) refs.Add((JsonNode)c!);
                obj["controlRefs"] = refs;
            }
            items.Add(obj);
        }

        var predicate = new JsonObject();
        if (!string.IsNullOrEmpty(Client)) predicate["client"] = Client;
        if (!string.IsNullOrEmpty(Project)) predicate["project"] = Project;
        if (!string.IsNullOrEmpty(Component)) predicate["component"] = Component;
        if (!string.IsNullOrEmpty(Version)) predicate["version"] = Version;
        if (!string.IsNullOrEmpty(Branch)) predicate["branch"] = Branch;
        predicate["items"] = items;

        var statement = new JsonObject
        {
            ["_type"] = StatementType,
            // Subject = the commit. gitCommit digest alg mirrors the in-toto convention for VCS subjects;
            // the exact key is pinned with findings' verifier (TFND-210).
            ["subject"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = $"{Client}/{Project}".Trim('/'),
                    ["digest"] = new JsonObject { ["gitCommit"] = CommitSha },
                },
            },
            ["predicateType"] = PredicateType,
            ["predicate"] = predicate,
        };

        return statement.ToJsonString(CompactDeterministic);
    }

    private static readonly JsonSerializerOptions CompactDeterministic = new() { WriteIndented = false };

    /// <summary>Lowercase-hex SHA-256 of the given bytes.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Lowercase-hex SHA-256 of a UTF-8 string.</summary>
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>Lowercase-hex SHA-256 of a file's contents.</summary>
    public static string Sha256HexOfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}

/// <summary>
/// A DSSE (Dead Simple Signing Envelope) wrapping a signed payload — the standard cosign-native shape
/// findings' <c>DsseVerifier</c> consumes. <see cref="Payload"/> is base64 of the in-toto Statement bytes.
/// </summary>
public sealed record DsseEnvelope
{
    /// <summary>Base64 of the signed payload (the in-toto Statement JSON bytes).</summary>
    public required string Payload { get; init; }
    /// <summary>DSSE payload type (<c>application/vnd.in-toto+json</c> for an in-toto Statement).</summary>
    public required string PayloadType { get; init; }
    /// <summary>One or more signatures over the DSSE PAE of (payloadType, payload).</summary>
    public required IReadOnlyList<DsseSignature> Signatures { get; init; }

    public const string InTotoPayloadType = "application/vnd.in-toto+json";
}

/// <summary>A single DSSE signature (base64 <see cref="Sig"/>) with an optional key id / cert chain hint.</summary>
public sealed record DsseSignature
{
    public required string Sig { get; init; }
    public string? KeyId { get; init; }
    /// <summary>PEM certificate (chain leaf) for CA-chain verification, when the signer is cert-backed.</summary>
    public string? Cert { get; init; }
}
