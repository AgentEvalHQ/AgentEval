import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { VerdictBadge, type Verdict } from "@/components/VerdictBadge";
import { TimelineChart, type TimelinePoint } from "@/components/charts/TimelineChart";
import { HistogramChart } from "@/components/charts/HistogramChart";
import { formatDateTime, formatScore } from "@/lib/format";

// Plan-08 Wave 2: per-subject detail with timeline + histogram.

interface SubjectDetailResponse {
  subject: {
    identity: {
      kind: "AGENT" | "WORKFLOW";
      name: string;
      framework: string | null;
      modelId: string | null;
      sourceProject: string | null;
    };
    lastRun: {
      runId: string;
      verdict: Verdict;
    } | null;
  } | null;
  recentRuns: {
    runId: string;
    subjectName: string;
    verdict: Verdict;
    timestamp: string;
    kind: "AGENT" | "WORKFLOW" | null;
    score: number | null;
  }[];
}

const SUBJECT_DETAIL_QUERY = /* GraphQL */ `
  query SubjectDetail($kind: SubjectKind!, $name: String!) {
    subject(kind: $kind, name: $name) {
      identity {
        kind
        name
        framework
        modelId
        sourceProject
      }
      lastRun {
        runId
        verdict
      }
    }
    recentRuns(count: 100) {
      runId
      subjectName
      verdict
      timestamp
      kind
      score
    }
  }
`;

export function SubjectDetailPage() {
  const params = useParams<{ kind: string; name: string }>();
  const kindParam = (params.kind ?? "").toUpperCase() as "AGENT" | "WORKFLOW";
  const name = params.name ? decodeURIComponent(params.name) : "";

  const detailQ = useQuery({
    queryKey: queryKeys.subjects.detail(kindParam, name),
    queryFn: () =>
      gqlRequest<SubjectDetailResponse>(SUBJECT_DETAIL_QUERY, {
        kind: kindParam,
        name,
      }),
    enabled: kindParam === "AGENT" || kindParam === "WORKFLOW",
  });

  if (kindParam !== "AGENT" && kindParam !== "WORKFLOW") {
    return (
      <BackLink message={`Unknown subject kind '${params.kind}'.`} />
    );
  }

  return (
    <DataState
      isPending={detailQ.isPending}
      isError={detailQ.isError}
      error={detailQ.error}
      data={detailQ.data}
      loadingMessage="Loading subject…"
      errorPrefix="Failed to load subject"
    >
      {(d) => {
        if (!d.subject) {
          return (
            <BackLink
              message={`No subject found for ${kindParam} / ${name}.`}
            />
          );
        }

        // Phase-7 Task 7.9: filter by BOTH kind AND name. An agent and a
        // workflow can legitimately share a name; the prior name-only filter
        // mixed their runs on the detail page. Run pointers with kind=null
        // (legacy / not yet backfilled) are accepted as a soft match — they
        // would otherwise disappear from history entirely.
        const subjectRuns = d.recentRuns
          .filter((r) => r.subjectName === name)
          .filter((r) => r.kind == null || r.kind === kindParam)
          .filter((r) => r.score !== null);

        const timelinePoints: TimelinePoint[] = subjectRuns.map((r) => ({
          timestamp: r.timestamp,
          score: r.score as number,
          runId: r.runId,
          verdict: r.verdict,
        }));

        const scoreList = subjectRuns.map((r) => r.score as number);

        return (
          <div className="space-y-6">
            <BackLink />

            <header>
              <span className="text-[10px] uppercase tracking-wider text-slate-400">
                {d.subject.identity.kind}
              </span>
              <h2 className="text-2xl font-bold text-slate-900 mt-1">
                {d.subject.identity.name}
              </h2>
              {(d.subject.identity.framework || d.subject.identity.modelId) && (
                <p className="text-sm text-slate-600 mt-1">
                  {d.subject.identity.framework}{" "}
                  {d.subject.identity.modelId && (
                    <span className="text-slate-400">·</span>
                  )}{" "}
                  <code className="text-xs">{d.subject.identity.modelId}</code>
                </p>
              )}
            </header>

            <section className="rounded-lg border border-slate-200 bg-white p-4">
              <h3 className="text-sm font-semibold text-slate-700 mb-3">
                Score over time ({subjectRuns.length} runs)
              </h3>
              <TimelineChart data={timelinePoints} threshold={0.7} />
            </section>

            <section className="grid grid-cols-1 lg:grid-cols-2 gap-4">
              <div className="rounded-lg border border-slate-200 bg-white p-4">
                <h3 className="text-sm font-semibold text-slate-700 mb-3">
                  Score distribution
                </h3>
                <HistogramChart scores={scoreList} />
              </div>

              <div className="rounded-lg border border-slate-200 bg-white p-4">
                <h3 className="text-sm font-semibold text-slate-700 mb-3">
                  Recent runs
                </h3>
                <ul className="divide-y divide-slate-100">
                  {subjectRuns.slice(-10).reverse().map((r) => (
                    <li key={r.runId} className="py-2 flex items-center justify-between gap-4">
                      <div className="min-w-0">
                        <div className="text-sm font-mono text-slate-900 truncate">
                          {r.runId}
                        </div>
                        <div className="text-xs text-slate-500">
                          {formatDateTime(r.timestamp)}
                        </div>
                      </div>
                      <div className="flex items-center gap-3 shrink-0">
                        <span className="text-sm font-mono text-slate-900">
                          {formatScore(r.score)}
                        </span>
                        <VerdictBadge verdict={r.verdict} />
                      </div>
                    </li>
                  ))}
                  {subjectRuns.length === 0 && (
                    <li className="py-3 text-sm text-slate-500 italic">
                      No runs with scored results yet.
                    </li>
                  )}
                </ul>
              </div>
            </section>
          </div>
        );
      }}
    </DataState>
  );
}

function BackLink({ message }: { message?: string }) {
  return (
    <div className="space-y-2">
      <Link
        to="/"
        className="inline-flex items-center gap-1 text-sm text-accent-700 hover:text-accent-500"
      >
        <ArrowLeft size={14} /> Back to dashboard
      </Link>
      {message && <p className="text-sm text-red-600">{message}</p>}
    </div>
  );
}
