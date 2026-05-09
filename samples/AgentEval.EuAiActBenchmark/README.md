# AgentEval.EuAiActBenchmark

EU AI Act compliance benchmark sample.

This sample evaluates an AI agent's dialog behavior against selected articles of the
**EU AI Act (Regulation (EU) 2024/1689)** — Art 5 prohibited practices, Art 13
deployer transparency, Art 14 human oversight, Art 15 robustness, Art 50 transparency
to natural persons, Annex III risk-tier recognition, and an Art 51-55 GPAI
self-provenance probe.

## Honest framing — read first

> *This benchmark evaluates AI-agent dialog behavior against EU AI Act articles. It is
> a first-line screening tool, **not** a legal compliance attestation. A passing run
> does not establish AI Act compliance; full compliance requires risk classification,
> conformity assessment, technical documentation, post-market monitoring, and (where
> applicable) registration in the EU AI database. Use this evidence as **one input**
> into a larger compliance program led by qualified personnel.*

The benchmark probes **dialog behavior only**. It does not validate:

- Risk classification of your AI system (Art 6, Art 7, Annex III).
- Conformity assessment procedures (Art 43).
- Technical documentation (Art 11).
- Quality management system (Art 17).
- Post-market monitoring (Art 72).
- Incident reporting (Art 73).
- EU database registration (Art 71).
- General-purpose AI model obligations at the model-provider level (Art 51-55).

## Running

```bash
agenteval bench eu-ai-act --preset smoke    --subject MyAgent
agenteval bench eu-ai-act --preset standard --subject MyAgent
agenteval bench eu-ai-act --preset audit    --subject MyAgent
```

Domain packs:

```bash
agenteval bench eu-ai-act --preset standard+high-risk-employment --subject HrAgent
agenteval bench eu-ai-act --preset standard+high-risk-credit     --subject CreditAgent
agenteval bench eu-ai-act --preset standard+high-risk-education  --subject EduAgent
```

Re-render an existing report without LLM cost:

```bash
agenteval compliance render --regulation eu-ai-act --subject MyAgent
```

Calibration:

```bash
agenteval bench eu-ai-act calibrate
```

## Pillars (6 total)

| Pillar | Articles | Weight | Severity |
|---|---|---|---|
| 1 — Prohibited Practices | Art 5(1)(a-h) | 0.30 | Critical |
| 2 — Transparency to Natural Persons | Art 50(1-4) | 0.20 | High |
| 3 — Human Oversight | Art 14 | 0.15 | High |
| 4 — Risk-Tier Behavior | Art 6, 7, 13, Annex III | 0.10 | High |
| 5 — Robustness & Accuracy | Art 15 | 0.15 | Medium |
| 6 — GPAI Self-Awareness | Art 51-55 (probe-only) | 0.10 | Low |

- [Full getting-started guide](../../docs/benchmarks/eu-ai-act/getting-started.md)
- [Implementation plan](../../strategy/FutureFeatures/todo/04-EuAiAct-Evals-CompositeEvals-Benchmark-Reporting-ImplementationPlan.md)
