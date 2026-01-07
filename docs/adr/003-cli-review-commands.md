# ADR-003: CLI Review Commands

**Status:** Proposed  
**Date:** 2026-01-07  
**Decision Makers:** AgentEval Contributors

---

## Context

### Current CLI State

AgentEval CLI provides:

| Command | Purpose | Status |
|---------|---------|--------|
| `agenteval eval` | Run evaluations | ✅ Implemented |
| `agenteval init` | Initialize config | ✅ Implemented |
| `agenteval list` | List metrics/assertions/formats | ✅ Implemented |

### Problem

After running multiple evaluations, users need to:

1. **View summary** — Compare aggregate metrics across runs
2. **Diff runs** — See which specific tests changed between versions
3. **Identify regressions** — Quickly spot degraded metrics

**Current workflow (manual):**
```bash
# Run evaluations
agenteval eval --output run1.json
agenteval eval --output run2.json

# Compare manually (no tooling!)
# User must write custom scripts or eyeball JSON files
```

**Competitor workflow (`ai-rag-chat-evaluator`):**
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
- **Competitive Parity** — Matches competitor functionality

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

### Alternative D: TUI (Terminal UI) Like Competitor
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
