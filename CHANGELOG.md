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

### Not yet

- The semantic (model-backed) conformance *evaluator* behind `ISemanticEvaluator` + the adversarial verify pass.
- Further BYOK adapters where auth diverges from OpenAI-compatible: Bedrock (SigV4), Vertex.
- Component-interface / target surface for wiring the three capabilities into a consumer's `Build.cs`.
