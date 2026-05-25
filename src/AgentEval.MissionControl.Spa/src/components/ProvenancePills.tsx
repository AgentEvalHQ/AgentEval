// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

import { useState } from "react";
import { Check, Copy, Gavel, Hash, Scale, ShieldCheck, Users } from "lucide-react";
import type { EvalProvenanceNode, EvalDetailsNode } from "@/lib/eval-tree";

// Plan-08 portal-review A4 (T2.2, 2026-05-25): two trustworthy-data pills
// rendered below the model id wherever a provenance block is shown.
//
//   - PromptHashPill: short SHA prefix on the surface, full hash in the
//     tooltip, click-to-copy with a transient ✓ confirmation. Surfaces the
//     SHA the run pinned a judge prompt at so reviewers can correlate with
//     the canonical prompts table.
//
//   - JudgeModePill: resolved judge mode (single / panel / majority-vote /
//     adjudicated) derived from the existing provenance shape:
//       • provenance.type === "multi-judge-adjudicated"     → adjudicated
//         (panel + adjudicator picks; adjudicator can override majority —
//          NOT equivalent to majority-vote, hence the distinct mode)
//       • provenance.type === "composite" + MajorityVote aggregation
//         with >=2 sub-results                              → majority-vote
//       • provenance.type === "composite" + panel aggregation
//         (WeightedMedian / Median) with >=2 sub-results    → panel
//       • otherwise                                          → single
//     This avoids extending the C# EvalProvenance record (a breaking change)
//     while still giving operators the honest "how was this scored" signal
//     the portal-review flagged as missing.
//     R7 review (2026-05-25) corrected the adjudicated↔majority-vote
//     conflation and gave each mode a unique lucide glyph (Gavel/Users/Scale/
//     ShieldCheck) so monochrome / a11y modes still distinguish them.

interface PromptHashPillProps {
  promptHash: string | null | undefined;
}

export function PromptHashPill({ promptHash }: PromptHashPillProps) {
  const [copied, setCopied] = useState(false);
  if (!promptHash) return null;

  const short = promptHash.length > 8 ? promptHash.slice(0, 8) : promptHash;

  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(promptHash);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1200);
    } catch {
      // Some browsers (or non-secure contexts) refuse clipboard writes.
      // Fall through silently — the full hash is still surfaced via the
      // title tooltip so reviewers can copy by hand.
    }
  };

  return (
    <button
      type="button"
      onClick={onCopy}
      title={`Prompt SHA: ${promptHash}\nClick to copy`}
      className="inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded bg-slate-100 text-slate-700 hover:bg-slate-200 focus:outline-none focus-visible:ring-2 focus-visible:ring-accent-500"
      aria-label={`Copy prompt SHA ${promptHash}`}
    >
      <Hash size={12} />
      <code className="font-mono">{short}</code>
      {copied ? (
        <Check size={12} className="text-green-600" aria-hidden="true" />
      ) : (
        <Copy size={12} className="text-slate-500" aria-hidden="true" />
      )}
    </button>
  );
}

export type JudgeMode = "single" | "panel" | "majority-vote" | "adjudicated";

/** Derive the resolved judge mode from provenance + aggregation shape.
 *  See ProvenancePills.tsx file header for the decision tree.
 */
export function deriveJudgeMode(
  provenance: EvalProvenanceNode,
  details: EvalDetailsNode | null | undefined,
): JudgeMode {
  // Adjudicated = panel + adjudicator. NOT the same as majority-vote:
  // the adjudicator can override majority. Surfaced as a distinct mode
  // per R7 review.
  if (provenance.type === "multi-judge-adjudicated") return "adjudicated";
  const agg = details?.aggregationStrategy ?? "";
  const subCount = details?.subResults?.length ?? 0;
  if (provenance.type === "composite" && subCount >= 2) {
    if (agg === "MajorityVote") return "majority-vote";
    if (agg === "WeightedMedian" || agg === "Median") return "panel";
  }
  return "single";
}

interface JudgeModePillProps {
  mode: JudgeMode;
}

const MODE_TONE: Record<JudgeMode, string> = {
  single: "bg-slate-100 text-slate-700",
  panel: "bg-indigo-50 text-indigo-700",
  "majority-vote": "bg-purple-50 text-purple-700",
  adjudicated: "bg-violet-50 text-violet-700",
};

const MODE_ICON: Record<JudgeMode, typeof Gavel> = {
  single: Gavel,
  panel: Users,
  "majority-vote": Scale,
  adjudicated: ShieldCheck,
};

export function JudgeModePill({ mode }: JudgeModePillProps) {
  const Icon = MODE_ICON[mode];
  return (
    <span
      className={`inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded ${MODE_TONE[mode]}`}
      title={`Judge mode: ${mode}`}
    >
      <Icon size={12} />
      {mode}
    </span>
  );
}
