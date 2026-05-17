import { Link } from "react-router-dom";
import { VerdictBadge, type Verdict } from "./VerdictBadge";
import { Sparkline } from "./charts/Sparkline";

// Plan-08 Wave 2 (MC1.6.4): subjects-list card with a tiny trend sparkline.
// Used on the Dashboard / Subjects pages.

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

export function SubjectCard({ subject }: SubjectCardProps) {
  const detailHref = `/subjects/${subject.kind.toLowerCase()}/${encodeURIComponent(subject.name)}`;
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
      <Sparkline data={subject.trend} height={32} />
    </Link>
  );
}
