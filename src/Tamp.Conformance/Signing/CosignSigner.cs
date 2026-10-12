using System.Diagnostics;
using System.Text;

namespace Tamp.Conformance.Signing;

/// <summary>
/// Base for the two <c>cosign</c>-backed signers (ADR 0005). cosign is the one portable signer that runs on
/// every target CI. This class holds the shared "write the statement, run cosign, wrap the result in a DSSE
/// envelope" flow; subclasses supply the mode-specific cosign flags via <see cref="BuildSignArgs"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format pin pending.</b> The exact cosign invocation + the resulting envelope shape are the contract with
/// findings' <c>DsseVerifier</c> (TFND-210), which is not yet live. <see cref="BuildSignArgs"/> is therefore a
/// tested, single-place surface to pin when that lands; <see cref="SignAsync"/> degrades to <c>null</c>
/// (unsigned) whenever cosign is absent or errors, so the optional contract always holds.
/// </para>
/// </remarks>
public abstract class CosignSignerBase : IEvidenceSigner
{
    /// <summary>Path to the cosign executable (default <c>cosign</c>, resolved on PATH).</summary>
    protected string CosignPath { get; }

    protected CosignSignerBase(string? cosignPath) => CosignPath = string.IsNullOrWhiteSpace(cosignPath) ? "cosign" : cosignPath!;

    public abstract string Mode { get; }
    public abstract bool IsAvailable { get; }

    /// <summary>
    /// The cosign arguments for signing the statement at <paramref name="payloadPath"/>, writing the detached
    /// signature to <paramref name="signaturePath"/> (and, in keyless mode, the cert to
    /// <paramref name="certPath"/>). Pure + deterministic so it can be unit-tested without running cosign.
    /// </summary>
    public abstract IReadOnlyList<string> BuildSignArgs(string payloadPath, string signaturePath, string? certPath);

    public virtual async Task<DsseEnvelope?> SignAsync(EvidenceManifest manifest, CancellationToken ct = default)
    {
        if (manifest is null) throw new ArgumentNullException(nameof(manifest));
        if (!IsAvailable) return null; // unsigned — expected on an unsupported agent

        var statement = manifest.ToStatementJson();
        var dir = Directory.CreateTempSubdirectory("tamp-sign-");
        try
        {
            var payloadPath = Path.Combine(dir.FullName, "statement.json");
            var sigPath = Path.Combine(dir.FullName, "statement.sig");
            var certPath = Path.Combine(dir.FullName, "statement.crt");
            await File.WriteAllTextAsync(payloadPath, statement, ct);

            var exit = await RunCosignAsync(BuildSignArgs(payloadPath, sigPath, certPath), ct);
            if (exit != 0 || !File.Exists(sigPath)) return null; // cosign failed → unsigned, not fatal

            var sig = (await File.ReadAllTextAsync(sigPath, ct)).Trim();
            var cert = File.Exists(certPath) ? (await File.ReadAllTextAsync(certPath, ct)).Trim() : null;
            return new DsseEnvelope
            {
                Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(statement)),
                PayloadType = DsseEnvelope.InTotoPayloadType,
                Signatures = new[] { new DsseSignature { Sig = sig, Cert = string.IsNullOrEmpty(cert) ? null : cert } },
            };
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private async Task<int> RunCosignAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(CosignPath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    /// <summary>True when the cosign executable resolves (presence is necessary for any real signing).</summary>
    protected bool CosignOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : new[] { "" };
        if (CosignPath.Contains('/') || CosignPath.Contains('\\')) return File.Exists(CosignPath);
        foreach (var dir in path.Split(Path.PathSeparator))
            foreach (var ext in exts)
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, CosignPath + ext))) return true;
        return false;
    }
}

/// <summary>
/// Tier 1 — managed-key / CA-chain signing (ADR 0005), the air-gap-capable tier that ships first. cosign
/// signs through a <b>key reference</b> — cloud KMS (<c>azurekms://</c>/<c>awskms://</c>/<c>gcpkms://</c>/
/// <c>hashivault://</c>) or an on-prem HSM via <c>pkcs11:</c>. The private key never leaves the KMS/HSM.
/// </summary>
public sealed class CosignManagedKeySigner : CosignSignerBase
{
    /// <summary>The cosign key reference (a KMS URI or <c>pkcs11:</c> URI). Never a raw private key.</summary>
    public string KeyRef { get; }

    public CosignManagedKeySigner(string keyRef, string? cosignPath = null) : base(cosignPath)
        => KeyRef = keyRef ?? throw new ArgumentNullException(nameof(keyRef));

    public override string Mode => "managed-key";
    public override bool IsAvailable => !string.IsNullOrWhiteSpace(KeyRef) && CosignOnPath();

    public override IReadOnlyList<string> BuildSignArgs(string payloadPath, string signaturePath, string? certPath)
        => new[] { "sign-blob", "--yes", "--key", KeyRef, "--output-signature", signaturePath, payloadPath };
}

/// <summary>
/// Tier 2 — keyless attestation (ADR 0005): GitHub Actions / GitLab.com only, via ambient workflow OIDC →
/// Fulcio ephemeral cert + Rekor. <b>Online/Rekor-bound — not air-gap-capable.</b> Ships after Tier 1.
/// </summary>
public sealed class KeylessOidcSigner : CosignSignerBase
{
    private readonly bool _oidcAmbient;

    public KeylessOidcSigner(bool oidcAmbient, string? cosignPath = null) : base(cosignPath) => _oidcAmbient = oidcAmbient;

    public override string Mode => "keyless";
    public override bool IsAvailable => _oidcAmbient && CosignOnPath();

    public override IReadOnlyList<string> BuildSignArgs(string payloadPath, string signaturePath, string? certPath)
        => new[] { "sign-blob", "--yes", "--output-signature", signaturePath, "--output-certificate", certPath ?? "cosign.crt", payloadPath };
}
