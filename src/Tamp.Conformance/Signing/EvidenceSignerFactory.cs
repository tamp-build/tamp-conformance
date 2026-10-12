namespace Tamp.Conformance.Signing;

/// <summary>
/// Auto-selects an <see cref="IEvidenceSigner"/> from the environment (ADR 0005) and provides the
/// never-throws signing entry point. Selection order:
/// <list type="number">
///   <item>an explicit managed <b>key reference</b> (env <c>TAMP_SIGN_KEY</c> — a KMS/<c>pkcs11:</c> URI) → <see cref="CosignManagedKeySigner"/>;</item>
///   <item>ambient keyless OIDC (GitHub Actions with <c>id-token</c>, or GitLab.com with a sigstore id token) → <see cref="KeylessOidcSigner"/>;</item>
///   <item>otherwise → <see cref="NullSigner"/> (unsigned).</item>
/// </list>
/// The default is always <see cref="NullSigner"/>: on any agent that isn't wired for signing, evidence still
/// flows — just unsigned. findings decides whether that's acceptable per policy tier, not the producer.
/// </summary>
public static class EvidenceSignerFactory
{
    /// <summary>Env var holding a cosign managed key reference (KMS URI or <c>pkcs11:</c> URI). Never a raw key.</summary>
    public const string KeyRefEnv = "TAMP_SIGN_KEY";

    /// <summary>Detect the appropriate signer. Pass <paramref name="env"/> to test deterministically; null reads the process environment.</summary>
    public static IEvidenceSigner Detect(IReadOnlyDictionary<string, string>? env = null, string? cosignPath = null)
    {
        string? Get(string k) =>
            env is not null ? (env.TryGetValue(k, out var v) ? v : null) : Environment.GetEnvironmentVariable(k);

        // 1) Explicit managed key reference wins — a deliberate, portable, air-gap-capable choice.
        var keyRef = Get(KeyRefEnv);
        if (!string.IsNullOrWhiteSpace(keyRef))
            return new CosignManagedKeySigner(keyRef!, cosignPath);

        // 2) Ambient keyless OIDC (online-only tier).
        if (HasAmbientOidc(Get))
            return new KeylessOidcSigner(oidcAmbient: true, cosignPath);

        // 3) Default: unsigned.
        return NullSigner.Instance;
    }

    /// <summary>
    /// True when the environment exposes an OIDC identity public Fulcio trusts: GitHub Actions with
    /// <c>id-token: write</c> (its <c>ACTIONS_ID_TOKEN_REQUEST_URL</c> is set) or GitLab.com with a sigstore
    /// id token. Other CIs have no public-keyless path and must use a managed key.
    /// </summary>
    public static bool HasAmbientOidc(Func<string, string?> get)
    {
        if (string.Equals(get("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(get("ACTIONS_ID_TOKEN_REQUEST_URL")))
            return true;

        if (string.Equals(get("CI"), "true", StringComparison.OrdinalIgnoreCase)
            && string.Equals(get("CI_SERVER_HOST"), "gitlab.com", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(get("SIGSTORE_ID_TOKEN")))
            return true;

        return false;
    }

    /// <summary>
    /// Sign the manifest, <b>never throwing</b>: returns the DSSE envelope, or <c>null</c> when the signer is
    /// unavailable, declines, or errors. This is the guarantee that signing can't break evidence flow — the
    /// runner calls this and posts the evidence either way (with the envelope when present, unsigned when not).
    /// </summary>
    public static async Task<DsseEnvelope?> SignSafelyAsync(IEvidenceSigner signer, EvidenceManifest manifest, CancellationToken ct = default)
    {
        if (signer is null || manifest is null) return null;
        try
        {
            return await signer.SignAsync(manifest, ct);
        }
        catch
        {
            // Signing is best-effort: any fault (missing cosign, KMS unreachable, OIDC hiccup, cancellation)
            // degrades to unsigned. The evidence still goes out; findings tiers it.
            return null;
        }
    }
}
