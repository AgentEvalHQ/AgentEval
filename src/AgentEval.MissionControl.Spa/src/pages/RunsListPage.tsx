import { Link } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { gqlRequest } from "@/lib/graphql-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { VerdictBadge, type Verdict } from "@/components/VerdictBadge";
import { formatDateTime, formatScore, formatCost } from "@/lib/format";

// Plan-08 Wave 3: cross-subject run-list table.
// Used as the /runs route + as a fallback when the dashboard doesn't have
// enough recent activity to render meaningfully.

interface RecentRunsResponse {
  recentRuns: {
    runId: string;
    subjectName: string;
    verdict: Verdict;
    timestamp: string;
    kind: "AGENT" | "WORKFLOW" | null;
    score: number | null;
    durationMs: number | null;
    estimatedCost: number | null;
  }[];
}

const RECENT_RUNS_QUERY = /* GraphQL */ `
  query AllRecentRuns($count: Int!) {
    recentRuns(count: $count) {
      runId
      subjectName
      verdict
      timestamp
      kind
      score
      durationMs
      estimatedCost
    }
  }
`;

export function RunsListPage() {
  const runsQ = useQuery({
    queryKey: queryKeys.runs.recent(100),
    queryFn: () =>
      gqlRequest<RecentRunsResponse>(RECENT_RUNS_QUERY, { count: 100 }),
  });

  return (
    <div className="space-y-4">
      <header>
        <h2 className="text-2xl font-bold text-slate-900">Recent runs</h2>
        <p className="text-sm text-slate-600">
          Last 100 runs across all subjects.
        </p>
      </header>

      <DataState
        isPending={runsQ.isPending}
        isError={runsQ.isError}
        error={runsQ.error}
        data={runsQ.data}
        isEmpty={(d) => d.recentRuns.length === 0}
        loadingMessage="Loading recent runs…"
        errorPrefix="Failed to load runs"
        emptyMessage="No runs yet. Once the agent ships a benchmark, it'll show up here."
      >
        {(d) => (
          <div className="rounded-lg border border-slate-200 bg-white overflow-hidden">
            <table className="w-full text-sm">
              <thead className="bg-slate-50 text-slate-600 text-xs uppercase">
                <tr>
                  <th className="px-3 py-2 text-left">When</th>
                  <th className="px-3 py-2 text-left">Subject</th>
                  <th className="px-3 py-2 text-left">Verdict</th>
                  <th className="px-3 py-2 text-right">Score</th>
                  <th className="px-3 py-2 text-right">Duration</th>
                  <th className="px-3 py-2 text-right">Cost</th>
                  <th className="px-3 py-2 text-left">Run ID</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {d.recentRuns.map((r) => (
                  <RunRow key={r.runId} run={r} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </DataState>
    </div>
  );
}

interface RunRowProps {
  run: RecentRunsResponse["recentRuns"][number];
}

function RunRow({ run }: RunRowProps) {
  return (
    <tr className="hover:bg-slate-50">
      <td className="px-3 py-2 whitespace-nowrap text-slate-700">
        {formatDateTime(run.timestamp)}
      </td>
      <td className="px-3 py-2">
        <span className="text-[10px] uppercase text-slate-400 mr-1">
          {run.kind ?? "—"}
        </span>
        <span className="font-medium text-slate-900">{run.subjectName}</span>
      </td>
      <td className="px-3 py-2">
        <VerdictBadge verdict={run.verdict} />
      </td>
      <td className="px-3 py-2 text-right font-mono text-slate-900">
        {formatScore(run.score)}
      </td>
      <td className="px-3 py-2 text-right font-mono text-slate-700">
        {run.durationMs !== null ? `${run.durationMs} ms` : "—"}
      </td>
      <td className="px-3 py-2 text-right font-mono text-slate-700">
        {formatCost(run.estimatedCost)}
      </td>
      <td className="px-3 py-2">
        <Link
          to={`/runs/${encodeURIComponent(run.runId)}`}
          className="font-mono text-xs text-accent-700 hover:text-accent-500"
        >
          {run.runId.slice(0, 24)}
          {run.runId.length > 24 ? "…" : ""}
        </Link>
      </td>
    </tr>
  );
}
