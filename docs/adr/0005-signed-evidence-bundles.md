# ADR 0005: Signed evidence bundles — portable, per-run provenance

* Status: Accepted
* Date: 2026-09-30
* Deciders: scott

## Context and Problem Statement

Ingested evidence today is only **bearer-token authorized**: a `prj_`/`cli_` token proves the *sender* holds a token, not that the *payload* genuinely came from the project's CI unmodified. Anyone who obtains the token can forge evidence — the "bearer-token authorizes the sender but does not attest the payload" gap (tamp-findings TFND-210). Until evidence is cryptographically authenticated, **SI-7 (software/firmware/information integrity) and its enhancements stay correctly Unmapped**.

Producers run on many CIs — GitHub Actions, GitLab (cloud + self-hosted), Azure DevOps (Services + Server), Jenkins, TeamCity — including **air-gapped federal environments with existing PKI/HSM**. A GitHub-OIDC-keyless-only design is *not* portable: public Sigstore Fulcio only trusts the GitHub Actions and GitLab.com OIDC issuers, and air-gapped shops cannot reach Fulcio/Rekor at all. Federal agencies (a primary target — several still run Jenkins as their main pipeline) already operate an acknowledged key/cert system, typically an on-prem HSM.

## Decision

**Governing invariant — producers generate evidence only; findings owns all policy.** No tool in the conformance tooling renders a pass/fail/allow/deny/gate decision. Every tool (including the ADR-conformance check) resolves / scans / extracts / verifies and emits *facts* — observations, resolved values, four-valued verdicts — stamped with provenance and claimed `controlRefs`, then submits. **All** policy lives in findings: whether a license is allowed, whether a verdict blocks, whether a `(control, toolCode, version)` is acceptable, whether weakened evidence passes a given tier. This keeps producers dumb and auditable and findings the single seat of judgment; the signing scheme below exists to make that evidence *authentic*, never to move judgment into the producer.

**Sign a re-signable manifest of digests, manifest-first.** The runner assembles an **in-toto v1 Statement** whose **`subject` is the build's commit SHA** (findings' `BuildResolver` reconciles builds on the commit, not the version string; version/client/project ride as predicate fields) and whose predicate is an inventory of every evidence item — `{ (axis, artifactId) → sha256, toolCode, toolVersion, controlRefs }` — signed as a single **DSSE envelope**. It is posted **manifest-first**: findings records the authorized `{ item → digest }` set for the build, then digest-matches each subsequent evidence POST against it.

The manifest is **re-signable and slot-superseding**, because ingest is piecemeal and replace-on-slot: one build's test-results is ~12 distinct `.trx` POSTs, SBOM corrections and single-axis re-runs are normal, and supersession is first-class. A re-posted axis ships a **new DSSE envelope that supersedes the prior one for its `(axis, artifactId)`** — **the latest signed manifest wins per slot.** A full run is **one** signing call; a one-axis re-run re-signs only the tiny manifest (digests), never the artifacts, so incremental re-posts stay cheap. (Rejected: a whole-run atomic manifest re-signed on every change — cryptographically simpler but it fights the replace-on-slot reality.)

**Two signing tiers, auto-selected by environment; `cosign` is the common signer** (one portable binary, built with PKCS#11 support, that runs on every target CI). Signature leaves are **ECDSA P-256/384 or RSA-PKCS1 (SHA-256)** — *not* Ed25519 (ECDSA P-256 is cosign's default and matches FIPS/agency-HSM norms; findings verifies exactly these):

* **Tier 1 — managed-key / CA-chain (offline; built first).** Everyone except GitHub Actions and GitLab.com — Azure DevOps, Jenkins, TeamCity, self-hosted GitLab, air-gapped. cosign signs through a **key reference** to a managed signer — cloud KMS (`azurekms://` / `awskms://` / `gcpkms://` / `hashivault://`) **or** an on-prem **HSM via PKCS#11 (`pkcs11:`)**. The private key never leaves the KMS/HSM; **no raw private keys in CI secret stores, ever.** (ADO signs secretlessly via Workload Identity Federation → Key Vault; the agency HSM speaks PKCS#11.) Verification is **offline by construction** — a signature check over the DSSE PAE against the registered trust root, **no Rekor lookup**. This is the tier air-gapped agencies run, so it ships first.
* **Tier 2 — keyless attestation (online-only; later).** GitHub Actions and GitLab.com *only* (the OIDC issuers public Fulcio trusts). Ambient workflow OIDC → Fulcio ephemeral certificate + **Rekor** transparency. **Keyless and offline do not compose**: Fulcio's short-lived cert depends on Rekor for its timestamp, so keyless is inherently online and Rekor-bound — it is **not air-gap-capable**. It also needs a keyless-verify path on findings (Fulcio-cert extraction, OIDC-identity binding, Rekor inclusion proof) that does not exist yet, so this tier lands **after** Tier 1 on both sides.
* **Unsigned** — local/dev and un-wired CI. Evidence is accepted but carries no authenticity and is **weakened**; whether that is acceptable is a findings policy-tier decision, never the producer's.

**CA-chain is the default trust model.** For managed-key signing, findings validates that the signer's certificate **chains to a CA root the project registered** (X.509 path validation + a leaf identity/EKU check) — so the agency's existing PKI is the root of trust for tamp evidence. Bare-public-key verification remains for teams without a PKI. (findings confirmed cert-chain path validation is net-new on their side — their current trust root is bare ECDSA/RSA keys — and will build the `X509Chain` mode. Tier-2 keyless's trust root is instead the Fulcio/OIDC identity matched to a registered identity pattern.)

**tamp produces + signs; findings owns trust + policy.** The producer assembles the bundle, stamps each item with `toolCode` + `toolVersion` + claimed `controlRefs`, signs per the CI mode, and POSTs. findings owns the **authorized-signer registry** (CA roots / OIDC identity patterns / registered public keys, multi-entry for rotation), signature + certificate-chain verification, digest matching, **trust tiering**, allow/deny of a tool+version per control, and gating. **The producer never decides whether weakened (unsigned/untrusted) evidence is acceptable — findings does, per policy tier.** A lower-tier policy may accept unsigned evidence; a federal High policy will not. That is findings' call, not tamp's.

**The signer is a seam, not a hardcode.** `IEvidenceSigner` with `KeylessOidcSigner` (GHA/GitLab.com) and `CosignManagedKeySigner` (a key reference — KMS or PKCS#11), plus `NullSigner` for unsigned. Auto-detected from the environment (ambient OIDC → keyless; a configured key-ref → managed; otherwise none), explicitly overridable.

## Consequences

* Closes the bearer-token forgery gap: evidence is *authenticated*, not merely token-authorized — unlocking SI-7 / 7(x) via findings' provenance-verified path (TFND-210).
* **Air-gap works via Tier 1 only:** managed-key signing is a local crypto op needing no Sigstore infrastructure, and findings' verify is offline by construction (DSSE PAE against the registered trust root, no Rekor). **Tier-2 keyless is online/Rekor-bound and explicitly not air-gap-capable** — nobody should read keyless as offline.
* Federal-friendly by construction: signing keys stay in a FIPS-validated HSM/KMS; the agency's own CA is the trust root; no exportable key material lives in CI.
* Trust is a spectrum, not a binary: Tier-1 managed-key/CA-chain and Tier-2 keyless/Rekor are both "trusted" (different roots); unsigned is "advisory." findings tiers and decides gating; producers do not.
* Cheap and incremental: **one signing call per build-state**; a re-posted axis re-signs only the tiny digest manifest, never the artifacts.

## Notes

* Manifest format is a standard **in-toto v1 Statement + DSSE** (cosign-native): subject = commit SHA, predicate = the re-signable `{ (axis, artifactId) → sha256, toolCode, toolVersion, controlRefs }` inventory; latest signed manifest wins per slot.
* **Contract pinned with tamp-findings (their two-stage determination):**
  * Cert-chain / X.509 path validation is **net-new** on their side (current trust root is bare ECDSA/RSA keys); they will add an `X509Chain` path-validate-to-CA-roots + leaf identity/EKU mode. Leaves are **ECDSA P-256/384 or RSA-PKCS1/SHA-256 — no Ed25519** (BCL gap on their side; unneeded given cosign/HSM norms).
  * Offline verify is **already native** — their `DsseVerifier` checks the DSSE PAE against the trust root with **no Rekor lookup**. (Conversely, they do not do keyless-verify yet — Tier-2 is later on both sides.)
  * Ingest is a **new build-level endpoint**, modeled on their existing `/sbom-snapshots/{id}/provenance` (same `DsseVerifier` + trust root + the new cert-chain mode), **keyed on commit SHA**, manifest-first, per-item digest match; policy tiers decide unmatched/unsigned handling. This is **TFND-210**'s producer contract.
  * Builds on TFND-159 (DSSE verify + trust root), TFND-160 (key-backed signature), TFND-162 (trust tier).
* The tool-provenance stamp (`toolCode` + `toolVersion` + `controlRefs`) is the same one used for control-driven tool selection and evidence claims; signing wraps the per-run manifest of those stamps. Tracking: tamp-conformance epic #15.
* Cross-refs: [ADR 0003](0003-findings-system-of-record.md) (findings owns transport/verification/scoring), [ADR 0004](0004-per-project-framework-and-evidence-contract.md) (the evidence event contract); core ADR 0023 (canonical event shape).
