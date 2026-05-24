// Plan-08 MC1.6.3 (Opus review F14): typed query-key factory.
//
// All TanStack Query cache keys flow through this factory so partial
// invalidation works predictably (e.g. invalidating `keys.runs.all()` after
// a write also invalidates every `keys.runs.detail(id)`). The hand-written
// keys in DashboardPage.tsx already follow this shape; future pages MUST
// import from here rather than constructing arrays inline.
export const queryKeys = {
  version: () => ["version"] as const,

  workspace: {
    state: () => ["workspace", "state"] as const,
  },

  subjects: {
    all: () => ["subjects"] as const,
    list: (kind?: "AGENT" | "WORKFLOW") => ["subjects", "list", { kind }] as const,
    detail: (kind: "AGENT" | "WORKFLOW", name: string) =>
      ["subjects", "detail", kind, name] as const,
  },

  runs: {
    all: () => ["runs"] as const,
    recent: (count: number) => ["runs", "recent", { count }] as const,
    detail: (runId: string) => ["runs", "detail", runId] as const,
    summary: (runId: string) => ["runs", "summary", runId] as const,
    scenarios: (runId: string) => ["runs", "scenarios", runId] as const,
    scenario: (runId: string, scenarioId: string) =>
      ["runs", "scenario", runId, scenarioId] as const,
    scenarioTree: (runId: string, scenarioId: string) =>
      ["runs", "tree", runId, scenarioId] as const,
    // Plan-08 T2.3 (2026-05-25): trace JSON for the waterfall page.
    trace: (runId: string) => ["runs", "trace", runId] as const,
  },

  compliance: {
    all: () => ["compliance"] as const,
    summary: () => ["compliance", "summary"] as const,
    matrix: (regulation: string) => ["compliance", "matrix", regulation] as const,
    evidence: (regulation: string, kind: string, name: string, ts: string) =>
      ["compliance", "evidence", regulation, kind, name, ts] as const,
  },

  evaluators: {
    all: () => ["evaluators"] as const,
    list: (filters?: { category?: string; costTier?: string }) =>
      ["evaluators", "list", filters ?? {}] as const,
    detail: (key: string) => ["evaluators", "detail", key] as const,
    timeline: (key: string, count: number) =>
      ["evaluators", "timeline", key, { count }] as const,
  },
};
