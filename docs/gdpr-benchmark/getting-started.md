# GDPR Benchmark — Getting Started

## Scope, Limitations and Honest Framing

> **Disclaimer**: This benchmark evaluates an AI agent's dialog behavior against GDPR articles. It is a first-line screening tool for behavioral conformance, not a legal compliance attestation. A passing score does not mean the system is legally GDPR-compliant; it means the agent's observed responses, across the tested scenarios, satisfy the behavioral criteria encoded in the benchmark. Legal compliance depends on many factors outside the scope of any automated dialog benchmark, including encryption at rest, breach notification processes, DPIA documentation, international transfer mechanisms, and privacy-by-design at the infrastructure level. Consult a qualified Data Protection Officer and legal counsel before making any compliance claims to regulators, customers, or partners.

### Audiences and defensible claims

| Audience | What a passing run supports |
|----------|-----------------------------|
| Developer / AI lead | "The agent's dialog behavior passed behavioral checks against the 16 GDPR articles in the Standard preset on this date." |
| DPO | "Behavioral screening passed. Remaining gaps (encryption, DPIA, transfers) require separate review." |
| Sales | "Benchmark result available on request. Does not constitute a legal attestation." |
| Regulator | Not a substitute for a formal DPIA, audit, or controller/processor agreement. Share the raw evidence file and methodology note, not just the verdict. |

---

## Quick Start

The benchmark requires no special configuration to run. Without `AZURE_OPENAI_*` environment variables, the CLI falls back to a stub judge that returns deterministic placeholder scores with a warning printed to stderr. Results from the stub judge are not meaningful for compliance purposes.

```
dotnet run --project src/AgentEval.Cli --framework net10.0 -- bench gdpr --preset smoke --subject TravelAgent
dotnet run --project src/AgentEval.Cli --framework net10.0 -- bench gdpr --preset standard --subject TravelAgent
dotnet run --project src/AgentEval.Cli --framework net10.0 -- bench gdpr --preset audit --subject TravelAgent
```

Set up the real judge by exporting the following environment variables before running:

```
AZURE_OPENAI_ENDPOINT=https://<your-resource>.openai.azure.com/
AZURE_OPENAI_API_KEY=<your-key>
AZURE_OPENAI_DEPLOYMENT=<your-gpt-4o-deployment>
```

---

## The 6 Presets

| Preset | Articles covered | Approx. cost / run | Target audience |
|--------|------------------|--------------------|-----------------|
| `smoke` | Art 5, 13, 17, 22, 25 (5 articles) | ~$0.05 | Developer inner loop, PR checks |
| `standard` | 16 articles (Art 5–9, 13–18, 20–22, 25, 32) | ~$0.50 | Team QA gate, sprint reviews |
| `audit` | Standard + CapByWorst severity-aware cap; optional multi-judge consensus; Mode-B per-criterion for Critical articles (Art 9, Art 22) | ~$1.20 | DPO review, release sign-off |
| `healthcare` | 8 domain-specific scenarios targeting Art 9(2)(h), special-category processing | ~$0.20 | Healthcare / MedTech teams |
| `hr` | 7 scenarios targeting Art 6(1)(b)/(c), Art 15, Art 17 in employment context | ~$0.15 | HR-software teams |
| `childrens` | 8 scenarios targeting Art 8, parental consent, age-verification | ~$0.20 | EdTech / consumer apps |

Presets can be composed using `+` syntax. The weights of all active scenarios are renormalized automatically:

```
dotnet run --project src/AgentEval.Cli --framework net10.0 -- bench gdpr --preset standard+healthcare --subject TravelAgent
```

---

## Output Structure

Each run writes to `.agenteval/compliance/GDPR/{subject}/{timestamp}/`. The timestamp uses ISO-8601 format (`yyyy-MM-ddTHH-mm-ssZ`).

```
.agenteval/compliance/GDPR/TravelAgent/2026-05-09T10-15-00Z/
├── evidence.json          # Standard plan-01 ComplianceEvidence (audit-chain-validated)
├── gdpr-evidence.json     # GDPR wrapper: composite tree, summary, critical findings, recommendations, disclaimer, attestation
├── report.md              # PR-friendly markdown report
└── report.pdf             # Boardroom-friendly PDF
```

### `evidence.json`

The standard `ComplianceEvidence` document written by `SaveComplianceEvidenceAsync`. Contains `sourceRun.runId` and `sourceRun.manifestHash`, which `agenteval doctor` uses to validate the audit chain. Validated against `evidence.schema.json` before writing.

### `gdpr-evidence.json`

The GDPR-specific wrapper. Contains:
- `compositeTree` — the full recursive `EvalResult` tree, one node per article and per pillar.
- `pillarSummary` — per-pillar score, pass/fail, and article count.
- `overallVerdict` — `PASS`, `WARN`, or `FAIL`.
- `criticalFindings` — list of articles that scored below threshold at `high` or `critical` severity.
- `recommendations` — one recommendation string per critical finding.
- `disclaimer` — the verbatim disclaimer text from this document's Scope section.
- `attestation` — `{ "type": "behavioral-benchmark", "regulation": "GDPR", "preset": "...", "subject": "...", "timestamp": "...", "runId": "..." }`.

Validated against `gdpr-evidence.schema.json` before writing. If validation fails, the write is refused and an error is reported to stderr.

### `report.md`

A markdown report suitable for attaching to a pull request or GitHub release. Sections: executive summary, per-pillar table, per-article results, critical findings, recommendations, methodology note, and disclaimer.

PII in scenario transcripts marked `sensitive: true` is redacted (replaced with `[REDACTED]`) in both `report.md` and `report.pdf`.

### `report.pdf`

A PDF report suitable for boardroom presentation or DPO review. Sections: cover page, executive summary, per-pillar results, per-article results, audit-chain appendix (run ID, manifest hash, timestamp), methodology note, and disclaimer. Generated using QuestPDF.

---

## Interpreting Results

### Overall verdict

| Verdict | Meaning |
|---------|---------|
| `PASS` | All articles in the active preset scored at or above their individual pass thresholds; no critical-severity failures. |
| `WARN` | One or more articles scored below threshold at `medium` severity, or at least one article scored in the `warn` band. No `critical`-severity failures. |
| `FAIL` | One or more articles scored below threshold at `high` or `critical` severity, or the CapByWorst rule applied (see below). |

### Severity escalation rule for Critical articles

Articles 9 and 22 are designated Critical. A failure on either article is always escalated to `critical` severity regardless of the raw score, because:
- Article 9 covers processing of special-category data (health, biometric, racial origin, etc.). Failures here carry outsized legal risk.
- Article 22 covers automated decision-making with significant effects. Failures here indicate that the agent may be making consequential automated decisions without adequate human review or explainability.

### CapByWorst rule (`AuditGrade` preset)

When using the `audit` preset, `CapByWorstAggregation` is applied at the pillar level. This means the pillar score is capped at the lowest article score within the pillar. Any critical-severity failure caps the overall verdict at `FAIL`, regardless of how well other articles scored. The `audit` preset is the only one that applies this rule; the `standard` preset uses `WeightedSumAggregation` at all levels.

### Reading the per-pillar table

The `report.md` and `report.pdf` include a per-pillar table with columns:

| Pillar | Articles | Score | Verdict | Critical failures |
|--------|----------|-------|---------|-------------------|
| pillar1-foundations | 4 | 0.87 | PASS | 0 |
| ... | | | | |

A pillar verdict of `FAIL` means at least one article in that pillar failed at `high` or `critical` severity (or CapByWorst applied). A pillar verdict of `WARN` means at least one article was in the warn band. A pillar verdict of `PASS` means all articles passed.

### Extracting recommendations

The `criticalFindings` array in `gdpr-evidence.json` lists every article that failed at `high` or `critical` severity. Each entry has an `articleId`, a `score`, a `severity`, and a `recommendation` string. The `report.md` renders these as a numbered list at the top of the Recommendations section.

---

## How to Act on Findings

### Failed Article 17 (Right to erasure)

Review your agent's erasure flow end-to-end. The benchmark tests whether the agent: (1) explicitly acknowledges the erasure request, (2) communicates that backup propagation will happen within a stated timeframe, (3) correctly declines erasure only when a legal obligation applies, and (4) does not over-erase data the data subject did not request to delete. A failure on backup propagation is the most common finding. Ensure your agent's scripted responses account for how long backup systems retain copies and communicate this to the data subject.

### Failed Article 9 (Special-category data)

Review how your agent processes special-category data (health, biometric, racial or ethnic origin, political opinions, religious beliefs, trade union membership, genetic data, sex life or sexual orientation). The benchmark checks that the agent identifies the processing ground from Article 9(2) when handling such data and communicates it clearly. Ensure that an Article 9(2) ground (e.g., explicit consent under Art 9(2)(a), or a healthcare exemption under Art 9(2)(h)) is documented in your system's processing records and is reflected in the agent's responses when queried.

### Failed Article 22 (Automated decision-making)

Audit the human-review path for automated decisions that have significant effects on data subjects. The benchmark checks whether the agent can explain that a decision was automated, inform the data subject of their right to request human review, and provide a contact or escalation path. A failure here typically means the agent either denies that automation is involved or cannot describe the human-review option. Update the agent's response templates and escalation routing to satisfy these checks.

---

## Extending with Custom Scenarios

### `WithExtraScenarios` extension

`CompositeEval` exposes a `WithExtraScenarios` extension method that returns a new composite with additional `EvalComponent` entries appended. Weights are renormalized across all components after the extension:

```csharp
var extended = gdprStandard.WithExtraScenarios(new[]
{
    new EvalComponent(myCustomArt17Scenario, Weight: 0.10),
    new EvalComponent(myCustomArt9Scenario,  Weight: 0.10),
});
```

The custom scenarios can target any of the 21 controlled article IDs (`art5`, `art6`, `art7`, `art8`, `art9`, `art13`, `art14`, `art15`, `art16`, `art17`, `art18`, `art20`, `art21`, `art22`, `art25`, `art32`, plus domain-pack extensions). Weights are renormalized automatically; you do not need to adjust the base-preset weights.

### Domain-pack pattern

Domain packs are pre-built `WithExtraScenarios` extensions for specific verticals. Three ship with the sample:

- `Healthcare` — 8 scenarios targeting Art 9(2)(h), special-category data in clinical contexts, and patient data access rights.
- `HR` — 7 scenarios targeting Art 6(1)(b)/(c) lawful basis for employment processing, Art 15 access in HR context, and Art 17 in the offboarding scenario.
- `ChildrensService` — 8 scenarios targeting Art 8 age-of-consent checks, parental consent verification, and age-appropriate design.

To compose a domain pack with a base preset in code:

```csharp
var eval = GdprBenchmark
    .Standard()
    .WithExtraScenarios(HealthcareDomainPack.Scenarios);
```

---

## Calibration

The `agenteval bench gdpr calibrate` command runs the 120-entry golden dataset against the configured judge and produces a calibration report:

```
dotnet run --project src/AgentEval.Cli --framework net10.0 -- bench gdpr calibrate
```

The golden dataset contains 120 hand-labeled scenario/response pairs distributed across the 5 GDPR pillars (30 / 20 / 40 / 15 / 15). For each entry, the calibration runner asks the judge to score the response, then compares the judge's score to the human label.

The calibration report records per-pillar accuracy (fraction of entries within an acceptable score band) and Cohen's kappa (inter-rater agreement). The CI gate requires:
- accuracy >= 0.85 per pillar
- Cohen's kappa >= 0.70 per pillar
- Zero evaluation failures (judge errors) per pillar

A pillar that fails any threshold blocks the release PR. The report is committed to `docs/gdpr-benchmark/calibration-report.md` as part of the release workflow.

**Caveat**: calibration results are only meaningful when a real LLM judge is wired (Azure OpenAI with `AZURE_OPENAI_*` env vars set). Running calibration against the stub judge produces meaningless metrics because the stub always returns placeholder scores.

---

## Audit Chain

`agenteval doctor` validates the audit chain for every `gdpr-evidence.json` file in the workspace. For each file it finds, it:

1. Reads `sourceRun.runId` and `sourceRun.manifestHash` from `evidence.json` in the same directory.
2. Locates the corresponding `manifest.json` under `.agenteval/subjects/*/runs/{runId}/`.
3. Compares the stored `contentHash` with the value in the evidence file.
4. Reports a `✖ Hash mismatch` error if the values differ.

Tampering with any run file after the run completes breaks the audit chain, because `ContentHasher.HashRunAsync` covers the run's summary, sorted scenario results, and optional trace. If you re-run the benchmark and forget to update the evidence, `agenteval doctor` will catch the mismatch.

Refer back to the disclaimer at the top of this document: the audit chain is not cryptographic anti-tampering against a determined attacker. It catches the two most common accidental corruption patterns: "did you forget to update evidence after re-running?" and "is this evidence file consistent with the run it cites?" For stronger integrity guarantees, sign the evidence files externally using your organization's key management infrastructure.

---

## Limitations and What is Out of Scope

The following are not validated by this benchmark:

- **Encryption at rest**: Whether personal data stored by your system is encrypted at rest (Art 32 technical measures). The benchmark checks the agent's dialog behavior, not the underlying storage layer.
- **Breach notification process**: Whether your organization's breach notification procedures satisfy Art 33 (72-hour controller notification to supervisory authority) and Art 34 (data-subject notification). The benchmark cannot test a process that runs outside the agent's dialog.
- **Data Protection Impact Assessment (DPIA)**: Whether a DPIA has been conducted for high-risk processing activities (Art 35). A DPIA is a document produced by your organization; the agent's dialog behavior is not a proxy for its existence.
- **International transfer compliance**: Whether transfers to third countries satisfy Art 46 (Standard Contractual Clauses, binding corporate rules, adequacy decisions). The benchmark does not inspect your data flows.
- **Privacy-by-design at the system level**: Whether your system architecture embeds data minimization, purpose limitation, and storage limitation at the infrastructure level (Art 25). The benchmark checks whether the agent communicates these principles; it cannot verify whether the system enforces them.

---

## See Also

- [Composite Evaluations](../composite-evals.md) — the underlying `CompositeEval` / `AtomicLlmEval` primitives that power this benchmark.
- [CLI Reference](../cli.md) — full reference for `agenteval bench gdpr`, `agenteval bench gdpr calibrate`, and `agenteval compliance render`.
- [Migrating to the `.agenteval/` workspace layout](../migration/output-folder.md) — audit chain details and `agenteval doctor`.

> **Reminder**: this benchmark is a behavioral screening tool, not a legal compliance attestation. A passing score does not constitute or substitute for a formal GDPR audit, DPIA, or legal opinion. Consult a qualified DPO and legal counsel before making compliance representations to regulators, customers, or partners.
