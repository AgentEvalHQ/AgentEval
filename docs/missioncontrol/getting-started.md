# AgentEval Mission Control — Getting Started

> **Status**: v1.7+ Phase 1 (local viewer + workspace aggregator). Mode C self-hosted server lands in v1.5+.

Mission Control is the visualisation, aggregation, and governance layer on top of `.agenteval/`. This guide gets you a working portal in under 30 seconds against a populated solution.

---

## 30-second quickstart (Mode A — local viewer)

From inside any solution that has run `agenteval init`:

```bash
dotnet run --project src/AgentEval.MissionControl
```

Open `http://localhost:5000/graphql` in your browser. ChilliCream's Nitro UI ships embedded — explore the schema, run queries, click through into the recursive `EvalResult` tree.

> The CLI subcommand `agenteval mc serve` is wired in [plan-08 MC1.7.1](../../strategy/FutureFeatures/todo/08-AgentEval-MissionControl-ImplementationPlan.md#mc171). Until that lands, use `dotnet run` as above.

---

## Sample queries

The portal exposes both REST and GraphQL surfaces. Reads → GraphQL; binary streams + ingest → REST.

### List subjects

```graphql
{
  subjects {
    identity { kind name }
    lastRun { runId verdict }
  }
}
```

### Recursive eval-result tree (the GraphQL killer feature)

```graphql
{
  scenarioTree(runId: "2026-05-09_14-30-22_a3f91c2b", scenarioId: "scenario-1") {
    metric { key name }
    score { value passed severity }
    details {
      aggregationStrategy
      subResults {
        metric { key name }
        score { value passed }
        details {
          subResults { metric { key } score { value } }
        }
      }
    }
  }
}
```

A single fragment walks the whole composite tree in **one round-trip**. Compare with REST, which would require either a fat endpoint (large response) or a `?depth=N` parameter (chatty).

### Compliance dashboard matrix

```graphql
{
  complianceMatrix(regulation: "gdpr") {
    subjects { name kind }
    controls { id title }
    cells {
      subjectName
      controlId
      status
      passRate
      lastEvidenceAt
    }
    allChainsValid
  }
}
```

`allChainsValid` reports `true` when every cell's source-evidence `manifest_hash` matches its run's `content_hash` — this is the audit-chain integrity check.

### Evaluator registry

```graphql
{
  evaluators(costTier: HIGH) {
    key
    name
    description
    expectedInputs { kind key required description }
  }
}
```

Filterable by `category` and `costTier`. Drives the portal's `<EvaluatorRegistry/>` page (plan-07 §10).

---

## REST endpoints

For binary / streaming data:

| Endpoint | Returns |
|---|---|
| `GET /api/v1/version` | `{ mode, agentEvalVersion, graphqlEndpoint }` |
| `GET /api/v1/runs/{runId}/trace` | `application/json` (the `agent-trace.json`) |
| `GET /api/v1/runs/{runId}/reports/{format}` | `markdown` / `html` / `junit` / `sarif` |
| `GET /api/v1/compliance/{reg}/{subject}/{ts}/report.pdf` | `application/pdf` |
| `GET /api/v1/compliance/{regulation}/schema` | The evidence JSON schema |
| `GET /api/v1/subjects/{kind}/{name}/history` | `application/x-ndjson` (history stream) |

REST stays for binary streams because GraphQL doesn't do streams cleanly (per [plan-07 §3 Challenge 1](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md#3-where-we-depart-from-the-master-analysis-seven-challenges)).

---

## Configuration

| Setting | Env var | Default | What it does |
|---|---|---|---|
| Workspace root | `AgentEval__Root` | `Directory.GetCurrentDirectory()` | Where to find `.agenteval/`. Useful when running the portal binary from a different folder than the solution. |
| Port | (ASP.NET default) | `5000` (HTTP) / `5001` (HTTPS) | Override via `--urls http://0.0.0.0:5050` |

> Note: ASP.NET Core uses double-underscore (`__`) as the env-var separator for hierarchical config keys. So `AgentEval:Root` in `appsettings.json` becomes `AgentEval__Root` as an env var.

---

## Architecture

- **Frontend** (when SPA ships): React 19 + Vite 6 + TypeScript 5.5 + Tailwind 4 + Recharts + Visx + TanStack Query + `graphql-request` (GraphQL transport) + GraphQL Code Generator (typed React hooks).
- **Backend**: .NET 10 + ASP.NET Minimal API + Hot Chocolate 16 (ChilliCream — *not* Microsoft) for GraphQL.
- **Storage**: filesystem-only in Mode A/B (the `.agenteval/` folder is the source of truth). Mode C adds SQLite (default) or PostgreSQL (config) as a hot-path index — see plan-07 §6.3.

---

## Modes

| Mode | When to use | Setup |
|---|---|---|
| **A — Local viewer** | Solo dev / single-team, single repo | `dotnet run --project src/AgentEval.MissionControl` |
| **B — Workspace aggregator** | Platform engineer / AI lead reviewing multiple repos on one machine | `--workspace ./repos` (lands with MC1.7.3) |
| **C — Self-hosted server** | Org-wide collaboration with auth + multi-tenant + sync | Plan-08 Phase 2 (v1.5+) |

---

## Read-only guarantee

Mission Control consumes only `IOutputStoreReader` (the read-only abstraction extracted in MC1.1.1). A reflection-based test (`ReaderOnlyArchitectureTests`) verifies at every build that no MissionControl type references `IOutputStore` (the write surface). The portal **cannot** corrupt your `.agenteval/` folder.

---

## Troubleshooting

**`Query.solution` returns `null`** — your `.agenteval/` folder isn't initialised. Run `agenteval init` in the solution root.

**Empty subjects / runs** — verify `.agenteval/subjects/` exists and contains `agents/` or `workflows/` subfolders.

**`GraphQL ... allowed depth: 8`** — you're issuing a query that recurses deeper than 8 levels. The depth limit guards against unbounded-tree attacks (plan-07 §8.1). Restructure the query.

**Tampered evidence detected** — `complianceMatrix.allChainsValid: false` means at least one piece of evidence's `manifest_hash` no longer matches its source run's `content_hash`. Run `agenteval doctor` to identify the affected runs.

---

## Further reading

- [Mission Control Design (plan-07)](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md) — architecture + 7 challenges to the master analysis.
- [Mission Control Implementation Plan (plan-08)](../../strategy/FutureFeatures/todo/08-AgentEval-MissionControl-ImplementationPlan.md) — per-task tracking.
- [`portal-ready-evaluators.md`](portal-ready-evaluators.md) — how to write an evaluator that renders well in the portal.
- [`charting.md`](charting.md) — Recharts vs Visx component mapping.
