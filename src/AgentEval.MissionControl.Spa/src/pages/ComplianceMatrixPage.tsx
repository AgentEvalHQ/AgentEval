import { Link, useNavigate, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ShieldAlert, ShieldCheck } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import {
  ComplianceMatrix,
  type MatrixCell,
  type MatrixControl,
  type MatrixSubject,
} from "@/components/ComplianceMatrix";
import { formatDate } from "@/lib/format";

// Plan-08 Wave 4 (MC1.6.7): subjects × controls heatmap for a single regulation.

interface MatrixResponse {
  complianceMatrix: {
    regulation: string;
    subjects: MatrixSubject[];
    controls: MatrixControl[];
    cells: MatrixCell[];
    allChainsValid: boolean;
    lastEvidenceAt: string | null;
  };
}

const MATRIX_QUERY = /* GraphQL */ `
  query ComplianceMatrix($regulation: String!) {
    complianceMatrix(regulation: $regulation) {
      regulation
      subjects { name kind }
      controls { id title }
      cells {
        subjectName
        controlId
        status
        passRate
        lastEvidenceAt
        lastEvidenceRunId
        regressedFromBaseline
      }
      allChainsValid
      lastEvidenceAt
    }
  }
`;

export function ComplianceMatrixPage() {
  const navigate = useNavigate();
  const { regulation: regulationParam } = useParams<{ regulation: string }>();
  const regulation = regulationParam ? decodeURIComponent(regulationParam) : "";

  const matrixQ = useQuery({
    queryKey: queryKeys.compliance.matrix(regulation),
    queryFn: () =>
      gqlRequest<MatrixResponse>(MATRIX_QUERY, { regulation }),
    enabled: regulation.length > 0,
  });

  return (
    <DataState
      isPending={matrixQ.isPending}
      isError={matrixQ.isError}
      error={matrixQ.error}
      data={matrixQ.data}
      loadingMessage="Loading compliance matrix…"
      errorPrefix="Failed to load matrix"
    >
      {(d) => {
        const m = d.complianceMatrix;
        return (
          <div className="space-y-6">
            <Link
              to="/compliance"
              className="inline-flex items-center gap-1 text-sm text-accent-700 hover:text-accent-500"
            >
              <ArrowLeft size={14} /> Back to compliance overview
            </Link>

            <header className="flex items-start justify-between gap-4">
              <div>
                <span className="text-[10px] uppercase tracking-wider text-slate-400">
                  Regulation
                </span>
                <h2 className="text-2xl font-bold text-slate-900 mt-1">
                  {m.regulation}
                </h2>
                <p className="text-sm text-slate-600 mt-1">
                  Latest evidence per (subject, control) — most recent state
                  per cell.
                </p>
              </div>
              <AuditChainBadge valid={m.allChainsValid} />
            </header>

            <section className="grid grid-cols-2 lg:grid-cols-4 gap-3">
              <Stat label="Subjects" value={`${m.subjects.length}`} />
              <Stat label="Controls" value={`${m.controls.length}`} />
              <Stat
                label="Cells"
                value={`${m.cells.length}`}
                hint={`${m.subjects.length * m.controls.length} possible`}
              />
              <Stat
                label="Last evidence"
                value={m.lastEvidenceAt ? formatDate(m.lastEvidenceAt) : "—"}
              />
            </section>

            <section className="rounded-lg border border-slate-200 bg-white p-4">
              <ComplianceMatrix
                subjects={m.subjects}
                controls={m.controls}
                cells={m.cells}
                onCellClick={(subjectName, controlId) => {
                  // Drill into the evidence record that backs THIS specific
                  // cell — finding by both subjectName + controlId ensures we
                  // navigate to the evidence the user actually clicked, not
                  // an arbitrary cell from the same row. Different controls
                  // for the same subject may live in distinct evidence files
                  // with distinct timestamps.
                  const cell = m.cells.find(
                    (c) => c.subjectName === subjectName && c.controlId === controlId,
                  );
                  if (!cell) return;
                  const subject = m.subjects.find((s) => s.name === subjectName);
                  if (!subject) return;
                  // The on-disk timestamp format mirrors what FileSystemOutputStore
                  // writes ("yyyy-MM-dd_HH-mm-ss"). The matrix's lastEvidenceAt is
                  // an ISO string; we re-format for the route param.
                  const tsForUrl = new Date(cell.lastEvidenceAt)
                    .toISOString()
                    .replace(/\.\d+Z$/, "Z")
                    .replace(/T/, "_")
                    .replace(/:/g, "-")
                    .replace(/Z$/, "");
                  const path = `/compliance/${encodeURIComponent(regulation)}/${subject.kind.toLowerCase()}/${encodeURIComponent(subjectName)}/${encodeURIComponent(tsForUrl)}`;
                  navigate(path);
                }}
              />
            </section>
          </div>
        );
      }}
    </DataState>
  );
}

function AuditChainBadge({ valid }: { valid: boolean }) {
  return (
    <span
      className={`inline-flex items-center gap-1 text-xs font-medium px-3 py-1 rounded ${
        valid ? "bg-green-100 text-green-800" : "bg-red-100 text-red-800"
      }`}
      title={
        valid
          ? "Every loaded evidence's manifest hash matches its source run's content hash."
          : "At least one piece of evidence does not match its source run — possible tampering."
      }
    >
      {valid ? <ShieldCheck size={14} /> : <ShieldAlert size={14} />}
      Audit chain {valid ? "verified" : "issues"}
    </span>
  );
}

function Stat({
  label,
  value,
  hint,
}: {
  label: string;
  value: string;
  hint?: string;
}) {
  return (
    <div className="rounded-lg border border-slate-200 bg-white p-3">
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className="text-lg font-semibold mt-1 text-slate-900">{value}</dd>
      {hint && <dd className="text-[11px] text-slate-400 mt-0.5">{hint}</dd>}
    </div>
  );
}
