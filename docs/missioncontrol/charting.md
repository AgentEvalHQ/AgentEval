# Mission Control Charting — Recharts vs Visx Decision Matrix

This is the operational companion to [plan-07 §3 Challenge 2](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md#3-where-we-depart-from-the-master-analysis-seven-challenges) where we challenged the master analysis's Apache ECharts choice.

---

## TL;DR

- **Recharts** for ~80% of charts (timelines, sparklines, bar/area, radar, score histograms, cost-tier breakdown).
- **Visx** for the remaining 20% — heatmaps (compliance matrix), sankey diagrams (handoff flows), node-link graphs (adjudication flow).
- **Custom React + Tailwind** for trace waterfalls (just absolutely-positioned divs — no library needed).
- **No Apache ECharts** — bundle is too heavy (~330 KB gzipped vs Recharts ~95 KB) and the imperative options-config style is non-idiomatic in React.

---

## Component → library map

| Component | Library | Why this library |
|---|---|---|
| `<TimelineChart/>` | Recharts `<LineChart>` | Built-in, declarative React |
| `<SubjectCard/>` sparkline | Recharts `<Sparkline>` | Same |
| `<HistogramChart/>` (score distribution) | Recharts `<BarChart>` | Same |
| `<RadarChart/>` (per-evaluator profile) | Recharts `<RadarChart>` | Built-in |
| `<CostTierBreakdownChart/>` | Recharts `<StackedBarChart>` | Same |
| `<ComplianceMatrix/>` | **Visx** `<HeatmapCircle/>` + Tailwind | Recharts can't render dense matrices well |
| `<EvidenceTimeline/>` | Visx | Vertical timeline with custom annotations |
| `<AdjudicationFlow/>` | Visx `<NodeLink/>` | 3 judges → adjudicator flow diagram |
| `<TraceReplayer/>` (waterfall) | Custom React + Tailwind | Just absolutely-positioned divs; no library needed |
| `<JudgeDriftChart/>` | Recharts `<LineChart>` | Same |
| `<CalibrationReport/>` | Recharts `<BarChart>` + custom annotations | Cohen's kappa visualised as bars + threshold lines |

---

## Bundle-size budget

The SPA targets a TTI under 2s on a populated dashboard. Approximate gzipped contributions:

| Lib | Size | Status |
|---|---|---|
| React 19 + Scheduler | ~45 KB | unavoidable |
| Recharts (used routes only) | ~95 KB | acceptable |
| Visx (`@visx/heatmap`, `@visx/network`, `@visx/scale`) | ~60 KB | acceptable for the matrix + sankey win |
| TanStack Query | ~14 KB | acceptable |
| `graphql-request` | ~10 KB | acceptable |
| Tailwind 4 (purged) | ~20 KB | acceptable |
| `react-pdf` (lazy-loaded on compliance routes) | ~150 KB | lazy — only loads on PDF view |
| **Initial bundle target** | **~245 KB** | leaves headroom for app code |

ECharts alone (~330 KB) would blow this budget.

---

## Why not D3 directly?

Visx *is* D3 — it's "D3 + React-friendly primitives". You get D3's mathematical primitives (`scaleLinear`, `scaleBand`, layouts, transitions) without the imperative DOM-manipulation that doesn't compose with React's render lifecycle.

For the compliance heatmap specifically, `@visx/heatmap`'s `<HeatmapCircle/>` produces SVG output that React can mount/unmount cleanly. D3 directly would require `useEffect` lifecycle management for every cell.

---

## How `recommendedVisualization` maps to components

The `EvaluatorCard.recommendedVisualization` field gives the portal a hint for what to render on a given metric's detail view. The values map as follows:

| Hint | Default component | Use when |
|---|---|---|
| `timeline` | `<TimelineChart/>` | Score over time (per-subject history) |
| `radar` | `<RadarChart/>` | Multi-dimensional metric (composite of N sub-judges) |
| `histogram` | `<HistogramChart/>` | Distribution of a single metric across scenarios |
| `heatmap` | `<ComplianceMatrix/>`-style cell grid | Subjects × controls or runs × metrics |
| `sparkline` | `<Sparkline/>` (Recharts inline) | Compact recent-trend view in a list cell |
| `sankey` | Visx `<Sankey/>` | Flow visualisations (tool-call handoffs) |
| `stackedBar` | Recharts `<StackedBarChart>` | Cost-tier breakdown, token-usage breakdown |
| `none` | (text-only render) | Pure-code metrics where a chart adds nothing |

---

## See also

- [Plan-07 §3 Challenge 2](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md#3-where-we-depart-from-the-master-analysis-seven-challenges) — original Recharts-vs-ECharts decision.
- [Plan-07 §10](../../strategy/FutureFeatures/todo/07-AgentEval-MissionControl-Design.md#10-ui-design) — full component inventory.
- [`portal-ready-evaluators.md`](portal-ready-evaluators.md) — `recommendedVisualization` values and how to author them.
