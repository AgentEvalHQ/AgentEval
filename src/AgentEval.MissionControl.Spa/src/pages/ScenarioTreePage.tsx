import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ExternalLink } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { restUrls } from "@/lib/rest-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { EvalResultNode } from "@/components/EvalResultNode";
import type { EvalResultNodeShape } from "@/lib/eval-tree";
import { formatDateTime, formatScore, formatCost } from "@/lib/format";

// Plan-08 Wave 8 (MC1.6.9): drill-down view of a single scenario's recursive
// EvalResult tree, fetched via Query.scenarioTree (MC1.4.6).
//
// Hot Chocolate's MaxAllowedExecutionDepth = 8 caps the query at four
// `details { subResults { ... } }` nestings — sufficient for typical
// composite trees (composite root → pillar → article → judges, ~4 levels).

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
                  Tip: nodes with severity ≥ medium and the root are expanded
                  by default; click any header to collapse / expand.
                  Multi-judge and adjudicated nodes render an inline panel
                  showing each judge's verdict.
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
