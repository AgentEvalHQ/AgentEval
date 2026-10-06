# AgentEval Mission Control SPA

React + Vite SPA that consumes the GraphQL + REST surface of `AgentEval.MissionControl`.

## Quick start

```bash
# Terminal 1 — start the dotnet backend (from the repository root)
dotnet run --project src/AgentEval.MissionControl

# Terminal 2 — install + run the SPA (from the repository root)
cd src/AgentEval.MissionControl.Spa
npm install
npm run dev
# → http://localhost:5173
```

The dev server proxies `/graphql` and `/api/v1/*` to `http://localhost:5000` (configured in `vite.config.ts`), so the SPA runs on its own port while the backend stays at 5000.

## GraphQL queries

The SPA uses [graphql-request](https://github.com/jasonkuhrt/graphql-request) as the GraphQL transport, with TanStack Query as the single cache layer for both GraphQL and REST. Queries are hand-written: pages call `gqlRequest<T>(query, variables)` from `src/lib/graphql-client.ts` with its own result type. No GraphQL code generator is installed, and `package.json` has no `codegen` script; `src/__generated__/` is only reserved in `.gitignore`.

## Stack

Versions are the ranges declared in `package.json`.

| Layer | Package | Version |
|---|---|---|
| Framework | `react`, `react-dom` | 19 |
| Build | `vite` | 8 |
| Language | `typescript` | 5.7 |
| Routing | `react-router` | 8 |
| State / fetch | `@tanstack/react-query` | 5 |
| GraphQL transport | `graphql-request` | 7 |
| Charts | `recharts` (the only chart library) | 2 |
| Styling | `tailwindcss` via `@tailwindcss/vite` | 4 |
| Icons | `lucide-react` | 0.469 |

## Routes

From `src/App.tsx`. Every route renders inside `AppShell`, which shows `WorkspaceLandingPage` instead when the workspace is not initialised.

| Route | Component |
|---|---|
| `/`, `/subjects` | `DashboardPage` |
| `/subjects/:kind/:name` | `SubjectDetailPage` |
| `/runs` | `RunsListPage` |
| `/runs/:runId` | `RunDetailPage` |
| `/runs/:runId/scenarios/:scenarioId` | `ScenarioTreePage` |
| `/runs/:runId/trace` | `TraceWaterfallPage` |
| `/compliance` | `ComplianceListPage` |
| `/compliance/:regulation` | `ComplianceMatrixPage` (the matrix itself is `components/ComplianceMatrix.tsx`, a CSS grid) |
| `/compliance/:regulation/:kind/:name/:ts` | `EvidenceDetailPage` |
| `/evaluators` | `EvaluatorsPage` |
| `/evaluators/:key` | `EvaluatorDetailPage` |
| `/red-team` | `RedTeamCampaigns` (a list; there is no campaign detail page) |
| anything else | `NotFoundPage` |

## Build artefacts

`npm run build` type-checks (`tsc -b`) and then writes the bundle into `../AgentEval.MissionControl/wwwroot/` (`build.outDir` in `vite.config.ts`), emptying that folder first. The folder is gitignored (`src/AgentEval.MissionControl/.gitignore`), so a fresh clone has no bundle until you build one. The backend serves it from the same port as the GraphQL and REST endpoints (`UseDefaultFiles`, `MapStaticAssets` and a `MapFallbackToFile("index.html")` for client-side routes, in `McHost.cs`), so after a build `dotnet run --project src/AgentEval.MissionControl` serves the SPA and the API together. `npm run dev` is the hot-reload workflow. The repository's `Dockerfile` runs the same build in its first stage.

## See also

- [`docs/missioncontrol/getting-started.md`](../../docs/missioncontrol/getting-started.md) — running the portal end-to-end.
- [`docs/missioncontrol/api-design.md`](../../docs/missioncontrol/api-design.md) — REST + GraphQL split rationale.
- [`docs/missioncontrol/charting.md`](../../docs/missioncontrol/charting.md) — what each view draws, and with which Recharts component.
