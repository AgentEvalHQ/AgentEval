# Mission Control Charting

This page describes what the Mission Control SPA (`src/AgentEval.MissionControl.Spa`) draws and how. It is
derived from the SPA source; where a component is mentioned, the file is under
`src/AgentEval.MissionControl.Spa/src/`.

---

## Summary

- **Recharts is the only charting library.** `package.json` lists `recharts` and no other drawing
  library — no Visx, D3 or ECharts.
- **Three views use no chart library at all**: the compliance matrix and the judge-panel view are CSS
  grid / flexbox with Tailwind classes, and the evaluator-detail preview falls back to pretty-printed JSON
  when it does not recognise a hint.
- **The trace waterfall is a Recharts bar chart**, not custom positioned divs.

The SPA's earlier design notes planned Visx for the compliance heatmap and the adjudication flow. That was
not built: both shipped as CSS grid / flexbox views, which keeps a single chart dependency.

---

## Component map

| Component | Where it appears | How it is drawn |
|---|---|---|
| `TimelineChart` (`components/charts/TimelineChart.tsx`) | Subject detail page ("Score over time", dashed threshold line fixed at 0.7) and evaluator detail page ("Score over recent runs", threshold line at the card's `defaultThreshold`) | Recharts `LineChart`, y-axis fixed to 0–1 |
| `Sparkline` (`components/charts/Sparkline.tsx`) | `SubjectCard` on the dashboard, latest 30 runs | Recharts `LineChart` with no axes, tooltip or legend |
| `HistogramChart` (`components/charts/HistogramChart.tsx`) | Subject detail page ("Score distribution") | Recharts `BarChart`; scores binned into 10 buckets of width 0.1 |
| `CostTierBreakdownChart` (`components/charts/CostTierBreakdownChart.tsx`) | Run detail page ("Cost by tier"), only when the run's total cost is above 0 | Recharts horizontal stacked `BarChart`: one segment per cost tier (trivial, low, medium, high) plus `unknown` |
| `TraceWaterfallPage` (`pages/TraceWaterfallPage.tsx`) | `/runs/:runId/trace` | Recharts horizontal `BarChart` with two stacked bars per event: a transparent one for the offset from the first event, and a visible one whose length is the gap to the next event. A trace event carries a single timestamp and no span, so the gap is the only duration available |
| `ComplianceMatrix` (`components/ComplianceMatrix.tsx`) | `/compliance/:regulation` | CSS grid of subjects × controls; each cell is a button coloured pass / warn / fail / no-data. A cell whose audit-chain check failed gets an amber ring, a striped overlay and a ⚠ glyph |
| `AdjudicationFlow` (`components/AdjudicationFlow.tsx`) | Inside an evaluation-result node that came from a judge panel, with or without an adjudicator | CSS grid / flexbox cards: one per panel judge, plus an adjudicator card when the panel disagreed and the adjudicator decided |
| `GenericEvaluatorRenderer` (`components/GenericEvaluatorRenderer.tsx`) | Evaluator detail page ("Visualisation preview") | Chosen by the card's `recommendedVisualization`; see the next section |
| `RadarProfileChart` (`components/charts/RadarProfileChart.tsx`) | Not rendered by any page | Recharts `RadarChart`; shows a placeholder below 3 dimensions |

---

## How `recommendedVisualization` is used

`EvaluatorCard.recommendedVisualization` is a free-text hint. The `EvaluatorCard` XML doc lists `radar`,
`timeline`, `histogram`, `heatmap`, `sparkline`, `sankey`, `stackedBar` and `none` as intended values; the 60
cards under `src/AgentEval.Evals.Agentic/EvaluatorCards/` use four of them: `histogram`, `radar`,
`timeline` and `stackedBar`.

The SPA reads the hint in one place, the evaluator detail page. It prints the hint, and passes it to
`GenericEvaluatorRenderer` together with the evaluator's recent-run timeline (one entry per run, with
`runId`, `timestamp` and `score`) as the payload. The renderer matches the hint case-insensitively:

| Hint | Preview |
|---|---|
| `bar-chart` or `bar` | Recharts `BarChart`, one bar per payload entry |
| `line-chart` or `line` | Recharts `LineChart`, one point per payload entry |
| `heatmap` | Single-row Recharts `BarChart`, each bar shaded by its value relative to the largest |
| `table` | Key / value table |
| anything else | The payload as pretty-printed JSON |

None of the four values the shipped cards use is in that list, so for every shipped evaluator the preview
is the JSON view. The same page always draws the score-over-time `TimelineChart` below it, whatever the
hint says. No component renders a `sankey`, and nothing draws a radar chart from a card's hint.

---

## Bundle size

No bundle-size budget is measured or enforced. `vite.config.ts` sets `chunkSizeWarningLimit: 800`, which
only controls when Vite prints a warning about a large chunk during `npm run build`.

---

## See also

- [`portal-ready-evaluators.md`](portal-ready-evaluators.md) — `recommendedVisualization` values and how to author them.
- [`getting-started.md`](getting-started.md) — running Mission Control locally.
- [`api-design.md`](api-design.md) — REST + GraphQL hybrid.
