# AgentEval.RedTeam — Strategic Documentation

> **Purpose**: Red-teaming and security testing capability for AgentEval
> **Status**: ✅ Feature-complete — OWASP LLM Top 10 fully covered (10/10); Waves A–E + C′ and NextWave Tiers 1–3 (partial) shipped on `feature/redteam-newwave-fixes`. PR-to-main pending authorization.
> **Last Updated**: June 13, 2026 (audited against code + git)

---

## 📊 Master Tracking Table

> Single-glance status of **every** RedTeam work item across all plans, verified against code + git on 2026-06-13 (16-agent audit). ✅ = done & verified in code · ❌ = not done / pending · 🅿️ = parked (blocked on an external dependency). Legend per section below.

### Feature-complete arc (`RedTeam-NewWave-FeatureComplete-Implementation-Plan.md`)

| # | Item | Status | Evidence (in code) |
|---|------|:------:|--------------------|
| Base | FixesImprovement (honest base: truthful verdicts/fidelity, MITRE remap, conclusive-only) | ✅ | `82c5e69` |
| A | Transforms pipeline (`IProbeTransformer` + 18 codecs + `TransformedAttack`) | ✅ | `Transforms/*` (`82c5e69`) |
| B | Tool harness + real surfaces (3 tiers, `CanaryTool`, `EvidenceFidelity`, tool-output/RAG injection) | ✅ | `Harness/*` (`82c5e69`) |
| C | Multi-turn orchestration (Crescendo, `TurnOrchestrator`, convergence) | ✅ | `MultiTurn/*` (`82c5e69`) |
| C′ | Attacker-LLM multi-turn (PAIR, TAP, `AttackerPlanner`, `TreeOrchestrator`) | ✅ | `Attacks/{Pair,Tap}Attack.cs` (`4f14feb`) |
| D | OWASP 6→10/10 + NIST AI RMF reporter + `FrameworkCrosswalk` | ✅ | `Attack.All`=13; `NistAiRmfComplianceReporter` (`eb4c751`/`0c124eb`) |
| E | Thin CLI + CI on-ramp (baseline regression gate, exit codes, SARIF transparency) | ✅ | `--save-baseline`/`--baseline`/`--fail-on` (`4f14feb`) |
| F | Ecosystem & packs (Attack Pack Spec, `PackDownloader`, benchmark packs) | ❌ | importer seam ✅ (Tier-1 #4); pack downloader pending → NextWave #10 |
| G | Long-horizon (memory-poisoning, multi-agent, atkgen) | ❌ | none → NextWave #13–#16 |

### Coverage & compliance

| Item | Status | Detail |
|------|:------:|--------|
| OWASP LLM Top 10 (LLM01–LLM10) | ✅ | 10/10 — every category maps to ≥1 attack |
| MITRE ATLAS techniques | ✅ | 8 applicable (T0010, T0020, T0034, T0037, T0051, T0054, T0056, T0057), source-verified vs ATLAS.yaml |
| Compliance reporters | ✅ | 5 — OWASP, MITRE, SOC2, ISO27001, NIST AI RMF |
| Export formats | ✅ | 5 — JSON, JUnit, SARIF, Markdown, PDF |
| Built-in attacks / probes | ✅ | 13 attacks (+3 opt-in) / 258 probes (Comprehensive) |
| CLI options | ✅ | 31 |
| ResponsibleAI metrics | ✅ | 3 — Toxicity, Bias, Misinformation |

### NextWave competitive-parity backlog (`RedTeam-NextWave-CompetitiveParity-and-Honesty-Plan.md`)

| # | Item | Tier | Status | Evidence / note |
|---|------|:----:|:------:|-----------------|
| 1 | Graded scorer (`LikertJudgeEvaluator`) | 1 | ✅ | `Evaluators/LikertJudgeEvaluator.cs` (`6c8f4fb`) |
| 2 | Skeleton-Key + Many-shot jailbreak probes | 1 | ✅ | `JailbreakAttack.cs` (`6c8f4fb`) |
| 3 | `--explain` LLM rationale | 1 | ✅ | `RedTeamCommand.cs` (`1dd4ac5`) |
| 4 | Seed-prompt dataset importer (`--import-probes`) | 1 | ✅ | `Importers/*` (`578235c`) |
| 5 | Divergence / repeat-token probes | 1 | ✅ | `PIILeakageAttack.cs` (`6c8f4fb`) |
| 7 | LLM03 live registry oracle (`--package-registry live`) | 2 | ✅ | `Evaluators/HttpPackageRegistry.cs` (`2cbd9f2`) |
| 8 | LLM08 real-retrieval RAG boundary | 2 | ✅ | `VectorEmbeddingAttack : IToolAwareAttack` (`b5c2804`) |
| 9 | z-score calibration (`--calibration`) | 3 | ✅ | `Calibration/{CalibrationProfile,Calibrator}.cs` (`cd97a11`) |
| 6 | leakreplay replay/membership probes | 3 | ❌ | not started |
| 10 | PackDownloader + `--accept-license` license gate | 3 | ❌ | not started (HarmBench/JailbreakBench/CyberSecEval) |
| 11 | BadLikertJudge multi-turn attack | 3 | ❌ | not started |
| 12 | LLM-driven converters (paraphrase/persuasion/tense) | 3 | ❌ | not started |
| 13 | Tool-aware multi-turn attack (Wave-B↔C DIM) | 3 | ❌ | not started |
| 14 | LLM10 transport-level metering harness | 3 | ❌ | not started (field ceiling — labeled) |
| 15 | atkgen adaptive attack generation | 3 | ❌ | not started |
| 16 | Memory-poisoning + multi-agent surfaces (Wave G) | 3 | ❌ | not started |

### Audit findings & fixes (2026-06-13)

| Finding | Status | Fix |
|---------|:------:|-----|
| Stale retired `AML.T0045` in `docs/redteam.md` MITRE coverage line | ✅ | Corrected → `AML.T0034` (`63c580d`) |
| `MitreBenchmark.cs` doc-comment listed old 6-technique catalog + "nine attacks" | ✅ | Corrected → 8-technique / 13-attack catalog (`63c580d`) |
| All `strategy/redteam` plan docs synced to verified done/pending state | ✅ | README/NextWave/FeatureComplete/01/02/181/202 + `redteam_todo`→`done/` (`63c580d`) |

### Future / parked plans (`futures/`, `181`, `202`)

| Plan | Status | Note |
|------|:------:|------|
| 181-P12 MITRE expansion — Phase 1 (InferenceAPIAbuse) | ✅ | shipped, redesigned to AML.T0034/LLM10 |
| 181-P12 MITRE expansion — Phases 2–4 (T0047/T0048/T0052 attacks) | ❌ | declined — `NotApplicable` at agent-API layer |
| 202-P19 multi-modal image attacks | 🅿️ | not started — needs MAF vision support |
| futures/188-P19 packaged CI/CD tasks (Azure DevOps task + GH Action) | ❌ | CLI CI on-ramp exists; dedicated packaged tasks not built |
| futures/189-P20 notifications & dashboards (Slack/Teams) | ❌ | not started |
| futures/190-P21 additional enterprise features (model-comparison mode) | ❌ | not started |

### Release gating

| Item | Status | Note |
|------|:------:|------|
| Branch work committed + pushed (`feature/redteam-newwave-fixes`) | ✅ | tip `63c580d` |
| Test suite green (net8/9/10) | ✅ | net8/9 4963/0, net10 5166/0/1 |
| **PR-to-main** | ❌ | **NOT authorized yet** — awaiting user go-ahead |

---

## Current State Summary

> Counts below are verified against `src/AgentEval.RedTeam/RedTeam/Attack.cs` and `docs/redteam.md` (the live user-facing reference). The MVP-era "Achieved" figures this README used to show (9 attacks / 192 probes / 60% OWASP / 6 MITRE) were a January-2026 snapshot and are now superseded.

| Metric | MVP (Jan 2026) | Current | Notes |
|--------|----------------|---------|-------|
| Attack Types | 9 | **13** (+ 3 opt-in: Crescendo, PAIR, TAP) | `Attack.All` = 13; opt-in multi-turn excluded from the default roster |
| Total Probes | 192 | **258** (Comprehensive) | sum of per-attack fixed-count tests |
| OWASP Coverage | 60% (6/10) | **100% (10/10)** | LLM01–LLM10 all mapped (Wave D closed 6→10) |
| MITRE ATLAS | 6 techniques | **8 techniques** | source-verified vs ATLAS.yaml (H13/H14) |
| Export Formats | 5 | **5** | JSON, JUnit, SARIF, Markdown, PDF |
| Compliance Reporters | 4 | **5** | OWASP, MITRE, SOC2, ISO27001, **NIST AI RMF** |
| ResponsibleAI Metrics | 3 | **3** | Toxicity, Bias, Misinformation |

### OWASP LLM Top 10 2025 Alignment — 10/10
Every category maps to ≥1 attack (probe counts at Comprehensive):
- **LLM01** Prompt Injection — PromptInjection (27) + Jailbreak (29) + IndirectInjection (19) + EncodingEvasion (23) = 98
- **LLM02** Sensitive Information Disclosure — PIILeakage (22)
- **LLM03** Supply Chain — SupplyChain (14) *(+ `--package-registry live` for real hallucinated-package detection)*
- **LLM04** Data & Model Poisoning — DataPoisoning (12)
- **LLM05** Improper Output Handling — InsecureOutput (31)
- **LLM06** Excessive Agency — ExcessiveAgency (15)
- **LLM07** System Prompt Leakage — SystemPromptExtraction (19)
- **LLM08** Vector & Embedding Weaknesses — VectorEmbedding (16) *(real `retrieve_context` RAG boundary at `--sut-tier instrumented`)*
- **LLM09** Misinformation — Misinformation (16)
- **LLM10** Unbounded Consumption — InferenceAPIAbuse (15)

### ResponsibleAI Namespace
Content safety metrics complementing security testing (`src/AgentEval.Core/Metrics/ResponsibleAI/`):
- **ToxicityMetric**: Pattern + LLM hybrid toxicity detection
- **BiasMetric**: Counterfactual testing for differential treatment
- **MisinformationMetric**: Claim verification and calibration

---

## Document Index

### Active Strategy Documents

| Document | Description | Status |
|----------|-------------|--------|
| [**RedTeam-NextWave-CompetitiveParity-and-Honesty-Plan**](RedTeam-NextWave-CompetitiveParity-and-Honesty-Plan.md) | The **current forward plan** (garak/PyRIT competitive parity + honesty). Owns the Tier-3 remainder + Wave-G backlog. | 🟡 Tier 1–2 + Tier-3 calibration shipped; rest pending |
| [**RedTeam-NewWave-FeatureComplete-Implementation-Plan**](RedTeam-NewWave-FeatureComplete-Implementation-Plan.md) | The A→G feature-complete arc + honesty guardrails. | ✅ Waves A–E + C′ shipped; F/G handed off to the NextWave plan |
| [01 Golden Path Plan](01-redteam-golden-path-plan.md) | Original vision (taxonomy, Attack Pack spec, report schema, packaging). | Reference (milestone plan superseded) |
| [02 Golden Path Analysis](02-golden-path-analysis.md) | Critical descope review — recommendations adopted. | Reference (complete) |
| [181 P12: MITRE Expansion](181-P12-MITRE-ATLAS-expansion-plan.md) | MITRE ATLAS technique expansion. | ⚠️ Partially overtaken (Phase 1 shipped/redesigned; 2–4 declined NotApplicable) |
| [202 P19: Multi-Modal](202-P19-multi-modal-attacks-impplan.md) | Multi-modal image attacks. | 📋 Not started (parked — needs MAF vision support) |
| [redteam_todo.md](done/redteam_todo.md) | MVP-era forward-planning TODO. | ✅ Superseded → moved to `done/` |

### Completed Plans — See [`done/`](done/) folder

All MVP plans (P0–P22), the NewWave per-wave plans (Waves A–E, C′, FixesImprovement), and the Fable-review fix arc are in `done/`. The two **umbrella** plans above (FeatureComplete, NextWave) stay at top level because they still carry pending backlog.

---

## Quick Reference

### What's Implemented

```
✅ 13 built-in attacks (PromptInjection, Jailbreak, PIILeakage, SystemPromptExtraction,
   IndirectInjection, ExcessiveAgency, InsecureOutput, InferenceAPIAbuse, EncodingEvasion,
   SupplyChain, DataPoisoning, VectorEmbedding, Misinformation) + 3 opt-in (Crescendo, PAIR, TAP)
✅ 258 total probes (Comprehensive), OWASP LLM 2025 aligned
✅ 100% OWASP LLM Top 10 coverage (LLM01–LLM10)
✅ 8 MITRE ATLAS techniques (source-verified vs ATLAS.yaml)
✅ 5 export formats (JSON, JUnit, SARIF, Markdown, PDF)
✅ 5 compliance reporters (OWASP, MITRE, SOC2, ISO27001, NIST AI RMF) + FrameworkCrosswalk
✅ Transform pipeline (IProbeTransformer + 18 codecs, correct-by-construction encodings)  [Wave A]
✅ Tiered tool harness — text / function-calling / instrumented; EvidenceFidelity Verbal/IntentToAct/Behavioral; real injection surfaces (tool-output, retrieved-document)  [Wave B]
✅ Multi-turn orchestration (Crescendo) + attacker-LLM PAIR/TAP  [Waves C / C′]
✅ Honesty discipline — conclusive-only scoring, never-fabricate PASS, Inconclusive/coverage state, governance-never-PASS
✅ CLI CI on-ramp — baseline regression gate (--save-baseline/--baseline/--fail-on), --import-probes, --explain, --package-registry live, --calibration  [Wave E + NextWave T1–T3]
✅ Fluent assertion API · Attack pipeline builder · progress callbacks · rich console output
✅ ResponsibleAI namespace (ToxicityMetric, BiasMetric, MisinformationMetric)
```

### What's Next

The active **[NextWave plan](RedTeam-NextWave-CompetitiveParity-and-Honesty-Plan.md)** owns the remaining backlog (Tier-3 + Wave G):
- **#6** leakreplay-style training-data replay probes
- **#10** PackDownloader + `--accept-license` license gate (HarmBench / JailbreakBench / CyberSecEval)
- **#11** BadLikertJudge multi-turn attack
- **#12** LLM-driven converters (paraphrase / persuasion / tense)
- **#13** tool-aware multi-turn attack (exercise the Wave-B↔C compose path)
- **#14** LLM10 transport-level metering harness
- **#15** atkgen adaptive attack generation · **#16** memory-poisoning + multi-agent surfaces
- **[202 P19](202-P19-multi-modal-attacks-impplan.md)** multi-modal image attacks (parked — needs MAF vision support)

---

## The Pitch

```csharp
// Before: Hope and pray
await agent.RunAsync("user input");
// 🤞

// After: Comprehensive security testing
var result = await AttackPipeline
    .Create()
    .WithAllAttacks()  // 13 attack types, 258 probes
    .WithIntensity(Intensity.Quick)
    .ScanAsync(agent);

result.Should()
    .HaveOverallScoreAbove(85)
    .HaveNoHighSeverityCompromises();
```

---

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **Taxonomy** | OWASP LLM + MITRE ATLAS | Industry standard, credibility |
| **Architecture** | Pipeline model | Composable, testable, extensible |
| **Packaging** | Single namespace | Simplicity, no fragmentation |
| **Attack sources** | Curate + Convert + Credit | Native .NET, no Python deps |
| **API style** | Simple wrapper over pipeline | 90% easy, 10% powerful |

---

## Reading Order

### For Strategy Review
1. [RedTeam-NextWave-CompetitiveParity-and-Honesty-Plan.md](RedTeam-NextWave-CompetitiveParity-and-Honesty-Plan.md) — current forward planning + competitive analysis
2. [RedTeam-NewWave-FeatureComplete-Implementation-Plan.md](RedTeam-NewWave-FeatureComplete-Implementation-Plan.md) — the A→G arc + honesty guardrails
3. [02-golden-path-analysis.md](02-golden-path-analysis.md) — why we scoped this way
4. [01-redteam-golden-path-plan.md](01-redteam-golden-path-plan.md) — original full vision

### For Future Implementation
1. [181-P12](181-P12-MITRE-ATLAS-expansion-plan.md) — MITRE expansion (partially overtaken)
2. [202-P19](202-P19-multi-modal-attacks-impplan.md) — multi-modal attacks (parked)

---

## Contact

Questions about red-team strategy? See the [NextWave plan](RedTeam-NextWave-CompetitiveParity-and-Honesty-Plan.md) or raise an issue.
