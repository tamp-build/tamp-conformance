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

## Model support (BYOK)

The semantic path is **bring-your-own-key**: pick a provider by dropping in an `IChatCompletion` adapter.
The API key/credentials are the adapter's concern, read from the environment (or `~/.claude/credentials.json`
for local Anthropic use) and **never emitted** on the build stream.

| Adapter package | Covers |
|---|---|
| `Tamp.Conformance.Anthropic` | Anthropic Messages API (the recommended default) |
| `Tamp.Conformance.OpenAiCompatible` | OpenAI, Azure OpenAI, Poolside, and any self-hosted / air-gapped OpenAI-compatible server — **vLLM, Ollama** — via a base-URL override |
| `Tamp.Conformance.Bedrock` | AWS Bedrock (Claude models), hand-rolled SigV4, GovCloud/FIPS/VPC endpoints |

Ollama needs no extra code — point `ModelConfig.Endpoint` at `http://localhost:11434/v1` and the
OpenAI-compatible adapter talks to it; `modelId` is frozen into provenance as `ollama/<model>`.

### How the models compare

A first-pass evaluation of **rule extraction** (the hardest structured task — ADR prose → a strict-JSON
rule-set) on a representative, rule-rich ADR. Judged on schema-valid JSON, rule count, how many rules
carry a runnable regex, pattern correctness, and latency.

| Model | Provider | Latency | Rules | JSON/schema | Pattern quality | Verdict |
|---|---|--:|--:|---|---|---|
| **claude-sonnet-5** | Anthropic | ~31s | 10 | ✅ | Excellent — precise, well-scoped | Best quality |
| **claude-opus-4-8** | Anthropic | ~12s | 9 | ✅ | Excellent — clean, correct | Fast + precise |
| **claude-haiku-4-5** | Anthropic | ~15s | 13 | ✅ | Good — mild over-extraction | Strong value pick |
| **qwen3:14b** | Ollama (local) | ~94s | 3 | ✅ | Mixed — some malformed patterns | Only viable local model |
| **llama3.1:8b** | Ollama (local) | ~12s | 4 | ✅ | Broken — regex matches everything | Unusable output |
| **gemma4:12b** | Ollama (local) | ~204s | — | ❌ missing required fields | — | Failed |
| **qwen3.5:9b** | Ollama (local) | ~169s | — | ❌ no JSON emitted | — | Failed |
| **granite4.2:8b** | Ollama (local) | ~349s | — | ❌ no JSON emitted | — | Failed |

**Takeaways.** All three Anthropic models are production-viable; **Sonnet/Opus** for the write-path
(rule generation lands as attestation evidence and is human-reviewed, so fidelity wins over speed) and
**Haiku** for the high-volume check/semantic-eval path. Among local models, only **qwen3:14b** produced
schema-valid, runnable rules — sparse and slow, but a reasonable **air-gapped fallback**; the smaller
(8–9B) and non-adhering models either emit broken regex or can't hold the strict-JSON contract.

> **Caveats — read this as a signal, not a benchmark.** Measured on a *single ADR* with a *single run*
> per model (2026-09; core commit `ec37295`). Semantic verdicts are non-deterministic (the newest Claude
> models have *deprecated* the `temperature` knob, so runs vary), and only the *extraction* task was
> measured — local models may fare better on the simpler yes/no semantic-eval verdicts. Latencies include
> cold model loads for local inference. Your ADRs, hardware, and Ollama context settings will shift these
> numbers. Re-run against your own corpus before trusting a model for attestation-grade output.

## Where the rules live

**tamp-findings is the authoritative store** for rules, results, and scoring ([ADR 0003](docs/adr/0003-findings-system-of-record.md)).
Generation writes rules to the working tree as a review artifact, then **pushes** them to findings
(`POST /projects/self/adr-rules`); the gate **fetches** the active set back (`GET /projects/self/adr-ruleset`)
and runs against it (`ConformanceRunner.CheckFetched`). Each rule carries the `sourceSha` of the ADR it came
from, so the staleness discipline still holds — an ADR that changed without its rules being regenerated fails
closed, closing the loophole where an edited decision silently stops being enforced.

## Rule lifecycle & the review gate

A generated rule is a *machine's reading* of an ADR — it doesn't get to enforce anything until a human has
looked at it. Enforcement is gated on a rule's **review status**, and that status self-heals when the rule
changes underneath it. The lifecycle is owned by tamp-findings; tamp-conformance drives it by pushing
generations and forwarding verdicts.

**Draft → Reviewed.** A freshly generated (or regenerated) rule lands **`Draft`**. A Draft rule is
**advisory**: it still produces verdicts and evidence, but it *never blocks a build and never raises a POA&M*.
A human promotes a rule to **`Reviewed`** in findings' policy surface. Only a Reviewed rule enforces:

- the **`adrConformance` gate** blocks a build only on a rule that is `Reviewed` **and** failing **and**
  undispositioned (a semantic fail additionally needs the verify pass to confirm);
- a **mandate POA&M** is raised only for a `Reviewed` mandate-mapped rule whose verdict is fail/unknown.

So `Draft` = advisory on *both* axes (gate and POA&M tracker); `Reviewed` = enforceable on both. This is the
human-first gate — a model's extraction can't gate a release until someone signs off on it.

**Regeneration forces re-review.** Push a generation whose rule *content* changed at the same
`(adrRef, ruleId)` — a different check, intent, or ZT/mandate mapping — and findings forces that rule **back to
`Draft`**, regardless of the `reviewStatus` the push claims. (Change is detected by the per-rule `rulesSha` the
client emits, plus a content-field diff for the annotations outside that hash.) An *identical* re-push
preserves the existing review, so routine re-generation doesn't churn it. The effect: an edited ADR that
regenerates a rule can't silently keep enforcing the old — or a changed — intent; a human must look again.

**POA&M self-heal (supersession).** When a rule is invalidated — forced to `Draft` by a content change, or
retired by being absent from a push — any POA&M that rule backed is **auto-cancelled** ("superseded") with an
audited reason that names the superseding generation, rather than dangling with a due date against a decision
that no longer holds. Superseded POA&Ms are reported in the push response and surfaced by the client as
`AdrRulesPushResult.Superseded` (`poamId` / `mandateId` / `reason`). If the mandate still fails under the new
rule set, a fresh POA&M is raised once the new rule is reviewed.

```text
  generate ──push──▶ Draft ──human review──▶ Reviewed ──fail verdict──▶ blocks / POA&M
                       ▲                          │
                       └──── content change ──────┘   (regeneration forces re-review;
                            (POA&M auto-cancels,        an identical re-push keeps Reviewed)
                             audited, in superseded[])
```

The full loop — amend an ADR → regenerate → the invalidated rule drops to Draft → its block clears and its
POA&M supersedes, all with an audit trail — is validated end-to-end against a live findings deployment.

## License

MIT © 2026 Scott Singleton
