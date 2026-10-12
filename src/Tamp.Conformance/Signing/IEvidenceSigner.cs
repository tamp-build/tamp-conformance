namespace Tamp.Conformance.Signing;

/// <summary>
/// The signer seam (ADR 0005). Implementations wrap a per-run <see cref="EvidenceManifest"/> in a DSSE
/// envelope per the CI's signing mode. <b>Signing is optional by construction</b>: on a build agent that
/// can't sign (local/dev, un-wired CI, missing cosign), the runner still posts its evidence — just
/// <i>unsigned</i> (weakened). Whether unsigned evidence is acceptable is <b>findings'</b> policy-tier
/// decision, never the producer's.
/// </summary>
public interface IEvidenceSigner
{
    /// <summary>Signer mode label for logs/provenance: <c>none</c>, <c>keyless</c>, or <c>managed-key</c>.</summary>
    string Mode { get; }

    /// <summary>
    /// True when this signer is actually usable in the current environment (tool present, key-ref/OIDC
    /// resolvable). When false, the run proceeds unsigned.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Sign the manifest, returning its DSSE envelope — or <c>null</c> when signing is unavailable/not
    /// applicable (treated as "unsigned", never an error). Implementations should prefer returning
    /// <c>null</c> over throwing for expected unavailability; genuine faults may throw and are caught by
    /// <see cref="EvidenceSignerFactory.SignSafelyAsync"/>.
    /// </summary>
    Task<DsseEnvelope?> SignAsync(EvidenceManifest manifest, CancellationToken ct = default);
}

/// <summary>
/// The unsigned path: always available, never signs. Used for local/dev and un-wired CI so evidence still
/// flows (weakened). This is the default the factory falls back to — the embodiment of "signing is optional".
/// </summary>
public sealed class NullSigner : IEvidenceSigner
{
    public static readonly NullSigner Instance = new();
    public string Mode => "none";
    public bool IsAvailable => true;
    public Task<DsseEnvelope?> SignAsync(EvidenceManifest manifest, CancellationToken ct = default)
        => Task.FromResult<DsseEnvelope?>(null);
}
