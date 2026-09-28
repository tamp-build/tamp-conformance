# ADR 0002: Bring-your-own-key model provider seam + adapter packages

* Status: Accepted
* Date: 2026-09-28
* Deciders: scott

## Context and Problem Statement

tamp-conformance targets the compliance market — including FedRAMP/GovRAMP and air-gapped enclaves. Those adopters frequently **must** run the model inside their own authorization boundary (Claude via Bedrock GovCloud, Azure OpenAI in a gov region) or, when air-gapped, against a **self-hosted** endpoint with no external calls. Hardcoding a single provider (or `api.anthropic.com`) locks out exactly the customers the tool is for. Provider-agnosticism is a hard requirement, not a convenience.

## Decision

**A thin provider seam, `IChatCompletion`** (send system+user prompt → text), lives in the core library. All the valuable, provider-agnostic logic — the extraction prompt, the rule schema, JSON parsing, calibrated abstention, the adversarial verify — sits above the seam and is unit-tested with no network.

**Providers ship as separate adapter packages**, each a small HTTP shim implementing `IChatCompletion`:
* `Tamp.Conformance.Anthropic` — the recommended default (Messages API); endpoint overridable.
* `Tamp.Conformance.OpenAiCompatible` — one shim covering OpenAI, Azure OpenAI (`api-key` header mode), Poolside, and any self-hosted / air-gapped OpenAI-compatible endpoint (vLLM, Ollama) via a base-URL override.
* Further adapters (Bedrock/SigV4, Vertex) as their auth diverges.

Core depends on none of them; an air-gapped shop installs only the one it's authorized for.

**Per-capability provider selection.** The rule extractor and the semantic evaluator are separate seams, so an adopter can use different providers per job (e.g. a strong general model for generation, an in-boundary model for checking).

**Keys** are read from the environment (or `~/.claude/credentials.json` for local dogfooding), carried only in the request header, and **never emitted** on the build stream. The resolved `modelId` is frozen into the evidence provenance so an auditor can see exactly which model produced each verdict.

## Consequences

* Any provider works, including in-boundary and fully air-gapped — the tool fits the highest-compliance adopters, not just cloud users.
* Adapters are ~thin HTTP shims; adding a provider is small and isolated.
* The attestation story is intact under BYOK: the evidence records the model.
* Model choice degrades gracefully (see [ADR 0001](0001-conformance-engine-model.md)) — the human-review + `unknown`/verify safety nets absorb a weaker provider.
