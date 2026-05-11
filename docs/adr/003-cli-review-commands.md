# ADR-003: CLI Review Commands

**Status:** Proposed — **Deferred** (not implemented as of v0.8.1-beta)
**Date:** 2026-01-07
**Last reviewed:** 2026-05-11
**Decision Makers:** AgentEval Contributors

> **Implementation status (2026-05-11)** — The `agenteval summary` and `agenteval diff` commands proposed below were never implemented. Their goals (cross-run comparison, regression detection) are now better served by **Mission Control** (`agenteval mc serve` — see `docs/missioncontrol/getting-started.md`), which renders the same comparisons as a richer interactive UI. The CLI-side proposal is retained here as a historical decision record; it may be revisited in a future minor if a headless terminal flow is requested.

---

## Context

### Current CLI State

AgentEval CLI provides:

| Command | Purpose | Status |
|---------|---------|--------|
| `agenteval init / doctor / migrate` | Workspace lifecycle | ✅ Implemented |
| `agenteval bench {gdpr,eu-ai-act,agentic}` | Run compliance + agentic benchmarks | ✅ Implemented |
| `agenteval bench {gdpr,eu-ai-act,agentic} calibrate` | Judge calibration suites | ✅ Implemented |
| `agenteval compliance render` / `agenteval render --benchmark agentic` | Re-render reports (no LLM cost) | ✅ Implemented |
| `agenteval mc serve / mc doctor` | Mission Control portal | ✅ Implemented |
| `agenteval eval` (cross-framework dataset runner) | Originally proposed in this ADR | ⬜ Deferred — superseded by `agenteval bench` and the in-tree `samples/AgentEval.Samples` runner |

### Problem

After running multiple evaluations, users need to:

1. **View summary** — Compare aggregate metrics across runs
2. **Diff runs** — See which specific tests changed between versions
3. **Identify regressions** — Quickly spot degraded metrics

**Current workflow** (as of v0.8.1-beta — Mission Control covers comparison; the CLI handles execution only):
```bash
# Run evaluations against your subject
agenteval bench agentic --subject MyAgent --input "..."   # writes under .agenteval/

# Compare runs interactively in Mission Control
agenteval mc serve
# Navigate to http://localhost:5000 → Compliance Matrix / Run Detail pages
```

The original proposal below describes a CLI-side comparison flow (`agenteval summary` / `agenteval diff`) that was superseded by Mission Control.

**Example workflow (ai-rag-chat-evaluator):**
```bash
# View all runs
python -m evaltools summary ./results

# Compare specific runs
python -m evaltools diff ./results/run1 ./results/run2 --changed=relevance
```

### User Value

| Feature | Value | Effort |
|---------|-------|--------|
| `summary` command | High — quick overview of all runs | Low — table formatting |
| `diff` command | High — identifies regressions | Medium — comparison logic |
| `--changed` filter | Medium — focuses attention | Low — simple filter |

---

## Decision

**Add two new CLI commands for reviewing evaluation results:**

### Command: `agenteval summary`

```bash
agenteval summary ./results

┌─────────────────────┬────────────┬─────────────┬──────────────┬───────┐
│ Run                 │ Pass Rate  │ Avg Latency │ Avg Relevance│ Tests │
├─────────────────────┼────────────┼─────────────┼──────────────┼───────┤
│ 2026-01-07_baseline │ 85%        │ 1.2s        │ 78           │ 50    │
│ 2026-01-07_v2       │ 92%        │ 1.1s        │ 85           │ 50    │
│ 2026-01-08_v3       │ 94%        │ 0.9s        │ 88           │ 50    │
└─────────────────────┴────────────┴─────────────┴──────────────┴───────┘
```

**Options:**
- `--format <table|json|markdown>` — Output format (default: table)
- `--highlight <run>` — Highlight a specific run for comparison

### Command: `agenteval diff`

```bash
agenteval diff ./results/run1 ./results/run2

Comparing: 2026-01-07_baseline → 2026-01-07_v2

Changed tests: 7 of 50

┌────────────────────────────────────┬──────────┬──────────┬────────┐
│ Test                               │ run1     │ run2     │ Delta  │
├────────────────────────────────────┼──────────┼──────────┼────────┤
│ customer_returns_question          │ 72       │ 89       │ +17 ⬆️ │
│ password_reset_flow                │ 85       │ 91       │ +6  ⬆️ │
│ billing_inquiry                    │ 90       │ 82       │ -8  ⬇️ │
└────────────────────────────────────┴──────────┴──────────┴────────┘
```

**Options:**
- `--changed <metric>` — Show only tests where metric changed
- `--threshold <n>` — Minimum delta to show (default: 0)
- `--format <table|json|markdown>` — Output format

### Console Visualization

Use **Spectre.Console** (already a dependency) for rich output:

```csharp
// Already referenced in AgentEval.Cli
using Spectre.Console;

// Rich table output
var table = new Table()
    .AddColumn("Run")
    .AddColumn("Pass Rate")
    .AddColumn("Latency");

table.AddRow("baseline", "[green]85%[/]", "1.2s");
table.AddRow("v2", "[green]92%[/]", "1.1s");

AnsiConsole.Write(table);
```

**No additional dependencies required.**

---

## Consequences

### Positive

- **Quick Insights** — See run status at a glance
- **Regression Detection** — Immediately spot degraded tests
- **CI-Friendly** — `--format json` enables scripted comparison
- **No New Dependencies** — Uses existing Spectre.Console
- **Industry Best Practice** — Matches evaluation framework standards

### Negative

- **Requires ADR-002** — Needs structured result directories
- **CLI Expansion** — More commands to maintain

### Neutral

- **Optional** — Users can still use raw JSON if preferred

---

## Alternatives Considered

### Alternative A: Web Dashboard Only
**Rejected** — Adds infrastructure requirements; CLI is simpler for local use.

### Alternative B: HTML Report Diff
**Rejected** — Better as Pro feature; CLI addresses immediate need.

### Alternative C: VS Code Extension
**Considered for future** — Good UX but higher effort.

### Alternative D: TUI (Terminal UI)
```
┌──────────────────────────────────────────────────┐
│ Question: How do I reset my password?            │
├────────────────────┬────────────────────────────┤
│ run1               │ run2                       │
│ Click forgot pass  │ To reset your password...  │
│ relevance: 72      │ relevance: 89 ⬆️           │
├────────────────────┴────────────────────────────┤
│ [Next] [Previous] [Quit]                        │
└──────────────────────────────────────────────────┘
```
**Deferred** — Good for v2; requires more effort. Start with simple table output.

---

## Implementation

1. **Prerequisite:** Implement ADR-002 (DirectoryExporter)
2. Add `SummaryCommand` reading `summary.json` files
3. Add `DiffCommand` reading `results.jsonl` files
4. Use Spectre.Console tables with color coding
5. Add `--format` option for CI integration

### File Dependencies

```
results/
└── run1/
    ├── results.jsonl   ← DiffCommand reads this
    └── summary.json    ← SummaryCommand reads this
```

---

## References

- [ai-rag-chat-evaluator summary/diff](https://github.com/Azure-Samples/ai-rag-chat-evaluator)
- [Spectre.Console Documentation](https://spectreconsole.net/)
- [ADR-002: Result Directory Structure](002-result-directory-structure.md)
