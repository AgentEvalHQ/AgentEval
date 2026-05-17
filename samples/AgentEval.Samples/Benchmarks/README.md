# `samples/AgentEval.Samples/Benchmarks/` — focused benchmark walkthroughs

One sample per registered benchmark family. Each sample exercises the **real**
production code path end-to-end:

- Builds a real Azure OpenAI–backed agent (when credentials are configured).
- Invokes the agent for live responses — **no stubs, no hardcoded responses**.
- Grades the response with a real LLM judge.
- Renders the unified `EvalResult` tree to JSON + HTML + PDF using the v0.10.1
  generic renderers (`HtmlEvalResultRenderer`, `PdfEvalResultRenderer`).
- Drops the artefacts in `samples/AgentEval.Samples/output/{family}/run-{utc}/`.

If `AZURE_OPENAI_ENDPOINT` / `_API_KEY` / `_DEPLOYMENT` are missing, samples skip
with a clear box and no exceptions — safe to run in CI.

---

## How to run

From the repo root:

```bash
dotnet run --project samples/AgentEval.Samples
```

Pick group **H — Benchmarks (v0.10.1)** from the menu, then choose a sample.

Or by legacy index (1-based across the flat sample list):

```bash
dotnet run --project samples/AgentEval.Samples -- 43   # Performance
dotnet run --project samples/AgentEval.Samples -- 44   # Agentic
dotnet run --project samples/AgentEval.Samples -- 45   # GDPR
# … etc.
```

---

## Preset selection

Every executing sample (B2 – B7) respects a **preset tier** so the same code
scales from a CI smoke check to a full audit:

| Sample              | Smoke (default)              | Standard                    | Audit-Grade                          |
|---------------------|------------------------------|------------------------------|---------------------------------------|
| **B2 Performance**  | 3 iters + 2s throughput      | 10 iters + 10s throughput   | 50 iters + 30s throughput            |
| **B3 Agentic**      | `ToolCallAccuracy` (5 evals) | `AgenticExecution` (6 evals)| `AgenticExecution` + stronger judge   |
| **B4 GDPR**         | `Smoke` (5 articles)         | `Standard` (21 articles)    | `AuditGrade` (21 articles, cap-by-worst) |
| **B5 EU AI Act**    | `Smoke` (5 controls)         | `Standard` (6 pillars)      | `AuditGrade` (6 pillars, cap-by-worst)   |
| **B6 OWASP**        | `Smoke` (3 attacks @ Quick)  | `Top10` (9 attacks @ Quick) | `AuditGrade` (9 @ Comprehensive)      |
| **B7 MITRE ATLAS**  | `AtlasSmoke`                 | `AtlasBaseline`             | `AtlasAuditGrade`                    |

### Selecting a preset

Resolution order (top wins):

1. **CLI args**: `--preset smoke | standard | audit-grade | audit`
2. **Environment variable**: `AGENTEVAL_SAMPLES_PRESET=smoke|standard|audit-grade`
   (case-insensitive)
3. **Non-interactive defaults**: when stdin is redirected or
   `AGENTEVAL_SAMPLES_NONINTERACTIVE=1`, defaults to **Smoke**.
4. **Interactive prompt**: `Preset? [s]moke (default) [t]andard [a]udit-grade`
   — single-char read. Enter or unknown → Smoke.

```bash
# Standard tier, no prompts (CI-friendly):
AGENTEVAL_SAMPLES_NONINTERACTIVE=1 AGENTEVAL_SAMPLES_PRESET=standard \
  dotnet run --project samples/AgentEval.Samples -- 45

# Audit-grade via command-line flag:
dotnet run --project samples/AgentEval.Samples -- 46 --preset audit-grade
```

---

## Cost + time expectations (rough)

These ranges assume `gpt-4o`-class deployment for both agent and judge, paid at
public list price. Faster or smaller models cost less; stronger judges cost more.

| Preset       | Wall time                  | Cost (per sample)         |
|--------------|-----------------------------|---------------------------|
| Smoke        | < 1 min                     | a few cents               |
| Standard     | 5 – 15 min                  | ~$0.50 – $2               |
| Audit-Grade  | 15 – 45 min                 | ~$2 – $10                 |

Compliance samples (B4 GDPR, B5 EU AI Act) sit at the upper end because every
article scenario invokes both the agent and the judge — Smoke = ~25 LLM round
trips, Standard / Audit-Grade = ~100+ round trips.

---

## Required credentials

```bash
export AZURE_OPENAI_ENDPOINT="https://<your-resource>.openai.azure.com/"
export AZURE_OPENAI_API_KEY="<your-key>"
export AZURE_OPENAI_DEPLOYMENT="<deployment-name>"   # e.g. gpt-4o
```

Without these set, every executing sample prints a skip box and exits with a
zero verdict. **B1 Registry Discovery** runs without credentials.

---

## Per-sample fidelity notes

| #  | Sample                  | What it exercises                                                                                       |
|----|-------------------------|----------------------------------------------------------------------------------------------------------|
| B1 | Registry Discovery      | No agent, no judge. Walks `BenchmarkFamilyRegistry` + force-loads sub-assemblies.                       |
| B2 | Performance             | Real agent, no judge. Latency + throughput + cost metrics against your live deployment.                  |
| B3 | Agentic                 | Real agent **invoked once** with a representative prompt; real judge grades the live response.           |
| B4 | GDPR                    | Real agent **invoked per scenario** with each YAML `input`; real judge grades each live response.        |
| B5 | EU AI Act               | Same per-scenario probing as GDPR; pillar-nested presets descend recursively.                            |
| B6 | OWASP LLM Top 10        | Real agent driven by the OWASP adversarial pipeline (Shape B runner — pipeline owns the probe loop).    |
| B7 | MITRE ATLAS             | Real agent driven by the ATLAS adversarial pipeline (Shape B runner).                                    |
| B8 | LongMemEval             | Metadata-only walkthrough. Real memory runs live in group G's `MemoryEvaluation/` samples.              |
| B9 | Report Browser          | No agent. Opens previously-generated JSON / HTML / PDF artefacts.                                       |

---

## Honest limitations

- **B3 Agentic** invokes the agent **once** with a representative query. Real
  agentic evaluations probe across a dataset (see `samples/datasets/benchmark-tool-accuracy.jsonl`
  and `DataAndInfrastructure/04_BenchmarkSystem.cs` for the JSONL-driven flow).
- **B4 GDPR / B5 EU AI Act**: per-scenario probing reuses the canonical
  `ScenarioToAtomicEval` and the preset's aggregation strategy, but rebuilds
  the verdict tree from individual scenario results because the default
  `CompositeEval.EvaluateAsync` threads one input to every leaf. Provider
  safety filters that reject a probe surface as honest "skipped" leaves
  (rather than aborting the run) — a real audit would route around the filter
  or document the filter itself as a control.
- **B2 Performance** at Smoke / Standard preset measures your deployment under
  light load. Audit-grade (50 iters + 30s throughput) is closer to real
  bake-off conditions but is still single-machine and single-client.

---

## Viewing past runs

Sample **B9 Report Browser** (`09_ReportBrowser.cs`) lists every report under
`samples/AgentEval.Samples/output/` and lets you re-open the HTML / PDF / JSON
without re-running the benchmark.

---

## Where the rendering lives

- `HtmlEvalResultRenderer` → `src/AgentEval.Core/Evals/Rendering/`
- `PdfEvalResultRenderer` → `src/AgentEval.Rendering.Pdf/`
- The unified output writer + cost rollup helpers used by every sample live in
  `Benchmarks/_BenchmarkSampleHelpers.cs`.
