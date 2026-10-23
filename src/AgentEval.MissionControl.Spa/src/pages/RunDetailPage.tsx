import { Link, useParams, useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, ExternalLink } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { restUrls } from "@/lib/rest-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { VerdictBadge, type Verdict } from "@/components/VerdictBadge";
import { ManifestHashBadge } from "@/components/ManifestHashBadge";
import { ModelBadge } from "@/components/ModelBadge";
import { CostTierBreakdownChart } from "@/components/charts/CostTierBreakdownChart";
import { formatDateTime, formatScore, formatCost } from "@/lib/format";

// Plan-08 Wave 3: full run detail with manifest + summary + scenarios.

interface RunDetailResponse {
  run: {
    schemaVersion: string;
    contentHash: string | null;
    evidence: { integrity: string; imported: boolean; signatureTrust: string };
    solution: { id: string | null; name: string | null } | null;
    subject: {
      kind: string | null;
      rawKind: string;
      name: string;
      modelId: string | null;
      framework: string | null;
    };
    run: {
      runId: string;
      timestamp: string | null;
      duration: string | null;
      verdict: Verdict | null;
      kind: string | null;
      scenarioCount: number;
      harness: string | null;
      seed: number | null;
    };
    git: { commit: string | null; branch: string | null; dirty: boolean | null } | null;
    environment: { os: string | null; dotnetVersion: string | null; ci: boolean | null } | null;
    agentEval: { version: string | null } | null;
  } | null;
  runSummary: {
    states: { state: string; count: number }[];
    verdict: Verdict | null;
    stats: {
      total: number;
      passed: number;
      failed: number;
      warnings: number;
      // Checks that did not run: skipped or errored. Neither a pass nor a fail, and they keep a run from passing.
      skipped: number;
    };
    cost: {
      estimatedCost: number | null;
      promptTokens: number | null;
      completionTokens: number | null;
    } | null;
  } | null;
  scenarios: {
    id: string;
    resultId: string;
    state: string;
    name: string;
    passed: boolean | null;
    score: number | null;
    measurement: string | null;
    duration: string | null;
    estimatedCost: number | null;
  }[];
  runArtifacts: {
    evidenceId: string;
    kind: string;
    description: string | null;
    captured: boolean;
    reportFormat: string | null;
    legacyTrace: boolean;
  }[];
  runCostBreakdown: {
    totalCost: number;
    byTier: { trivial: number; low: number; medium: number; high: number };
    unknownKeyCost: number;
    legacyFlatCost: number;
    filteredOut: string[];
    unpricedCount: number;
    isComplete: boolean;
  } | null;
}

const RUN_DETAIL_QUERY = /* GraphQL */ `
  query RunDetail($runId: String!, $sourceId: String) {
    run(runId: $runId, sourceId: $sourceId) {
      schemaVersion
      contentHash
      evidence { integrity imported signatureTrust }
      solution { id name }
      subject { kind rawKind name modelId framework }
      run {
        runId
        timestamp
        duration
        verdict
        kind
        scenarioCount
        harness
        seed
      }
      git { commit branch dirty }
      environment { os dotnetVersion ci }
      agentEval { version }
    }
    runSummary(runId: $runId, sourceId: $sourceId) {
      verdict
      states { state count }
      stats { total passed failed warnings skipped }
      cost { estimatedCost promptTokens completionTokens }
    }
    scenarios(runId: $runId, sourceId: $sourceId) {
      id
      resultId
      state
      name
      passed
      score
      measurement
      duration
      estimatedCost
    }
    runArtifacts(runId: $runId, sourceId: $sourceId) {
      evidenceId kind description captured reportFormat legacyTrace
    }
    runCostBreakdown(runId: $runId, sourceId: $sourceId) {
      totalCost
      byTier { trivial low medium high }
      unknownKeyCost
      legacyFlatCost
      filteredOut
      unpricedCount
      isComplete
    }
  }
`;

export function RunDetailPage() {
  const { runId: runIdParam } = useParams<{ runId: string }>();
  const runId = runIdParam ? decodeURIComponent(runIdParam) : "";

  const [search] = useSearchParams();
  const sourceId = search.get("sourceId");
  const detailQ = useQuery({
    queryKey: queryKeys.runs.detail(runId, sourceId),
    queryFn: () =>
      gqlRequest<RunDetailResponse>(RUN_DETAIL_QUERY, { runId, sourceId }),
    enabled: runId.length > 0,
  });

  return (
    <DataState
      isPending={detailQ.isPending}
      isError={detailQ.isError}
      error={detailQ.error}
      data={detailQ.data}
      loadingMessage="Loading run…"
      errorPrefix="Failed to load run"
    >
      {(d) => {
        if (!d.run) {
          return (
            <BackLink message={`No run found for runId='${runId}'.`} />
          );
        }
        return (
          <div className="space-y-6">
            <BackLink />

            <header className="flex items-start justify-between gap-4">
              <div>
                <span className="text-[10px] uppercase tracking-wider text-slate-400">
                  {d.run.subject.rawKind} · {d.run.run.kind}
                </span>
                <h2 className="text-2xl font-bold text-slate-900 mt-1">
                  {d.run.subject.name}
                </h2>
                <p className="text-sm text-slate-600 mt-1">
                  {formatDateTime(d.run.run.timestamp)} · run{" "}
                  <code className="font-mono text-xs">{d.run.run.runId}</code>
                </p>
                <div className="flex items-center gap-2 mt-3 flex-wrap">
                  <VerdictBadge verdict={d.run.run.verdict} size="md" />
                  <ManifestHashBadge contentHash={d.run.contentHash} integrity={d.run.evidence.integrity} imported={d.run.evidence.imported} />
                  <ModelBadge model={d.run.subject.modelId} role="agent" />
                  {d.run.subject.framework && (
                    <span className="text-xs text-slate-500">
                      {d.run.subject.framework}
                    </span>
                  )}
                </div>
              </div>
              <div className="flex flex-col items-end gap-1 shrink-0 text-right">
                {d.runArtifacts.length === 0 && <span className="text-xs text-slate-500">No captured artifacts</span>}
                {d.runArtifacts.map(artifact => {
                  const label = artifact.legacyTrace ? "Trace JSON"
                    : artifact.reportFormat ? `${artifact.reportFormat} report`
                    : artifact.description ?? `${artifact.kind} · ${artifact.evidenceId}`;
                  if (!artifact.captured) return <span key={artifact.evidenceId} className="text-xs text-slate-500">
                    {label} · content not captured
                  </span>;
                  const uniqueFormat = artifact.reportFormat && d.runArtifacts.filter(a => a.reportFormat === artifact.reportFormat).length === 1;
                  const href = artifact.legacyTrace ? restUrls.trace(runId, sourceId)
                    : uniqueFormat ? restUrls.report(runId, artifact.reportFormat!, sourceId)
                    : restUrls.artifact(runId, artifact.evidenceId, sourceId);
                  return <a key={artifact.evidenceId} href={href} target="_blank" rel="noreferrer"
                    className="inline-flex items-center gap-1 text-xs text-accent-700 hover:text-accent-500">
                    {label} <ExternalLink size={12} />
                  </a>;
                })}
              </div>
            </header>

            <section className="grid grid-cols-2 lg:grid-cols-5 gap-3">
              <Stat
                label="Verdict"
                value={
                  d.runSummary?.verdict ?? d.run.run.verdict ?? "NOT RECORDED"
                }
              />
              <Stat
                label="Scenarios"
                value={`${d.runSummary?.stats.passed ?? "—"} / ${
                  d.runSummary?.stats.total ?? d.run.run.scenarioCount
                }`}
              />
              <Stat
                label="Failures"
                value={`${d.runSummary?.stats.failed ?? "—"}`}
                tone={d.runSummary && d.runSummary.stats.failed > 0 ? "warn" : "neutral"}
              />
              {/* Without this tile a run whose every check errored showed FAIL beside "Failures 0". */}
              <Stat
                label="Errors / unavailable"
                value={d.runSummary ? `${d.runSummary.states.filter(s => ["error", "not_measured", "not_applicable", "skipped"].includes(s.state)).reduce((sum, s) => sum + s.count, 0)}` : "—"}
                tone={d.runSummary && d.runSummary.stats.skipped > 0 ? "warn" : "neutral"}
                hint={d.runSummary?.states.map(s => `${s.state}: ${s.count}`).join(" · ")}
              />
              <Stat
                label="Cost"
                value={formatCost(d.runSummary?.cost?.estimatedCost ?? null)}
              />
            </section>

            {/* A run with unpriced costs keeps the section, so a partial total is never silently hidden (T83). */}
            {d.runCostBreakdown && (d.runCostBreakdown.totalCost > 0 || !d.runCostBreakdown.isComplete) && (
              <section className="rounded-lg border border-slate-200 bg-white p-4">
                <h3 className="text-sm font-semibold text-slate-700 mb-2">
                  Cost by tier
                </h3>
                <CostTierBreakdownChart
                  byTier={d.runCostBreakdown.byTier}
                  unknown={d.runCostBreakdown.unknownKeyCost}
                />
                <p className="text-xs text-slate-500 mt-2">
                  Tier-attributed total: {formatCost(
                    d.runCostBreakdown.totalCost - d.runCostBreakdown.legacyFlatCost
                  )}
                  {d.runCostBreakdown.legacyFlatCost > 0 && (
                    <>
                      {" "}(+ {formatCost(d.runCostBreakdown.legacyFlatCost)} legacy flat){" "}
                    </>
                  )}
                  {" "}— derived from each leaf evaluator's{" "}
                  <code>provenance.estimatedCost</code> in the recursive{" "}
                  <code>EvalResult</code> tree.{" "}
                  {d.runCostBreakdown.isComplete ? "Inclusive total" : "Priced part (partial)"}{" "}
                  {formatCost(d.runCostBreakdown.totalCost)}
                  {!d.runCostBreakdown.isComplete && (
                    <>
                      {" "}— {d.runCostBreakdown.unpricedCount} cost(s) not priced, so this is
                      not the run's cost
                    </>
                  )}{" "}
                  (chart segments above exclude the legacy-flat slice; see breakdown below).
                </p>
                {(d.runCostBreakdown.unknownKeyCost > 0 ||
                  d.runCostBreakdown.legacyFlatCost > 0) && (
                  <dl className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-x-4 gap-y-1 text-xs">
                    {d.runCostBreakdown.unknownKeyCost > 0 && (
                      <div className="flex items-baseline gap-2">
                        <dt className="text-slate-500 font-medium">
                          Unknown bucket
                        </dt>
                        <dd className="font-mono text-amber-700">
                          {formatCost(d.runCostBreakdown.unknownKeyCost)}
                        </dd>
                        <span className="text-slate-400">
                          modern entries without category mapping
                        </span>
                      </div>
                    )}
                    {d.runCostBreakdown.legacyFlatCost > 0 && (
                      <div className="flex items-baseline gap-2">
                        <dt className="text-slate-500 font-medium">
                          Legacy flat
                        </dt>
                        <dd className="font-mono text-slate-600">
                          {formatCost(d.runCostBreakdown.legacyFlatCost)}
                        </dd>
                        <span className="text-slate-400">
                          pre-v0.8.1-beta evidence without per-leaf cost attribution
                        </span>
                      </div>
                    )}
                  </dl>
                )}
              </section>
            )}

            <section className="rounded-lg border border-slate-200 bg-white">
              <header className="px-4 py-2 border-b border-slate-200 flex items-baseline justify-between">
                <h3 className="text-sm font-semibold text-slate-700">
                  Scenarios <span className="text-slate-400 font-normal">({d.scenarios.length})</span>
                </h3>
              </header>
              {d.scenarios.length === 0 ? (
                <p className="px-4 py-3 text-sm text-slate-500 italic">
                  No scenarios persisted for this run.
                </p>
              ) : (
                <table className="w-full text-sm">
                  <thead className="bg-slate-50 text-slate-600 text-xs uppercase">
                    <tr>
                      <th className="px-4 py-2 text-left">Scenario</th>
                      <th className="px-4 py-2 text-left">ID</th>
                      <th className="px-4 py-2 text-right">Score</th>
                      <th className="px-4 py-2 text-right">Cost</th>
                      <th className="px-4 py-2 text-center">Passed</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {d.scenarios.map((s) => (
                      <tr key={s.resultId} className="hover:bg-slate-50">
                        <td className="px-4 py-2 text-slate-900">
                          <Link
                            to={`/runs/${encodeURIComponent(runId)}/scenarios/${encodeURIComponent(s.id)}`}
                            className="text-accent-700 hover:text-accent-500"
                          >
                            {s.name}
                          </Link>
                        </td>
                        <td className="px-4 py-2 font-mono text-xs text-slate-600">{s.id}</td>
                        <td className="px-4 py-2 text-right font-mono">{formatScore(s.score)}</td>
                        <td className="px-4 py-2 text-right font-mono text-slate-700">
                          {formatCost(s.estimatedCost)}
                        </td>
                        <td className="px-4 py-2 text-center">
                          {s.measurement === "not_applicable" ? (
                            <span className="text-slate-600 font-medium text-xs">NOT APPLICABLE</span>
                          ) : s.measurement === "not_measured" ? (
                            <span className="text-slate-600 font-medium text-xs">NOT MEASURED</span>
                          ) : s.passed === null ? (
                            <span className="text-slate-600 font-medium text-xs">{s.state.toUpperCase()}</span>
                          ) : s.passed ? (
                            <span className="text-green-700 font-medium text-xs">PASS</span>
                          ) : (
                            <span className="text-red-700 font-medium text-xs">FAIL</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </section>

            <section className="rounded-lg border border-slate-200 bg-white p-4">
              <h3 className="text-sm font-semibold text-slate-700 mb-3">
                Provenance
              </h3>
              <dl className="grid grid-cols-2 sm:grid-cols-4 gap-x-6 gap-y-2 text-sm">
                <DT label="Solution" value={d.run.solution?.name ?? "not recorded"} />
                <DT label="Harness" value={d.run.run.harness ?? "not recorded"} />
                <DT label="Seed" value={`${d.run.run.seed ?? "—"}`} />
                <DT label="Schema" value={d.run.schemaVersion} />
                <DT label="OS" value={d.run.environment?.os ?? "not recorded"} />
                <DT label=".NET" value={d.run.environment?.dotnetVersion ?? "not recorded"} />
                <DT label="Git branch" value={d.run.git?.branch ?? "—"} />
                <DT
                  label="Commit"
                  value={
                    d.run.git?.commit
                      ? `${d.run.git.commit.slice(0, 8)}${d.run.git.dirty ? " (dirty)" : ""}`
                      : "—"
                  }
                />
              </dl>
            </section>
          </div>
        );
      }}
    </DataState>
  );
}

function BackLink({ message }: { message?: string } = {}) {
  return (
    <div className="space-y-2">
      <Link
        to="/runs"
        className="inline-flex items-center gap-1 text-sm text-accent-700 hover:text-accent-500"
      >
        <ArrowLeft size={14} /> Back to runs
      </Link>
      {message && <p className="text-sm text-red-600">{message}</p>}
    </div>
  );
}

function Stat({
  label,
  value,
  tone = "neutral",
  hint,
}: {
  label: string;
  value: string;
  tone?: "neutral" | "warn";
  hint?: string;
}) {
  const valueClass =
    tone === "warn" ? "text-red-700" : "text-slate-900";
  return (
    <div className="rounded-lg border border-slate-200 bg-white p-3">
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className={`text-lg font-semibold mt-1 ${valueClass}`}>{value}</dd>
      {hint && <p className="text-[11px] text-slate-500 mt-0.5">{hint}</p>}
    </div>
  );
}

function DT({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className="font-mono text-slate-900">{value}</dd>
    </div>
  );
}
