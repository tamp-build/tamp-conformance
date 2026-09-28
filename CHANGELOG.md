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

### Not yet

- The in-box agent evaluator behind the semantic seams (next milestone).
- Component-interface / target surface for wiring the three capabilities into a consumer's `Build.cs`.
