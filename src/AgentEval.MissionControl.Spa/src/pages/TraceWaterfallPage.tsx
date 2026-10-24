// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

import { Link, useParams, useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ExternalLink } from "lucide-react";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

import { DataState } from "@/components/DataState";
import { queryKeys } from "@/lib/keys";
import {
  fetchTrace,
  sourceQuery,
  restUrls,
  type AgentTraceDocument,
  type TraceEvent,
  TraceNotFoundError,
} from "@/lib/rest-client";

// The recorded sequence contains event timestamps, not operation spans. Bars
// show nonnegative gaps to the next event; a minimum pixel width makes zero
// gaps and final events visible without inventing a duration.

interface WaterfallRow {
  /** Display index, padded out to keep rows on distinct y-bands. */
  index: number;
  /** "01 — agent.start" etc. — what the y-axis tick prints. */
  label: string;
  /** Offset from the earliest recorded event in ms — Bar `dataKey` for the offset stack. */
  startMs: number;
  /** Recorded timestamp difference to the next event, possibly negative; absent on the final event. */
  gapMs: number | null;
  /** Nonnegative gap used for drawing only. */
  visualGapMs: number;
  /** Color band derived from event kind (see kindColor below). */
  kind: string;
  name?: string | null;
  payload?: string | null;
  /** ISO timestamp for the tooltip. */
  timestamp: string;
}

// Color palette: stable per kind so repeated "agent.message" rows look the
// same hue. ERROR kinds get red; success/OK get green; everything else gets
// a slate-blue band by hashing the kind string. This avoids hard-coding a
// taxonomy that the trace recorder is still evolving — see TraceEvent's
// `Kind` field being free-form `string` in IOutputStore.cs:102.
function kindColor(kind: string): string {
  const lower = kind.toLowerCase();
  if (lower.includes("error") || lower.includes("fail") || lower.includes("exception")) {
    return "#dc2626"; // red-600
  }
  if (lower.includes("ok") || lower.includes("success") || lower.includes("complete")) {
    return "#16a34a"; // green-600
  }
  // Stable hue for everything else. The palette is the 6 Tailwind 500-series
  // colors that work alongside the slate / accent body palette.
  const palette = [
    "#3b82f6", // blue-500
    "#8b5cf6", // violet-500
    "#0ea5e9", // sky-500
    "#14b8a6", // teal-500
    "#f59e0b", // amber-500
    "#ec4899", // pink-500
  ];
  let hash = 0;
  for (let i = 0; i < kind.length; i++) {
    hash = ((hash << 5) - hash + kind.charCodeAt(i)) | 0;
  }
  return palette[Math.abs(hash) % palette.length];
}

function eventLabel(idx: number, event: TraceEvent): string {
  // Two-digit index + kind + optional name so duplicate kinds stay
  // distinguishable on the y-axis. Truncate names that would overrun.
  const idxStr = String(idx + 1).padStart(2, "0");
  const namePart = event.name ? ` · ${truncate(event.name, 28)}` : "";
  return `${idxStr} — ${event.kind}${namePart}`;
}

function truncate(s: string, max: number): string {
  if (s.length <= max) return s;
  return s.slice(0, max - 1) + "…";
}

function projectEvents(trace: AgentTraceDocument): WaterfallRow[] {
  const events = trace.events;
  if (events.length === 0) return [];
  const times = events.map(event => new Date(event.timestamp).getTime());
  if (times.some(time => !Number.isFinite(time))) return [];
  const first = times.reduce((earliest, time) => Math.min(earliest, time), times[0]);
  return events.map((event, index) => {
    const gapMs = index + 1 < times.length ? times[index + 1] - times[index] : null;
    return {
      index, label: eventLabel(index, event), startMs: times[index] - first,
      gapMs, visualGapMs: Math.max(0, gapMs ?? 0), kind: event.kind,
      name: event.name, payload: event.payload, timestamp: event.timestamp,
    };
  });
}

function formatMs(ms: number): string {
  if (ms < 1) return `${ms.toFixed(2)} ms`;
  if (ms < 1000) return `${ms.toFixed(0)} ms`;
  return `${(ms / 1000).toFixed(2)} s`;
}

export function TraceWaterfallPage() {
  const { runId: runIdParam } = useParams<{ runId: string }>();
  const runId = runIdParam ? decodeURIComponent(runIdParam) : "";

  const [search] = useSearchParams();
  const sourceId = search.get("sourceId");

  const traceQ = useQuery({
    queryKey: queryKeys.runs.trace(runId, sourceId),
    queryFn: () => fetchTrace(runId, sourceId),
    enabled: runId.length > 0,
    // Absent content, unsupported formats and missing sources are stable
    // availability responses; retrying does not create the required evidence.
    retry: (failureCount, error) => {
      if (error instanceof TraceNotFoundError) return false;
      return failureCount < 2;
    },
  });

  return (
    <div className="space-y-5">
      <Link
        to={runId ? `/runs/${encodeURIComponent(runId)}${sourceQuery(sourceId)}` : "/runs"}
        className="inline-flex items-center gap-1 text-sm text-accent-700 hover:text-accent-500"
      >
        <ArrowLeft size={14} /> Back to run
      </Link>

      <header className="flex items-start justify-between gap-4">
        <div>
          <span className="text-[10px] uppercase tracking-wider text-slate-400">
            Run trace
          </span>
          <h2 className="text-2xl font-bold text-slate-900 mt-1">
            Trace Waterfall
          </h2>
          {runId && (
            <p className="text-sm text-slate-600 mt-1">
              Run{" "}
              <code className="font-mono text-xs bg-slate-100 px-1 rounded">
                {runId}
              </code>
            </p>
          )}
        </div>
        {traceQ.data && (
          <a
            href={restUrls.trace(runId, sourceId)}
            target="_blank"
            rel="noreferrer"
            className="inline-flex items-center gap-1 text-xs text-accent-700 hover:text-accent-500"
          >
            Trace JSON <ExternalLink size={12} />
          </a>
        )}
      </header>

      {traceQ.isError && traceQ.error instanceof TraceNotFoundError ? (
        <TraceUnavailableBanner message={traceQ.error.message} />
      ) : (
        <DataState
          isPending={traceQ.isPending}
          isError={traceQ.isError}
          error={traceQ.error}
          data={traceQ.data}
          loadingMessage="Loading trace…"
          errorPrefix="Failed to load trace"
          isEmpty={(d) => (d.events?.length ?? 0) === 0}
          emptyMessage="The captured trace contains no events."
        >
          {(trace) => <Waterfall trace={trace} />}
        </DataState>
      )}
    </div>
  );
}

function Waterfall({ trace }: { trace: AgentTraceDocument }) {
  const rows = projectEvents(trace);
  const totalMs = rows.reduce((latest, row) => Math.max(latest, row.startMs), 0);

  // Recharts horizontal-bar height: ~28 px per row plus chart-frame chrome.
  // Cap the page height at 800 px so very long traces scroll inside the
  // container instead of pushing the rest of the layout off-screen.
  const chartHeight = Math.min(800, Math.max(220, rows.length * 28 + 48));

  return (
    <div className="space-y-4">
      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <header className="flex items-baseline justify-between mb-3">
          <h3 className="text-sm font-semibold text-slate-700">
            Events{" "}
            <span className="text-slate-400 font-normal">({rows.length})</span>
          </h3>
          <span className="text-xs text-slate-500 font-mono">
            Observed timestamp span: {formatMs(totalMs)}
          </span>
        </header>

        <ResponsiveContainer width="100%" height={chartHeight}>
          <BarChart
            layout="vertical"
            data={rows}
            margin={{ top: 8, right: 24, bottom: 20, left: 8 }}
            barCategoryGap={4}
          >
            <CartesianGrid stroke="#e2e8f0" strokeDasharray="3 3" horizontal={false} />
            <XAxis
              type="number"
              domain={[0, totalMs || 1]}
              tickFormatter={(t) => formatMs(t as number)}
              stroke="#94a3b8"
              fontSize={11}
              label={{
                value: "ms from earliest recorded event",
                position: "insideBottom",
                offset: -8,
                fontSize: 11,
                fill: "#64748b",
              }}
            />
            <YAxis
              type="category"
              dataKey="label"
              width={260}
              stroke="#94a3b8"
              fontSize={11}
              interval={0}
            />
            <Tooltip
              cursor={{ fill: "#f1f5f9" }}
              content={<WaterfallTooltip />}
            />
            {/* Stack trick: an invisible bar provides the start-offset spacing,
                then the real bar paints from there with the event's color.
                Recharts has no native "floating bar" primitive; this is the
                canonical workaround documented in the recharts BarChart
                examples. */}
            <Bar
              dataKey="startMs"
              stackId="waterfall"
              fill="transparent"
              isAnimationActive={false}
            />
            <Bar
              dataKey="visualGapMs"
              minPointSize={2}
              stackId="waterfall"
              isAnimationActive={false}
              radius={[2, 2, 2, 2]}
            >
              {rows.map((row) => (
                <Cell key={row.index} fill={kindColor(row.kind)} />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      </section>

      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <h3 className="text-sm font-semibold text-slate-700 mb-2">Legend</h3>
        <p className="text-xs text-slate-500 mb-2">
          Bars show gaps between recorded event timestamps, in recorded event order.
          Operation durations were not recorded. The chart uses browser millisecond precision.
          Final and simultaneous events use a
          minimum visual width only; it does not contribute to the observed timestamp span.
          Negative gaps indicate out-of-order timestamps and remain visible in the tooltip.
          Hover a bar for the timestamp and captured payload.
        </p>
      </section>
    </div>
  );
}

interface RechartsTooltipPayload {
  payload: WaterfallRow;
}

interface RechartsTooltipProps {
  active?: boolean;
  payload?: RechartsTooltipPayload[];
}

function WaterfallTooltip({ active, payload }: RechartsTooltipProps) {
  if (!active || !payload || payload.length === 0) return null;
  // recharts passes one payload entry per stacked bar; both share the same
  // row, so reading the first is fine.
  const row = payload[0].payload;
  return (
    <div className="rounded-md border border-slate-200 bg-white p-3 text-xs shadow-lg max-w-md">
      <div className="font-semibold text-slate-900 mb-1 font-mono">{row.label}</div>
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-slate-700">
        <dt className="text-slate-500">kind</dt>
        <dd className="font-mono">{row.kind}</dd>
        {row.name && (
          <>
            <dt className="text-slate-500">name</dt>
            <dd className="font-mono break-all">{row.name}</dd>
          </>
        )}
        <dt className="text-slate-500">start</dt>
        <dd className="font-mono">{formatMs(row.startMs)}</dd>
        <dt className="text-slate-500">gap to next event</dt>
        <dd className="font-mono">{row.gapMs === null ? "No following event" : formatMs(row.gapMs)}</dd>
        <dt className="text-slate-500">timestamp</dt>
        <dd className="font-mono">{row.timestamp}</dd>
      </dl>
      {row.payload && (
        <div className="mt-2 pt-2 border-t border-slate-100">
          <div className="text-slate-500 mb-1">payload</div>
          <pre className="font-mono text-[11px] text-slate-800 whitespace-pre-wrap break-all max-h-40 overflow-y-auto">
            {truncate(row.payload, 800)}
          </pre>
        </div>
      )}
    </div>
  );
}

function TraceUnavailableBanner({ message }: { message: string }) {
  return (
    <section className="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
      <p>{message}</p>
    </section>
  );
}
