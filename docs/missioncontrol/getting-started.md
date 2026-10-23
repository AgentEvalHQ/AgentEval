# AgentEval Mission Control — Getting Started

> **Status**: Phase 1 — local viewer (Mode A) is shipped and what `agenteval mc serve` gives you today.
> Mode B (multi-workspace aggregator) and Mode C (self-hosted server) are both **not yet built** — see the
> Deployment Modes table below. `--workspace <path>` today only re-points the single workspace root Mode A
> reads from; it does not aggregate multiple repos.

Mission Control is the visualisation, aggregation, and governance layer on top of `.agenteval/`. This guide gets you a working portal in under 30 seconds against a populated solution.

---

## 30-second quickstart (Mode A — local viewer)

From inside any solution that has run `agenteval init-workspace`:

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

A multi-stage `Dockerfile` ships at the repo root. **Prerequisite**: run `agenteval init-workspace` in the host directory first — without an existing `.agenteval/` the container will render the empty-workspace landing page.

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

If `.agenteval/` doesn't exist yet (or exists but isn't initialised), the SPA renders a guided welcome page with the three-step `init → bench → refresh` workflow instead of an empty dashboard. Run `agenteval init-workspace` then refresh.

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

### Canonical scenario tree

Use `scenarioResultTree` for native AEF and converted legacy evidence. Pass the `sourceId` returned by `recentRuns` when selecting a run; duplicate run IDs require it. An optional `resultId` selects a node without dropping its siblings or other trials.

```graphql
query CaseTree($runId: String!, $scenarioId: String!, $sourceId: String) {
  scenarioResultTree(runId: $runId, scenarioId: $scenarioId, sourceId: $sourceId) {
    sourceId caseId rootResultIds
    metrics { id kind direction scale minimum maximum unit }
    nodes {
      resultId parentResultId path trial evaluatorId state
      scores { metricId value normalized }
      startedAt endedAt durationMs
      recordedJson
    }
  }
}
```

The response contains a flat list of every recorded node in the case, including trial and final roots. Follow `parentResultId` to reconstruct the hierarchy without recursive GraphQL selection limits. Cases larger than 10,000 nodes are refused explicitly; inspect their AEF results directly. Missing times, costs and scores remain unknown, and metric definitions determine each score's meaning. `recordedJson` retains fields outside the typed projection, including producer extensions.

Canonical descendants remain available with content capture off. Captured artifacts are listed separately and are downloadable only when their bytes exist in the verified snapshot. The older `scenarioTree` operation remains a legacy compatibility surface; it does not provide this canonical native view.

When an import includes a captured legacy evaluation tree, the page also shows **Captured legacy details** using the existing judge, adjudication, dimensions and recommendation panels. These details are checked against their canonical results before display; conflicting captures are refused. Missing measurements and costs remain explicitly unrecorded, while a recorded zero cost stays zero. Older imports can retain descendants only in this attachment; they do not acquire invented canonical result IDs. Both tree operations accept source/result selection for disambiguation.

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

- **Frontend** (`src/AgentEval.MissionControl.Spa`): React 19 + Vite 8 + TypeScript 5.7 + Tailwind 4 + Recharts (the only chart library) + TanStack Query 5 + react-router 8 + `graphql-request` (GraphQL transport, with hand-written queries). `npm run build` writes the bundle into `src/AgentEval.MissionControl/wwwroot/`, and the backend serves it on the same port as the API.
- **Backend**: .NET 10 + ASP.NET Minimal API + Hot Chocolate 16 (ChilliCream — *not* Microsoft) for GraphQL.
- **Storage**: filesystem-only in Mode A/B (the `.agenteval/` folder is the source of truth). Mode C adds SQLite (default) or PostgreSQL (config) as a hot-path index.

---

## Modes

| Mode | When to use | Setup |
|---|---|---|
| **A — Local viewer** | Solo dev / single-team, single repo | `agenteval mc serve` (or `dotnet run --project src/AgentEval.MissionControl`). The default `--workspace` is the current directory; pass `--workspace <path>` to point at a different repo's `.agenteval/`. |
| **B — Multi-workspace aggregator** | Platform engineer / AI lead reviewing multiple repos on one host | Phase 2 (target v1.5). Phase 1 reads exactly one `.agenteval/` per process; aggregation across multiple workspaces is deferred. |
| **C — Self-hosted server** | Org-wide collaboration with auth + multi-tenant + sync | Phase 2 (target v1.5) |

---

## Read-only guarantee

Mission Control consumes only `IOutputStoreReader` (the read-only abstraction). A reflection-based test (`ReaderOnlyArchitectureTests`) verifies at every build that no MissionControl type references `IOutputStore` (the write surface). The portal **cannot** corrupt your `.agenteval/` folder.

---

## Troubleshooting

**`Query.solution` returns `null`** — your `.agenteval/` folder isn't initialised. Run `agenteval init-workspace` in the solution root.

**Empty subjects / runs** — verify `.agenteval/subjects/` exists and contains `agents/` or `workflows/` subfolders.

**`GraphQL ... allowed depth: 10`** — you're issuing a query that recurses deeper than 10 levels. The depth limit guards against unbounded-tree attacks. Restructure the query — most production trees fit within 3 nested `details { subResults { ... } }` pairs.

**Tampered evidence detected** — `complianceMatrix.allChainsValid: false` means at least one piece of evidence's `manifest_hash` no longer matches its source run's `content_hash`. Run `agenteval doctor` to identify the affected runs.

---

## Further reading

- [`api-design.md`](api-design.md) — REST + GraphQL hybrid split.
- [`portal-ready-evaluators.md`](portal-ready-evaluators.md) — how to write an evaluator that renders well in the portal.
- [`charting.md`](charting.md) — what each SPA view draws, and with which Recharts component.
- [`agenteval-workspace.md`](../agenteval-workspace.md) — the on-disk standard Mission Control reads from.
