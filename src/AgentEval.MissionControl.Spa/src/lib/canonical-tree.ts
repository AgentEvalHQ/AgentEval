// AEF results are a flat, identity-bearing forest. Trial roots and final roots are distinct observations.
export interface CanonicalMetric {
  id: string; kind: string; direction: string; scale: string;
  minimum: number | null; maximum: number | null; unit: string | null; description: string | null;
}
export interface CanonicalNode {
  resultId: string; parentResultId: string | null; caseId: string; path: string; trial: number | null;
  evaluatorId: string; evaluatorVersion: string | null; state: string; reason: string | null; severity: string | null;
  startedAt: string | null; endedAt: string | null; durationMs: number | null;
  scores: { metricId: string; value: number; normalized: number | null; label: string | null }[];
  trials: { n: number; passed: number; aggregation: string; agree: boolean; k: number | null } | null;
  component: { weight: number; required: boolean } | null;
  aggregation: { strategy: string; rulePath: string; score: number | null; threshold: number | null;
    measured: number; total: number; minimumMeasuredShare: number | null; decisive: string[];
    unmeasured: { state: string; count: number }[] } | null;
  annotator: { kind: string; model: string | null; promptHash: string | null; rubricDigest: string | null;
    panelAgree: number | null; panelOf: number | null } | null;
  verdictRule: { expr: string; threshold: number | null; source: string | null } | null;
  usage: { role: string; model: string | null; costUsd: number | null; costSource: string | null;
    inputTokens: number | null; outputTokens: number | null; cacheReadInputTokens: number | null;
    cacheWriteInputTokens: number | null; reasoningOutputTokens: number | null }[];
  evidenceIds: string[]; recordedJson: string; capturedLegacyJson?: string | null;
}
export interface CanonicalTree {
  sourceId: string; runId: string; caseId: string; selectedResultId: string | null; contentCapture: string;
  evidence: { integrity: string; imported: boolean; signatureTrust: string };
  rootResultIds: string[]; metrics: CanonicalMetric[]; nodes: CanonicalNode[];
  artifacts: { evidenceId: string; kind: string; description: string | null; captured: boolean }[];
}

/** Iterative traversal avoids browser call-stack limits. Every recorded node must appear exactly once. */
export function treeRows(tree: CanonicalTree): { node: CanonicalNode; depth: number }[] {
  const byId = new Map(tree.nodes.map(n => [n.resultId, n]));
  const children = new Map<string, CanonicalNode[]>();
  for (const node of tree.nodes) if (node.parentResultId !== null) {
    const group = children.get(node.parentResultId) ?? [];
    group.push(node); children.set(node.parentResultId, group);
  }
  const pending = tree.rootResultIds.map(id => ({ node: byId.get(id)!, depth: 0 })).reverse();
  const rows: { node: CanonicalNode; depth: number }[] = [];
  const seen = new Set<string>();
  while (pending.length) {
    const row = pending.pop()!;
    if (!row.node || seen.has(row.node.resultId)) throw new Error('The recorded tree has inconsistent result identities.');
    seen.add(row.node.resultId); rows.push(row);
    const descendants = children.get(row.node.resultId) ?? [];
    for (let i = descendants.length - 1; i >= 0; i--) pending.push({ node: descendants[i], depth: row.depth + 1 });
  }
  if (byId.size !== tree.nodes.length || rows.length !== tree.nodes.length)
    throw new Error('Some recorded results are disconnected from their case roots.');
  return rows;
}
