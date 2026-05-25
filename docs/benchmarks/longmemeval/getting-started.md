# LongMemEval Benchmark — Getting Started

> Status: beta. LongMemEval is a thin façade over the `LongMemEvalBenchmarkRunner` that drives the agent under test against the academic LongMemEval dataset (ICLR 2025) and grades each response with a per-question-type LLM judge using the paper's binary (0/1) methodology.
>
> Coverage: 2 presets — `subset` (default 50 stratified questions from the real ~500-question dataset) and `full` (all ~500 questions). Both presets read the REAL `longmemeval_s_cleaned.json` from disk — no embedded subset is bundled (v0.10.0's hand-authored 10-entry approximation was removed in v0.10.1-beta because it produced misleading scores that looked paper-comparable but were not).

## What this measures

LongMemEval is an external academic benchmark for evaluating long-term memory in LLM-based agents. It exercises 6 question-type labels in the cleaned dataset (information_extraction, multi_session_reasoning, knowledge_update, temporal_reasoning, single_session_user, single_session_assistant) and reports overall accuracy + task-averaged accuracy.

> **Reconciliation with the LongMemEval paper (arXiv 2410.10813).** The published paper defines **5 question categories** by treating the `single_session_*` family as one bucket ("single-session"). The cleaned dataset (`longmemeval_s_cleaned.json` on Hugging Face) splits that bucket into the 2 `single_session_*` labels you see above, yielding **6 type labels** at the data level. Both views are valid; the doc and runner work in terms of the 6 dataset labels (because that's what the JSONL carries) but paper-comparable results aggregate them as 5. When citing results to reviewers, prefer the paper's 5-category framing; when reading per-type breakdowns in `report-native.json`, expect the 6 dataset labels.

The runner replays each entry's `haystack_sessions` (multi-turn conversation history) into the agent under test via `AsEvaluableAgent(includeHistory: true)`, then asks the question and grades the answer with a per-question-type LLM judge using LongMemEval's official binary correctness criterion. ~2 LLM calls per question (one for the agent's query response + one for the judge).

What IS tested: multi-turn memory recall across stratified question types from the official LongMemEval corpus, with binary correctness grading that matches the paper's methodology. What is NOT tested: the agent's own memory architecture choices (reducers, vector stores, summarisation strategies) in isolation — LongMemEval treats the agent as a black box and only sees the final answer. For architecture-level memory testing (reducer fidelity, context-overflow behaviour, cross-session state), see the sibling `memory` family.

## Scope and omissions

- Covered (with rationale per item):
  - 6 official question types via stratified sampling — preserves the paper's distribution so results are comparable to published baselines.
  - Per-question-type LLM judge with binary (0/1) scoring — matches the paper's reproducibility criterion.
  - Real dataset (no synthetic / approximated entries) — every run reads `longmemeval_s_cleaned.json` from disk.
  - Overall accuracy + task-averaged accuracy reporting — task-averaged corrects for type imbalance across the sample.
  - Stratified sampling with reproducible seed (`RandomSeed = 42`) — same `subset` cap → same sample → comparable runs over time.
- Out of scope (with rationale):
  - Per-agent memory architecture comparison — LongMemEval treats the agent as a black box; for architecture-level scoring use the `memory` family.
  - Multi-turn judge — the judge sees only `(history, question, answer)` per entry, not the full conversational context the agent received.
  - Multi-language support — LongMemEval is English-only.
  - Code-grader fallback — when the judge call fails the entry is marked as judge-failure; there is no syntactic / regex fallback.
  - Per-entry trace inspection — the native `ExternalBenchmarkResult` reports aggregate accuracies and per-question correctness, but not full per-turn trace artefacts (use programmatic access to the runner for that).

## Presets

Sourced verbatim from `BenchmarkFamilyRegistry` (see `src/AgentEval.Memory/External/LongMemEval/LongMemEvalBenchmarkRegistration.cs:51-55`).

| Preset | Description (verbatim) | Cost tier | Typical entry count | Approx. LLM cost |
|---|---|---|---|---|
| `subset` | Real LongMemEval dataset capped to MaxQuestions (default 50) - requires the dataset file on disk | Medium | 50 stratified questions (cap configurable via `ExternalBenchmarkOptions.MaxQuestions`) | ~$0.25 - $1.00 against gpt-4o-mini (50 questions x 2 LLM calls/question) |
| `full` | Full ~500-question dataset (requires LONGMEMEVAL_DATASET_PATH env var explicitly) | High | ~500 questions (entire `longmemeval_s_cleaned.json`) | ~$2.50 - $10.00 against gpt-4o-mini (500 questions x 2 LLM calls/question) |

Cost estimates assume `gpt-4o-mini` judge pricing and short answers; cost scales linearly with question count and inversely with judge model tier. A `gpt-4o`-class judge will run roughly 10x more expensive.

## Dataset acquisition (REQUIRED)

LongMemEval is an external dataset — AgentEval does NOT bundle it (the v0.10.0 embedded approximation was removed in v0.10.1-beta). Both presets require the real `longmemeval_s_cleaned.json` on disk:

- Canonical download: `https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main`
- Research repo: `https://github.com/xiaowu0162/LongMemEval` (cleanup pipeline + reference implementation)
- Paper: `https://arxiv.org/abs/2410.10813`

Dataset resolution order (highest precedence first):
1. Explicit `ExternalBenchmarkOptions.DatasetPath` (programmatic override).
2. `LONGMEMEVAL_DATASET_PATH` environment variable.
3. Canonical local default: `<workspace-root>/src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json`.

The `subset` preset falls through all three steps. The `full` preset REQUIRES the env var explicitly (no fallback to the canonical local path) — audit-grade runs against the full dataset must be deliberate, not accidental.

If the dataset cannot be located, the runner throws `LongMemEvalDatasetNotFoundException` with a message naming the canonical path + the env var + the download URL.

## CLI usage

```bash
# Subset (default 50 stratified questions) — falls through env var → canonical local path
agenteval bench longmemeval --preset subset --subject MyAgent

# Full (~500 questions) — requires LONGMEMEVAL_DATASET_PATH explicitly
export LONGMEMEVAL_DATASET_PATH=/data/longmemeval/longmemeval_s_cleaned.json
agenteval bench longmemeval --preset full --subject MyAgent
```

REQUIRES Azure OpenAI — no stub fallback. All three of `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_DEPLOYMENT` must be set because the LLM round-trips ARE the correctness signal (~2 LLM calls per question for the agent query + the per-type judge).

The CLI runs the agent-under-test via `chatClient.AsEvaluableAgent(name: subject, includeHistory: true)` against the resolved Azure deployment. Programmatic callers can swap in any `IChatClient`-backed agent and pass it to `LongMemEvalBenchmark.Subset(client)` / `LongMemEvalBenchmark.Full(client)` directly.

## Output

Each run writes to the canonical run dir under `.agenteval/subjects/agents/{subject}/runs/{runId}/`:

- `report-native.json` — the native `ExternalBenchmarkResult` (Shape B, ADR-017 Convention 3): full per-question correctness, overall accuracy, task-averaged accuracy, total LLM calls, total duration.
- The canonical `manifest.json` / `summary.json` carry the run-level audit-chain metadata (run ID, content hash, timestamp, verdict, metrics).

LongMemEval is a Shape B family (per ADR-017): its native semantics ("N questions → accuracy") do not map onto the Convention-2 `(EvalInput) → EvalResult` shape that OWASP / MITRE / Perf use, so no `report.json` / `report.md` / `report.html` / `report.pdf` sidecars are emitted. Mission Control renders the native shape directly.

The CLI verdict is `PASS` when `overall_accuracy >= 0.5`, otherwise `FAIL`. The 0.5 threshold is a defensive smoke-bar — the paper baselines run substantially above 0.5 for production-quality memory architectures.

## Interpreting results

LongMemEval uses binary (0/1) per-question correctness, then aggregates to:

- `overall_accuracy` — total correct / total questions. Sensitive to the natural type-distribution of the sample.
- `task_averaged_accuracy` — mean per-type accuracy. Corrects for type imbalance; useful when comparing across runs with different `MaxQuestions` caps.

Per-type accuracy is available in `report-native.json` under `QuestionResults` (grouped by `task` / question type). Each `QuestionResult` carries the question id, the type, the agent's answer, the judge's grade (correct / incorrect), and the LLM call count.

CLI verdict mapping:
- `overall_accuracy >= 0.5` → exit 0 (PASS).
- `overall_accuracy < 0.5` → exit 2 (FAIL).

Treat the threshold as a defensive smoke-bar, not a published acceptance criterion. The paper baselines for production-quality memory architectures sit substantially above 0.5; calibrate against your own baseline before treating PASS as production-ready.

## How to act on findings

- Low `single_session_user` / `single_session_assistant` accuracy — basic in-session recall is failing; check that the agent is receiving the full session history (`includeHistory: true` in the chat-client adapter).
- Low `multi_session_reasoning` accuracy — cross-session memory is failing; review reducer / summarisation strategy if any, and the chat-client's history-window policy.
- Low `temporal_reasoning` accuracy — the agent isn't tracking timestamps or relative ordering; check how (or whether) timestamps reach the agent (the runner exposes them via `IncludeTimestamps = true`).
- Low `knowledge_update` accuracy — the agent is sticking with the original fact rather than picking up the updated one; review summarisation / reducer policy for over-aggressive compression.
- Low `information_extraction` accuracy — basic recall is failing; check that the agent's response format matches what the judge expects (verbose preambles can confuse binary grading).

## When to use this benchmark

- You ship a memory-capable agent and want a published-baseline-comparable score on the 6 official LongMemEval question types.
- You are iterating on memory architecture (reducers, vector stores, summarisation) and need a held-out academic benchmark to detect regressions across the 6 question types.
- You want a cost-bounded smoke (default 50-question subset, ~$0.25 - $1.00) for CI sanity-checks.
- You need to produce a research / publication-ready evaluation result against a peer-reviewed benchmark.

When NOT to use:
- For architecture-level memory testing (reducer fidelity, context-overflow stress) — see the sibling `memory` family which targets the architecture directly.
- For non-English memory tests — LongMemEval is English-only.
- For agents without multi-turn history support — the runner replays `haystack_sessions` via `includeHistory: true`; agents that ignore history will trivially fail.
- When you need a stub / dry-run path — there is none. The LLM round-trip IS the signal.

## Programmatic use

The CLI is the supported path for canonical run persistence, but the `LongMemEvalBenchmark` factory + `LongMemEvalBenchmarkRunner` runner are public and usable from C# directly. Minimal example:

```csharp
using AgentEval.Benchmarks;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;

// Provide your own IChatClient (Azure OpenAI, OpenAI direct, etc).
IChatClient chatClient = /* your IChatClient */;

// Subset preset (default 50 stratified questions).
var runner = LongMemEvalBenchmark.Subset(chatClient);

// Or Full preset (requires LONGMEMEVAL_DATASET_PATH env var).
// var runner = LongMemEvalBenchmark.Full(chatClient);

var agent = chatClient.AsEvaluableAgent(
    name: "MyAgent",
    systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
    includeHistory: true);

var config = new AgentBenchmarkConfig
{
    AgentName = "MyAgent",
    ModelId = "gpt-4o-mini",
    ReducerStrategy = "None",
    MemoryProvider = "InMemoryChatHistory",
};

var result = await runner.RunAsync(agent, config, LongMemEvalBenchmark.SubsetOptions);
Console.WriteLine($"Overall accuracy: {result.OverallAccuracy:P1}");
Console.WriteLine($"Task-averaged:    {result.TaskAveragedAccuracy:P1}");
foreach (var q in result.QuestionResults.Take(10))
    Console.WriteLine($"  [{q.QuestionType}] {(q.Correct ? "+" : "-")} {q.QuestionId}");
```

The runner accepts any `IEvaluableAgent` — the `chatClient.AsEvaluableAgent(...)` extension is one convenience binding; you can pass any custom agent that knows how to consume injected history.

## Comparing across runs / baselines

LongMemEval runs are stored canonically under `.agenteval/subjects/agents/{subject}/runs/{runId}/`. Compare runs via:

- `git diff` on `report-native.json` — surfaces per-question correctness changes plus overall + task-averaged accuracy deltas.
- Mission Control — renders the native shape; visual diff across runs.
- Programmatic post-processing of `ExternalBenchmarkResult.QuestionResults` for per-type accuracy tracking outside AgentEval.

When tracking baselines, prefer `task_averaged_accuracy` to `overall_accuracy` — it corrects for type imbalance and stays comparable across runs with different `MaxQuestions` caps.

## Limitations and roadmap

Known limitations:
- The dataset is NOT bundled. Both presets require disk access to `longmemeval_s_cleaned.json` (env var or canonical local path); the runner throws a friendly exception with download instructions if missing.
- LLM-judge cost dominates (~2 calls per question + per-question-type judge prompts). Budget accordingly when running `full`.
- Per-turn trace inspection is not surfaced in the native `ExternalBenchmarkResult` — programmatic callers can use the runner's lower-level APIs for that.
- Multi-language support is out of scope (LongMemEval is English-only).
- The CLI verdict threshold (`overall_accuracy >= 0.5`) is a defensive smoke-bar, not a published acceptance criterion; calibrate against your own baseline rather than treating PASS as production-ready.

Tracking backlog (see `strategy/FutureFeatures/todo/13-pending-issues-tasks.md`):
- T0.6 — `agenteval bench longmemeval` CLI command (shipped 2026-05-24).
- T0.11 — Record judge model in baseline + pin Azure deployment (open; would make judge-model drift visible in `git diff`).
- T3.11 — Multi-provider agent-manifest schema (would let LongMemEval target non-Azure agents directly).

See also:
- [Memory getting-started](../memory/getting-started.md) — sister memory family targeting architecture-level evaluation.
- [Agentic getting-started](../agentic/getting-started.md) — broader agentic-quality benchmarks.
- LongMemEval paper: `https://arxiv.org/abs/2410.10813`
- LongMemEval research repo: `https://github.com/xiaowu0162/LongMemEval`
- `src/AgentEval.Memory/External/LongMemEval/LongMemEvalBenchmark.cs` — preset factory source.
- `src/AgentEval.Memory/External/LongMemEval/LongMemEvalBenchmarkRunner.cs` — runner source.
- `src/AgentEval.Cli/Commands/BenchLongMemEvalCommand.cs` — CLI subcommand source.
