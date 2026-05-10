import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ExternalLink, FileCode } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { TimelineChart, type TimelinePoint } from "@/components/charts/TimelineChart";
import { CostTierPill } from "./EvaluatorsPage";

// Plan-08 Wave 5 (MC1.6.10): per-evaluator card detail.
// Plan-08 Wave 8b (MC1.6.9): adds the per-evaluator timeline chart fed by
// Query.evaluatorTimeline. For judge-quality evaluators (judge_drift,
// judge_agreement, calibration_accuracy, confidence_calibration) this is the
// drift / calibration surface called out in plan-07 §6.1.

interface EvaluatorTimelineResponse {
  evaluatorTimeline: {
    runId: string;
    timestamp: string;
    subjectKind: "AGENT" | "WORKFLOW";
    subjectName: string;
    score: number;
    passed: boolean;
    severity: string;
    confidence: number | null;
    manifestHash: string;
  }[];
}

const EVALUATOR_TIMELINE_QUERY = /* GraphQL */ `
  query EvaluatorTimeline($key: String!, $count: Int!) {
    evaluatorTimeline(evaluatorKey: $key, count: $count) {
      runId
      timestamp
      subjectKind
      subjectName
      score
      passed
      severity
      confidence
      manifestHash
    }
  }
`;

interface EvaluatorDetailResponse {
  evaluator: {
    key: string;
    name: string;
    category: string;
    version: string;
    description: string;
    costTier: "TRIVIAL" | "LOW" | "MEDIUM" | "HIGH";
    higherIsBetter: boolean;
    defaultThreshold: number | null;
    recommendedVisualization: string | null;
    expectedInputs: {
      kind: string;
      key: string;
      required: boolean;
      description: string;
    }[];
    compatibleWith: { system: string; uri: string }[];
    links: { documentation: string | null; source: string | null } | null;
  } | null;
}

const EVALUATOR_DETAIL_QUERY = /* GraphQL */ `
  query EvaluatorDetail($key: String!) {
    evaluator(key: $key) {
      key
      name
      category
      version
      description
      costTier
      higherIsBetter
      defaultThreshold
      recommendedVisualization
      expectedInputs {
        kind
        key
        required
        description
      }
      compatibleWith { system uri }
      links { documentation source }
    }
  }
`;

export function EvaluatorDetailPage() {
  const { key: keyParam } = useParams<{ key: string }>();
  const key = keyParam ?? "";

  const detailQ = useQuery({
    queryKey: queryKeys.evaluators.detail(key),
    queryFn: () =>
      gqlRequest<EvaluatorDetailResponse>(EVALUATOR_DETAIL_QUERY, { key }),
    enabled: key.length > 0,
  });

  const timelineCount = 30;
  const timelineQ = useQuery({
    queryKey: queryKeys.evaluators.timeline(key, timelineCount),
    queryFn: () =>
      gqlRequest<EvaluatorTimelineResponse>(EVALUATOR_TIMELINE_QUERY, {
        key,
        count: timelineCount,
      }),
    enabled: key.length > 0,
  });

  return (
    <DataState
      isPending={detailQ.isPending}
      isError={detailQ.isError}
      error={detailQ.error}
      data={detailQ.data}
      loadingMessage="Loading evaluator card…"
      errorPrefix="Failed to load evaluator"
    >
      {(d) => {
        if (!d.evaluator) {
          return (
            <div>
              <BackLink />
              <p className="mt-2 text-sm text-red-600">
                No evaluator card registered for key{" "}
                <code className="bg-red-50 px-1 rounded">{key}</code>.
              </p>
            </div>
          );
        }
        const ev = d.evaluator;
        return (
          <div className="space-y-6">
            <BackLink />

            <header>
              <span className="text-[10px] uppercase tracking-wider text-slate-400">
                {ev.category} · v{ev.version}
              </span>
              <h2 className="text-2xl font-bold text-slate-900 mt-1">
                {ev.name}
              </h2>
              <p className="text-sm text-slate-600 mt-1">
                <code className="font-mono text-xs bg-slate-100 px-1 rounded">{ev.key}</code>
              </p>
              <div className="flex items-center gap-2 mt-3">
                <CostTierPill tier={ev.costTier} />
                {ev.higherIsBetter ? (
                  <span className="text-xs text-slate-600">↑ higher is better</span>
                ) : (
                  <span className="text-xs text-slate-600">↓ lower is better</span>
                )}
                {ev.defaultThreshold !== null && (
                  <span className="text-xs text-slate-600">
                    threshold ≥ {ev.defaultThreshold.toFixed(2)}
                  </span>
                )}
                {ev.recommendedVisualization && (
                  <span className="text-xs text-slate-600">
                    chart hint: <code>{ev.recommendedVisualization}</code>
                  </span>
                )}
              </div>
            </header>

            <section className="rounded-lg border border-slate-200 bg-white p-4">
              <h3 className="text-sm font-semibold text-slate-700 mb-2">
                What it measures
              </h3>
              <p className="text-sm text-slate-700 leading-relaxed">
                {ev.description}
              </p>
            </section>

            <TimelineSection
              evaluatorKey={ev.key}
              threshold={ev.defaultThreshold ?? undefined}
              points={timelineQ.data?.evaluatorTimeline ?? null}
              isPending={timelineQ.isPending}
              isError={timelineQ.isError}
            />

            <section className="rounded-lg border border-slate-200 bg-white p-4">
              <h3 className="text-sm font-semibold text-slate-700 mb-3">
                Expected inputs ({ev.expectedInputs.length})
              </h3>
              {ev.expectedInputs.length === 0 ? (
                <p className="text-sm text-slate-500 italic">
                  No declared inputs — eval reads only from EvalInput's primary fields.
                </p>
              ) : (
                <ul className="space-y-2">
                  {ev.expectedInputs.map((inp, idx) => (
                    <li key={idx} className="flex items-start gap-3 text-sm">
                      <span className={`shrink-0 text-xs font-mono px-2 py-0.5 rounded ${
                        inp.required
                          ? "bg-accent-50 text-accent-700"
                          : "bg-slate-100 text-slate-600"
                      }`}>
                        {inp.kind}{inp.key && `[${inp.key}]`}
                      </span>
                      <span className="text-slate-700">
                        {inp.description}
                        {!inp.required && (
                          <span className="text-slate-400"> (optional)</span>
                        )}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </section>

            {ev.compatibleWith.length > 0 && (
              <section className="rounded-lg border border-slate-200 bg-white p-4">
                <h3 className="text-sm font-semibold text-slate-700 mb-2">
                  External compatibility
                </h3>
                <ul className="space-y-1">
                  {ev.compatibleWith.map((c, idx) => (
                    <li key={idx} className="text-sm">
                      <span className="text-slate-500 mr-2">{c.system}:</span>
                      <code className="font-mono text-xs">{c.uri}</code>
                    </li>
                  ))}
                </ul>
              </section>
            )}

            {ev.links && (ev.links.documentation || ev.links.source) && (
              <section className="rounded-lg border border-slate-200 bg-white p-4">
                <h3 className="text-sm font-semibold text-slate-700 mb-2">Links</h3>
                <ul className="space-y-1.5 text-sm">
                  {ev.links.documentation && (
                    <li>
                      <a
                        href={ev.links.documentation}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center gap-1 text-accent-700 hover:text-accent-500"
                      >
                        Documentation <ExternalLink size={12} />
                      </a>
                    </li>
                  )}
                  {ev.links.source && (
                    <li className="inline-flex items-center gap-1 text-slate-700">
                      <FileCode size={14} />
                      <code className="font-mono text-xs">{ev.links.source}</code>
                    </li>
                  )}
                </ul>
              </section>
            )}
          </div>
        );
      }}
    </DataState>
  );
}

function BackLink() {
  return (
    <Link
      to="/evaluators"
      className="inline-flex items-center gap-1 text-sm text-accent-700 hover:text-accent-500"
    >
      <ArrowLeft size={14} /> Back to evaluator registry
    </Link>
  );
}

function TimelineSection({
  evaluatorKey,
  threshold,
  points,
  isPending,
  isError,
}: {
  evaluatorKey: string;
  threshold?: number;
  points: EvaluatorTimelineResponse["evaluatorTimeline"] | null;
  isPending: boolean;
  isError: boolean;
}) {
  const chartPoints: TimelinePoint[] = (points ?? []).map((p) => ({
    timestamp: p.timestamp,
    score: p.score,
    runId: p.runId,
    verdict: p.passed
      ? "PASS"
      : p.severity === "medium"
        ? "WARN"
        : "FAIL",
  }));

  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4">
      <header className="flex items-baseline justify-between mb-2">
        <h3 className="text-sm font-semibold text-slate-700">
          Score over recent runs
        </h3>
        <span className="text-xs text-slate-500">
          {chartPoints.length > 0 && `${chartPoints.length} runs`}
        </span>
      </header>
      {isPending ? (
        <div className="text-sm text-slate-500 italic py-12 text-center">
          Loading timeline…
        </div>
      ) : isError ? (
        <div className="text-sm text-red-600 italic py-12 text-center">
          Failed to load timeline.
        </div>
      ) : chartPoints.length === 0 ? (
        <div className="text-sm text-slate-500 italic py-12 text-center">
          <code className="font-mono text-xs">{evaluatorKey}</code> hasn't been
          recorded in any recent runs yet.
        </div>
      ) : (
        <TimelineChart data={chartPoints} threshold={threshold} />
      )}
    </section>
  );
}
