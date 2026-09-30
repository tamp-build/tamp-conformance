# ADR 0005: Signed evidence bundles — portable, per-run provenance

* Status: Accepted
* Date: 2026-09-30
* Deciders: scott

## Context and Problem Statement

Ingested evidence today is only **bearer-token authorized**: a `prj_`/`cli_` token proves the *sender* holds a token, not that the *payload* genuinely came from the project's CI unmodified. Anyone who obtains the token can forge evidence — the "bearer-token authorizes the sender but does not attest the payload" gap (tamp-findings TFND-210). Until evidence is cryptographically authenticated, **SI-7 (software/firmware/information integrity) and its enhancements stay correctly Unmapped**.

Producers run on many CIs — GitHub Actions, GitLab (cloud + self-hosted), Azure DevOps (Services + Server), Jenkins, TeamCity — including **air-gapped federal environments with existing PKI/HSM**. A GitHub-OIDC-keyless-only design is *not* portable: public Sigstore Fulcio only trusts the GitHub Actions and GitLab.com OIDC issuers, and air-gapped shops cannot reach Fulcio/Rekor at all. Federal agencies (a primary target — several still run Jenkins as their main pipeline) already operate an acknowledged key/cert system, typically an on-prem HSM.

## Decision

**Sign the per-run evidence bundle, once.** The runner assembles an **in-toto v1 Statement** (subject = the build: client/project/commit/version; predicate = an inventory of every evidence item with its `toolCode`, `toolVersion`, `controlRefs`, and the `sha256` of the posted payload) and signs it as a single **DSSE envelope**. One signing operation per build. The signature covers the whole set, so no item can be added, swapped, or dropped undetected; findings recomputes each posted item's digest against the signed manifest.

**Two producer signing modes, auto-selected by environment; `cosign` is the common signer** (a single portable binary, built with PKCS#11 support, that runs on every target CI):

* **Attestation / keyless** — GitHub Actions and GitLab.com *only* (the OIDC issuers public Fulcio trusts). Ambient workflow OIDC → Fulcio ephemeral certificate + Rekor transparency. Identity-bound, zero key management.
* **Managed key** — everyone else (Azure DevOps, Jenkins, TeamCity, self-hosted GitLab, air-gapped). cosign signs through a **key reference** to a managed signer — cloud KMS (`azurekms://` / `awskms://` / `gcpkms://` / `hashivault://`) **or** an on-prem **HSM via PKCS#11 (`pkcs11:`)**. The private key never leaves the KMS/HSM. **No raw private keys in CI secret stores, ever.** (Azure DevOps signs secretlessly via Workload Identity Federation → Key Vault; the agency HSM speaks PKCS#11.)
* **Unsigned** — local/dev and un-wired CI. Evidence is still accepted but carries no authenticity and is **weakened**.

**CA-chain is the default trust model.** For managed-key signing, findings validates that the signer's certificate **chains to a CA root the project registered** — the agency's existing PKI is the root of trust for tamp evidence. Bare-public-key verification is supported as an alternative for teams without a PKI. (Keyless mode's trust root is the Fulcio/OIDC identity, matched to a registered identity pattern.)

**tamp produces + signs; findings owns trust + policy.** The producer assembles the bundle, stamps each item with `toolCode` + `toolVersion` + claimed `controlRefs`, signs per the CI mode, and POSTs. findings owns the **authorized-signer registry** (CA roots / OIDC identity patterns / registered public keys, multi-entry for rotation), signature + certificate-chain verification, digest matching, **trust tiering**, allow/deny of a tool+version per control, and gating. **The producer never decides whether weakened (unsigned/untrusted) evidence is acceptable — findings does, per policy tier.** A lower-tier policy may accept unsigned evidence; a federal High policy will not. That is findings' call, not tamp's.

**The signer is a seam, not a hardcode.** `IEvidenceSigner` with `KeylessOidcSigner` (GHA/GitLab.com) and `CosignManagedKeySigner` (a key reference — KMS or PKCS#11), plus `NullSigner` for unsigned. Auto-detected from the environment (ambient OIDC → keyless; a configured key-ref → managed; otherwise none), explicitly overridable.

## Consequences

* Closes the bearer-token forgery gap: evidence is *authenticated*, not merely token-authorized — unlocking SI-7 / 7(x) via findings' provenance-verified path (TFND-210).
* Portable across every target CI, **including air-gapped**: managed-key signing is a local crypto operation needing no Sigstore public infrastructure, and CA-chain verification works offline against the registered root. Rekor/transparency-log upload is optional (public or private), never required to verify.
* Federal-friendly by construction: signing keys stay in a FIPS-validated HSM/KMS; the agency's own CA is the trust root; no exportable key material lives in CI.
* Trust is a spectrum, not a binary: keyless+Rekor and managed-key+CA-chain are both "trusted"; unsigned is "advisory." findings tiers and decides gating; producers do not.
* Cheap: one signing call per build; `cosign` is the single portable binary across all CIs.

## Notes

* Manifest format is a standard **in-toto v1 Statement + DSSE** (cosign-native), not a bespoke shape.
* Depends on tamp-findings (TFND-210 provenance gate; TFND-159 DSSE verify + trust root; TFND-160 key-backed signature; TFND-162 trust tier). Two contract points to pin with findings, because the target agencies fail without them: the authorized-signer registry must support **CA-root / cert-chain entries with X.509 path validation** (not just bare pubkeys), and verification must have an **offline path with no mandatory Rekor lookup**.
* The tool-provenance stamp (`toolCode` + `toolVersion` + `controlRefs`) is the same one used for control-driven tool selection and evidence claims; signing simply wraps the per-run bundle of those stamps.
* Cross-refs: [ADR 0003](0003-findings-system-of-record.md) (findings owns transport/verification/scoring), [ADR 0004](0004-per-project-framework-and-evidence-contract.md) (the evidence event contract); core ADR 0023 (canonical event shape).
