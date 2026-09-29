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

## The rules file

`adr-rules.json` lives in the **governed repo's** git, not here — versioned intent next to the code it
governs. Each rule-set records the `sourceSha` of the ADR it came from; the gate fails closed when an
ADR changed without its rules being refreshed, closing the loophole where an edited decision silently
stops being enforced.

## License

MIT © 2026 Scott Singleton
