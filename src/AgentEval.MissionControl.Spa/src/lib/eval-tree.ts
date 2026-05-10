// Plan-08 Wave 8 (MC1.6.9): types + helpers for the recursive EvalResult tree
// returned by Query.scenarioTree.
//
// Hot Chocolate maps `IReadOnlyDictionary<string, double>` to
// [KeyValuePairOfStringAndDouble!] — a list of `{ key, value }` records.
// Dimensions therefore arrive as that shape and are converted to a plain
// Record<string, number> for ergonomic React access.
//
// The depth-8 query cap (Program.cs MaxAllowedExecutionDepth) means we can
// fetch up to four `details { subResults { ... } }` nestings per round-trip.
// Beyond that the SPA shows "subtree truncated" so users know the JSON trace
// is the source of truth.

export type Severity = "none" | "low" | "medium" | "high" | "critical";

export interface EvalMetricNode {
  key: string;
  name: string;
  category: string;
  version: string;
}

export interface EvalScoreNode {
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
  estimatedCost: number;
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
