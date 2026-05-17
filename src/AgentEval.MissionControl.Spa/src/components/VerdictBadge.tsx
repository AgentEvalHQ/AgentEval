// Plan-08 Wave 2: extracted verdict pill — shared between SubjectCard,
// RunRow, and the dashboard's subjects list.

export type Verdict = "PASS" | "WARN" | "FAIL" | "PENDING";

const TONE_BY_VERDICT: Record<Verdict, string> = {
  PASS: "bg-green-100 text-green-800",
  WARN: "bg-amber-100 text-amber-800",
  FAIL: "bg-red-100 text-red-800",
  PENDING: "bg-slate-100 text-slate-600",
};

interface VerdictBadgeProps {
  verdict: Verdict;
  size?: "sm" | "md";
}

export function VerdictBadge({ verdict, size = "sm" }: VerdictBadgeProps) {
  const sizeClasses =
    size === "sm" ? "text-xs px-2 py-0.5" : "text-sm px-3 py-1";
  return (
    <span
      className={`inline-flex items-center font-medium rounded ${sizeClasses} ${TONE_BY_VERDICT[verdict]}`}
    >
      {verdict}
    </span>
  );
}
