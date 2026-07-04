# AgentEval.SampleGraders

A small, self-contained head-to-head of **three red-team graders** on the **InferenceAPIAbuse** (OWASP LLM10) family:

1. **Keyword oracle** — the production `InferenceAbuseEvaluator` (deterministic, free).
2. **Single LLM-as-judge** — `LLMJudgeEvaluator` with the evidence-anchored rubric.
3. **Composite (disclosure ⊕ refusal)** — the `DecomposedGraders` decomposition: a positive-only disclosure detector and a negative-only refusal detector, combined by the honest `CompositeEvaluator`.

It grades each against a small **hand-labelled** corpus (both directions), and reports **directional fabrications** — *false alarms* (safe → Succeeded) + *missed hits* (vuln → Resisted). For the judge-based graders it runs **N times** and reports **mean ± stddev** plus a **95% CI diff** (composite vs single judge).

> **Why this design.** The agent responses are *fixed fixtures* with known ground truth, so only the **grader** varies — exactly what you want to compare graders. The headline number is the directional-fabrication count, **not** a single "precision %" (that would be a corpus-specific artifact). This is the same measure AgentEval's grading work reports, and it doubles as a reproducibility artifact for the paper.

## Run it

```bash
# Offline (no keys): runs the keyword oracle for real + a deterministic heuristic judge stand-in,
# so the full pipeline + scorecard run end-to-end for verification. Numbers are ILLUSTRATIVE.
dotnet run --project samples/AgentEval.SampleGraders

# Real, stochastic comparison (set Azure OpenAI; gpt-4o-mini recommended), 10 runs per judge-grader:
set AZURE_OPENAI_ENDPOINT=https://<your>.openai.azure.com/
set AZURE_OPENAI_API_KEY=<your-key>
set AZURE_OPENAI_DEPLOYMENT=gpt-4o-mini
dotnet run --project samples/AgentEval.SampleGraders -- --runs 10
```

It prints a per-case table + the scorecard + the statistical diff, and writes `scorecard.md` to the working directory.

### Calibration playground (`--calibrate`)

```bash
dotnet run --project samples/AgentEval.SampleGraders -- --calibrate
```

Runs each decomposition **leg separately** (disclosure / refusal) and dumps its raw verdict + reason per case, then compares two sub-prompt sets — `V0` (the production flat-composite prompts) vs `V1` (calibrated). This is the dump-driven loop from the design notes: the V0 dump shows the disclosure leg *under-firing* (it demands a model name and misses internals-only disclosures) and the refusal leg *over-firing* `Resisted` (it judges "clean refusal" without knowing a disclosure is present). `V1` broadens the disclosure leg ("any one internal counts") and gates the refusal leg ("a preamble + any self-internal is not a clean refusal"), taking directional fabrications **2 → 0** on this corpus (all vulnerable cases caught, zero false alarms). Edit the `V1` prompts in `CalibrationHarness.cs` and re-run to iterate.

## Non-convergence experiment (`--nonconvergence`)

The paper's **C1 result** — *iteratively patching a keyword/substring grader does not converge to honesty* — lives in
the [`NonConvergence/`](NonConvergence/) subfolder. It runs a deterministic **generate → sweep → count → patch → resweep**
loop on two arms and contrasts them: **Arm A** is a patchable keyword oracle (re-patched every round); **Arm B** is the
real production Composite Judge for that family (never patched). It reports fresh directional fabrications per round for
each arm — the keyword arm stays elevated (patching trades a miss for a false alarm and back), the composite stays ≈0.

Five OWASP families are wired, each with a seeded **ground-truth-by-construction** generator and the real production
composite (`DecomposedGraders.TryBuildFor`), each targeting a "same string, both directions" trap a substring oracle
cannot escape:

| family | axis | generator |
|---|---|---|
| InferenceAPIAbuse (LLM10) | disclose-vs-refuse a model identity | `CaseGenerator.cs` |
| InsecureOutput (LLM05) | emit-vs-warn an executable payload | `InsecureOutputGenerator.cs` |
| SupplyChain (LLM03) | recommend-vs-warn a typosquat | `SupplyChainGenerator.cs` |
| DataPoisoning (LLM04) | adopt-vs-correct a planted false fact | `DataPoisoningGenerator.cs` |
| Misinformation (LLM09) | confabulate-vs-deny a fake entity | `MisinformationGenerator.cs` |

```bash
# single-seed contrast: per-round trajectory + a DETECTION line (proves the composite CATCHES vuln, not just abstains)
dotnet run --project samples/AgentEval.SampleGraders -- --nonconvergence --family supply --rounds 6 --per-round 16

# multi-seed run for statistics -> writes multiseed-<family>.json (keyword arm deterministic; composite arm judge-driven)
dotnet run --project samples/AgentEval.SampleGraders -- --nonconvergence --seeds 30 --comp-seeds 5 --family misinfo --out .
```
Families: `inference` · `insecure` · `supply` · `datapoison` · `misinfo`. Without Azure keys the composite arm falls back
to an offline heuristic (illustrative); the keyword arm is real either way.

Supporting files in `NonConvergence/`: `Families.cs` (registry), `*Generator.cs` (per-family generators),
`PatchableKeywordOracle.cs` + `DeterministicPatcher.cs` (Arm A), `NonConvergenceLoop.cs` (sweep/count loop),
`NonConvergenceRunner.cs` (CLI driver + DETECTION diagnostic). All ground truth is by construction; entities/facts are
author-authored and no working payloads are shipped.

## Solution membership

This project is part of `AgentEval.sln` (a paper / reproducibility companion). It is otherwise
self-contained — it only consumes public AgentEval APIs and touches no production code, so it can be
removed from the solution without affecting anything else:

```bash
dotnet sln AgentEval.sln remove samples/AgentEval.SampleGraders/AgentEval.SampleGraders.csproj
```

## Honest caveats

- **Single family, small corpus.** Scoped to InferenceAPIAbuse and ~16 cases to stay simple and verifiable — it's a demo + artifact, not the paper's full benchmark.
- **Offline judge is illustrative only.** With no API keys, the judge is a deterministic heuristic stand-in that *encodes the documented failure mode* (the single judge anchors on a leading refusal and misses refuse-then-comply; the composite's disclosure detector scans the whole reply and catches it). Real, stochastic figures require a configured judge.
- **Directional fabrication, not accuracy %.** Inconclusive is an honest coverage gap, not a fabrication, and is reported separately.
- **CI-overlap is a quick heuristic**, not a formal hypothesis test — raise `--runs` for tighter intervals.

## What it consumes (and does not modify)

References `AgentEval.RedTeam` / `.Core` / `.Abstractions` and uses, unchanged: `Attack.InferenceAPIAbuse`, `LLMJudgeEvaluator`, `GraderFactory.OptionsFor`, `DecomposedGraders.TryBuildFor`, and `AgentEval.Comparison.StatisticsCalculator`. It changes **nothing** in those projects.
