import { Gavel, Users, AlertTriangle, CheckCircle2 } from "lucide-react";
import {
  type EvalResultNodeShape,
  dimensionsToRecord,
  isAdjudicatedNode,
} from "@/lib/eval-tree";
import { VerdictBadge, type Verdict } from "@/components/VerdictBadge";
import { ModelBadge } from "@/components/ModelBadge";
import { formatScore, formatCost } from "@/lib/format";

// Plan-08 Wave 8 (MC1.6.9): visualises a panel of judges + (optionally) an
// adjudicator. Driven by AdjudicatedMultiJudgeWrapper / MultiJudgeWrapper
// output. Covers two shapes:
//
//   1. Adjudicated, undisputed → panel agreed, aggregation produced verdict.
//      Show panel grid only; agreement value highlighted as green.
//   2. Adjudicated, disputed → panel disagreed, adjudicator overrode.
//      Show panel grid + adjudicator card with "OVERRIDES" arrow.
//
// Plan-07 §6.1 specced Visx NodeLink for adjudication; CSS Grid/Flexbox
// keeps the bundle smaller and renders cleanly for the typical 2-5 judge
// panel size. Visx upgrade documented as polish item if larger panels emerge.

interface Props {
  node: EvalResultNodeShape;
}

function labelToVerdict(label: string): Verdict {
  const lower = (label ?? "").toLowerCase();
  if (lower === "pass" || lower === "passed") return "PASS";
  if (lower === "warn" || lower === "warning") return "WARN";
  if (lower === "fail" || lower === "failed") return "FAIL";
  return "PENDING";
}

export function AdjudicationFlow({ node }: Props) {
  const adjudicated = isAdjudicatedNode(node);
  const dims = dimensionsToRecord(node.details.dimensions);
  const subs = node.details.subResults ?? [];
  if (subs.length === 0) return null;

  const agreement = dims["agreement"];
  const disputed = dims["disputed"] === 1;

  const panelJudges = adjudicated && disputed ? subs.slice(0, -1) : subs;
  const adjudicator = adjudicated && disputed ? subs[subs.length - 1] : null;

  return (
    <div className="rounded-lg border border-slate-200 bg-slate-50/40 p-4 space-y-3">
      <header className="flex items-center justify-between gap-3 flex-wrap">
        <div className="flex items-center gap-2">
          <Users size={16} className="text-slate-600" />
          <span className="text-xs font-semibold uppercase tracking-wider text-slate-700">
            Judge panel ({panelJudges.length})
          </span>
        </div>
        {agreement !== undefined && (
          <AgreementBadge agreement={agreement} disputed={disputed} />
        )}
      </header>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
        {panelJudges.map((judge, i) => (
          <JudgeCard key={`${judge.metric.key}-${i}`} judge={judge} />
        ))}
      </div>

      {adjudicator && (
        <>
          <div className="flex items-center gap-2 pt-2">
            <AlertTriangle size={14} className="text-amber-600" />
            <span className="text-xs font-medium text-amber-700">
              Panel disagreed — adjudicator override
            </span>
            <span className="text-slate-300">→</span>
          </div>
          <div className="rounded border-2 border-amber-300 bg-amber-50 p-3">
            <div className="flex items-center justify-between gap-3 flex-wrap mb-2">
              <div className="flex items-center gap-2">
                <Gavel size={14} className="text-amber-700" />
                <span className="text-sm font-semibold text-amber-900">
                  {adjudicator.metric.name}
                </span>
              </div>
              <VerdictBadge
                verdict={labelToVerdict(adjudicator.score.label)}
                size="sm"
              />
            </div>
            <div className="flex items-center gap-3 flex-wrap text-xs text-slate-700">
              <span>
                Score:{" "}
                <span className="font-mono">
                  {formatScore(adjudicator.score.value)}
                </span>
              </span>
              {adjudicator.provenance.judgeModel && (
                <ModelBadge
                  model={adjudicator.provenance.judgeModel}
                  role="judge"
                />
              )}
              <span className="text-slate-500">
                {formatCost(adjudicator.provenance.estimatedCost)}
              </span>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function JudgeCard({ judge }: { judge: EvalResultNodeShape }) {
  return (
    <div className="rounded border border-slate-200 bg-white p-2.5">
      <div className="flex items-center justify-between gap-2 mb-1">
        <span className="text-sm font-medium text-slate-900 truncate">
          {judge.metric.name}
        </span>
        <VerdictBadge
          verdict={labelToVerdict(judge.score.label)}
          size="sm"
        />
      </div>
      <div className="flex items-center gap-2 flex-wrap text-[11px] text-slate-600">
        <span className="font-mono">{formatScore(judge.score.value)}</span>
        {judge.provenance.judgeModel && (
          <ModelBadge
            model={judge.provenance.judgeModel}
            role="judge"
          />
        )}
        {judge.provenance.estimatedCost > 0 && (
          <span className="text-slate-500">
            {formatCost(judge.provenance.estimatedCost)}
          </span>
        )}
      </div>
    </div>
  );
}

function AgreementBadge({
  agreement,
  disputed,
}: {
  agreement: number;
  disputed: boolean;
}) {
  const tone = disputed
    ? "bg-amber-100 text-amber-800 border-amber-200"
    : "bg-green-100 text-green-800 border-green-200";
  const Icon = disputed ? AlertTriangle : CheckCircle2;
  return (
    <span
      className={`inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded border ${tone}`}
      title={
        disputed
          ? "Inter-rater agreement below threshold — adjudicator was invoked"
          : "Inter-rater agreement above threshold — panel verdict accepted"
      }
    >
      <Icon size={12} />
      Agreement {agreement.toFixed(2)}
    </span>
  );
}
