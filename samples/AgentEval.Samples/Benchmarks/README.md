# `samples/AgentEval.Samples/Benchmarks/` — focused benchmark walkthroughs

One sample per registered benchmark family. The **running** benchmark samples
(H2 Performance, H3 Agentic, H4 GDPR, H5 EU AI Act, H6 OWASP, H7 MITRE) each
exercise the **real** production code path end-to-end:

- Build a real Azure OpenAI–backed agent (when credentials are configured).
- Invoke the agent for live responses — **no stubs, no hardcoded responses**.
- Grade the response with a real LLM judge.
- Render the unified `EvalResult` tree to JSON + HTML + PDF using the v0.10.1
  generic renderers (`HtmlEvalResultRenderer`, `PdfEvalResultRenderer`).
- Write a canonical audit-chained run under the **repo-root `.agenteval/`**
  workspace via `FileSystemOutputStore` (the same one `agenteval init` creates —
  resolved by walking up to the nearest `*.sln`/`*.slnx`/`.git/` ancestor, so
  every AgentEval surface — CLI, samples, Mission Control, `agenteval doctor` —
  shares one workspace).
- Mirror the HTML / PDF / bare-JSON sidecar to
  `samples/AgentEval.Samples/output/{family}/run-{utc}-{suffix}/` for direct
  human consumption (this stays project-local).

Two samples have a different shape and do **not** invoke an agent or write
reports:

- **H1 Registry Discovery** — read-only walk of `BenchmarkFamilyRegistry.All`,
  no Azure needed.
- **H9 Report Browser** — interactive browser over past runs written by the
  running samples above; opens JSON / HTML / PDF in the OS default app.

**H8 LongMemEval** (ICLR 2025) is a Shape B benchmark — its runner returns an
`ExternalBenchmarkResult` (per-question / per-type) rather than the
`EvalResult` composite the renderers expect. The sample bridges by
synthesising an `EvalResult` tree (root = overall accuracy; children =
per-type composites; grandchildren = per-question atomic leaves with 0/1
score) so it produces the same canonical store + JSON + HTML + PDF artefacts
as every other running sample. The unaltered native shape is **also** written
to `report-native.json` alongside `report.json`.

If `AZURE_OPENAI_ENDPOINT` / `_API_KEY` / `_DEPLOYMENT` are missing, the running
samples skip with a clear box and no exceptions — safe to run in CI.

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

Every executing sample (H2 – H8) respects a **preset tier** so the same code
scales from a CI smoke check to a full audit:

| Sample              | Smoke (default)              | Standard                    | Audit-Grade                          |
|---------------------|------------------------------|------------------------------|---------------------------------------|
| **H2 Performance**  | 3 iters + 2s throughput      | 10 iters + 10s throughput   | 50 iters + 30s throughput            |
| **H3 Agentic**      | `ToolCallAccuracy` (5 evals) | `AgenticExecution` (6 evals)| `AgenticExecution` + stronger judge   |
| **H4 GDPR**         | `Smoke` (5 articles)         | `Standard` (21 articles)    | `AuditGrade` (21 articles, cap-by-worst) |
| **H5 EU AI Act**    | `Smoke` (5 controls)         | `Standard` (6 pillars)      | `AuditGrade` (6 pillars, cap-by-worst)   |
| **H6 OWASP**        | `Smoke` (3 attacks @ Quick)  | `Top10` (9 attacks @ Quick) | `AuditGrade` (9 @ Comprehensive)      |
| **H7 MITRE ATLAS**  | `AtlasSmoke`                 | `AtlasBaseline`             | `AtlasAuditGrade`                    |

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

Compliance samples (H4 GDPR, H5 EU AI Act) sit at the upper end because every
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
zero verdict. **H1 Registry Discovery** runs without credentials.

---

## Per-sample fidelity notes

| #  | Sample                  | What it exercises                                                                                       |
|----|-------------------------|----------------------------------------------------------------------------------------------------------|
| H1 | Registry Discovery      | No agent, no judge. Walks `BenchmarkFamilyRegistry` + force-loads sub-assemblies.                       |
| H2 | Performance             | Real agent, no judge. Latency + throughput + cost metrics against your live deployment.                  |
| H3 | Agentic                 | Real agent **invoked once** with a representative prompt; real judge grades the live response.           |
| H4 | GDPR                    | Real agent **invoked per scenario** with each YAML `input`; real judge grades each live response.        |
| H5 | EU AI Act               | Same per-scenario probing as GDPR; pillar-nested presets descend recursively.                            |
| H6 | OWASP LLM Top 10        | Real agent driven by the OWASP adversarial pipeline (Shape B runner — pipeline owns the probe loop).    |
| H7 | MITRE ATLAS             | Real agent driven by the ATLAS adversarial pipeline (Shape B runner).                                    |
| H8 | LongMemEval             | Real history-injectable agent + LLM judge. Subset (10Q/30Q embedded) or Full (~500Q via `LONGMEMEVAL_DATASET_PATH`). Shape-B result is synthesised into an `EvalResult` tree; native shape preserved in `report-native.json`. |
| H9 | Report Browser          | No agent. Opens previously-generated JSON / HTML / PDF artefacts.                                       |

---

## Honest limitations

- **H3 Agentic** invokes the agent **once** with a representative query. Real
  agentic evaluations probe across a dataset (see `samples/datasets/benchmark-tool-accuracy.jsonl`
  and `DataAndInfrastructure/04_BenchmarkSystem.cs` for the JSONL-driven flow).
- **H4 GDPR / H5 EU AI Act**: per-scenario probing reuses the canonical
  `ScenarioToAtomicEval` and the preset's aggregation strategy, but rebuilds
  the verdict tree from individual scenario results because the default
  `CompositeEval.EvaluateAsync` threads one input to every leaf. Provider
  safety filters that reject a probe surface as honest "skipped" leaves
  (rather than aborting the run) — a real audit would route around the filter
  or document the filter itself as a control.
- **H2 Performance** at Smoke / Standard preset measures your deployment under
  light load. Audit-grade (50 iters + 30s throughput) is closer to real
  bake-off conditions but is still single-machine and single-client.

---

## Viewing past runs

Sample **H9 Report Browser** (`09_ReportBrowser.cs`) lists every report under
`samples/AgentEval.Samples/output/` and lets you re-open the HTML / PDF / JSON
without re-running the benchmark.

---

## Where the rendering lives

- `HtmlEvalResultRenderer` → `src/AgentEval.Core/Evals/Rendering/`
- `PdfEvalResultRenderer` → `src/AgentEval.Rendering.Pdf/`
- The unified output writer + cost rollup helpers used by every sample live in
  `Benchmarks/_BenchmarkSampleHelpers.cs`.

---

## Where the runs are saved

Samples H2 – H8 write to **two** locations per run (v0.10.1, plan-25):

| Artefact                                      | Location                                                           | Why                                                                                                                              |
|-----------------------------------------------|--------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------|
| Manifest, scenarios, summary, compliance evidence | `<repo>/.agenteval/`                                              | **Canonical.** Same workspace `agenteval init` creates — resolved via the `*.sln`/`*.slnx`/`.git/` walk-up. Audit-chained; Mission Control + `agenteval doctor` read here.            |
| HTML report                                   | `samples/AgentEval.Samples/output/{family}/run-{utc}-{suffix}/report.html` | Direct browser open. Carries the canonical manifest's `ContentHash` in the audit-hash footer.                                    |
| PDF report                                    | `samples/AgentEval.Samples/output/{family}/run-{utc}-{suffix}/report.pdf`  | Direct PDF open. Same audit-hash footer.                                                                                          |
| Bare `report.json` (composite EvalResult)     | `samples/AgentEval.Samples/output/{family}/run-{utc}-{suffix}/report.json` | Legacy compat — what `09_ReportBrowser` reads to surface the score / label per run.                                              |

### Mission Control on a sample run

```bash
# From the repo root — MC auto-discovers .agenteval/ at the cwd.
dotnet run --project src/AgentEval.MissionControl
# Or via the CLI (when installed):
agenteval mc serve
```

MC reads the canonical tree only — sidecar HTML / PDF are out of scope for the
portal. Audit-chain validation (`agenteval doctor`) works against the canonical
tree as well.

### Compliance evidence

For H4 (GDPR) and H5 (EU AI Act) the sample additionally writes a regulator-grade
evidence document via `GDPRComplianceReporter` / `EuAiActComplianceReporter`:

- `.agenteval/compliance/GDPR/{subject}/{ts}/evidence.json`
- `.agenteval/compliance/EU-AI-Act/{subject}/{ts}/evidence.json`

For H6 (OWASP) and H7 (MITRE) the same shape lands via
`OWASPComplianceReporter` / `MITREATLASReporter`:

- `.agenteval/compliance/OWASP-LLM-Top10/{subject}/{ts}/evidence.json`
- `.agenteval/compliance/MITRE-ATLAS/{subject}/{ts}/evidence.json`

Each evidence file references the source run's `ContentHash`, so an auditor can
re-verify the chain end-to-end.
