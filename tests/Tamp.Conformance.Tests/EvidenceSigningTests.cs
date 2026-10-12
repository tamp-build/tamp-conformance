using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tamp.Conformance.Signing;
using Xunit;

namespace Tamp.Conformance.Tests;

public sealed class EvidenceSigningTests
{
    private static EvidenceManifest SampleManifest() => new()
    {
        CommitSha = "deadbeefcafe",
        Client = "Tamp",
        Project = "tamp-conformance",
        Version = "0.2.0",
        Branch = "main",
        Items = new[]
        {
            new EvidenceItem { Axis = "sbom", ArtifactId = "sbom.cdx.json", Sha256 = "aaaa", ToolCode = "sbom", ToolVersion = "6.2.0", ControlRefs = new[] { "SR-4" } },
            new EvidenceItem { Axis = "conformance", ArtifactId = "run", Sha256 = "bbbb", ToolCode = "adr", ToolVersion = "0.2.0", ControlRefs = new[] { "CM-3", "SA-15" } },
        },
    };

    // ---- manifest / in-toto statement ----

    [Fact]
    public void Statement_Has_InToto_Shape_And_Commit_Subject()
    {
        using var doc = JsonDocument.Parse(SampleManifest().ToStatementJson());
        var root = doc.RootElement;
        Assert.Equal(EvidenceManifest.StatementType, root.GetProperty("_type").GetString());
        Assert.Equal(EvidenceManifest.PredicateType, root.GetProperty("predicateType").GetString());
        var subject = root.GetProperty("subject")[0];
        Assert.Equal("deadbeefcafe", subject.GetProperty("digest").GetProperty("gitCommit").GetString());
        var items = root.GetProperty("predicate").GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
    }

    [Fact]
    public void Statement_Items_Are_Ordered_And_Deterministic()
    {
        var m = SampleManifest();
        var a = m.ToStatementJson();
        var b = m.ToStatementJson();
        Assert.Equal(a, b); // deterministic bytes (reproducible signatures)

        // Ordered by (axis, artifactId): "conformance" sorts before "sbom".
        using var doc = JsonDocument.Parse(a);
        var items = doc.RootElement.GetProperty("predicate").GetProperty("items");
        Assert.Equal("conformance", items[0].GetProperty("axis").GetString());
        Assert.Equal("sbom", items[1].GetProperty("axis").GetString());
        Assert.Equal(new[] { "CM-3", "SA-15" },
            System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(items[0].GetProperty("controlRefs").EnumerateArray(), e => e.GetString())));
    }

    [Fact]
    public void Sha256Hex_Is_Lowercase_Hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", EvidenceManifest.Sha256Hex("abc"));
    }

    // ---- auto-detection (ADR 0005 order) ----

    [Fact]
    public void Detect_NoEnv_IsNullSigner()
    {
        var s = EvidenceSignerFactory.Detect(new Dictionary<string, string>());
        Assert.IsType<NullSigner>(s);
        Assert.Equal("none", s.Mode);
        Assert.True(s.IsAvailable);
    }

    [Fact]
    public void Detect_KeyRef_IsManagedKey()
    {
        var s = EvidenceSignerFactory.Detect(new Dictionary<string, string> { ["TAMP_SIGN_KEY"] = "azurekms://vault/key" });
        var m = Assert.IsType<CosignManagedKeySigner>(s);
        Assert.Equal("managed-key", m.Mode);
        Assert.Equal("azurekms://vault/key", m.KeyRef);
    }

    [Fact]
    public void Detect_GithubActions_Oidc_IsKeyless()
    {
        var s = EvidenceSignerFactory.Detect(new Dictionary<string, string>
        {
            ["GITHUB_ACTIONS"] = "true",
            ["ACTIONS_ID_TOKEN_REQUEST_URL"] = "https://token.actions.githubusercontent.com/...",
        });
        Assert.IsType<KeylessOidcSigner>(s);
        Assert.Equal("keyless", s.Mode);
    }

    [Fact]
    public void Detect_GitLabCom_Oidc_IsKeyless()
    {
        var s = EvidenceSignerFactory.Detect(new Dictionary<string, string>
        {
            ["CI"] = "true",
            ["CI_SERVER_HOST"] = "gitlab.com",
            ["SIGSTORE_ID_TOKEN"] = "ey...",
        });
        Assert.IsType<KeylessOidcSigner>(s);
    }

    [Fact]
    public void Detect_KeyRef_Wins_Over_Oidc()
    {
        var s = EvidenceSignerFactory.Detect(new Dictionary<string, string>
        {
            ["TAMP_SIGN_KEY"] = "pkcs11:token=hsm;object=signer",
            ["GITHUB_ACTIONS"] = "true",
            ["ACTIONS_ID_TOKEN_REQUEST_URL"] = "https://x",
        });
        Assert.IsType<CosignManagedKeySigner>(s); // explicit config beats ambient OIDC
    }

    [Fact]
    public void Detect_GithubActions_Without_IdToken_IsNull()
    {
        // id-token: write not granted → no OIDC → unsigned.
        var s = EvidenceSignerFactory.Detect(new Dictionary<string, string> { ["GITHUB_ACTIONS"] = "true" });
        Assert.IsType<NullSigner>(s);
    }

    // ---- the optional / non-fatal contract ----

    [Fact]
    public async Task NullSigner_Produces_No_Envelope()
    {
        Assert.Null(await NullSigner.Instance.SignAsync(SampleManifest()));
    }

    [Fact]
    public async Task SignSafely_Swallows_Signer_Faults_To_Unsigned()
    {
        var thrown = await EvidenceSignerFactory.SignSafelyAsync(new ThrowingSigner(), SampleManifest());
        Assert.Null(thrown); // a faulting signer never breaks the run — evidence goes out unsigned
    }

    [Fact]
    public async Task ManagedKey_Unavailable_When_Cosign_Absent_YieldsUnsigned()
    {
        // cosign path that doesn't exist → IsAvailable false → SignAsync returns null (not an error).
        var signer = new CosignManagedKeySigner("azurekms://v/k", cosignPath: "/no/such/cosign-xyzzy");
        Assert.False(signer.IsAvailable);
        Assert.Null(await EvidenceSignerFactory.SignSafelyAsync(signer, SampleManifest()));
    }

    // ---- cosign argument construction (the surface to pin with findings TFND-210) ----

    [Fact]
    public void ManagedKey_Builds_KeyRef_SignBlob_Args()
    {
        var args = new CosignManagedKeySigner("azurekms://v/k").BuildSignArgs("s.json", "s.sig", "s.crt");
        Assert.Equal(new[] { "sign-blob", "--yes", "--key", "azurekms://v/k", "--output-signature", "s.sig", "s.json" }, args);
    }

    [Fact]
    public void Keyless_Builds_Certificate_Emitting_Args()
    {
        var args = new KeylessOidcSigner(oidcAmbient: true).BuildSignArgs("s.json", "s.sig", "s.crt");
        Assert.Contains("--output-certificate", args);
        Assert.Contains("s.crt", args);
        Assert.Equal("sign-blob", args[0]);
    }

    private sealed class ThrowingSigner : IEvidenceSigner
    {
        public string Mode => "managed-key";
        public bool IsAvailable => true;
        public Task<DsseEnvelope?> SignAsync(EvidenceManifest manifest, CancellationToken ct = default)
            => throw new System.InvalidOperationException("KMS unreachable");
    }
}
