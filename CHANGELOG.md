# Changelog

All notable changes to Tamp.Conformance are documented in this file.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/). Pre-1.0 versions may break public API freely between minor versions; the `0.x` line is a stabilization run.

## [Unreleased]

## [0.1.0] — 2026-09-28 — Scaffold: deterministic ADR-conformance

Initial cut. Emits the `conformance.evaluated` contract from Tamp.Core 1.17.0 (core [ADR 0023](https://github.com/tamp-build/tamp/blob/main/docs/adr/0023-attestation-evidence-contract.md)).

### Added

- **Rule model + loader.** `AdrRule` / `AdrRuleSet` and `RuleLoader` — camelCase JSON `adr-rules.json` (the committed "lockfile for architectural intent"), with `IsStale` detecting an ADR that changed without its rules being refreshed (`sourceSha` mismatch).
- **Deterministic conformance check (`ConformanceCheck`).** Pure predicate evaluation (forbidden/required regex, glob-scoped) yielding the four-valued `pass | fail | unknown | error` verdict — a matched forbidden pattern is `fail` with the offending line as evidence; a scope that matched no files is `unknown` (never a silent pass); a fault is `error`. Emits `conformance.evaluated` via `BuildEvents.Conformance(...)`.
- **Rule generation scaffolding (`RuleGeneration`).** `Seed` (empty rule-set stamped with the ADR hash) and `Generate` (via a host-provided `IRuleExtractor`).
- **Reverse-examination triggers (`ReverseExamination`).** Deterministic decision-signal scan feeding a host-provided `IDecisionDetector`; advisory only.
- **Semantic seams.** `ISemanticEvaluator` / `IRuleExtractor` / `IDecisionDetector` — the model-backed plug-in points the deterministic path does not depend on.
- **BYOK model seam + provider-agnostic rule extraction.** `IChatCompletion` (thin provider shim) + `ModelConfig` (endpoint-overridable for gov/Azure/air-gapped endpoints), and `LlmRuleExtractor` — an `IRuleExtractor` that turns ADR prose into rules via any provider, with the extraction prompt, deterministic/semantic classification, calibrated abstention, and JSON parsing all in core (unit-tested without a network).
- **`Tamp.Conformance.Anthropic` adapter.** `AnthropicChat : IChatCompletion` — a thin shim over the Anthropic Messages API (the recommended default), endpoint-overridable, key resolved from `ANTHROPIC_API_KEY` or `~/.claude/credentials.json`, never emitted. Verified end-to-end extracting rules from a live ADR.
- **`Tamp.Conformance.OpenAiCompatible` adapter.** One `OpenAiCompatibleChat : IChatCompletion` that, via a base-URL override, covers **OpenAI, Azure OpenAI, Poolside, and any self-hosted / air-gapped OpenAI-compatible endpoint** (vLLM, Ollama). Configurable auth (`Authorization: Bearer` or Azure's raw `api-key` header), a `ForPoolside` convenience, clean standard payloads (no `cache_control`), and tolerant of an extra `reasoning_content` (Poolside thinking).
- **Target surface + orchestration.** `ConformanceOptions` / `RuleStore` (per-ADR rule-set layout, ADR + code-file discovery) and `ConformanceRunner`: `GenerateRules` (write-to-working-tree only — no git op, so branch protection is respected by construction) and `Check` (read-only; fails closed on **missing** or **stale** rules, runs the deterministic path, routes semantic rules to the evaluator or `unknown`, and honors advisory vs enforcing). Exposed as Tamp.Components-style DIM targets `IAdrRules` (write) and `ICheckAdrConformance` (read-only gate) via `IHazConformance`.
- **Semantic evaluator (`LlmSemanticEvaluator`).** Provider-agnostic `ISemanticEvaluator` over any `IChatCompletion`: an evaluation pass returns a four-valued verdict, and — only on a claimed `fail` — an **adversarial verify pass** confirms it, downgrading any violation that can't re-quote both the ADR text and the exact code line to `unknown` (never a false `fail`). A `fail` lacking quotable evidence is `unknown` without even needing the verify call; file-scope budget-capped; verdict frozen with `modelId` for reproducible evidence.

- **adr-rules store clients (`FindingsAdrRulesClient`).** findings is the authoritative adr-rules store (findings ADR 0012 / tamp-conformance ADR 0003). `PushGeneration` → `POST /projects/self/adr-rules` (whole-set replace; upsert by (adrRef,ruleId), absent rules soft-retire; returns {upserted,retired,active}); `FetchActive` → `GET /projects/self/adr-ruleset` (the active set the analyzer runs). `AdrRule` extended with ZT/mandate annotations (`ztPillar`/`ztFunction`/`ztStage`/`mandateId`/`reviewStatus`); our check definition rides `checkSpec` (opaque to findings, symmetric for us). Bearer `prj_`.
- **ZT ruleset fetch (`FindingsZtProfileClient`).** `IZtProfileClient` fetching findings' `GET /projects/self/zt-profile` (Bearer `prj_`; 404 → not-ZT-scored → null) — the CISA ZTMM model (pillars → functions → stages, cross-cutting folded per-pillar) + the project's operational mandates with an `inForce` flag (the emit-but-flag-dormant contract, driven by the EO registry). findings is the system of record (ADR 0003); the tooling fetches, derives, and posts back.
- **tamp-findings ingest wiring.** `FindingsEvidenceClient` (`IEvidenceIngestClient`) POSTs the canonical BuildEvent stream (NDJSON) to findings' `POST /ingest/conformance` (Bearer `prj_`/`cli_` ingest token); findings filters `conformance.evaluated` and skips the rest, so the whole `TAMP_EVENTS` stream can be forwarded. `FindingsComplianceProfileClient` re-pointed to findings' canonical read path `GET /projects/self/compliance-profile` (un-prefixed; versioning in the body). Both contracts reconciled directly with tamp.findings.
- **Per-project extraction profile + control-framework source.** `ExtractionProfile` injects per-project context (control framework, control example/catalogue hint, project/stack conventions) into the extraction prompt while the integrity clauses (deterministic/semantic split, abstention, strict JSON) stay fixed. `ControlProfileSource` picks the framework via **two mutually-exclusive tracks** — **Manual** (standalone/export) or **FromFindings** (fetch the project's framework + controls from tamp-findings via the ingest token, so extraction matches the exact project the evidence lands in). `FromConfig` enforces the exclusivity fail-closed. `IComplianceProfileClient` + `FindingsComplianceProfileClient` implement the `GET /api/v1/projects/self/compliance-profile` contract.

- **Reverse-examination detector (`LlmDecisionDetector`).** Provider-agnostic `IDecisionDetector`: deterministic `ReverseExamination.Triggers` flag cheap candidates (new dependency, new endpoint, …), then the model judges which are architecturally significant AND uncovered by an existing ADR (abstains generously). `ReverseExamination.Detect(...)` runs the end-to-end flow and surfaces each as an **advisory** SARIF `note` diagnostic (`undocumented-decision:<kind>`) — never blocks. `RuleStore.AdrSummaries` supplies the "known ADRs" context.

### Not yet

- Further BYOK adapters where auth diverges from OpenAI-compatible: Bedrock (SigV4), Vertex.
- Stamping the resolved framework into the committed `adr-rules` (reproducibility).
- A worked dogfood: committed `adr-rules` for tamp's own ADRs + the gate wired into a build. (Requires the tamp-findings compliance-profile endpoint for the integrated path.)
