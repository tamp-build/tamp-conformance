# ADR 0003: tamp-findings is the system of record; the tooling is analysis-only

* Status: Accepted
* Date: 2026-09-28
* Deciders: scott

## Context and Problem Statement

The original design (the tamp-conformance / tamp-ztt design briefs) put the rules in the **governed repo's git** — a committed, human-reviewed `adr-rules.json` lockfile, reviewed via PR, with branch protection as the control. It also implied the scoring/roll-up lived in the analyzer (tamp-ztt).

That placement was reconsidered. tamp-conformance is nearly useless without tamp-findings to surface, gate, and attest the evidence; and each project's compliance framework, control set, and policy templates already live in findings. Splitting the rules and the scoring away from findings created two systems of record and a drift surface between "what the rules tag" and "what findings attests against."

## Decision

**tamp-findings is the single system of record** for rulesets, results, and scoring. Scoring, aggregation, and roll-up happen **at findings' surface**, not in the tooling.

**tamp-conformance is an analysis-only execution loop:**
1. **Fetch** the ruleset/policy for the project from findings (e.g. `GET /projects/self/zt-profile`, `GET /projects/self/compliance-profile`).
2. **Process** — run the conformance engine (deterministic + semantic + reverse) against the repo + ADRs.
3. **Report** — POST the evidence back to findings (`POST /ingest/conformance`).

**Generated rules are pushed to findings**, which owns their storage and review (in its policy-template surface), and serves them back to the runner. This **retires the git-committed `adr-rules.json` lockfile as the authoritative store.**

**This reverses the "rules live in the governed repo" principle** from the design briefs. It is recorded here as the deliberate architectural decision it is, per the tamp practice that a decision moves with a documented ADR — it does not drift.

The staleness discipline (an ADR changed without its rules being regenerated → fail closed) is retained; findings tracks it against the source hash.

## Consequences

* **One source of truth.** No divergence between the rules/scores the tooling would compute and what findings attests against.
* **Scoring collapses into findings.** Much of the tamp-ztt brief (scoring, roll-up, inheritance, provider registry, menu picks) is surface/config and belongs to findings; the tooling only *derives per-function / per-mandate evidence*. Fewer moving parts on the tooling side, one scoring authority.
* **Branch-protection concern for rules is moot** — the tooling never writes rules to git; review moves to findings' policy UI.
* **Trade-off:** rule review is no longer a git PR diff next to the ADR; it is a findings-surface workflow. Accepted, because findings owns policy review already (hard/soft templates, gates, dispositions).
* The generation-push client and the retirement of the git lockfile proceed on this decision; the ruleset-fetch and evidence-POST clients are already built against findings' contracts.

## Notes

Companion contracts owned by tamp-findings: the ruleset-serve endpoints, `POST /ingest/conformance`, the compliance-profile read (their ADR 0008), and the shared POA&M + scoring model. The conformance *evidence event* remains core-owned (core ADR 0023); see [ADR 0004](0004-per-project-framework-and-evidence-contract.md).

### Rule lifecycle (review gate + supersession)

"Review moves to findings' policy UI" (above) is realized as an explicit rule lifecycle, owned by findings and driven by this tooling's pushes/verdicts. Validated live end-to-end against a findings deployment.

- **Draft → Reviewed.** A pushed (or regenerated) rule lands `Draft` = advisory (emits verdicts, never blocks, never raises a POA&M). A human promotes it to `Reviewed` in findings; only then does it enforce — the `adrConformance` gate blocks on a `Reviewed` + failing + undispositioned rule, and a mandate POA&M is raised only for a `Reviewed` mandate rule. `Draft`/`Reviewed` gate **both** the block and the POA&M axes identically.
- **Regeneration forces re-review.** A push whose rule *content* changed at the same `(adrRef, ruleId)` forces that rule back to `Draft` regardless of the pushed `reviewStatus` (detected via the per-rule `rulesSha` this client emits + a content-field diff for annotations outside the hash). An identical re-push preserves review. This keeps an edited/regenerated decision from silently continuing to enforce.
- **POA&M supersession.** Invalidating a rule (forced `Draft`, or retired) auto-cancels any POA&M it backed, with an audited reason naming the superseding generation; the push response returns these and the client surfaces them as `AdrRulesPushResult.Superseded`. A still-failing mandate re-raises a fresh POA&M once its new rule is reviewed.

The producer's role is: push generations (with a stable per-rule `rulesSha` as the change signal), carry `reviewStatus` on the wire (findings overrides it on content change), forward verdicts (which trigger the mandate reconciler on ingest), and surface `superseded[]`. Everything else — promotion, gating, POA&M raise/cancel, audit — is findings-side. See the README's "Rule lifecycle & the review gate" for the user-facing walkthrough.
