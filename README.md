# Tamp.Conformance

Agentic **ADR-conformance review** for [Tamp](https://github.com/tamp-build/tamp) builds. It compares a
repository's code against its Architecture Decision Records and emits conformance verdicts on the
canonical `BuildEvent` stream (`conformance.evaluated`, core [ADR 0023](https://github.com/tamp-build/tamp/blob/main/docs/adr/0023-attestation-evidence-contract.md))
for downstream attestation in [tamp-findings](https://github.com/tamp-build/tamp-findings).

> **Status: 0.1.x — scaffold.** The deterministic (predicate) path is complete and tested. The
> semantic (model-backed) path is wired as plug-in seams (`ISemanticEvaluator`, `IRuleExtractor`,
> `IDecisionDetector`) that a host supplies; the in-box agent evaluator is the next milestone.

## Why

An ADR records a decision; nothing usually checks the code still honors it. Tamp.Conformance turns each
ADR into machine-checkable rules and verifies them in CI, so a documented decision that the code has
quietly drifted from becomes a visible, attestable finding — evidence for change-management controls
(NIST CM-3/6, SA-15), not just a stale document.

## Three capabilities

| Capability | Type | What it does |
|---|---|---|
| **Rule generation** (`RuleGeneration`) | generator | Turns an ADR's prose into a committed `adr-rules.json` — a "lockfile for architectural intent," versioned in git next to the ADRs, human-reviewable. Deterministic scaffolding (hashing, seeding) needs no model; prose→rules extraction plugs in via `IRuleExtractor`. |
| **Rule examination** (`ConformanceCheck`) | gate | Checks code against the rules per PR. Deterministic rules run as pure predicates here; semantic rules go to `ISemanticEvaluator`. Emits four-valued verdicts. |
| **Reverse examination** (`ReverseExamination`) | detector | Flags decisions the *code* made with no covering ADR (change-control drift). Cheap deterministic triggers feed a model-backed `IDecisionDetector`. **Advisory only — never gates.** |

## The four-valued verdict

Verdicts are `pass | fail | unknown | error` (matching tamp-findings' model). The load-bearing case is
**`unknown`** — a semantic check that could not decide, or a deterministic probe whose scope matched no
files. `unknown` **blocks** (with a different remedy than `fail`) and is *never* a silent pass: a check
that did not run is an unanswered question, not a green build.

The verify pass decides `fail` vs `unknown`: a claimed violation that cannot quote both the ADR text and
the offending code line resolves to `unknown`, not `fail`.

## Reproducibility

A semantic (model-produced) verdict is non-deterministic, so it is **frozen** into the attestation
snapshot with its `commitSha` + `rulesSha` + `modelId` (a `Provenance` record on the event) rather than
recomputed — the "snapshot the verdict" posture from core ADR 0023 / tamp-findings ADR 0001.

## The rules file

`adr-rules.json` lives in the **governed repo's** git, not here — versioned intent next to the code it
governs. Each rule-set records the `sourceSha` of the ADR it came from; the gate fails closed when an
ADR changed without its rules being refreshed, closing the loophole where an edited decision silently
stops being enforced.

## License

MIT © 2026 Scott Singleton
