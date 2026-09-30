# ADR 0006: Quality + SAST evidence adapter — multi-source, dedup + route-by-type, with analysis-coverage completeness

* Status: Draft (contract locked with findings 2026-09-30; held in draft until the emit shapes are frozen + findings ships type-routing / `qualityGate` / `/ingest/analysis-coverage`, in case implementation forces edits)
* Date: 2026-09-30
* Deciders: scott

## Context and Problem Statement

Code quality and static application security testing (SAST) are distinct compliance concerns — SA-11(1) (static analysis performed + documented), SA-15 (tooled SDLC), CM-3 / CM-4 (change control / PR gating), SI-2 (remediation) — yet the raw findings come intermixed from the same tools. A single SonarQube/SonarCloud analysis emits bugs, code smells, vulnerabilities, and security hotspots together; Roslyn / SonarAnalyzer running in CI does the same; opengrep emits security findings; ESLint and other-language linters emit mostly quality with some security. Feeding these to findings tool-by-tool would (a) misfile a Sonar code smell as a security finding just because it came from a "SAST" tool, (b) double-count the C# overlap between server-side SonarQube and in-CI Roslyn (which *is* SonarAnalyzer), and (c) — most dangerously — let **"0 findings" masquerade as "clean"** when the truth is "that language was never analyzed."

That last gap is acute in mixed-language repositories (e.g. a .NET backend + a React front end): running only C# analysis silently ignores a large fraction of the codebase, and **neither SonarQube nor findings can detect it** — SonarQube only knows what it was fed, and findings has no source checkout. Only the producer, tamp, sees the full tree in CI.

## Decision

**Governing invariant (inherited from ADR 0005) — producers generate evidence only; findings owns all policy.** The adapter resolves / scans / normalizes / accounts and emits *facts* — findings, receipts, verdicts-as-observed, coverage numbers — each stamped with provenance (`producedBy`, `toolCode`, `toolVersion`) and claimed `controlRefs`. It renders **no** pass/fail decision. Whether a quality gate blocks, whether coverage is adequate, whether a `(control, toolCode, version)` is acceptable — all of that is findings' judgment.

**Two logical reports — `quality` and `SAST` — routed by finding TYPE, not by tool.** `bug` / `code_smell` → quality; `vulnerability` / `security_hotspot` → SAST. A Sonar code smell never trips a SAST gate; a vulnerability lands in SAST no matter which tool produced it.

**The adapter is: normalize → dedup → route → aggregate → emit.** Each source is a pluggable `IQualitySource` producing a common `NormalizedFinding { producedBy, toolCode, toolVersion, ruleId, type, severity, file, line, message, controlRefs }`. Sources today: SonarQube/SonarCloud (quality + SAST, split by `type`), Roslyn/SonarAnalyzer SARIF (quality + SAST — its SARIF `properties.category` carries `"<Severity> <Type>"`, so type+severity need no external map), opengrep (SAST), ESLint / other-language linters (mostly quality; `eslint-plugin-security` → vulnerability). Dedup is by `(normalizedRuleId, file, line)` — the SonarQube ⇄ local-Roslyn C# overlap is real, so a finding is counted once but keeps both provenances; a **canonical-source-per-language** policy keeps overlap the exception rather than the rule.

**Analysis-coverage completeness is a first-class evidence type — only tamp can produce it.** The adapter includes a *coverage accountant*: (1) inventory source files by language, minus excludes (bin/obj, node_modules, generated/vendored); (2) attribute what each analyzer actually covered; (3) delta = inventory − union(analyzed) = the unanalyzed set; (4) emit per-language `% analyzed`, by which tool, plus the unanalyzed remainder. This is distinct from *test* coverage. Language-level in v1, file-level in v2. It makes "0 findings" meaningful: a clean result credits SA-11 **only alongside** an adequate coverage figure, and a language with a real footprint but no analyzer becomes a *visible gap*, not a silent pass.

**PR gating is the SCM's job; tamp attests the requirement.** Branch protection *requires* the SonarQube/SonarCloud status check, and GitHub performs the synchronous wait — so the async analysis becomes a real merge blocker. tamp's `bp`/`pr` posture check *attests* that this requirement is configured (CM-3 / CM-4); tamp does not itself enforce the merge gate.

**SonarQube's async CE task is handled by reading, not by tamp waiting.** In CI, `sonar.qualitygate.wait=true` blocks the pipeline until the compute-engine task finishes, at which point results are queryable. The adapter reads the outcome with one `measures/component` call (`alert_status` + `quality_gate_details` + magnitude counts = gate verdict + failing conditions + score core), plus `issues/search` and `hotspots/search` for per-finding drill-down, all pinned to the run's `analysisId` (via `report-task.txt` → `/api/ce/task`) so a concurrent build cannot race the read. The quality-gate outcome is emitted as an **attested external control result**, sidestepping any need for tamp to poll asynchronously.

## Contract with tamp-findings (LOCKED 2026-09-30)

findings confirmed the **typed-unified** shape (the lean option) and will make the corresponding ingestion changes:

1. **Typed-unified, findings routes by type.** The producer POSTs each finding tagged with its Sonar `type` and severity; findings routes by `type`. **`type` overrides the scanner → bucket default** — this is the one real ingestion change: today findings buckets by `ScannerKind` (Roslyn ⇒ SAST), but under the lock a Roslyn finding tagged `code_smell` lands in **quality**. The producer dedups `(ruleId, file, line)`; re-posts are replace-by-source (no double counting). `ScannerKind.SonarQube` is added; other sources keep their kind (type, not kind, drives the bucket).
2. **Process half.** A scan-ran receipt uses the existing `ScanRunReceipt` shape (`ScannerKind.SonarQube`; notes carry server / project / analysisId / version, like the Grype receipt) → SA-11(1). The quality-gate outcome is a small attested external control result (pass/fail + failing conditions) → a new **`qualityGate`** gate → SA-15.
3. **Analysis-coverage = new evidence type + new endpoint + new gate.** `POST /ingest/analysis-coverage` takes per-build, per-language rows `{ language, filesTotal, filesAnalyzed | loc, analyzedByTools[], percentAnalyzed, unanalyzedPaths[] (sample ok) }` plus an overall roll-up. A new **`analysisCoverage`** gate blocks when a language above a footprint threshold has no analyzer or falls below a coverage floor — advisory now (enforcement off org-wide), enforcing later. It is kin to findings' `missingScanners` gate, but at file/language granularity.

findings' work is **TFND-175 expanded** (SonarQube kind + type-routing + quality receipt/gate) plus a **new sub-ticket** for the analysis-coverage type + gate. The producer's source of truth (SonarCloud pull vs on-prem SonarQube vs in-CI Roslyn SARIF) is tamp's choice — the payloads to findings are identical.

## Consequences

* Quality and SAST are scored on the right axes regardless of which tool surfaced a finding; a code smell can never inflate a security gate, and a vulnerability is never lost in a "quality" bucket.
* "0 findings" is no longer a blind spot: SA-11 credit is conditioned on measured analysis coverage, and unanalyzed languages surface as explicit gaps.
* The producer stays dumb and auditable; findings remains the single seat of policy (routing table, gate thresholds, coverage floors, tool allow/deny).
* Mixed-language repos get honest evidence — the accountant is the only place in the pipeline with both the full checkout and the per-tool analyzed set, so it is the only place this can be computed.
* Cost of the SonarQube async model is paid by reading a completed analysis, not by tamp holding a connection open — the gate verdict is captured as an attested fact.

## Notes

* Dedup key is `(normalizedRuleId, file, line)`; provenance is retained per source so double-attributed findings remain traceable.
* Severity is normalized to a single scale (Blocker / Critical / Major / Minor / Info); ESLint (no native type) maps security-plugin rules ⇒ vulnerability, the rest ⇒ code_smell.
* Next producer step: **freeze the four emit shapes** (typed findings, scan-ran receipt, quality-gate verdict, analysis-coverage) and hand them to findings for per-endpoint field-name pinning — the same handshake used for the `/raw` endpoints and the signing manifest. Proposed shapes with concrete samples: [quality-adapter-emit-shapes.md](../quality-adapter-emit-shapes.md).
* Tracking: tamp-conformance epic #16. findings: TFND-175 (expanded) + a new analysis-coverage sub-ticket. Controls: SA-11(1) / SA-15 / CM-3 / CM-4 / SI-2.
* Cross-refs: [ADR 0003](0003-findings-system-of-record.md) (findings owns transport / verification / scoring), [ADR 0004](0004-per-project-framework-and-evidence-contract.md) (evidence event contract), [ADR 0005](0005-signed-evidence-bundles.md) (the authenticity layer these emissions are signed under; same governing invariant).
