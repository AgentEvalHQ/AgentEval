import { useQuery } from "@tanstack/react-query";
import { gqlRequest } from "@/lib/graphql-client";
import { fetchVersion } from "@/lib/rest-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { SubjectCard, type SubjectCardData } from "@/components/SubjectCard";
import type { Verdict } from "@/components/VerdictBadge";

// Plan-08 Wave 2: dashboard with SubjectCard tiles + recent runs trend.

interface SubjectsQueryResponse {
  subjects: {
    identity: {
      kind: "AGENT" | "WORKFLOW";
      name: string;
    };
    lastRun: {
      runId: string;
      verdict: Verdict;
    } | null;
  }[];
}

interface RecentRunsResponse {
  recentRuns: {
    runId: string;
    subjectName: string;
    verdict: Verdict;
    timestamp: string;
    kind: "AGENT" | "WORKFLOW" | null;
    score: number | null;
  }[];
}

const SUBJECTS_QUERY = /* GraphQL */ `
  query SubjectsList {
    subjects {
      identity {
        kind
        name
      }
      lastRun {
        runId
        verdict
      }
    }
  }
`;

const RECENT_RUNS_QUERY = /* GraphQL */ `
  query RecentRuns {
    recentRuns(count: 50) {
      runId
      subjectName
      verdict
      timestamp
      kind
      score
    }
  }
`;

export function DashboardPage() {
  const versionQ = useQuery({
    queryKey: queryKeys.version(),
    queryFn: fetchVersion,
  });

  const subjectsQ = useQuery({
    queryKey: queryKeys.subjects.list(),
    queryFn: () => gqlRequest<SubjectsQueryResponse>(SUBJECTS_QUERY),
  });

  const recentRunsQ = useQuery({
    queryKey: queryKeys.runs.recent(50),
    queryFn: () => gqlRequest<RecentRunsResponse>(RECENT_RUNS_QUERY),
  });

  // Build the trend (sparkline data) per subject by filtering recentRuns.
  // Phase-7 Task 7.9: key by `${kind}::${name}` so an agent and a workflow
  // with the same name don't share a sparkline.
  const trendBySubject = buildTrendIndex(recentRunsQ.data?.recentRuns ?? []);
  const cards: SubjectCardData[] = (subjectsQ.data?.subjects ?? []).map((s) => ({
    kind: s.identity.kind,
    name: s.identity.name,
    lastVerdict: s.lastRun?.verdict ?? null,
    lastRunId: s.lastRun?.runId ?? null,
    trend: trendBySubject.get(`${s.identity.kind}::${s.identity.name}`) ?? [],
  }));

  return (
    <div className="space-y-6">
      <header>
        <h2 className="text-2xl font-bold text-slate-900">Dashboard</h2>
        <p className="text-sm text-slate-600">
          AgentEval Mission Control — Mode A (local viewer).
        </p>
      </header>

      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <h3 className="text-sm font-semibold text-slate-700 mb-2">Server</h3>
        <DataState
          isPending={versionQ.isPending}
          isError={versionQ.isError}
          error={versionQ.error}
          data={versionQ.data}
          loadingMessage="Loading version…"
          errorPrefix="Failed to reach /api/v1/version"
        >
          {(v) => (
            <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm">
              <dt className="text-slate-500">Mode</dt>
              <dd className="font-mono text-slate-900">{v.mode}</dd>
              <dt className="text-slate-500">AgentEval version</dt>
              <dd className="font-mono text-slate-900">{v.agentEvalVersion}</dd>
              <dt className="text-slate-500">GraphQL endpoint</dt>
              <dd className="font-mono text-slate-900">{v.graphqlEndpoint}</dd>
            </dl>
          )}
        </DataState>
      </section>

      <section>
        <header className="flex items-baseline justify-between mb-3">
          <h3 className="text-sm font-semibold text-slate-700">
            Subjects {subjectsQ.data && (
              <span className="text-slate-400 font-normal">
                ({subjectsQ.data.subjects.length})
              </span>
            )}
          </h3>
          <span className="text-xs text-slate-400">click a card for details</span>
        </header>
        <DataState
          isPending={subjectsQ.isPending}
          isError={subjectsQ.isError}
          error={subjectsQ.error}
          data={subjectsQ.data}
          isEmpty={(d) => d.subjects.length === 0}
          loadingMessage="Querying GraphQL…"
          errorPrefix="GraphQL request failed"
          emptyMessage={
            <div>
              No subjects registered. Run{" "}
              <code className="bg-slate-100 px-1 rounded">agenteval init</code>{" "}
              in the solution root + ship at least one evaluation, then refresh.
            </div>
          }
        >
          {() => (
            <div className="grid gap-3 grid-cols-1 sm:grid-cols-2 lg:grid-cols-3">
              {cards.map((c) => (
                <SubjectCard key={`${c.kind}-${c.name}`} subject={c} />
              ))}
            </div>
          )}
        </DataState>
      </section>
    </div>
  );
}

// Build a per-subject trend (oldest → newest scores) from a flat list of
// RunPointers. Filters out scoreless points (null score). Caller can render
// these via the Sparkline component without further reshaping.
function buildTrendIndex(
  runs: RecentRunsResponse["recentRuns"],
): Map<string, { value: number }[]> {
  // Phase-7 Task 7.9: key the trend map by `${kind}::${name}` so an agent and
  // a workflow sharing a name don't have their sparklines merged. Runs with
  // a null kind (legacy / not-yet-backfilled run pointers) bucket under an
  // explicit `UNKNOWN::{name}` so they still appear if any subject lookup
  // happens to match — but they will not show up on the agent OR workflow
  // detail page, which is the right answer for ambiguous legacy data.
  const byKey = new Map<string, { ts: string; value: number }[]>();
  for (const r of runs) {
    if (r.score === null) continue;
    const key = `${r.kind ?? "UNKNOWN"}::${r.subjectName}`;
    const list = byKey.get(key) ?? [];
    list.push({ ts: r.timestamp, value: r.score });
    byKey.set(key, list);
  }
  // Sort each list by timestamp ascending; drop the ts after sort.
  return new Map(
    Array.from(byKey.entries()).map(([key, points]) => [
      key,
      points
        .sort((a, b) => a.ts.localeCompare(b.ts))
        .map((p) => ({ value: p.value })),
    ]),
  );
}
