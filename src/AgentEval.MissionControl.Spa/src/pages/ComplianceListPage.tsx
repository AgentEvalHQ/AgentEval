import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ShieldCheck } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { formatDate } from "@/lib/format";

// Plan-08 Wave 4 (MC1.6.7 partial): list of regulations with at least one
// stored evidence + their per-regulation summary. Click into the matrix.

type OverallStatus = "pass" | "warn" | "fail" | "not_evaluated";

interface ComplianceListResponse {
  compliance: {
    regulation: string;
    subjectCount: number;
    evidenceCount: number;
    lastEvidenceAt: string | null;
    overallStatus: OverallStatus | null;
  }[];
}

const COMPLIANCE_QUERY = /* GraphQL */ `
  query ComplianceList {
    compliance {
      regulation
      subjectCount
      evidenceCount
      lastEvidenceAt
      overallStatus
    }
  }
`;

const STATUS_TONE: Record<string, string> = {
  pass: "bg-green-100 text-green-800",
  warn: "bg-amber-100 text-amber-800",
  fail: "bg-red-100 text-red-800",
  // A run that conclusively evaluated zero controls is honestly NOT_EVALUATED, not a green pass.
  not_evaluated: "bg-slate-100 text-slate-600",
};

// Tolerate any backend casing and unknown values (e.g. "NOT_EVALUATED") with a neutral tone
// rather than rendering an unstyled badge.
const toneFor = (status: string | null | undefined): string =>
  STATUS_TONE[(status ?? "").toLowerCase()] ?? "bg-slate-100 text-slate-600";

export function ComplianceListPage() {
  const compQ = useQuery({
    queryKey: queryKeys.compliance.summary(),
    queryFn: () => gqlRequest<ComplianceListResponse>(COMPLIANCE_QUERY),
  });

  return (
    <div className="space-y-4">
      <header>
        <h2 className="text-2xl font-bold text-slate-900">Compliance</h2>
        <p className="text-sm text-slate-600">
          Regulation-level dashboards backed by audit-chain-validated evidence
          (subjects × controls × passRate matrix).
        </p>
      </header>

      <DataState
        isPending={compQ.isPending}
        isError={compQ.isError}
        error={compQ.error}
        data={compQ.data}
        isEmpty={(d) => d.compliance.length === 0}
        loadingMessage="Loading compliance summary…"
        errorPrefix="Failed to load compliance summary"
        emptyMessage={
          <div>
            No compliance evidence registered yet. Run a benchmark with a
            compliance preset (e.g. <code className="bg-slate-100 px-1 rounded">agenteval bench gdpr</code>)
            to populate this view.
          </div>
        }
      >
        {(d) => (
          <div className="grid gap-3 grid-cols-1 sm:grid-cols-2 lg:grid-cols-3">
            {d.compliance.map((c) => (
              <Link
                key={c.regulation}
                to={`/compliance/${encodeURIComponent(c.regulation)}`}
                className="block rounded-lg border border-slate-200 bg-white p-4 hover:border-accent-500 hover:shadow-sm transition"
              >
                <div className="flex items-start justify-between mb-3">
                  <div>
                    <span className="text-[10px] uppercase tracking-wider text-slate-400">
                      Regulation
                    </span>
                    <h3 className="text-lg font-semibold text-slate-900 mt-0.5">
                      {c.regulation}
                    </h3>
                  </div>
                  {c.overallStatus && (
                    <span
                      className={`inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded ${toneFor(c.overallStatus)}`}
                    >
                      <ShieldCheck size={12} />
                      {c.overallStatus}
                    </span>
                  )}
                </div>
                <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
                  <dt className="text-slate-500">Subjects</dt>
                  <dd className="text-slate-900 text-right">{c.subjectCount}</dd>
                  <dt className="text-slate-500">Evidence</dt>
                  <dd className="text-slate-900 text-right">{c.evidenceCount}</dd>
                  <dt className="text-slate-500">Last evidence</dt>
                  <dd className="text-slate-700 text-right text-xs">
                    {c.lastEvidenceAt ? formatDate(c.lastEvidenceAt) : "—"}
                  </dd>
                </dl>
              </Link>
            ))}
          </div>
        )}
      </DataState>
    </div>
  );
}
