// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Line,
  LineChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

// Plan-08 portal-review A5 (T2.2, 2026-05-25): switches on the EvaluatorCard
// `recommendedVisualization` hint and renders an appropriate preview chart
// using recharts (already in package.json). Before this shipped, the page
// only displayed the hint as a text label — meaning the operator had no
// in-product preview of how the evaluator's signal would be visualised.
//
// Recognised hints (see EvaluatorCard.recommendedVisualization in
// AgentEval.Abstractions and the JSON cards in
// src/AgentEval.Evals.Agentic/EvaluatorCards/):
//   - "bar-chart"  → vertical bars; one bar per payload point
//   - "heatmap"    → coloured grid; rows × cols inferred from payload shape
//   - "table"      → key / value table
//   - "line-chart" → continuous line; one point per payload entry
//   - timeline / sparkline → handled elsewhere (TimelineChart / Sparkline)
//   - anything unknown / null → pretty-printed JSON fallback so reviewers
//     can still inspect the raw payload shape and file a follow-up.

interface GenericEvaluatorRendererProps {
  recommendedVisualization: string | null | undefined;
  /** Raw payload to render — typically the evaluator timeline (runId/score/
   *  timestamp), the recursive EvalResult tree, or whatever Map-shaped data
   *  the GraphQL response carries. Free-form by design — the renderer is
   *  best-effort and falls back to JSON if the shape doesn't match the hint. */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  payload: any;
}

export function GenericEvaluatorRenderer({
  recommendedVisualization,
  payload,
}: GenericEvaluatorRendererProps) {
  const hint = (recommendedVisualization ?? "").toLowerCase().trim();

  switch (hint) {
    case "bar-chart":
    case "bar":
      return <BarChartRenderer payload={payload} />;
    case "heatmap":
      return <HeatmapRenderer payload={payload} />;
    case "table":
      return <TableRenderer payload={payload} />;
    case "line-chart":
    case "line":
      return <LineChartRenderer payload={payload} />;
    default:
      return <JsonFallback payload={payload} />;
  }
}

// ─── Normalisation ──────────────────────────────────────────────────────────

interface Point {
  label: string;
  value: number;
}

/** Coerce a free-form payload into a flat list of {label, value} points the
 *  bar/line renderers can consume. Recognised shapes:
 *    - Array of timeline entries: { runId, timestamp, score, ... }
 *    - Array of plain numbers: [0.81, 0.72, ...]
 *    - Array of {label/key/name, value/score}: {label, value}
 *    - Map-shaped object: { key1: 0.81, key2: 0.72 }
 *  Returns [] when nothing extractable is found. */
function normaliseToPoints(payload: unknown): Point[] {
  if (payload == null) return [];
  if (Array.isArray(payload)) {
    return payload
      .map((entry, idx): Point | null => {
        if (typeof entry === "number" && Number.isFinite(entry)) {
          return { label: String(idx + 1), value: entry };
        }
        if (entry && typeof entry === "object") {
          const obj = entry as Record<string, unknown>;
          const value =
            pickNumber(obj.value) ??
            pickNumber(obj.score) ??
            pickNumber(obj.count);
          if (value == null) return null;
          const label =
            pickString(obj.label) ??
            pickString(obj.name) ??
            pickString(obj.key) ??
            pickString(obj.runId) ??
            pickString(obj.timestamp) ??
            String(idx + 1);
          return { label, value };
        }
        return null;
      })
      .filter((p): p is Point => p !== null);
  }
  if (typeof payload === "object") {
    const obj = payload as Record<string, unknown>;
    return Object.entries(obj)
      .map(([k, v]): Point | null => {
        const value = pickNumber(v);
        return value == null ? null : { label: k, value };
      })
      .filter((p): p is Point => p !== null);
  }
  return [];
}

function pickNumber(v: unknown): number | null {
  return typeof v === "number" && Number.isFinite(v) ? v : null;
}

function pickString(v: unknown): string | null {
  return typeof v === "string" && v.length > 0 ? v : null;
}

// ─── Renderers ──────────────────────────────────────────────────────────────

function BarChartRenderer({ payload }: { payload: unknown }) {
  const points = normaliseToPoints(payload);
  if (points.length === 0) return <EmptyState hint="bar-chart" />;
  return (
    <div className="h-64">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={points} margin={{ top: 8, right: 8, bottom: 16, left: 8 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
          <XAxis dataKey="label" tick={{ fontSize: 11, fill: "#64748b" }} />
          <YAxis tick={{ fontSize: 11, fill: "#64748b" }} />
          <Tooltip
            contentStyle={{
              backgroundColor: "white",
              border: "1px solid #e2e8f0",
              fontSize: 12,
            }}
          />
          <Bar dataKey="value" fill="#3b82f6" />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

function LineChartRenderer({ payload }: { payload: unknown }) {
  const points = normaliseToPoints(payload);
  if (points.length === 0) return <EmptyState hint="line-chart" />;
  return (
    <div className="h-64">
      <ResponsiveContainer width="100%" height="100%">
        <LineChart data={points} margin={{ top: 8, right: 8, bottom: 16, left: 8 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
          <XAxis dataKey="label" tick={{ fontSize: 11, fill: "#64748b" }} />
          <YAxis tick={{ fontSize: 11, fill: "#64748b" }} />
          <Tooltip
            contentStyle={{
              backgroundColor: "white",
              border: "1px solid #e2e8f0",
              fontSize: 12,
            }}
          />
          <Line
            type="monotone"
            dataKey="value"
            stroke="#3b82f6"
            strokeWidth={1.5}
            dot={{ r: 3 }}
            isAnimationActive={false}
          />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}

function TableRenderer({ payload }: { payload: unknown }) {
  const points = normaliseToPoints(payload);
  if (points.length === 0) return <EmptyState hint="table" />;
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm border-collapse">
        <thead>
          <tr className="border-b border-slate-200 text-left text-xs uppercase tracking-wider text-slate-500">
            <th className="py-2 pr-4 font-medium">Key</th>
            <th className="py-2 pr-4 font-medium text-right">Value</th>
          </tr>
        </thead>
        <tbody>
          {points.map((p, i) => (
            <tr key={`${p.label}-${i}`} className="border-b border-slate-100 last:border-0">
              <td className="py-1.5 pr-4 text-slate-700">{p.label}</td>
              <td className="py-1.5 pr-4 text-right font-mono text-slate-900">
                {Number.isInteger(p.value) ? p.value : p.value.toFixed(3)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function HeatmapRenderer({ payload }: { payload: unknown }) {
  // A heatmap is rendered as a single-row bar chart with intensity-based fill.
  // Recharts has no first-class heatmap, but Bar + per-cell `fill` gives an
  // honest preview at zero extra dependency cost. Multi-row heatmaps fall back
  // to the JSON view because the {label, value} shape can't carry a grid.
  const points = normaliseToPoints(payload);
  if (points.length === 0) return <EmptyState hint="heatmap" />;
  const max = points.reduce((m, p) => Math.max(m, p.value), 0) || 1;
  return (
    <div className="h-32">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={points} margin={{ top: 8, right: 8, bottom: 16, left: 8 }}>
          <XAxis dataKey="label" tick={{ fontSize: 11, fill: "#64748b" }} />
          <YAxis hide />
          <Tooltip
            contentStyle={{
              backgroundColor: "white",
              border: "1px solid #e2e8f0",
              fontSize: 12,
            }}
          />
          <Bar dataKey="value">
            {points.map((p, i) => {
              const intensity = Math.min(1, Math.max(0, p.value / max));
              // Linear-interpolate slate-100 → accent-700 on the value axis.
              const r = Math.round(241 - intensity * (241 - 29));
              const g = Math.round(245 - intensity * (245 - 78));
              const b = Math.round(249 - intensity * (249 - 216));
              return <Cell key={`hm-${i}`} fill={`rgb(${r},${g},${b})`} />;
            })}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

function JsonFallback({ payload }: { payload: unknown }) {
  return (
    <pre className="text-xs bg-slate-50 border border-slate-200 rounded p-3 overflow-x-auto max-h-64">
      {JSON.stringify(payload, null, 2)}
    </pre>
  );
}

function EmptyState({ hint }: { hint: string }) {
  return (
    <div className="text-sm text-slate-500 italic py-8 text-center border border-dashed border-slate-200 rounded">
      No payload data yet for{" "}
      <code className="font-mono text-xs">{hint}</code> preview.
    </div>
  );
}
