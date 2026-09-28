# ADR 0001: Conformance engine model — three capabilities, four-valued verdict, abstention, adversarial verify

* Status: Accepted
* Date: 2026-09-28
* Deciders: scott

## Context and Problem Statement

An Architecture Decision Record captures a decision, its context, and its consequences — then rots: the code drifts, the decision is forgotten, and nobody notices until an audit or an incident. There is no continuous check that the code still honors what was decided.

tamp-conformance closes that loop with AI in the pipeline. The hard constraint: **the model must be the weakest link that cannot hurt you** — it does the judgment a machine can't automate, but never the part a deterministic check can do, and never without a safety net. A tool that silently enforces a hallucinated verdict is worse than no tool.

## Decision

**Three capabilities.**
1. **Rule generation** — a model turns ADR prose into machine-checkable rules.
2. **Rule examination (conformance)** — code is checked against those rules.
3. **Reverse examination** — decisions the *code* made that no ADR records (advisory only, never gates).

**A four-valued verdict**, not pass/fail: `pass | fail | unknown | error`. The load-bearing case is **`unknown`** — a check that did not run, or a semantic check that could not decide, is an unanswered question, not a green light. It blocks (with a different remedy than a real failure) and is *never* a silent pass. "didn't run" ≠ "passed."

**Deterministic-first with semantic escalation.** Rules are classified `deterministic` (a pure regex/predicate — free, reproducible, safe to gate) or `semantic` (a judgment no regex can settle — needs a model). The model only touches what a machine genuinely cannot.

**Abstention over invention.** At every model step, emitting nothing beats emitting a shaky rule or a guessed verdict.

**Adversarial verify decides fail-vs-unknown.** A claimed semantic `fail` must be independently confirmed by a second pass that re-quotes *both* the ADR text and the offending code line. If it can't, the verdict is downgraded to `unknown`, never shipped as a false `fail`.

**Write/read target split.** Generation writes only the working tree (no git operation — branch protection is respected by construction). Checking is read-only and never writes.

**Verdicts are frozen evidence.** A non-deterministic (model-produced) verdict is emitted with its commit hash, rules hash, and model id and is *not* recomputed later — the frozen event is the reproducible record (see [ADR 0004](0004-per-project-framework-and-evidence-contract.md) and core ADR 0023).

## Consequences

* **Robust to model quality.** Generation is human-reviewed; a weak model means "more to correct." A check that can't decide degrades to `unknown` (ask a human), never a wrong answer enforced.
* Deterministic rules cost nothing and carry no hallucination risk; the model's budget is spent only where prose→code truly needs judgment.
* The adversarial pass is what makes a semantic verdict safe to gate on.
* Every path where the model could be wrong degrades to "ask a human," not "wrong answer, enforced." That is the defensibility property.
