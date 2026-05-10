# AgentEval Mission Control — Getting Started

> **Status**: Phase 1 — local viewer + workspace aggregator (target v1.2–v1.4). Mode C self-hosted server is Phase 2 (target v1.5).

Mission Control is the visualisation, aggregation, and governance layer on top of `.agenteval/`. This guide gets you a working portal in under 30 seconds against a populated solution.

---

## 30-second quickstart (Mode A — local viewer)

From inside any solution that has run `agenteval init`:

```bash
agenteval mc serve
```

Open `http://localhost:5000` in your browser — SPA, GraphQL (`/graphql`),
and REST (`/api/v1/*`) all serve from the same port. Use `--port N` to bind
elsewhere, or `--workspace <path>` to read a different `.agenteval/` folder.

ChilliCream's Nitro GraphQL playground is embedded at `/graphql` — explore the schema, run queries, click through into the recursive `EvalResult` tree.

### Equivalent without the CLI
If you've cloned the repo and want to run the project directly:

```bash
dotnet run --project src/AgentEval.MissionControl
```

Same endpoints, same port, same behaviour.

### Docker (single-binary container)

A multi-stage `Dockerfile` ships at the repo root. **Prerequisite**: run `agenteval init` in the host directory first — without an existing `.agenteval/` the container will render the empty-workspace landing page.

To build + run with your `.agenteval/` mounted read-only:

```bash
docker build -t agenteval/mc:latest .
docker run --rm -p 5000:5000 \
  -v "$(pwd)/.agenteval:/workspace/.agenteval:ro" \
  agenteval/mc:latest
```

Or, using the `docker-compose.yml` shipped at the repo root:

```bash
docker compose up
```

The image runs as a non-root user (UID 1654), exposes port 5000, and reads from `/workspace/.agenteval` (override the host path with `AGENTEVAL_WORKSPACE=…`).

**Cross-architecture builds.** Both base images (`node:22-alpine` and `mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0`) are multi-arch, so a single `docker build` produces an image native to your host (Apple Silicon → arm64, Intel/AMD → amd64). To publish a multi-arch image for both:

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t agenteval/mc:latest --push .
```

### First run — empty workspace?

If `.agenteval/` doesn't exist yet (or exists but isn't initialised), the SPA renders a guided welcome page with the three-step `init → bench → refresh` workflow instead of an empty dashboard. Run `agenteval init` then refresh.

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

Filterable by `category` and `costTier`. Drives the portal's evaluator-registry page.

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

REST stays for binary streams because GraphQL doesn't do streams cleanly. See [`api-design.md`](api-design.md) for the full split rationale.

---

## Configuration

| Setting | CLI flag | Env var | Default | What it does |
|---|---|---|---|---|
| Workspace root | `--workspace <path>` | `AgentEval__Root` | `Directory.GetCurrentDirectory()` | Where to find `.agenteval/`. Useful when running the portal binary from a different folder than the solution. |
| Port | `--port N` | n/a (see note) | `5000` | Bind a different HTTP port. |

> **Bind address.** `agenteval mc serve` always binds Mission Control to `http://127.0.0.1:<port>` (loopback only) — it overrides any pre-set `ASPNETCORE_URLS`. To expose Mission Control on a different interface (e.g. LAN), launch the portal binary directly instead: `dotnet run --project src/AgentEval.MissionControl` honours your `ASPNETCORE_URLS`. Phase 1 has no built-in auth — only bind broader interfaces on trusted networks.

> Note: ASP.NET Core uses double-underscore (`__`) as the env-var separator for hierarchical config keys. So `AgentEval:Root` in `appsettings.json` becomes `AgentEval__Root` as an env var.

---

## Architecture

- **Frontend** (when SPA ships): React 19 + Vite 6 + TypeScript 5.5 + Tailwind 4 + Recharts + Visx + TanStack Query + `graphql-request` (GraphQL transport) + GraphQL Code Generator (typed React hooks).
- **Backend**: .NET 10 + ASP.NET Minimal API + Hot Chocolate 16 (ChilliCream — *not* Microsoft) for GraphQL.
- **Storage**: filesystem-only in Mode A/B (the `.agenteval/` folder is the source of truth). Mode C adds SQLite (default) or PostgreSQL (config) as a hot-path index.

---

## Modes

| Mode | When to use | Setup |
|---|---|---|
| **A — Local viewer** | Solo dev / single-team, single repo | `agenteval mc serve` (or `dotnet run --project src/AgentEval.MissionControl`). The default `--workspace` is the current directory; pass `--workspace <path>` to point at a different repo's `.agenteval/`. |
| **B — Multi-workspace aggregator** | Platform engineer / AI lead reviewing multiple repos on one host | Phase 2 (target v1.5). Phase 1 reads exactly one `.agenteval/` per process; aggregation across multiple workspaces is deferred. See [`docs/deferred-pending.md`](../deferred-pending.md). |
| **C — Self-hosted server** | Org-wide collaboration with auth + multi-tenant + sync | Phase 2 (target v1.5) |

---

## Read-only guarantee

Mission Control consumes only `IOutputStoreReader` (the read-only abstraction). A reflection-based test (`ReaderOnlyArchitectureTests`) verifies at every build that no MissionControl type references `IOutputStore` (the write surface). The portal **cannot** corrupt your `.agenteval/` folder.

---

## Troubleshooting

**`Query.solution` returns `null`** — your `.agenteval/` folder isn't initialised. Run `agenteval init` in the solution root.

**Empty subjects / runs** — verify `.agenteval/subjects/` exists and contains `agents/` or `workflows/` subfolders.

**`GraphQL ... allowed depth: 10`** — you're issuing a query that recurses deeper than 10 levels. The depth limit guards against unbounded-tree attacks. Restructure the query — most production trees fit within 3 nested `details { subResults { ... } }` pairs.

**Tampered evidence detected** — `complianceMatrix.allChainsValid: false` means at least one piece of evidence's `manifest_hash` no longer matches its source run's `content_hash`. Run `agenteval doctor` to identify the affected runs.

---

## Further reading

- [`api-design.md`](api-design.md) — REST + GraphQL hybrid split.
- [`portal-ready-evaluators.md`](portal-ready-evaluators.md) — how to write an evaluator that renders well in the portal.
- [`charting.md`](charting.md) — Recharts vs Visx component mapping.
- [`agenteval-workspace.md`](../agenteval-workspace.md) — the on-disk standard Mission Control reads from.
