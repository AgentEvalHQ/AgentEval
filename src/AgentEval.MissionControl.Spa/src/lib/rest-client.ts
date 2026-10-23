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

/** A missing run, absent captured content, or an unsupported trace viewer. */
export class TraceNotFoundError extends Error {
  readonly notFound = true;
  constructor(code?: string) {
    super(code === "trace_viewer_unsupported"
      ? "This captured transcript uses a format this viewer does not support. Download it from the run's artifacts."
      : code === "trace_not_captured" ? "No captured trace is available for this run."
      : code === "run_not_found" ? "The selected run source was not found."
      : "The trace viewer is unavailable for this selected source.");
  }
}

export async function fetchTrace(runId: string, sourceId?: string | null): Promise<AgentTraceDocument> {
  const response = await fetch(restUrls.trace(runId, sourceId));
  if (response.status === 404) {
    const problem = await response.json().catch(() => null) as { code?: string } | null;
    throw new TraceNotFoundError(problem?.code);
  }
  if (!response.ok) {
    throw new Error(`${restUrls.trace(runId, sourceId)} returned ${response.status}`);
  }
  return (await response.json()) as AgentTraceDocument;
}

/**
 * URL builder for REST routes. Components that render binary content (PDF
 * viewer, image embeds) take a URL string and let the browser fetch directly.
 */
export function sourceQuery(sourceId?: string | null): string {
  return sourceId == null ? "" : `?sourceId=${encodeURIComponent(sourceId)}`;
}

export const restUrls = {
  trace: (runId: string, sourceId?: string | null) => `${REST_BASE}/runs/${encodeURIComponent(runId)}/trace${sourceQuery(sourceId)}`,
  report: (runId: string, format: string, sourceId?: string | null) =>
    `${REST_BASE}/runs/${encodeURIComponent(runId)}/reports/${encodeURIComponent(format)}${sourceQuery(sourceId)}`,
  artifact: (runId: string, evidenceId: string, sourceId?: string | null) =>
    `${REST_BASE}/runs/${encodeURIComponent(runId)}/artifacts/${encodeURIComponent(evidenceId)}${sourceQuery(sourceId)}`,
  compliancePdf: (regulation: string, subjectName: string, ts: string) =>
    `${REST_BASE}/compliance/${encodeURIComponent(regulation)}/${encodeURIComponent(subjectName)}/${encodeURIComponent(ts)}/report.pdf`,
  complianceSchema: (regulation: string) =>
    `${REST_BASE}/compliance/${encodeURIComponent(regulation)}/schema`,
  history: (kind: string, name: string) =>
    `${REST_BASE}/subjects/${encodeURIComponent(kind)}/${encodeURIComponent(name)}/history`,
};
