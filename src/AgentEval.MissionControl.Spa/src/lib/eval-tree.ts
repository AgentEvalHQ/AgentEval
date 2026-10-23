// Plan-08 Wave 8 (MC1.6.9): types + helpers for the recursive EvalResult tree
// returned by Query.scenarioTree.
//
// Hot Chocolate maps `IReadOnlyDictionary<string, double>` to
// [KeyValuePairOfStringAndDouble!] — a list of `{ key, value }` records.
// Dimensions therefore arrive as that shape and are converted to a plain
// Record<string, number> for ergonomic React access.
//
// The canonical page supplies a validated captured document, independent of GraphQL nesting depth.

export type Severity = "none" | "low" | "medium" | "high" | "critical";

export interface EvalMetricNode {
  key: string;
  name: string;
  category: string;
  version: string;
}

export interface EvalScoreNode {
  measurement?: string;
  value: number;
  ordinal: number | null;
  label: string;
  passed: boolean;
  threshold: number | null;
  severity: Severity;
  confidence: number | null;
}

export interface EvalProvenanceNode {
  type: string;
  judgeModel: string | null;
  promptId: string | null;
  promptHash: string | null;
  tokensUsed: number | null;
  estimatedCost: number | null;
  cacheHit: boolean;
}

export interface DimensionEntry {
  key: string;
  value: number;
}

export interface EvalDetailsNode {
  dimensions: DimensionEntry[] | null;
  recommendations: string[] | null;
  aggregationStrategy: string | null;
  subResults: EvalResultNodeShape[] | null;
}

export interface EvalResultNodeShape {
  metric: EvalMetricNode;
  score: EvalScoreNode;
  provenance: EvalProvenanceNode;
  details: EvalDetailsNode;
  evaluatedAt: string;
}

export function dimensionsToRecord(
  dims: DimensionEntry[] | null | undefined,
): Record<string, number> {
  if (!dims) return {};
  return Object.fromEntries(dims.map((d) => [d.key, d.value]));
}

/**
 * Returns true when the node was produced by AdjudicatedMultiJudgeWrapper
 * (panel of judges + optional adjudicator). Driven by the provenance type
 * the wrapper sets in `AdjudicatedMultiJudgeWrapper.EvaluateAsync`.
 */
export function isAdjudicatedNode(node: EvalResultNodeShape): boolean {
  return node.provenance.type === "multi-judge-adjudicated";
}

/**
 * Returns true when the node was produced by a plain MultiJudgeWrapper —
 * panel of judges aggregated WITHOUT adjudication. Drives the simpler
 * panel-only display in <AdjudicationFlow/>.
 *
 * Heuristic: provenance.type=="composite" + aggregationStrategy is one of
 * the panel aggregations + at least 2 sub-results.
 */
export function isPanelNode(node: EvalResultNodeShape): boolean {
  if (isAdjudicatedNode(node)) return false;
  const agg = node.details.aggregationStrategy ?? "";
  const isPanelAgg =
    agg === "WeightedMedian" || agg === "MajorityVote" || agg === "Median";
  return (
    isPanelAgg &&
    node.provenance.type === "composite" &&
    (node.details.subResults?.length ?? 0) >= 2
  );
}

export function severityClass(s: Severity): string {
  switch (s) {
    case "critical":
      return "bg-red-100 text-red-800 border-red-200";
    case "high":
      return "bg-red-50 text-red-700 border-red-200";
    case "medium":
      return "bg-amber-50 text-amber-700 border-amber-200";
    case "low":
      return "bg-yellow-50 text-yellow-700 border-yellow-200";
    case "none":
    default:
      return "bg-slate-50 text-slate-700 border-slate-200";
  }
}

// Glass Box (Phase 7): per-turn chat-boundary drill-down (mirrors the GraphQL ChatTurn type).
// Chat-turn entries also render today via the REST /api/v1/runs/{runId}/trace waterfall; this typed
// shape backs the eval-tree-integrated timeline.
export interface ChatTurnNode {
  index: number;
  correlationId: string | null;
  finishReason: string | null;
  text: string | null;
  toolNames: string[];
  toolDefinitions: string[];
  promptTokens: number;
  completionTokens: number;
  latencyMs: number;
}


/** Legacy placeholder values are not measured scores. */
export function capturedScore(node: EvalResultNodeShape): number | null {
  const measurement = node.score.measurement?.toLowerCase();
  return (measurement && measurement !== 'measured') ||
    ['error', 'skipped', 'inapplicable', 'not_measured'].includes(node.score.label.toLowerCase()) ? null : node.score.value;
}

type StoredEvalTree = Omit<EvalResultNodeShape, 'details'> & {
  details: Omit<EvalDetailsNode, 'dimensions' | 'subResults'> & {
    dimensions?: Record<string, number> | null; subResults?: StoredEvalTree[] | null;
  };
};
/** Adapt the server-validated captured JSON to the existing rich components' dictionary shape. */
export function capturedTree(document: string): EvalResultNodeShape {
  function adapt(node: StoredEvalTree): EvalResultNodeShape {
    return { ...node,
      score: { ...node.score, ordinal: node.score.ordinal ?? null, threshold: node.score.threshold ?? null,
        confidence: node.score.confidence ?? null },
      provenance: { ...node.provenance, judgeModel: node.provenance.judgeModel ?? null, promptId: node.provenance.promptId ?? null,
        promptHash: node.provenance.promptHash ?? null, tokensUsed: node.provenance.tokensUsed ?? null,
        estimatedCost: node.provenance.estimatedCost ?? null },
      details: { ...node.details,
        dimensions: node.details.dimensions == null ? null : Object.entries(node.details.dimensions).map(([key, value]) => ({ key, value })),
        recommendations: node.details.recommendations ?? null, aggregationStrategy: node.details.aggregationStrategy ?? null,
        subResults: node.details.subResults?.map(adapt) ?? null },
    };
  }
  return adapt(JSON.parse(document) as StoredEvalTree);
}
