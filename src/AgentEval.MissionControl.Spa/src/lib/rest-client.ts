// Plan-08 MC1.6.3: small REST client for binary endpoints.
//
// REST surface (per plan-07 §8.2): trace JSON, reports (markdown/html/junit/sarif),
// compliance PDFs, history NDJSON, /api/v1/version. GraphQL handles the rest.
//
// We use the native `fetch` with an auto-prefixed base URL — no 3rd-party
// dependency. Vite proxies /api/v1 to the dotnet backend in dev; same-origin
// in production.

const REST_BASE = "/api/v1";

export interface VersionInfo {
  mode: string;
  agentEvalVersion: string;
  graphqlEndpoint: string;
}

export async function fetchVersion(): Promise<VersionInfo> {
  const response = await fetch(`${REST_BASE}/version`);
  if (!response.ok) {
    throw new Error(`/api/v1/version returned ${response.status}`);
  }
  return (await response.json()) as VersionInfo;
}

/**
 * Human-readable label for the deployment mode reported by `/api/v1/version` (`mode` field —
 * `"local"` | `"aggregator"` | `"server"`, see McHost.cs). This is the ONE place that maps mode -> display
 * text — components must never hard-code a mode label, since Mode B/C would then render as "Mode A" even
 * when the server is honestly reporting otherwise (portal-review A16).
 */
export function formatModeLabel(mode: string): string {
  switch (mode.toLowerCase()) {
    case "local":
      return "Mode A — Local viewer";
    case "aggregator":
      return "Mode B — Workspace aggregator";
    case "server":
      return "Mode C — Server";
    default:
      return mode;
  }
}

/**
 * Agent trace shape served by `/api/v1/runs/{runId}/trace`. Matches the
 * canonical AgentTrace + TraceEvent records in
 * src/AgentEval.Abstractions/Output/IOutputStore.cs (camelCase via
 * BinaryEndpoints.s_traceJson).
 *
 * Note: TraceEvent is a flat sequence with a single Timestamp per event —
 * not a span tree with start/duration/parent. The waterfall page derives
 * per-event durations from the gap to the next event.
 */
export interface TraceEvent {
  timestamp: string; // ISO 8601 with offset
  kind: string;
  name?: string | null;
  payload?: string | null;
}

export interface AgentTraceDocument {
  runId: string;
  scenarioId: string;
  events: TraceEvent[];
}

/**
 * TRACE_NOT_FOUND is thrown by fetchTrace when the REST endpoint returns 404,
 * so the TraceWaterfallPage can render its empty-state message instead of
 * the generic "Failed to load" text. The REST endpoint returns 404 in three
 * cases (BinaryEndpoints.cs MC1.3.2): unknown runId, store unavailable, no
 * trace.json captured for the run — all of which collapse to the same
 * "no trace data" UX.
 */
export class TraceNotFoundError extends Error {
  readonly notFound = true;
  constructor(runId: string) {
    super(`No trace data captured for run '${runId}'.`);
  }
}

export async function fetchTrace(runId: string): Promise<AgentTraceDocument> {
  const response = await fetch(restUrls.trace(runId));
  if (response.status === 404) {
    throw new TraceNotFoundError(runId);
  }
  if (!response.ok) {
    throw new Error(`${restUrls.trace(runId)} returned ${response.status}`);
  }
  return (await response.json()) as AgentTraceDocument;
}

/**
 * URL builder for REST routes. Components that render binary content (PDF
 * viewer, image embeds) take a URL string and let the browser fetch directly.
 */
export const restUrls = {
  trace: (runId: string) => `${REST_BASE}/runs/${encodeURIComponent(runId)}/trace`,
  report: (runId: string, format: string) =>
    `${REST_BASE}/runs/${encodeURIComponent(runId)}/reports/${format}`,
  compliancePdf: (regulation: string, subjectName: string, ts: string) =>
    `${REST_BASE}/compliance/${encodeURIComponent(regulation)}/${encodeURIComponent(subjectName)}/${encodeURIComponent(ts)}/report.pdf`,
  complianceSchema: (regulation: string) =>
    `${REST_BASE}/compliance/${encodeURIComponent(regulation)}/schema`,
  history: (kind: string, name: string) =>
    `${REST_BASE}/subjects/${encodeURIComponent(kind)}/${encodeURIComponent(name)}/history`,
};
