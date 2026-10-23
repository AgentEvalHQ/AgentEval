import { Link, useParams, useSearchParams } from "react-router";
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
      kind: string;
      rawKind: string;
      ref: string;
      name: string;
      framework: string | null;
      modelId: string | null;
      sourceProject: string | null;
    };
    lastRun: {
      runId: string;
      verdict: Verdict | null;
    } | null;
  } | null;
  recentRuns: {
    runId: string;
    subjectName: string;
    subjectRef: string;
    sourceId: string;
    verdict: Verdict | null;
    timestamp: string | null;
    kind: string | null;
    score: number | null;
  }[];
}

const SUBJECT_DETAIL_QUERY = /* GraphQL */ `
  query SubjectDetail($kind: AefSubjectKind!, $name: String!, $subjectRef: String) {
    subject(kind: $kind, name: $name, subjectRef: $subjectRef) {
      identity {
        kind
        rawKind
        ref
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
      subjectRef
      sourceId
      verdict
      timestamp
      kind
      score
    }
  }
`;

export function SubjectDetailPage() {
  const params = useParams<{ kind: string; name: string }>();
  const kindParam = (params.kind ?? "").toUpperCase() as string;
  const name = params.name ?? "";
  const [search] = useSearchParams();
  const subjectRef = search.get("subjectRef");

  const detailQ = useQuery({
    queryKey: queryKeys.subjects.detail(kindParam, name, subjectRef),
    queryFn: () =>
      gqlRequest<SubjectDetailResponse>(SUBJECT_DETAIL_QUERY, {
        kind: kindParam,
        name,
        subjectRef,
      }),
    enabled: ["AGENT", "WORKFLOW", "MODEL", "ENDPOINT", "MCP_SERVER", "OTHER"].includes(kindParam),
  });

  if (!["AGENT", "WORKFLOW", "MODEL", "ENDPOINT", "MCP_SERVER", "OTHER"].includes(kindParam)) {
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

        // Exact references distinguish subjects whose display names happen to match.
        const subjectRuns = d.recentRuns.filter((r) => r.subjectRef === d.subject!.identity.ref);
        const timelinePoints: TimelinePoint[] = subjectRuns
          .filter((r) => r.timestamp !== null && r.score !== null)
          .map((r) => ({ timestamp: r.timestamp!, score: r.score!, runId: r.runId, verdict: r.verdict }));
        const scoreList = subjectRuns.flatMap((r) => r.score === null ? [] : [r.score]);

        return (
          <div className="space-y-6">
            <BackLink />

            <header>
              <span className="text-[10px] uppercase tracking-wider text-slate-400">
                {d.subject.identity.rawKind}
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
                Score over time ({timelinePoints.length} dated runs)
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
                  {subjectRuns.slice(0, 10).map((r) => (
                    <li key={r.sourceId} className="py-2 flex items-center justify-between gap-4">
                      <div className="min-w-0">
                        <div className="text-sm font-mono text-slate-900 truncate">
                          <Link to={`/runs/${encodeURIComponent(r.runId)}?sourceId=${encodeURIComponent(r.sourceId)}`}>{r.runId}</Link>
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
                      No recorded runs yet.
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
