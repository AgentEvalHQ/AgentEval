import { Link } from "react-router";
import { TimelineChart, type TimelinePoint } from "@/components/charts/TimelineChart";
import { formatDateTime } from "@/lib/format";

export interface EvaluatorOccurrence {
  sourceId: string; runId: string; resultId: string; caseId: string; path: string; trial: number | null;
  evaluatorVersion: string | null; subjectRef: string; subjectKind: string; subjectName: string;
  timestamp: string | null; score: number | null; scoreMetricId: string | null; threshold: number | null;
  state: string; passed: boolean | null; severity: string | null; confidence: number | null; manifestHash: string | null;
  scores: { metricId: string; value: number; normalized: number | null }[];
  metrics: { id: string; kind: string; direction: string; scale: string; minimum: number | null; maximum: number | null; unit: string | null }[];
  evidence: { integrity: string; imported: boolean; signatureTrust: string };
}

// Keep the existing normalized chart only when every displayed observation declares the same context.
export function comparableTimeline(points: EvaluatorOccurrence[]): TimelinePoint[] {
  if (!points.length || points.some(p => p.timestamp == null || p.score == null || p.trial != null || !p.evaluatorVersion)) return [];
  const contexts = points.map(p => {
    const metric = p.metrics.find(m => m.id === p.scoreMetricId);
    if (!metric || metric.kind !== "score" || metric.direction !== "higher_better" || metric.unit != null ||
      metric.scale !== "bounded" || metric.minimum !== 0 || metric.maximum !== 1) return null;
    return JSON.stringify([p.subjectRef, p.evaluatorVersion, p.caseId, p.path, metric, p.threshold]);
  });
  if (contexts.some(c => c == null) || new Set(contexts).size !== 1) return [];
  return points.map(p => ({ timestamp: p.timestamp!, score: p.score!, runId: p.runId,
    verdict: p.state === "passed" ? "PASS" : p.state === "failed" ? "FAIL" : p.state === "warn" ? "WARN" : null }));
}

export function EvaluatorTimeline({ evaluatorKey, points, isPending, isError }: {
  evaluatorKey: string; points: EvaluatorOccurrence[] | null; isPending: boolean; isError: boolean;
}) {
  const rows = points ?? [];
  const chart = comparableTimeline(rows);
  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4 space-y-3">
      <h3 className="text-sm font-semibold text-slate-700">Recorded evaluator occurrences</h3>
      {isPending ? <p>Loading timeline…</p> : isError ? <p className="text-red-600">Failed to load timeline.</p>
        : rows.length === 0 ? <p className="text-slate-500">No recorded occurrences for <code>{evaluatorKey}</code>.</p>
        : <>
          <p className="text-xs text-slate-500">{rows.length} occurrences in the requested recent window. Missing values remain unrecorded.</p>
          {chart.length > 0 ? <TimelineChart data={chart} threshold={rows[0].threshold ?? undefined} />
            : <p className="text-xs text-slate-500">Chart unavailable for mixed contexts, trials or unrecorded measurements. Recorded values appear below.</p>}
          <div className="overflow-x-auto">
            <table className="w-full text-xs text-left">
              <thead><tr><th>Run / time</th><th>Subject</th><th>Result / trial</th><th>State</th><th>Recorded scores</th><th>Evidence</th></tr></thead>
              <tbody>{rows.map(p => <tr key={`${p.sourceId}:${p.runId}:${p.resultId}`} className="border-t border-slate-100 align-top">
                <td className="py-2 pr-3"><Link className="text-accent-700" to={`/runs/${encodeURIComponent(p.runId)}?sourceId=${encodeURIComponent(p.sourceId)}`}>{p.runId}</Link>
                  <div>{p.timestamp ? formatDateTime(p.timestamp) : "time not recorded"}</div></td>
                <td className="pr-3">{p.subjectKind} · {p.subjectName}<div>v{p.evaluatorVersion ?? "not recorded"}</div></td>
                <td className="pr-3"><code>{p.resultId}</code><div>{p.caseId} · {p.path}</div><div>{p.trial == null ? "final result" : `trial ${p.trial}`}</div></td>
                <td className="pr-3">{p.state}</td>
                <td className="pr-3">{p.scores.length === 0 ? "No numeric score recorded" : p.scores.map(score => {
                  const metric = p.metrics.find(m => m.id === score.metricId);
                  return <div key={score.metricId}><code>{score.metricId}</code>: {score.value} {metric?.unit}
                    <div className="text-slate-500">{metric?.kind} · {metric?.direction} · {metric?.scale === "bounded" ? `${metric.minimum}..${metric.maximum}` : metric?.scale}</div>
                    {score.normalized != null && <div>recorded normalized value: {score.normalized}</div>}</div>;
                })}</td>
                <td>{p.evidence.integrity}{p.evidence.imported && <div>imported; original execution custody not established</div>}
                  <div>signature trust: {p.evidence.signatureTrust}</div></td>
              </tr>)}</tbody>
            </table>
          </div>
        </>}
    </section>
  );
}
