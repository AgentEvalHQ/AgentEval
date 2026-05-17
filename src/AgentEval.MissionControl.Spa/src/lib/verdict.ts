// Phase-7 Task 7.11: shared label → verdict mapper. Previously duplicated in
// AdjudicationFlow.tsx and EvalResultNode.tsx; lifting it here prevents drift
// (e.g. one site recognising "warn" but the other not).

import type { Verdict } from "@/components/VerdictBadge";

/**
 * Maps a free-text score label (as returned by the backend) onto the
 * VerdictBadge enum. Accepts canonical labels plus common synonyms:
 *  - "pass" | "passed" → PASS
 *  - "warn" | "warning" → WARN
 *  - "fail" | "failed" → FAIL
 *  - anything else → PENDING (badge renders as "—")
 */
export function labelToVerdict(label: string): Verdict {
  const lower = (label ?? "").toLowerCase();
  if (lower === "pass" || lower === "passed") return "PASS";
  if (lower === "warn" || lower === "warning") return "WARN";
  if (lower === "fail" || lower === "failed") return "FAIL";
  return "PENDING";
}
