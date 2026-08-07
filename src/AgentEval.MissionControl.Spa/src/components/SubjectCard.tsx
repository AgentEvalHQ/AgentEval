import { Link } from "react-router";
import { VerdictBadge, type Verdict } from "./VerdictBadge";
import { Sparkline } from "./charts/Sparkline";

// Plan-08 Wave 2 (MC1.6.4): subjects-list card with a tiny trend sparkline.
// Used on the Dashboard / Subjects pages.
//
// Plan-08 portal-review A22 (T2.2, 2026-05-25): sparkline input is capped at
// the most-recent 30 points. With long histories the dense line collapses to
// a noisy ribbon and the recent trend (the only thing the dashboard tile is
// supposed to communicate) is lost. The cap is applied at render time so the
// caller can pass the full series without pre-trimming; a "showing latest 30
// of N runs" footer surfaces when the cap is engaged.

export interface SubjectCardData {
  kind: "AGENT" | "WORKFLOW";
  name: string;
  lastVerdict: Verdict | null;
  lastRunId: string | null;
  trend: { value: number }[]; // recent scores, oldest → newest
}

interface SubjectCardProps {
  subject: SubjectCardData;
}

/** A22 cap — keep aligned with the Sparkline footer text below. */
const SPARKLINE_MAX_POINTS = 30;

export function SubjectCard({ subject }: SubjectCardProps) {
  const detailHref = `/subjects/${subject.kind.toLowerCase()}/${encodeURIComponent(subject.name)}`;
  const total = subject.trend.length;
  const cappedTrend =
    total > SPARKLINE_MAX_POINTS
      ? subject.trend.slice(total - SPARKLINE_MAX_POINTS)
      : subject.trend;
  return (
    <Link
      to={detailHref}
      className="block rounded-lg border border-slate-200 bg-white p-4 hover:border-accent-500 hover:shadow-sm transition"
    >
      <div className="flex items-start justify-between gap-2 mb-3">
        <div>
          <span className="text-[10px] uppercase tracking-wider text-slate-400">
            {subject.kind}
          </span>
          <h3 className="font-semibold text-slate-900 mt-0.5">{subject.name}</h3>
        </div>
        {subject.lastVerdict && <VerdictBadge verdict={subject.lastVerdict} />}
      </div>
      <Sparkline data={cappedTrend} height={32} />
      {total > SPARKLINE_MAX_POINTS && (
        <p className="mt-1 text-[10px] text-slate-400">
          showing latest {SPARKLINE_MAX_POINTS} of {total} runs
        </p>
      )}
    </Link>
  );
}
