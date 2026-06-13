import { Link, useParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ExternalLink, ShieldAlert, ShieldCheck } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { restUrls } from "@/lib/rest-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { formatDate, formatDateTime } from "@/lib/format";

// Plan-08 Wave 7 (MC1.6.8): per-evidence detail page reachable from the
// compliance matrix. Renders the full evidence (controls + summary +
// attestation + audit-chain badge) and links to the source PDF / source run.
//
// Phase-0 security 0.8 (2026-05-13): the resolver now wraps the evidence in
// a `complianceEvidenceWithChain` shape that exposes `chainValid` +
// `chainBreakReason`. The detail page renders a visible warning badge when
// the per-doc audit chain is broken (source-run-not-found or hash-mismatch).

type CellStatus = "pass" | "warn" | "fail";
type ChainBreakReason = "source-run-not-found" | "hash-mismatch";

interface EvidenceDetailResponse {
  complianceEvidence: {
    chainValid: boolean;
    chainBreakReason: ChainBreakReason | null;
    evidence: {
      schemaVersion: string;
      regulation: string;
      subject: {
        kind: "AGENT" | "WORKFLOW";
        name: string;
      };
      generatedAt: string;
      sourceRun: {
        runId: string;
        manifestHash: string;
      };
      summary: {
        controlsTotal: number;
        passed: number;
        warnings: number;
        failed: number;
        overallStatus: CellStatus;
      };
      controls: {
        id: string;
        title: string;
        status: CellStatus;
        passRate: number;
        notes: string | null;
        regressedFromBaseline: boolean | null;
      }[];
      attestation: {
        agentEvalVersion: string;
        configurationId: string | null;
        evaluator: string;
        evaluatorModel: string;
      };
    };
  } | null;
}

const EVIDENCE_QUERY = /* GraphQL */ `
  query EvidenceDetail($regulation: String!, $kind: SubjectKind!, $name: String!, $ts: String!) {
    complianceEvidence(regulation: $regulation, subjectKind: $kind, subjectName: $name, timestamp: $ts) {
      chainValid
      chainBreakReason
      evidence {
        schemaVersion
        regulation
        subject { kind name }
        generatedAt
        sourceRun { runId manifestHash }
        summary { controlsTotal passed warnings failed overallStatus }
        controls {
          id
          title
          status
          passRate
          notes
          regressedFromBaseline
        }
        attestation { agentEvalVersion configurationId evaluator evaluatorModel }
      }
    }
  }
`;

const CHAIN_BREAK_MESSAGE: Record<ChainBreakReason, string> = {
  "source-run-not-found":
    "The source run referenced by this evidence is missing from the workspace. The evidence may be orphaned or the run was deleted after attestation.",
  "hash-mismatch":
    "The source run's content hash does not match the value recorded in this evidence. The run body has been modified after attestation — treat the evidence as untrusted until verified.",
};

const STATUS_TONE: Record<string, string> = {
  pass: "bg-green-100 text-green-800",
  warn: "bg-amber-100 text-amber-800",
  fail: "bg-red-100 text-red-800",
  // A run that conclusively evaluated zero controls is honestly NOT_EVALUATED, not a green pass.
  not_evaluated: "bg-slate-100 text-slate-600",
};

// Tolerate any backend casing and unknown values (e.g. "NOT_EVALUATED") with a neutral tone.
const toneFor = (status: string | null | undefined): string =>
  STATUS_TONE[(status ?? "").toLowerCase()] ?? "bg-slate-100 text-slate-600";

export function EvidenceDetailPage() {
  const { regulation: regParam, kind: kindParam, name: nameParam, ts: tsParam } =
    useParams<{ regulation: string; kind: string; name: string; ts: string }>();

  const regulation = regParam ? decodeURIComponent(regParam) : "";
  const kindUpper = (kindParam ?? "").toUpperCase() as "AGENT" | "WORKFLOW";
  const name = nameParam ? decodeURIComponent(nameParam) : "";
  const ts = tsParam ? decodeURIComponent(tsParam) : "";

  const evidenceQ = useQuery({
    queryKey: queryKeys.compliance.evidence(regulation, kindUpper, name, ts),
    queryFn: () =>
      gqlRequest<EvidenceDetailResponse>(EVIDENCE_QUERY, {
        regulation,
        kind: kindUpper,
        name,
        ts,
      }),
    enabled: regulation.length > 0 && (kindUpper === "AGENT" || kindUpper === "WORKFLOW") && name.length > 0 && ts.length > 0,
  });

  return (
    <DataState
      isPending={evidenceQ.isPending}
      isError={evidenceQ.isError}
      error={evidenceQ.error}
      data={evidenceQ.data}
      loadingMessage="Loading evidence…"
      errorPrefix="Failed to load evidence"
    >
      {(d) => {
        const wrapper = d.complianceEvidence;
        if (!wrapper) {
          return (
            <div>
              <BackLink regulation={regulation} subjectName={name} />
              <p className="mt-2 text-sm text-red-600">
                No evidence found for{" "}
                <code className="bg-red-50 px-1 rounded">
                  {regulation} / {name} / {ts}
                </code>
                .
              </p>
            </div>
          );
        }
        const ev = wrapper.evidence;
        const chainBroken = !wrapper.chainValid;
        return (
          <div className="space-y-6">
            <BackLink regulation={regulation} subjectName={name} />

            {chainBroken && wrapper.chainBreakReason && (
              <div
                role="alert"
                className="rounded-lg border border-red-300 bg-red-50 p-4 flex items-start gap-3"
              >
                <ShieldAlert className="text-red-600 shrink-0 mt-0.5" size={20} aria-hidden="true" />
                <div>
                  <h3 className="text-sm font-semibold text-red-900">
                    Audit chain broken ({wrapper.chainBreakReason})
                  </h3>
                  <p className="text-sm text-red-800 mt-1">
                    {CHAIN_BREAK_MESSAGE[wrapper.chainBreakReason]}
                  </p>
                </div>
              </div>
            )}

            <header className="flex items-start justify-between gap-4">
              <div>
                <span className="text-[10px] uppercase tracking-wider text-slate-400">
                  {ev.regulation} · {ev.subject.kind} · {ev.subject.name}
                </span>
                <h2 className="text-2xl font-bold text-slate-900 mt-1">
                  Evidence — {formatDate(ev.generatedAt)}
                </h2>
                <p className="text-sm text-slate-600 mt-1">
                  Generated {formatDateTime(ev.generatedAt)} · schema v{ev.schemaVersion}
                </p>
              </div>
              <div className="flex flex-col items-end gap-1 shrink-0 text-right">
                <span
                  className={`inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded ${toneFor(ev.summary.overallStatus)}`}
                >
                  Overall: {ev.summary.overallStatus}
                </span>
                <a
                  href={restUrls.compliancePdf(regulation, name, ts)}
                  target="_blank"
                  rel="noreferrer"
                  className="inline-flex items-center gap-1 text-xs text-accent-700 hover:text-accent-500"
                >
                  Source PDF <ExternalLink size={12} />
                </a>
                <Link
                  to={`/runs/${encodeURIComponent(ev.sourceRun.runId)}`}
                  className="inline-flex items-center gap-1 text-xs text-accent-700 hover:text-accent-500"
                >
                  Source run
                </Link>
              </div>
            </header>

            <section className="grid grid-cols-2 lg:grid-cols-4 gap-3">
              <Stat label="Controls" value={`${ev.summary.controlsTotal}`} />
              <Stat label="Passed" value={`${ev.summary.passed}`} tone="ok" />
              <Stat label="Warnings" value={`${ev.summary.warnings}`} tone={ev.summary.warnings > 0 ? "warn" : "neutral"} />
              <Stat label="Failed" value={`${ev.summary.failed}`} tone={ev.summary.failed > 0 ? "fail" : "neutral"} />
            </section>

            <section className="rounded-lg border border-slate-200 bg-white">
              <header className="px-4 py-2 border-b border-slate-200">
                <h3 className="text-sm font-semibold text-slate-700">
                  Controls ({ev.controls.length})
                </h3>
              </header>
              <table className="w-full text-sm">
                <thead className="bg-slate-50 text-slate-600 text-xs uppercase">
                  <tr>
                    <th className="px-4 py-2 text-left">Id</th>
                    <th className="px-4 py-2 text-left">Title</th>
                    <th className="px-4 py-2 text-left">Status</th>
                    <th className="px-4 py-2 text-right">Pass rate</th>
                    <th className="px-4 py-2 text-left">Notes</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {ev.controls.map((c) => (
                    <tr key={c.id} className="hover:bg-slate-50">
                      <td className="px-4 py-2 font-mono text-xs text-slate-700">{c.id}</td>
                      <td className="px-4 py-2 text-slate-900">{c.title}</td>
                      <td className="px-4 py-2">
                        <span className={`inline-flex items-center text-xs font-medium px-2 py-0.5 rounded ${toneFor(c.status)}`}>
                          {c.status}
                        </span>
                        {c.regressedFromBaseline && (
                          <span className="ml-2 text-xs text-amber-700">↘ regressed</span>
                        )}
                      </td>
                      <td className="px-4 py-2 text-right font-mono">
                        {(c.passRate * 100).toFixed(0)}%
                      </td>
                      <td className="px-4 py-2 text-xs text-slate-700">
                        {c.notes ?? "—"}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>

            <section className="rounded-lg border border-slate-200 bg-white p-4">
              <div className="flex items-center justify-between mb-2">
                <h3 className="text-sm font-semibold text-slate-700">
                  Audit chain
                </h3>
                {chainBroken ? (
                  <span className="inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded bg-red-100 text-red-800">
                    <ShieldAlert size={12} aria-hidden="true" />
                    broken
                  </span>
                ) : (
                  <span className="inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded bg-green-100 text-green-800">
                    <ShieldCheck size={12} aria-hidden="true" />
                    valid
                  </span>
                )}
              </div>
              <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm">
                <DT label="Source run" value={ev.sourceRun.runId} mono />
                <DT label="Manifest hash" value={ev.sourceRun.manifestHash.slice(0, 16) + "…"} mono />
                <DT label="AgentEval version" value={ev.attestation.agentEvalVersion} />
                <DT label="Evaluator" value={ev.attestation.evaluator} mono />
                <DT label="Evaluator model" value={ev.attestation.evaluatorModel} mono />
              </dl>
            </section>
          </div>
        );
      }}
    </DataState>
  );
}

function BackLink({
  regulation,
  subjectName,
}: {
  regulation: string;
  subjectName: string;
}) {
  return (
    <Link
      to={`/compliance/${encodeURIComponent(regulation)}`}
      className="inline-flex items-center gap-1 text-sm text-accent-700 hover:text-accent-500"
    >
      <ArrowLeft size={14} /> Back to {regulation} matrix · {subjectName}
    </Link>
  );
}

function Stat({
  label,
  value,
  tone = "neutral",
}: {
  label: string;
  value: string;
  tone?: "neutral" | "ok" | "warn" | "fail";
}) {
  const toneClass = {
    neutral: "text-slate-900",
    ok:      "text-green-700",
    warn:    "text-amber-700",
    fail:    "text-red-700",
  }[tone];
  return (
    <div className="rounded-lg border border-slate-200 bg-white p-3">
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className={`text-lg font-semibold mt-1 ${toneClass}`}>{value}</dd>
    </div>
  );
}

function DT({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className={mono ? "font-mono text-sm text-slate-900" : "text-sm text-slate-900"}>
        {value}
      </dd>
    </div>
  );
}
