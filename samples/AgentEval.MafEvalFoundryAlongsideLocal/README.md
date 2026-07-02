# AgentEval — Foundry evals alongside local evals (hybrid)

Score **one** MAF agent run with an **AgentEval Composite Eval** *and* **Azure AI Foundry** evals, and
render **both** in a single source-tagged HTML report. Demonstrates two patterns:

- **[A] MAF-native mix** — `agent.EvaluateAsync(queries, IAgentEvaluator[] { local, foundry })`. Simple;
  runs the evaluators sequentially and returns one result per evaluator.
- **[B] `CompositeAgentEvaluator`** *(recommended)* — one evaluator that fans out to both **concurrently**,
  isolates each (a Foundry failure/timeout becomes a visible "skipped" branch instead of losing the local
  results), bounds the slow cloud call with a per-source timeout, and merges into one report via
  `UnifiedEvalReport`.

## Prerequisites

```bash
# The AgentEval judge + the fallback (non-Foundry) agent:
export AZURE_OPENAI_ENDPOINT="https://<your>.openai.azure.com/"
export AZURE_OPENAI_API_KEY="<key>"
export AZURE_OPENAI_DEPLOYMENT="gpt-4o-mini"

# The Foundry evals backend + the Foundry-hosted agent (optional — omit to run local-only):
export FOUNDRY_PROJECT_ENDPOINT="https://<your-foundry-project>..."
export FOUNDRY_MODEL="gpt-4o-mini"
```

`FOUNDRY_PROJECT_ENDPOINT` uses `DefaultAzureCredential` (dev-friendly; use a specific credential in
production). If it's unset, the sample runs **AgentEval-local only** and the Foundry branch is skipped.

## Run

```bash
dotnet run --project samples/AgentEval.MafEvalFoundryAlongsideLocal
```

Writes `report-hybrid-B.html` (Pattern B) — and, when Foundry is configured, `report-mixed-A.html`
(Pattern A) — to the working directory. Each report has **one branch per source**: the AgentEval-local
branch keeps its full weighted composite hierarchy; the Foundry branch surfaces its portal report URL.

## Notes

- Uses the **preview** `Microsoft.Agents.AI.Foundry` package (the Foundry evals SDK has no stable release
  yet); the rest of the MAF stack is on the stable release. This is the only project in the repo that
  references Foundry — the core AgentEval libraries stay provider-agnostic.
- The composer, report merge, and circuit breaker live in `AgentEval.MAF` (`CompositeAgentEvaluator`,
  `UnifiedEvalReport`, `CircuitBreaker`) and work with **any** `IAgentEvaluator`, not just Foundry.
