# AgentEval Mission Control SPA

React + Vite SPA that consumes the GraphQL + REST surface of `AgentEval.MissionControl`. Plan-08 MC1.6.*.

## Quick start

```bash
# Terminal 1 — start the dotnet backend
dotnet run --project ../AgentEval.MissionControl

# Terminal 2 — install + run the SPA
cd src/AgentEval.MissionControl.Spa
npm install
npm run dev
# → http://localhost:5173
```

The dev server proxies `/graphql` and `/api/v1/*` to `http://localhost:5000` (configured in `vite.config.ts`), so the SPA runs on its own port while the backend stays at 5000.

## GraphQL Code Generator

The SPA uses [graphql-request](https://github.com/jasonkuhrt/graphql-request) as the GraphQL transport (per plan-07 §6.1 — Apollo Client was rejected for bundle-size + cache-conflict reasons; TanStack Query is the single cache layer). [GraphQL Code Generator](https://the-guild.dev/graphql/codegen) emits typed React-Query hooks at `src/__generated__/graphql.ts`.

```bash
# One-shot codegen (requires backend running)
npm run codegen

# Watch mode (re-emits on .graphql / .ts query changes)
npm run codegen:watch
```

After `codegen` runs, you can replace hand-written `gqlRequest<T>(query, variables)` calls with typed hooks like `useSubjectsListQuery()`. See `src/pages/DashboardPage.tsx` for the pre-codegen pattern that the SPA falls back to until codegen has been run at least once.

## Stack

| Layer | Choice | Why |
|---|---|---|
| Framework | React 19 | Plan-07 §6.1 |
| Build | Vite 6 | Same |
| Language | TypeScript 5.7 | Same |
| Routing | react-router 7 | Same (note: the v7 API is back-compat with v6 patterns we use) |
| State / fetch | TanStack Query 5 | Same — handles cache for both GraphQL + REST |
| GraphQL transport | graphql-request 7 | ~10 KB; smaller than Apollo |
| GraphQL types | GraphQL Code Generator | Typed hooks from schema |
| Styling | Tailwind CSS 4 (via @tailwindcss/vite) | Plan-07 §6.1 |
| Icons | lucide-react | Same |

## Routes (current)

| Route | Status | Component |
|---|---|---|
| `/` | live | `DashboardPage` — version + subjects list (the smoke deliverable) |
| `/subjects` | live | Same as `/` for now (Wave 2 will split out a richer subjects view) |
| `/compliance` | placeholder | `ComplianceListPage` — Wave 4 implements the Visx matrix |
| `/evaluators` | placeholder | `EvaluatorsPage` — Wave 5 implements the registry table |
| `/runs/:runId` etc. | not yet | Waves 2-3 |

## Build artefacts

`dist/` is gitignored — produced by `npm run build`. The dotnet `MapStaticAssets` (plan-08 MC1.8.1) will eventually serve `dist/` from the same binary as the GraphQL endpoint; until then, `npm run dev` is the dev workflow.

## See also

- [`docs/missioncontrol/getting-started.md`](../../docs/missioncontrol/getting-started.md) — running the portal end-to-end.
- [`docs/missioncontrol/api-design.md`](../../docs/missioncontrol/api-design.md) — REST + GraphQL split rationale.
- [`docs/missioncontrol/charting.md`](../../docs/missioncontrol/charting.md) — Recharts vs Visx component-library map.
