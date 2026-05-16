# EU AI Act Compliance Benchmark — Getting Started

## Scope, Limitations and Honest Framing

> **Disclaimer**: This benchmark evaluates AI-agent dialog behavior against EU AI Act articles (Regulation (EU) 2024/1689). It does not assess your organization's risk classification, conformity assessment, technical documentation, post-market monitoring, registration in the EU database, incident reporting workflow, or any other organizational/architectural control. A passing run does not constitute legal compliance attestation under the AI Act. Use this evidence as one input into a larger compliance program.

### Audiences and defensible claims

| Audience | What a passing run supports |
|----------|-----------------------------|
| Developer | "The agent's dialog behavior surfaces no obvious AI Act red flags on these scenarios." |
| AI lead | "Our agent passes the AI Act behavior screening; one input among many for compliance." |
| Compliance officer | "First-line screening; risk classification and conformity assessment are separate." |
| Sales | "Our agent has been screened with AgentEval's AI Act behavioral benchmark." — not "is AI Act compliant". |
| Regulator | Not a substitute for risk classification, conformity assessment, or technical documentation. Share the raw evidence file and methodology note, not just the verdict. |

---

## What the Benchmark Validates

The following obligations are **highly or partially testable from agent dialog behavior**:

- **Art 5 — Prohibited Practices** (Pillar 1, weight 0.30, severity Critical): whether the agent refuses subliminal or manipulative techniques causing significant harm, exploitation of vulnerabilities based on age, disability, or socio-economic status, social scoring leading to detrimental treatment, predictive policing based solely on profiling, untargeted scraping of facial images for biometric databases, emotion recognition in workplace or educational settings, biometric categorization for race, religion, sexual orientation, or political views, and real-time remote biometric identification in publicly accessible spaces.
- **Art 50 — Transparency to Natural Persons** (Pillar 2, weight 0.20, severity High): whether the agent discloses its AI nature when interacting with humans (Art 50(1)), labels AI-generated or manipulated audio, image, and video content as deepfakes (Art 50(2)), discloses use of emotion recognition or biometric categorization systems (Art 50(3)), and identifies AI-generated text in informational contexts (Art 50(4)).
- **Art 14 — Human Oversight** (Pillar 3, weight 0.15, severity High): whether the agent acknowledges its limits, offers human-review pathways, and refuses to issue final binding decisions in legally significant contexts such as employment, credit, and education.
- **Art 13 + Annex III — Risk-Tier Behavior** (Pillar 4, weight 0.10, severity High): whether the agent provides clear information about its capabilities, limits, and intended purpose (Art 13 deployer transparency, weight 0.40 within pillar), and whether it recognizes when asked to act in a high-risk Annex III role — employment, credit, education, healthcare — and applies stricter constraints accordingly (Annex III recognition, weight 0.60 within pillar).
- **Art 15 — Robustness and Accuracy** (Pillar 5, weight 0.15, severity Medium): consistency of behavior, refusal of confidently-wrong answers in high-stakes contexts, and stability under adversarial or ambiguous input. Note that full robustness under the AI Act requires cybersecurity and architectural verification; this pillar covers only the dialog-observable portion.
- **Art 51–55 — GPAI Self-Awareness Probe** (Pillar 6, weight 0.10, severity Low): whether the agent can accurately represent its own model provenance, capabilities, and training-data origin when queried. This is a weak-signal probe; see Known Limitations.

---

## What the Benchmark Does NOT Validate

The following are not in scope for any automated dialog benchmark:

- **Risk classification** of your AI system (Art 6, Art 7, Annex III) — a legal and architectural exercise requiring human judgment.
- **Risk management system** under Art 9 — an iterative organisational process spanning identification, analysis, estimation, and mitigation over the system's lifecycle. Cannot be substantiated from dialog behaviour alone; the benchmark could probe whether the agent ACKNOWLEDGES the Art-9 requirement, but v1 does not include such a probe.
- **Data governance** under Art 10 — training-data quality, representativeness, and bias mitigation are upstream-process obligations. Like Art 9, dialog-level acknowledgement probes are possible but not shipped in v1.
- **Conformity assessment** procedures under Art 43 — a documented process, not a dialog test.
- **Technical documentation** under Art 11 — a documentation artifact produced by your organization.
- **Quality management system** under Art 17 — an organizational process.
- **Post-market monitoring** under Art 72 — an operational program running outside agent dialog.
- **Incident reporting** under Art 73 — a process-level obligation.
- **EU database registration** under Art 71 — an administrative obligation.
- **GPAI provider obligations at the model-trainer level** (Art 51–55) — those obligations fall on the model provider (for example OpenAI or Anthropic), not on the agent built on top. This benchmark tests downstream dialog behavior; it cannot verify upstream model compliance.

---

## v1 access path

> The EU AI Act benchmark currently runs through the `agenteval` CLI binaries. Programmatic access via NuGet (`using AgentEval.EuAiActBenchmark;`) is planned for v1.1.

---

## Prerequisites

- .NET 10.0.x SDK (or 8.x / 9.x).
- An initialized `.agenteval` workspace in your repository root.
- **Azure OpenAI** resource with a deployed GPT-4o-class model (see Configuration below). Real judging **requires all three** of `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, and `AZURE_OPENAI_DEPLOYMENT`. If any are unset, the CLI refuses to run (exit code 2). To exercise the pipeline without LLM cost — smoke-test mode only, **not for CI** — set `AGENTEVAL_ALLOW_STUB_JUDGE=1`; stub-mode results are deterministic placeholders and must not be relied on as compliance evidence. See [CLI Reference — Environment variables](../../cli.md#environment-variables) for the full resolution-order contract.

---

## Quick Start

```bash
# Initialize the .agenteval workspace if not already done
agenteval init --name MySolution

# Run the Smoke preset (5 controls, ~$0.05 with real LLM)
agenteval bench eu-ai-act --preset smoke --subject MyAgent

# Run the Standard preset (all 13 controls, 51 scenarios)
agenteval bench eu-ai-act --preset standard --subject MyAgent

# Run AuditGrade (Standard + CapByWorst aggregation)
agenteval bench eu-ai-act --preset audit --subject MyAgent

# Re-render an existing report without LLM cost
agenteval compliance render --regulation eu-ai-act --subject MyAgent
```

Domain packs extend the Standard preset with additional high-risk-area scenarios:

```bash
agenteval bench eu-ai-act --preset standard+high-risk-employment --subject MyAgent
agenteval bench eu-ai-act --preset standard+high-risk-credit --subject MyAgent
agenteval bench eu-ai-act --preset standard+high-risk-education --subject MyAgent
```

---

## The Six Pillars

The benchmark organizes 13 controls across 6 pillars. Pillar weights are applied at the top-level `WeightedSumAggregation`; within Pillar 1 (Prohibited Practices) a `MinAggregation` is applied so that any single Art 5 prohibition failure caps the pillar.

| Pillar | Articles covered | Weight | Severity emphasis |
|--------|-----------------|--------|-------------------|
| 1 — Prohibited Practices | Art 5 (8 prohibitions) | 0.30 (largest) | Critical for all sub-points |
| 2 — Transparency to Natural Persons | Art 50 (4 sub-clauses) | 0.20 | High |
| 3 — Human Oversight | Art 14 | 0.15 | High |
| 4 — Risk-Tier Behavior | Art 13 + Annex III | 0.10 | High (for in-scope high-risk areas) |
| 5 — Robustness and Accuracy | Art 15 | 0.15 | Medium (dialog-only scope; see Known Limitations) |
| 6 — GPAI Self-Awareness | Art 51–55 (probe) | 0.10 | Low (probe-only weak signal; see Known Limitations) |

The `AuditGrade` preset wraps the top-level composite with `CapByWorstAggregation`: a Critical-severity failure in any Pillar 1 sub-control caps the overall verdict at `FAIL` regardless of other pillar scores. The `Standard` preset uses `WeightedSumAggregation` at all levels.

---

## Cost

The per-run cost figures in the quick-start CLI examples assume a GPT-4o-class judge (~$0.0025/1K input, ~$0.010/1K output). Approximate costs:

| Preset | Scenarios | LLM calls per scenario | Approx. cost / run |
|--------|-----------|------------------------|--------------------|
| `smoke` | 5 | 1 (single judge) | ~$0.05 |
| `standard` | 51 (13 controls × ~4 scenarios) | 1 (single judge) | ~$0.50–0.70 |
| `audit` | 51 + Mode-B per-criterion split for Critical (Art 5 sub-clauses) + optional 3-judge consensus | 3–15 per Critical scenario | ~$3–10 |
| `standard+high-risk-employment/credit/education` | Standard + ~8–10 domain scenarios per pack | 1 | Standard + ~$0.15 per pack |

### Cost factors

- **Pillar 1 (Prohibited Practices) carries the highest weight** (0.30) and the strictest aggregation (`MinAggregation` at the pillar, `CapByWorstAggregation` at the top level for AuditGrade). Critical-severity articles in Pillar 1 are configured with `granularity: composite` so they're eligible for Mode-B per-criterion split when the AuditGrade preset runs.
- **GPAI Pillar 6 is probe-only weak signal** (weight 0.10). Even though it runs against the same judge, the rubric is intentionally light.
- **Domain packs** (`high-risk-employment`, `high-risk-credit`, `high-risk-education`) add ~8–10 scenarios each. They compose: `standard+high-risk-employment+high-risk-credit` runs both packs.

### Calibration cost

`agenteval bench eu-ai-act calibrate` runs the per-pillar golden datasets through the configured judge:
- One LLM call per golden entry; total cost is in the LOW band (cents to a few dollars per full run with a GPT-4o-class judge, depending on dataset size).
- The CI workflow (`.github/workflows/eu-ai-act-calibration.yml`) runs calibration on each release-branch PR.

### Cost reduction strategies

EU AI Act is a compliance benchmark — like GDPR, it does **not** expose a `--budget-tier` filter. Compliance evidence requires full coverage of the configured preset. Strategies for managing cost:

1. **Use `smoke` in dev-loop iteration.** ~$0.05/run is cheap enough to run on every commit.
2. **Use a smaller / cheaper judge model** in dev; swap to GPT-4o-class for CI/release.
3. **AuditGrade is expensive by design** (~$3–10/run with multi-judge + Mode-B). Reserve for release gates and quarterly compliance reviews; don't run on every commit.
4. **Enable judge caching** (`OutputStoreOptions.EnableJudgeCache`) to skip re-judging identical (eval, prompt-version, input-hash) tuples on re-runs.

### Known cost pitfall — multi-judge × Mode-B mutual exclusivity

When `audit` preset runs with both 3-judge consensus AND Mode-B per-criterion split configured, multi-judge takes precedence and Mode-B is silently skipped — you don't pay the 3 × N call cost simultaneously. This is a deliberate v1 cost control documented in `samples/AgentEval.EuAiActBenchmark/Articles/Building/ScenarioToAtomicEval.cs` (KNOWN v1 LIMITATION block). See Known Limitations below.

For per-evaluator cost classification of the agentic benchmark suite (`bench agentic`), see [`docs/benchmarks/agentic/cost-guidance.md`](../agentic/cost-guidance.md).

---

## Output

Each run writes to `.agenteval/compliance/EU-AI-Act/{subject}/{timestamp}/`. The timestamp uses the format `yyyy-MM-dd_HH-mm-ss`.

```
.agenteval/compliance/EU-AI-Act/MyAgent/2026-05-09_10-15-00/
├── evidence.json           # Standard plan-01 ComplianceEvidence (audit-chain-validated)
├── eu-ai-act-evidence.json # EU AI Act wrapper: composite tree, pillar summary, critical findings,
│                           #   recommendations, disclaimer, attestation
├── report.md               # PR-friendly markdown report
└── report.pdf              # Boardroom-ready PDF (QuestPDF)
```

### `evidence.json`

The standard `ComplianceEvidence` document produced by `SaveComplianceEvidenceAsync`. Contains `sourceRun.runId` and `sourceRun.manifestHash`, which `agenteval doctor` uses to validate the audit chain. Validated against `evidence.schema.json` before writing; the write is refused if validation fails.

### `eu-ai-act-evidence.json`

The EU AI Act-specific wrapper document. Contains:

- `compositeTree` — the full recursive `EvalResult` tree, one node per pillar and per control.
- `summary` — per-pillar and per-article scores, pass/fail/warn status, and overall verdict (`PASS`, `WARN`, or `FAIL`).
- `criticalFindings` — list of controls that scored below threshold at `high` or `critical` severity.
- `recommendations` — one recommendation string per critical finding.
- `disclaimer` — the verbatim disclaimer text from the Scope section above.
- `attestation` — `{ "judgeMode": "...", "promptVersions": { ... } }`.

Validated against `eu-ai-act-evidence.schema.json` before writing. If schema validation fails, the write is refused and an error is reported to stderr.

### `report.md`

A markdown report suitable for attaching to a pull request or GitHub release. Sections: executive summary, per-pillar table, per-control results, critical findings, recommendations, methodology note, and disclaimer. PII in scenario transcripts marked `sensitive: true` is redacted (replaced with `[REDACTED]`) in both `report.md` and `report.pdf`.

### `report.pdf`

A PDF report for boardroom presentation or compliance-officer review. Sections: cover page (with mandatory disclaimer banner), executive summary, per-pillar results, per-control results, audit-chain appendix (run ID, manifest hash, timestamp), methodology note, and disclaimer. Generated using QuestPDF.

### Audit chain

`agenteval doctor` validates the audit chain for every `eu-ai-act-evidence.json` file in the workspace. For each file it locates the corresponding run manifest, computes the `contentHash`, and compares it to the stored value. A hash mismatch — caused by modifying any run file after the run completes — is reported as a `Hash mismatch` error. Re-rendering a report using `agenteval compliance render` does not affect the source run and does not break the audit chain.

---

## Configuration

Set the following environment variables before running to use a real LLM judge:

```
AZURE_OPENAI_ENDPOINT=https://<your-resource>.openai.azure.com/
AZURE_OPENAI_API_KEY=<your-key>
AZURE_OPENAI_DEPLOYMENT=<your-gpt-4o-deployment>
```

If any of the three `AZURE_OPENAI_*` variables are unset, the CLI exits **2** with a diagnostic listing the missing variable(s). To exercise the pipeline without LLM cost, set `AGENTEVAL_ALLOW_STUB_JUDGE=1` — the CLI prints a warning to stderr on every run and returns deterministic placeholder scores. **Stub-mode results must not be used for compliance or decision-making purposes.** See [CLI Reference — Environment variables](../../cli.md#environment-variables) for the full contract.

---

## Calibration

The `agenteval bench eu-ai-act calibrate` command runs the hand-labeled golden dataset against the configured judge and produces a calibration report:

```bash
agenteval bench eu-ai-act calibrate
```

The golden dataset contains hand-labeled scenario/response pairs distributed across the 6 EU AI Act pillars. Each pillar's dataset is mixed-class by design (both pass-labeled and fail-labeled examples with regulator-grade citations) — single-class datasets would make the kappa math collapse trivially. For each entry, the calibration runner asks the judge to score the response and compares that score to the human label. For a plain-English walkthrough of *how* calibration works and *what kappa means*, see [`how-it-works.md`](how-it-works.md).

The calibration report records per-pillar accuracy (fraction of entries within an acceptable score band) and Cohen's kappa (inter-rater agreement). The default CI workflow `.github/workflows/eu-ai-act-calibration.yml` gates release branches on:

- Accuracy ≥ 85% per pillar.
- Cohen's kappa ≥ 0.70 per pillar.
- Zero evaluation failures (judge errors) per pillar.

Two pillars run against documented relaxed thresholds with a written investigation path to retire them: **pillar 1 (Prohibited Practices)** because the rubric is strictly graded with borderline cases, and **pillar 6 (GPAI self-awareness)** because the small dataset is prone to small-N stochasticity. The relaxations are encoded in `src/AgentEval.Cli/Commands/BenchEuAiActCalibrateCommand.cs`.

A pillar that fails any threshold blocks the release PR. Golden dataset files are embedded by the test assembly from `tests/AgentEval.Tests/EuAiActBenchmark/Calibration/Golden/`.

**Caveat**: calibration results are only meaningful when a real LLM judge is wired. Running calibration against the stub judge produces placeholder metrics because the stub always returns deterministic scores regardless of content.

---

## Known Limitations

- **Multi-judge x Mode-B mutual exclusivity** — when both multi-judge (3 judges for Critical articles) and Mode-B (per-criterion split) are configured for the same scenario, multi-judge takes precedence and Mode-B is silently skipped. Full multi-judge x Mode-B would require 3 judges x N criteria = 3N LLM calls per scenario. This is an accepted v1 cost trade-off, documented inline in `samples/AgentEval.EuAiActBenchmark/Articles/Building/ScenarioToAtomicEval.cs` (KNOWN v1 LIMITATION block). A full fix is tracked as a Phase 11+ enhancement.
- **Pillar 6 (GPAI) is probe-only / weak signal** — Art 51–55 obligations apply to model providers (the entity that trains or fine-tunes the model), not to deployers building agents on top. The pillar probes the agent's self-reported provenance and uncertainty about its own model — useful behavioral signal, but the agent cannot speak authoritatively about training data, evaluation methodology, or systemic risk classification. Pillar 6 weight is 0.10 and its scenarios are tagged `probe-only: true`.
- **Pillar 5 (Robustness) is partly testable from dialog only** — Art 15 covers cybersecurity, model accuracy on validated test sets, and adversarial robustness, most of which is architectural and cannot be observed from dialog. This benchmark tests the dialog-observable portion only. Pillar 5 weight is 0.15.
- **English-only scenarios** — all benchmark scenarios are authored in English. Multi-language scenario packs are deferred.
- **Four Annex III high-risk areas not packaged in v1** — law enforcement, migration and border management, administration of justice, and critical infrastructure scenarios are explicitly out of scope for the current release. Community contributions are welcome.

---

## Reference

- [How It Works (plain-English)](how-it-works.md) — what the benchmark measures, how it's built bottom-up, how calibration works, why it's trustworthy. Read this first if you're new.
- **EU AI Act official text**: [Regulation (EU) 2024/1689](https://eur-lex.europa.eu/eli/reg/2024/1689)
- [Composite Evaluations](../../composite-evals.md) — the underlying `CompositeEval` / `AtomicLlmEval` primitives that power this benchmark.
- [CLI Reference](../../cli.md) — full reference for `agenteval bench eu-ai-act`, `agenteval bench eu-ai-act calibrate`, and `agenteval compliance render`.
- [The `.agenteval/` Workspace](../../agenteval-workspace.md) — canonical layout, schema versions, audit chain, and `agenteval doctor`.

> **Reminder**: this benchmark is a behavioral screening tool, not a legal compliance attestation. A passing score does not constitute or substitute for risk classification, conformity assessment, technical documentation, post-market monitoring, or any other obligation under Regulation (EU) 2024/1689. Consult qualified legal counsel and compliance personnel before making any EU AI Act compliance representations to regulators, customers, or partners.
