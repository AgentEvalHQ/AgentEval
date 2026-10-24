import { useQuery } from "@tanstack/react-query";
import { Link, useParams, useSearchParams } from "react-router";
import { ArrowLeft } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { restUrls } from "@/lib/rest-client";
import { queryKeys } from "@/lib/keys";
import { EvalResultNode } from "@/components/EvalResultNode";
import { capturedTree } from "@/lib/eval-tree";
import { DataState } from "@/components/DataState";
import { treeRows, type CanonicalTree, type CanonicalNode, type CanonicalMetric } from "@/lib/canonical-tree";

const SCENARIO_TREE_QUERY = /* GraphQL */ `
  query ScenarioResultTree($runId: String!, $scenarioId: String!, $sourceId: String, $resultId: String) {
    scenarioResultTree(runId: $runId, scenarioId: $scenarioId, sourceId: $sourceId, resultId: $resultId) {
      sourceId runId caseId selectedResultId contentCapture rootResultIds
      evidence { integrity imported signatureTrust }
      metrics { id kind direction scale minimum maximum unit description }
      artifacts { evidenceId kind description captured }
      nodes {
        resultId parentResultId caseId path trial evaluatorId evaluatorVersion state reason severity
        startedAt endedAt durationMs evidenceIds recordedJson capturedLegacyJson
        scores { metricId value normalized label }
        trials { n passed aggregation agree k }
        component { weight required }
        aggregation { strategy rulePath score threshold measured total minimumMeasuredShare decisive unmeasured { state count } }
        annotator { kind model promptHash rubricDigest panelAgree panelOf }
        verdictRule { expr threshold source }
        usage { role model costUsd costSource inputTokens outputTokens cacheReadInputTokens cacheWriteInputTokens reasoningOutputTokens }
      }
    }
  }
`;

export function ScenarioTreePage() {
  // Router params are already decoded; decoding twice changes literal percent sequences in case IDs.
  const { runId = "", scenarioId = "" } = useParams<{ runId: string; scenarioId: string }>();
  const [search] = useSearchParams();
  const sourceId = search.get("sourceId");
  const resultId = search.get("resultId");
  const treeQ = useQuery({
    queryKey: queryKeys.runs.scenarioTree(runId, scenarioId, sourceId, resultId),
    queryFn: () => gqlRequest<{ scenarioResultTree: CanonicalTree | null }>(SCENARIO_TREE_QUERY,
      { runId, scenarioId, sourceId, resultId }),
    enabled: runId.length > 0 && scenarioId.length > 0,
  });
  return <DataState isPending={treeQ.isPending} isError={treeQ.isError} error={treeQ.error} data={treeQ.data}
    loadingMessage="Loading scenario tree…" errorPrefix="Failed to load scenario tree">
    {data => <div className="space-y-5">
      <Link to={`/runs/${encodeURIComponent(runId)}${sourceId ? `?${new URLSearchParams({ sourceId })}` : ""}`}
        className="inline-flex items-center gap-1 text-sm text-accent-700 hover:text-accent-500"><ArrowLeft size={14} /> Back to run</Link>
      {data.scenarioResultTree ? <CaseTree tree={data.scenarioResultTree} /> :
        <p className="rounded border border-amber-200 bg-amber-50 p-4 text-sm">No recorded case matches this run source and result selection.</p>}
    </div>}
  </DataState>;
}

function CaseTree({ tree }: { tree: CanonicalTree }) {
  let rows: ReturnType<typeof treeRows>;
  try { rows = treeRows(tree); }
  catch (error) { return <p className="text-red-700">{error instanceof Error ? error.message : "The recorded tree cannot be displayed."}</p>; }
  const metrics = new Map(tree.metrics.map(metric => [metric.id, metric]));
  return <>
    <header className="rounded-xl border border-slate-200 bg-white p-5">
      <p className="text-xs uppercase tracking-wide text-slate-500">Scenario</p>
      <h2 className="mt-1 break-words text-2xl font-bold text-slate-900">{tree.caseId}</h2>
      <p className="mt-2 text-sm text-slate-600">{tree.nodes.length} recorded result nodes · {tree.rootResultIds.length} roots · Content capture: {tree.contentCapture}</p>
      <p className="mt-1 text-xs text-slate-500">Evidence integrity: {tree.evidence.integrity}
        {tree.evidence.imported && " · imported; original execution custody not established"} · Signature trust: {tree.evidence.signatureTrust}</p>
    </header>
    <p className="text-sm text-slate-600">Trials and final results are shown separately. Scores keep their recorded scale and unit. Expand a result for its evidence and policy.</p>
    <section aria-label="Recorded result tree" className="space-y-3">
      {rows.map(({ node, depth }) => <ResultCard key={node.resultId} node={node} depth={depth} metrics={metrics} selected={tree.selectedResultId === node.resultId} />)}
    </section>
    {tree.nodes.filter(node => node.capturedLegacyJson).map(node => <section key={node.resultId} className="space-y-3">
      <h3 className="font-semibold">Captured legacy details</h3>
      <p className="text-xs text-slate-500">These details come from the captured attachment for {node.path}. Older imports may retain descendants only in this attachment.</p>
      <EvalResultNode node={capturedTree(node.capturedLegacyJson!)} defaultExpanded />
    </section>)}
    <section className="rounded-lg border border-slate-200 bg-white p-4">
      <h3 className="font-semibold">Captured case artifacts</h3>
      {tree.artifacts.length === 0 ? <p className="mt-2 text-sm text-slate-500">No captured artifacts are linked to this case.</p> :
        <ul className="mt-2 space-y-2 text-sm">{tree.artifacts.map(artifact => <li key={artifact.evidenceId}>
          {artifact.captured ? <a className="text-accent-700 underline" href={restUrls.artifact(tree.runId, artifact.evidenceId, tree.sourceId)}>{artifact.description ?? artifact.evidenceId}</a>
            : <span>{artifact.description ?? artifact.evidenceId} — content not captured</span>}
        </li>)}</ul>}
    </section>
  </>;
}

function ResultCard({ node, depth, metrics, selected }: { node: CanonicalNode; depth: number; metrics: Map<string, CanonicalMetric>; selected: boolean }) {
  const tone = node.state === "passed" ? "bg-emerald-100 text-emerald-900" : node.state === "failed" || node.state === "error"
    ? "bg-red-100 text-red-900" : node.state === "warn" ? "bg-amber-100 text-amber-900" : "bg-slate-100 text-slate-800";
  return <details open={selected || depth === 0 || node.state !== "passed"}
    className={`rounded-lg border bg-white p-4 ${selected ? "border-accent-500 ring-1 ring-accent-200" : "border-slate-200"}`}
    style={{ marginLeft: `${Math.min(depth, 6) * 16}px` }}>
    <summary className="cursor-pointer">
      <span className="font-semibold text-slate-900">{node.evaluatorId}</span>
      <span className={`ml-3 rounded px-2 py-0.5 text-xs ${tone}`}>{node.state.replaceAll("_", " ")}</span>
      <span className="ml-3 text-xs text-slate-500">{node.trial === null ? "Final result" : `Trial ${node.trial}`} · depth {depth}</span>
      <p className="mt-1 break-all text-xs text-slate-500">{node.path} · version {node.evaluatorVersion ?? "not recorded"}</p>
      <div className="mt-2 flex flex-wrap gap-3 text-sm">{node.scores.length === 0 ? <span className="text-slate-500">No numeric score recorded</span>
        : node.scores.map(score => <span key={score.metricId} className="rounded bg-slate-50 px-2 py-1 font-mono">{score.metricId}: {score.value}{metrics.get(score.metricId)?.unit ? ` ${metrics.get(score.metricId)!.unit}` : ""}</span>)}</div>
    </summary>
    <div className="mt-4 space-y-3 border-t border-slate-100 pt-3 text-sm">
      {node.reason && <p className="whitespace-pre-wrap text-slate-700">{node.reason}</p>}
      <dl className="grid gap-2 sm:grid-cols-2">
        <div><dt className="text-slate-500">Duration</dt><dd>{node.durationMs === null ? "not recorded" : `${node.durationMs} ms`}</dd></div>
        <div><dt className="text-slate-500">Severity</dt><dd>{node.severity ?? "not recorded"}</dd></div>
        <div><dt className="text-slate-500">Started</dt><dd className="break-all">{node.startedAt ?? "not recorded"}</dd></div>
        <div><dt className="text-slate-500">Ended</dt><dd className="break-all">{node.endedAt ?? "not recorded"}</dd></div>
      </dl>
      {node.scores.map(score => {
        const metric = metrics.get(score.metricId);
        return <p key={score.metricId} className="text-xs text-slate-500">{score.metricId}: {metric?.kind ?? "kind not recorded"} · direction {metric?.direction ?? "not recorded"} · scale {metric?.scale === "bounded" ? `${metric.minimum}–${metric.maximum}` : metric?.scale ?? "not recorded"}
          {score.normalized !== null && ` · recorded normalized value ${score.normalized}`}{score.label !== null && ` · ${score.label}`}</p>;
      })}
      {node.verdictRule && <p>Recorded rule: {node.verdictRule.expr}{node.verdictRule.threshold !== null && ` · threshold ${node.verdictRule.threshold}`}</p>}
      {node.component && <p>Contribution: weight {node.component.weight} · {node.component.required ? "required" : "not required"}</p>}
      {node.aggregation && <p>Aggregation: {node.aggregation.strategy} · {node.aggregation.rulePath} · {node.aggregation.measured}/{node.aggregation.total} children measured</p>}
      {node.trials && <p>Recorded trial summary: {node.trials.passed}/{node.trials.n} passed · {node.trials.aggregation} · agreement {node.trials.agree ? "yes" : "no"}</p>}
      {node.annotator && <p>Grader: {node.annotator.kind}{node.annotator.model && ` · ${node.annotator.model}`}</p>}
      {node.usage.length === 0 ? <p className="text-slate-500">Usage and cost not recorded</p> : <div>
        <p className="text-xs text-slate-500">Node usage may include child costs; these entries are not added into a run total.</p>
        {node.usage.map((usage, i) => <p key={i}>{usage.role}{usage.model && ` · ${usage.model}`} · cost {usage.costUsd === null ? "not recorded" : `$${usage.costUsd}`} · input tokens {usage.inputTokens ?? "not recorded"} · output tokens {usage.outputTokens ?? "not recorded"}</p>)}
      </div>}
      <details className="text-xs text-slate-500"><summary className="cursor-pointer">Recorded result fields</summary>
        <pre className="mt-2 max-h-96 overflow-auto whitespace-pre-wrap break-all rounded bg-slate-50 p-3">{node.recordedJson}</pre>
      </details>
    </div>
  </details>;
}
