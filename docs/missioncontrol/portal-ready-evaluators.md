# Portal-Ready Evaluators — Author Guide

This guide tells you how to write a new `IEval` so it renders well in Mission Control's `<EvaluatorRegistry/>` and per-metric tooltips. Plan-08 MC1.5.0 introduced the `EvaluatorCard` primitive; this is the user-facing companion.

---

## Why this exists

The shipped `eval-result.schema.json` carries the *result* shape (metric, score, details, provenance) but not the *evaluator's* metadata — cost tier, description, expected inputs, recommended visualisation, external compatibility. The portal needs all of those to render a useful registry view.

`EvaluatorCard` files fill the gap. One JSON file per evaluator at `src/AgentEval.Evals.Agentic/EvaluatorCards/<key>.json`, embedded as a resource, loaded into memory by `EvaluatorCardRegistry` at server startup.

---

## Five-step checklist

1. **Register a cost tier** in `EvaluatorCostMap` (`src/AgentEval.Abstractions/Evals/EvaluatorCostMap.cs`). One line. The portal's `Query.evaluators(costTier:)` filter drives off this.

2. **Author the EvaluatorCard JSON** at `src/AgentEval.Evals.Agentic/EvaluatorCards/<key>.json`. Schema below.

3. **Use `MetadataKey` constants** for any `EvalInput.Metadata` keys your evaluator reads (R4 convention from plan-06). E.g.:
    ```csharp
    public const string MetadataKey = "reasoning_trace";
    ```
    This way tests + external consumers reference the constant, not a string literal.

4. **Inherit from `AtomicLlmEval` / `AtomicCodeEval` / `CompositeEval`** for free `EvalProvenance` plumbing (judge model, prompt id, tokens, cost, cache-hit).

5. **Use the shipped helpers** where applicable: `ConversationHistoryHelper` for conversation history I/O (R1), `AdversarialPatternLibrary` for regex pattern libraries (R2). Don't re-implement.

---

## EvaluatorCard schema

```jsonc
{
  "schemaVersion": "1.0",                          // always "1.0" today
  "key": "tool_call_accuracy",                     // matches IEval.Key (snake_case)
  "name": "Tool Call Accuracy",                    // UI label
  "category": "process",                           // folder name (system/process/quality/...)
  "version": "1.0.0",                              // SemVer
  "description": "Composite of tool selection ...", // human-readable explanation
  "costTier": "Medium",                            // Trivial / Low / Medium / High
  "higherIsBetter": true,                          // false for refusal-resistance / cost / latency
  "defaultThreshold": 0.8,                         // suggested pass threshold (0..1)
  "expectedInputs": [
    { "kind": "query", "key": "", "required": true,
      "description": "User request that triggered the tool calls." },
    { "kind": "metadata", "key": "expected_actions", "required": false,
      "description": "Optional ground-truth actions for stricter scoring." }
  ],
  "recommendedVisualization": "radar",             // timeline / radar / histogram / heatmap /
                                                    // sparkline / sankey / stackedBar / none
  "compatibleWith": [
    { "system": "foundry",
      "uri": "azureai://built-in/evaluators/tool_call_accuracy" }
  ],
  "links": {
    "documentation": "/docs/benchmarks/agentic/getting-started.md#tool-call-accuracy",
    "source": "src/AgentEval.Evals.Agentic/Process/ToolCallAccuracyAggregateEval.cs"
  }
}
```

Validation: every shipped card validates against `evaluator-card.schema.json` v1.0 (in `AgentEval.DataLoaders/Output/Schema/v1/`). Lock-down tests (in `tests/AgentEval.Tests/EvaluatorCards/`) verify:

- Schema validation passes.
- Card's `key` matches an `EvaluatorCostMap` registration with the same tier.
- `links.source` resolves to an existing repo file.
- No duplicate keys across cards.

These tests run on every CI build — typos and tier-drift get caught at PR time.

---

## `expectedInputs.kind` enum

| Kind | Where it comes from | Typical use |
|---|---|---|
| `query` | `EvalInput.Query` | The user's request |
| `response` | `EvalInput.Response` | The agent's response |
| `context` | `EvalInput.Context` | Retrieved RAG context |
| `toolCalls` | `EvalInput.ToolCalls` | Tool invocations made by the agent |
| `systemMessage` | `EvalInput.SystemMessage` | The agent's system message |
| `metadata` | `EvalInput.Metadata[<key>]` | Anything else: telemetry, conversation history, expected_response, etc. |

For `metadata`, the `key` field names the dictionary key. For other kinds, `key` is empty.

---

## Authoring shortcut: the generator script

For batches of mechanically similar evaluators (e.g., all 6 telemetry ones), `tools/gen_evaluator_cards.py` ships in-tree with a compact in-script spec. Add an entry, re-run `python3 tools/gen_evaluator_cards.py`, and the card appears.

The generator is idempotent — it skips keys that already have a hand-authored card. Cards committed to the repo are the source of truth; the generator is just an ergonomic convenience.

---

## Forward compatibility

The `EvaluatorCard` v1.0 schema is intentionally minimal. Future versions may add:

- `complexityCost` — alongside `costTier`, a fine-grained complexity weight (for Hot Chocolate's complexity analyzer when it ships in the portal).
- `chartHints` — a richer rendering DSL (e.g., colour-stop thresholds for heatmaps).
- `provenance.expectedJudgeModel` — for cards that recommend specific judge models.

The portal's GraphQL schema auto-discovers the `EvaluatorCard` C# record, so adding fields is non-breaking for clients that don't query them.

---

## See also

- [`getting-started.md`](getting-started.md) — running the portal.
- [`charting.md`](charting.md) — what `recommendedVisualization` values mean in the SPA.
- [Plan-07 §11](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md#11-evaluatorcard--the-missing-primitive-for-schema-driven-ui) — design rationale.
