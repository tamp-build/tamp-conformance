# ADR 0004: Per-project framework + the conformance evidence contract

* Status: Accepted
* Date: 2026-09-28
* Deciders: scott

## Context and Problem Statement

Every project differs — its compliance framework (NIST 800-53, SOC 2, ISO 27001, CISA ZTMM), its stack, its conventions. Generic prompting produces noise (an early dogfood run confirmed it), and control ids tagged in the wrong framework are useless for attestation. Separately, the conformance *evidence* must ride the ecosystem's canonical event shape so every consumer reads one contract.

## Decision

**Per-project extraction profile.** `ExtractionProfile` injects per-project context (control framework + example/catalogue hint, project/stack conventions) into the rule-extraction prompt. The prompt's **integrity clauses** (deterministic/semantic split, abstention, strict JSON) are fixed and never project-tunable; only the project *context* is injectable.

**Control framework via two mutually-exclusive sources** (`ControlProfileSource`, fail-closed if both/neither):
* **Manual** — framework specified in config (standalone / export-elsewhere).
* **FromFindings** — framework + controls fetched from tamp-findings via the project's ingest token, so extraction matches the exact project the evidence lands in. The integrated norm; eliminates drift between what the rules tag and what findings attests against.

**Evidence rides core ADR 0023.** Verdicts are emitted as the canonical `conformance.evaluated` `BuildEvent` (four-valued verdict, structured reason, `provenance`, `controlRefs`). Consumers ingest that shape unchanged.

**Overlays are additive `dimensions`, not a widening field list.** ZT and mandate metadata (`ztPillar` / `ztFunction` / `ztStage` — the stage the decision represents, orthogonal to the verdict — and `mandateId`) travel as additive fields/dimensions on the event beside `controlRefs`. `controlRefs` are **opaque framework ids** (mapped by findings via the compliance-profile); the framework catalogue is consumer-side. Schema stays additive-only.

**Disposition is findings-side.** The producer never emits accepted-deviation/waiver state; disposition (Suppression/VEX + role-gated accept) lives in findings.

## Consequences

* Extraction is correct per project, not generically noisy; the framework the rules tag is the one the project is audited against.
* The evidence contract is one shape ecosystem-wide (core-owned), extended only additively — findings and ztt-derivation consume the same event.
* CISA ZTMM is "just another framework vocabulary" through the existing per-project fetch — not a new pipeline.
* Frozen model verdicts carry `modelId` + `rulesSha` + `commitSha`, satisfying reproducibility for non-pure checks.

## Notes

The ingest transport, the ruleset-serve/compliance-profile reads, and scoring are findings-owned (see [ADR 0003](0003-findings-system-of-record.md)); the event *shape* is core-owned (core ADR 0023). Clients (`FindingsEvidenceClient`, `FindingsComplianceProfileClient`) are already wired to those contracts.
