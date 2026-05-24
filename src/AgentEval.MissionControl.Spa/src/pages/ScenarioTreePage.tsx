import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ExternalLink } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { restUrls } from "@/lib/rest-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { EvalResultNode } from "@/components/EvalResultNode";
import { ModelBadge } from "@/components/ModelBadge";
import {
  PromptHashPill,
  JudgeModePill,
  deriveJudgeMode,
} from "@/components/ProvenancePills";
import type { EvalResultNodeShape } from "@/lib/eval-tree";
import { formatDateTime, formatScore, formatCost } from "@/lib/format";

// Plan-08 Wave 8 (MC1.6.9): drill-down view of a single scenario's recursive
// EvalResult tree, fetched via Query.scenarioTree (MC1.4.6).
//
// Hot Chocolate's MaxAllowedExecutionDepth = 10 (Program.cs) accommodates
// three nested `details { subResults { ... } }` levels — depth 9 — which
// covers the typical composite tree shape (root → pillar → article →
// judges, where the L3 article composite uses an `AdjudicatedMultiJudgeWrapper`
// whose panel judges are part of L3's own `subResults` and are rendered
// inline by `<AdjudicationFlow/>` without requiring a deeper query).

interface ScenarioTreeResponse {
  scenarioTree: EvalResultNodeShape | null;
}

// Eight effective depth (root + 4 details/subResults pairs). The query is
// spelled out manually because GraphQL doesn't support recursive fragments.
const NODE_FIELDS = `
  metric { key name category version }
  score {
    value ordinal label passed threshold severity confidence
  }
  provenance {
    type judgeModel promptId promptHash tokensUsed estimatedCost cacheHit
  }
  evaluatedAt
`;

const SCENARIO_TREE_QUERY = /* GraphQL */ `
  query ScenarioTree($runId: String!, $scenarioId: String!) {
    scenarioTree(runId: $runId, scenarioId: $scenarioId) {
      ${NODE_FIELDS}
      details {
        dimensions { key value }
        recommendations
        aggregationStrategy
        subResults {
          ${NODE_FIELDS}
          details {
            dimensions { key value }
            recommendations
            aggregationStrategy
            subResults {
              ${NODE_FIELDS}
              details {
                dimensions { key value }
                recommendations
                aggregationStrategy
                subResults {
                  ${NODE_FIELDS}
                  details {
                    dimensions { key value }
                    recommendations
                    aggregationStrategy
                  }
                }
              }
            }
          }
        }
      }
    }
  }
`;
// Three nested `subResults` produce execution depth 9 (within the depth=10
// cap). For unusually deep trees (>4 levels), the trace JSON link remains
// authoritative.

export function ScenarioTreePage() {
  const { runId: runIdParam, scenarioId: scenarioIdParam } = useParams<{
    runId: string;
    scenarioId: string;
  }>();
  const runId = runIdParam ? decodeURIComponent(runIdParam) : "";
  const scenarioId = scenarioIdParam ? decodeURIComponent(scenarioIdParam) : "";

  const treeQ = useQuery({
    queryKey: queryKeys.runs.scenarioTree(runId, scenarioId),
    queryFn: () =>
      gqlRequest<ScenarioTreeResponse>(SCENARIO_TREE_QUERY, {
        runId,
        scenarioId,
      }),
    enabled: runId.length > 0 && scenarioId.length > 0,
  });

  return (
    <DataState
      isPending={treeQ.isPending}
      isError={treeQ.isError}
      error={treeQ.error}
      data={treeQ.data}
      loadingMessage="Loading scenario tree…"
      errorPrefix="Failed to load scenario tree"
    >
      {(d) => {
        const tree = d.scenarioTree;
        return (
          <div className="space-y-5">
            <BackLink runId={runId} />
            {!tree ? (
              <div className="rounded-lg border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
                No recursive <code>EvalResult</code> tree was persisted for
                scenario{" "}
                <code className="font-mono">{scenarioId}</code>. This usually
                means the scenario is a legacy flat result whose{" "}
                <code>Output</code> field is plain text rather than a serialised
                tree.
              </div>
            ) : (
              <>
                <header>
                  <span className="text-[10px] uppercase tracking-wider text-slate-400">
                    Scenario
                  </span>
                  <h2 className="text-2xl font-bold text-slate-900 mt-1">
                    {tree.metric.name}
                  </h2>
                  <p className="text-sm text-slate-600 mt-1">
                    Evaluated {formatDateTime(tree.evaluatedAt)} · root score{" "}
                    <span className="font-mono">{formatScore(tree.score.value)}</span>
                    {tree.provenance.estimatedCost > 0 && (
                      <>
                        {" "}· cost{" "}
                        <span className="font-mono">
                          {formatCost(tree.provenance.estimatedCost)}
                        </span>
                      </>
                    )}
                  </p>
                  {/* Plan-08 portal-review A4 (T2.2, 2026-05-25): root-level
                      provenance — model id + promptHash pill (click-to-copy)
                      + judge-mode pill. The promptHash pill exposes the SHA
                      the run was pinned at so reviewers can correlate with the
                      canonical prompts table; the judge-mode pill surfaces
                      whether the root composite was scored single / panel /
                      majority-vote. Both are sourced from the existing
                      `provenance` shape — no schema change. */}
                  <div className="mt-3 flex items-center gap-2 flex-wrap">
                    {tree.provenance.judgeModel && (
                      <ModelBadge model={tree.provenance.judgeModel} role="judge" />
                    )}
                    <PromptHashPill promptHash={tree.provenance.promptHash} />
                    <JudgeModePill mode={deriveJudgeMode(tree.provenance, tree.details)} />
                  </div>
                  <div className="mt-2 flex items-center gap-3 flex-wrap">
                    <a
                      href={restUrls.trace(runId)}
                      target="_blank"
                      rel="noreferrer"
                      className="inline-flex items-center gap-1 text-xs text-accent-700 hover:text-accent-500"
                    >
                      Trace JSON <ExternalLink size={12} />
                    </a>
                  </div>
                </header>

                <section>
                  <EvalResultNode node={tree} depth={0} defaultExpanded />
                </section>

                <p className="text-xs text-slate-500">
                  Tip: the root and any non-passing node are expanded by
                  default; click any header to collapse / expand. Multi-judge
                  and adjudicated nodes render an inline panel showing each
                  judge's verdict.
                </p>
              </>
            )}
          </div>
        );
      }}
    </DataState>
  );
}

function BackLink({ runId }: { runId: string }) {
  return (
    <Link
      to={`/runs/${encodeURIComponent(runId)}`}
      className="inline-flex items-center gap-1 text-sm text-accent-700 hover:text-accent-500"
    >
      <ArrowLeft size={14} /> Back to run
    </Link>
  );
}
