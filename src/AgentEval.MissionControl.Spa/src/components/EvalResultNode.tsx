import { useState, type ReactNode } from "react";
import { ChevronDown, ChevronRight, Sigma } from "lucide-react";
import {
  type EvalResultNodeShape,
  dimensionsToRecord,
  isAdjudicatedNode,
  isPanelNode,
  severityClass,
} from "@/lib/eval-tree";
import { VerdictBadge, type Verdict } from "@/components/VerdictBadge";
import { ModelBadge } from "@/components/ModelBadge";
import { AdjudicationFlow } from "@/components/AdjudicationFlow";
import { formatScore, formatCost } from "@/lib/format";

// Plan-08 Wave 8 (MC1.6.9): recursive collapsible card for the EvalResult
// tree returned by Query.scenarioTree. Each node renders:
//   - score / verdict / cost / agent or judge model badge
//   - dimensions table (key/value pairs from Hot Chocolate's KeyValuePair list)
//   - recommendations
//   - <AdjudicationFlow/> when the node is multi-judge or adjudicated
//   - recursive sub-nodes (collapsible) for the rest of the tree
//
// Atomic-leaf nodes (no sub-results, no dimensions, no recommendations, no
// extended meta) skip the collapse mechanism entirely so the header isn't
// a misleading no-op control.

interface Props {
  node: EvalResultNodeShape;
  depth?: number;
  defaultExpanded?: boolean;
}

function labelToVerdict(label: string): Verdict {
  const lower = (label ?? "").toLowerCase();
  if (lower === "pass" || lower === "passed") return "PASS";
  if (lower === "warn" || lower === "warning") return "WARN";
  if (lower === "fail" || lower === "failed") return "FAIL";
  return "PENDING";
}

export function EvalResultNode({
  node,
  depth = 0,
  defaultExpanded,
}: Props) {
  const isRoot = depth === 0;
  // Roots and any failed node default expanded; others collapsed to keep
  // the page readable on big composite trees.
  const initial =
    defaultExpanded ?? (isRoot || !node.score.passed);
  const [open, setOpen] = useState<boolean>(initial);

  const hasSubResults = (node.details.subResults?.length ?? 0) > 0;
  const adjudicated = isAdjudicatedNode(node);
  const isPanel = isPanelNode(node);
  // Adjudication / panel display takes the place of generic sub-result rendering.
  const showAdjudicationBlock = adjudicated || isPanel;

  const dims = dimensionsToRecord(node.details.dimensions);
  const dimEntries = Object.entries(dims);
  const hasRecommendations =
    (node.details.recommendations?.length ?? 0) > 0;
  const hasExtendedMeta =
    node.score.threshold !== null ||
    node.score.confidence !== null ||
    !!node.details.aggregationStrategy ||
    !!node.provenance.judgeModel ||
    node.provenance.estimatedCost > 0;
  // Headers act as a disclosure widget only when there is a body to disclose.
  // Pure leaves (atomic-code evals with no dimensions, no recommendations, no
  // sub-results, and no extended meta) render a static row instead.
  const collapsible =
    hasSubResults || dimEntries.length > 0 || hasRecommendations || hasExtendedMeta;

  return (
    <div
      className={`rounded-lg border ${severityClass(node.score.severity)} ${
        depth > 0 ? "ml-3" : ""
      }`}
    >
      <Header
        collapsible={collapsible}
        open={open}
        onToggle={() => setOpen((o) => !o)}
      >
        <div className="flex items-center gap-2 min-w-0 flex-1">
          {collapsible ? (
            open ? (
              <ChevronDown size={14} className="shrink-0 text-slate-600" />
            ) : (
              <ChevronRight size={14} className="shrink-0 text-slate-600" />
            )
          ) : (
            <span className="w-3.5 shrink-0" />
          )}
          <span className="text-xs uppercase tracking-wider text-slate-500 shrink-0">
            {node.metric.category}
          </span>
          <span className="font-medium text-slate-900 truncate" title={node.metric.key}>
            {node.metric.name}
          </span>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <span className="font-mono text-sm text-slate-700">
            {formatScore(node.score.value)}
          </span>
          <VerdictBadge verdict={labelToVerdict(node.score.label)} size="sm" />
        </div>
      </Header>

      {collapsible && open && (
        <div className="px-3 pb-3 pt-0 space-y-3 bg-white/40 rounded-b-lg">
          <div className="flex items-center gap-2 text-xs text-slate-600 flex-wrap">
            {node.score.threshold !== null && (
              <span>
                threshold ≥{" "}
                <span className="font-mono">{node.score.threshold.toFixed(2)}</span>
              </span>
            )}
            {node.score.confidence !== null && (
              <span>
                confidence{" "}
                <span className="font-mono">
                  {(node.score.confidence * 100).toFixed(0)}%
                </span>
              </span>
            )}
            <span>
              <code className="text-slate-500">{node.provenance.type}</code>
            </span>
            {node.provenance.judgeModel && (
              <ModelBadge model={node.provenance.judgeModel} role="judge" />
            )}
            {node.provenance.estimatedCost > 0 && (
              <span className="text-slate-500">
                {formatCost(node.provenance.estimatedCost)}
              </span>
            )}
            {node.details.aggregationStrategy && (
              <span className="inline-flex items-center gap-1 text-slate-600">
                <Sigma size={11} />
                {node.details.aggregationStrategy}
              </span>
            )}
          </div>

          {dimEntries.length > 0 && (
            <DimensionsBlock dims={dims} />
          )}

          {node.details.recommendations &&
            node.details.recommendations.length > 0 && (
              <ul className="text-xs text-slate-700 list-disc pl-5 space-y-0.5">
                {node.details.recommendations.map((r, i) => (
                  <li key={i}>{r}</li>
                ))}
              </ul>
            )}

          {showAdjudicationBlock && hasSubResults && (
            <AdjudicationFlow node={node} />
          )}

          {!showAdjudicationBlock && hasSubResults && (
            <div className="space-y-1.5 mt-2">
              {(node.details.subResults ?? []).map((child, i) => (
                <EvalResultNode
                  key={`${child.metric.key}-${i}`}
                  node={child}
                  depth={depth + 1}
                />
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function Header({
  collapsible,
  open,
  onToggle,
  children,
}: {
  collapsible: boolean;
  open: boolean;
  onToggle: () => void;
  children: ReactNode;
}) {
  // When collapsible we render a `<button>` so the disclosure widget is
  // focusable + keyboard-activatable (Enter / Space) and announces "expanded
  // / collapsed" to assistive tech via aria-expanded. Static leaves get a
  // plain div so they don't appear in the keyboard tab order as no-ops.
  if (collapsible) {
    return (
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={open}
        className="w-full flex items-center justify-between gap-3 px-3 py-2 hover:bg-white/30 text-left rounded-t-lg focus:outline-none focus-visible:ring-2 focus-visible:ring-accent-500"
      >
        {children}
      </button>
    );
  }
  return (
    <div className="flex items-center justify-between gap-3 px-3 py-2">
      {children}
    </div>
  );
}

function DimensionsBlock({ dims }: { dims: Record<string, number> }) {
  const entries = Object.entries(dims);
  return (
    <div className="grid grid-cols-2 sm:grid-cols-3 gap-x-4 gap-y-1 text-xs bg-white/60 rounded border border-slate-200 p-2">
      {entries.map(([k, v]) => (
        <div key={k} className="flex items-baseline justify-between gap-2">
          <span className="text-slate-500 truncate" title={k}>
            {k}
          </span>
          <span className="font-mono text-slate-800 shrink-0">
            {Number.isInteger(v) ? v : v.toFixed(3)}
          </span>
        </div>
      ))}
    </div>
  );
}
