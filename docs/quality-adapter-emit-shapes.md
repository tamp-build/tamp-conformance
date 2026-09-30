# Quality + SAST adapter — frozen emit shapes (producer → findings)

Status: **Proposed for field-name pinning** (2026-09-30). Companion to [ADR 0006](adr/0006-quality-sast-evidence-adapter.md) and epic #16. Same handshake used for the `/raw` endpoints and the signing manifest: the producer proposes the wire shapes below; **tamp-findings pins the exact field names / endpoint paths**, then both sides freeze.

All four shapes carry the standard build envelope the other ingest endpoints already use (camelCase JSON):

```
client, project, component, componentKind, version, commitSha, branch
```

Build identity is reconciled by `commitSha` (findings' `BuildResolver`), exactly as the existing `/ingest/*` and `/raw` endpoints. Samples below are drawn from the live tamp-core SonarCloud analysis (`tamp-build_tamp`): 58 issues (45 code_smell / 11 vulnerability / 2 bug), gate = ERROR on `new_reliability_rating`, ncloc 12306.

---

## Shape 1 — Typed findings (the routing change)

Endpoint: **existing `POST /ingest/findings`** (`TampIngestClient.PostFindingsAsync`). One request per source scanner; **every finding carries `type`**; findings routes by `type`, and **`type` overrides the scanner→bucket default**.

Producer contract:
- The adapter **dedups `(ruleId, filePath, line)`** across sources before emit (canonical-source-per-language), so no cross-scanner double count reaches findings.
- Re-posting a source's findings is **replace-by-source** (idempotent per `(scanner)` within the build).

Two additions to the current `IngestFinding` (`{ ruleId, title, description, severity, filePath, line, snippet, subCategory }`):

| Field | Values | Meaning |
|---|---|---|
| `type` **(new, routing key)** | `bug` \| `code_smell` \| `vulnerability` \| `security_hotspot` | Route: `bug`/`code_smell` → **quality**, `vulnerability`/`security_hotspot` → **SAST**. Overrides `scanner`→bucket. |
| `source` **(new, provenance)** | object (below) | The native source values, preserved so findings can re-derive under its own policy (MQR, etc.) without losing information. |

`severity` stays on the canonical 5-scale (`Info`/`Low`/`Medium`/`High`/`Critical`). Sonar legacy severity maps `BLOCKER→Critical, CRITICAL→High, MAJOR→Medium, MINOR→Low, INFO→Info`; the raw values ride in `source`.

New enum value needed: **`ScannerKind.SonarQube`** (not in Tamp.Ingest.V1 0.2.2 today).

```jsonc
// POST /ingest/findings
{
  "client": "Tamp", "project": "tamp-core", "component": "tamp-core", "componentKind": "solution",
  "version": "1.17.3", "commitSha": "6e9ed40…", "branch": "main",
  "scanner": "sonarqube",                 // ScannerKind.SonarQube (new)
  "findings": [
    {
      "ruleId": "csharpsquid:S4036",
      "type": "vulnerability",            // → SAST (routing key)
      "severity": "Low",                  // MINOR → Low on the 5-scale
      "title": "Use an absolute path for this command.",
      "description": "Use an absolute path for this command.",
      "filePath": "src/Tamp.Core/WorkerIdResolver.cs",
      "line": 74,
      "snippet": null,
      "subCategory": "csharpsquid",       // Sonar repository (optional finer tag; NOT the routing key)
      "source": {                         // provenance — native values, findings may re-derive
        "tool": "sonarqube", "severityRaw": "MINOR",
        "impacts": [ { "softwareQuality": "SECURITY", "severity": "LOW" } ],
        "effort": "15min", "issueKey": "…", "analysisId": "AY9…"
      }
    },
    {
      "ruleId": "csharpsquid:S6966",
      "type": "code_smell",               // → quality (even though many Roslyn rules default to SAST bucket)
      "severity": "Medium",               // MAJOR → Medium
      "title": "Await LoadFromFileAsync instead.",
      "filePath": "tools/findings-ingest/Program.cs",
      "line": 138,
      "subCategory": "csharpsquid",
      "source": { "tool": "sonarqube", "severityRaw": "MAJOR",
                  "impacts": [ { "softwareQuality": "RELIABILITY", "severity": "MEDIUM" } ], "effort": "5min" }
    }
  ]
}
```

**Roslyn/SonarAnalyzer source** emits the identical shape with `"scanner": "roslyn"` and `type` derived from the SARIF `properties.category` (`"<Severity> <Type>"`), proving the type-overrides-scanner routing.

---

## Shape 2 — Scan-ran receipt (SA-11(1))

Endpoint: **existing `POST /ingest/scan-runs`** (`PostScanRunsAsync`). Existing `ScanRunReceipt` shape; only the new `ScannerKind.SonarQube` value + a provenance `notes` convention (mirrors the Grype receipt). This is what makes "0 findings" mean *scanned*, not *absent*.

```jsonc
// one receipt within the existing ScanRunsIngestRequest.receipts[]
{
  "scanner": "sonarqube",
  "status": "Succeeded",
  "startedAt": "2026-09-30T13:00:00Z",
  "completedAt": "2026-09-30T13:02:11Z",
  "findingsCount": 58,
  "toolName": "SonarCloud",
  "toolVersion": "server-2026.3",
  "notes": "server=https://sonarcloud.io; projectKey=tamp-build_tamp; analysisId=AY9…; branch=main; ncloc=12306; issues=58(vuln=11,bug=2,smell=45); hotspots=0"
}
```

---

## Shape 3 — Quality-gate verdict (SA-15 → new `qualityGate` gate)

A small **attested external control result** — the tool/CI system already rendered the gate decision; the producer *attests* it (it does not decide). Feeds the new `qualityGate` gate.

Proposed endpoint: **`POST /ingest/quality-gate`** (findings to confirm path — could also be a variant of an attested-result endpoint). Grounded in `alert_status` + `quality_gate_details`.

```jsonc
// POST /ingest/quality-gate
{
  "client": "Tamp", "project": "tamp-core", "component": "tamp-core", "componentKind": "solution",
  "version": "1.17.3", "commitSha": "6e9ed40…", "branch": "main",
  "source": "sonarqube",
  "status": "fail",                        // pass | fail | warn  (from alert_status OK/ERROR/WARN)
  "attestedAt": "2026-09-30T13:02:11Z",
  "analysisId": "AY9…",                    // pins the exact analysis; concurrent builds can't race
  "conditions": [
    { "metric": "new_reliability_rating",       "op": "GT", "threshold": "1", "actual": "3",   "status": "fail" },
    { "metric": "new_security_rating",          "op": "GT", "threshold": "1", "actual": "1",   "status": "pass" },
    { "metric": "new_maintainability_rating",   "op": "GT", "threshold": "1", "actual": "1",   "status": "pass" },
    { "metric": "new_duplicated_lines_density", "op": "GT", "threshold": "3", "actual": "0.0", "status": "pass" },
    { "metric": "new_security_hotspots_reviewed","op": "LT","threshold": "100","actual": "100.0","status": "pass" }
  ],
  "measures": { "bugs": 2, "vulnerabilities": 11, "codeSmells": 45, "securityHotspots": 0, "ncloc": 12306, "sqaleIndex": 240 }
}
```

Note: whether `status: "fail"` **blocks** is findings' policy on the `qualityGate` gate — the producer only reports what the gate evaluated to.

---

## Shape 4 — Analysis-coverage (new type → new `analysisCoverage` gate)

New evidence type + endpoint. **Only tamp can compute this** (full checkout in CI). Makes "0 findings" meaningful and surfaces mixed-language blind spots.

Proposed endpoint: **`POST /ingest/analysis-coverage`**. Per-build, per-language rows + an overall roll-up.

```jsonc
// POST /ingest/analysis-coverage
{
  "client": "Tamp", "project": "tamp-core", "component": "tamp-core", "componentKind": "solution",
  "version": "1.17.3", "commitSha": "6e9ed40…", "branch": "main",
  "computedAt": "2026-09-30T13:02:30Z",
  "languages": [
    {
      "language": "csharp",
      "filesTotal": 214, "filesAnalyzed": 214, "loc": 12306,
      "percentAnalyzed": 100.0,
      "analyzedByTools": [ "sonarqube", "roslyn" ],
      "unanalyzedPaths": []                 // sample of the remainder when < 100%
    },
    {
      "language": "typescript",
      "filesTotal": 0, "filesAnalyzed": 0, "loc": 0,
      "percentAnalyzed": 100.0,
      "analyzedByTools": [],
      "unanalyzedPaths": []
    }
  ],
  "overall": {
    "filesTotal": 214, "filesAnalyzed": 214, "percentAnalyzed": 100.0,
    "languagesWithFootprintNoAnalyzer": [],  // e.g. ["typescript"] in a .NET+React repo w/ only C# analyzed → the visible gap
    "excludes": [ "bin/**", "obj/**", "node_modules/**", "**/*.g.cs", "**/generated/**" ]
  }
}
```

The **`analysisCoverage`** gate (advisory first, enforcing later) blocks when `languagesWithFootprintNoAnalyzer` is non-empty above a footprint threshold, or any language falls below a coverage floor. Kin to `missingScanners`, but at file/language granularity.

---

## What findings pins

1. Field name for the routing key (`type`) + the controlled vocabulary + how `severity` is carried (5-scale + `source` raw).
2. `ScannerKind.SonarQube` added to Tamp.Ingest.V1 (+ their routing table entry).
3. Endpoint path + field names for **Shape 3** (`/ingest/quality-gate`?) and the `qualityGate` gate key.
4. Endpoint path + field names for **Shape 4** (`/ingest/analysis-coverage`?) and the `analysisCoverage` gate key + thresholds ownership (theirs).

Producer source of truth (SonarCloud pull vs on-prem SonarQube vs in-CI Roslyn SARIF) is tamp's choice — the four shapes above are identical regardless.
