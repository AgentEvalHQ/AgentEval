// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

import { useState } from "react";
import { Check, Copy, Gavel, Hash, Users } from "lucide-react";
import type { EvalProvenanceNode, EvalDetailsNode } from "@/lib/eval-tree";

// Plan-08 portal-review A4 (T2.2, 2026-05-25): two trustworthy-data pills
// rendered below the model id wherever a provenance block is shown.
//
//   - PromptHashPill: short SHA prefix on the surface, full hash in the
//     tooltip, click-to-copy with a transient ✓ confirmation. Surfaces the
//     SHA the run pinned a judge prompt at so reviewers can correlate with
//     the canonical prompts table.
//
//   - JudgeModePill: resolved judge mode (single / panel / majority-vote)
//     derived from the existing provenance shape:
//       • provenance.type === "multi-judge-adjudicated"     → majority-vote
//       • provenance.type === "composite" + panel aggregation
//         (WeightedMedian / MajorityVote / Median) with >=2 sub-results → panel
//       • otherwise                                          → single
//     This avoids extending the C# EvalProvenance record (a breaking change)
//     while still giving operators the honest "how was this scored" signal
//     the portal-review flagged as missing.

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

export type JudgeMode = "single" | "panel" | "majority-vote";

/** Derive the resolved judge mode from provenance + aggregation shape.
 *  See ProvenancePills.tsx file header for the decision tree.
 */
export function deriveJudgeMode(
  provenance: EvalProvenanceNode,
  details: EvalDetailsNode | null | undefined,
): JudgeMode {
  if (provenance.type === "multi-judge-adjudicated") return "majority-vote";
  const agg = details?.aggregationStrategy ?? "";
  const isPanelAgg =
    agg === "WeightedMedian" || agg === "MajorityVote" || agg === "Median";
  if (
    isPanelAgg &&
    provenance.type === "composite" &&
    (details?.subResults?.length ?? 0) >= 2
  ) {
    return "panel";
  }
  return "single";
}

interface JudgeModePillProps {
  mode: JudgeMode;
}

export function JudgeModePill({ mode }: JudgeModePillProps) {
  const tone =
    mode === "majority-vote"
      ? "bg-purple-50 text-purple-700"
      : mode === "panel"
      ? "bg-indigo-50 text-indigo-700"
      : "bg-slate-100 text-slate-700";
  const Icon = mode === "single" ? Gavel : Users;
  const label =
    mode === "majority-vote"
      ? "majority-vote"
      : mode === "panel"
      ? "panel"
      : "single";
  return (
    <span
      className={`inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded ${tone}`}
      title={`Judge mode: ${label}`}
    >
      <Icon size={12} />
      {label}
    </span>
  );
}
