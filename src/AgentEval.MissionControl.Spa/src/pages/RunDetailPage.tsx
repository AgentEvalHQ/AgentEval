import { Link, useParams } from "react-router";
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
    contentHash: string;
    solution: { id: string; name: string };
    subject: {
      kind: "AGENT" | "WORKFLOW";
      name: string;
      modelId: string | null;
      framework: string | null;
    };
    run: {
      runId: string;
      timestamp: string;
      duration: string;
      verdict: Verdict;
      kind: string;
      scenarioCount: number;
      harness: string;
      seed: number | null;
    };
    git: { commit: string | null; branch: string | null; dirty: boolean };
    environment: { os: string; dotnetVersion: string; ci: boolean };
    agentEval: { version: string };
  } | null;
  runSummary: {
    verdict: Verdict;
    stats: {
      total: number;
      passed: number;
      failed: number;
      warnings: number;
    };
    cost: {
      estimatedCost: number;
      promptTokens: number;
      completionTokens: number;
    } | null;
  } | null;
  scenarios: {
    id: string;
    name: string;
    passed: boolean;
    score: number;
    duration: string;
    estimatedCost: number;
  }[];
  runCostBreakdown: {
    totalCost: number;
    byTier: { trivial: number; low: number; medium: number; high: number };
    unknownKeyCost: number;
    legacyFlatCost: number;
    filteredOut: string[];
  } | null;
}

const RUN_DETAIL_QUERY = /* GraphQL */ `
  query RunDetail($runId: String!) {
    run(runId: $runId) {
      schemaVersion
      contentHash
      solution { id name }
      subject { kind name modelId framework }
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
    runSummary(runId: $runId) {
      verdict
      stats { total passed failed warnings }
      cost { estimatedCost promptTokens completionTokens }
    }
    scenarios(runId: $runId) {
      id
      name
      passed
      score
      duration
      estimatedCost
    }
    runCostBreakdown(runId: $runId) {
      totalCost
      byTier { trivial low medium high }
      unknownKeyCost
      legacyFlatCost
      filteredOut
    }
  }
`;

export function RunDetailPage() {
  const { runId: runIdParam } = useParams<{ runId: string }>();
  const runId = runIdParam ? decodeURIComponent(runIdParam) : "";

  const detailQ = useQuery({
    queryKey: queryKeys.runs.detail(runId),
    queryFn: () =>
      gqlRequest<RunDetailResponse>(RUN_DETAIL_QUERY, { runId }),
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
                  {d.run.subject.kind} · {d.run.run.kind}
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
                  <ManifestHashBadge contentHash={d.run.contentHash} />
                  <ModelBadge model={d.run.subject.modelId} role="agent" />
                  {d.run.subject.framework && (
                    <span className="text-xs text-slate-500">
                      {d.run.subject.framework}
                    </span>
                  )}
                </div>
              </div>
              <div className="flex flex-col items-end gap-1 shrink-0 text-right">
                <a
                  href={restUrls.trace(runId)}
                  target="_blank"
                  rel="noreferrer"
                  className="inline-flex items-center gap-1 text-xs text-accent-700 hover:text-accent-500"
                >
                  Trace JSON <ExternalLink size={12} />
                </a>
                <a
                  href={restUrls.report(runId, "markdown")}
                  target="_blank"
                  rel="noreferrer"
                  className="inline-flex items-center gap-1 text-xs text-accent-700 hover:text-accent-500"
                >
                  Markdown report <ExternalLink size={12} />
                </a>
              </div>
            </header>

            <section className="grid grid-cols-2 lg:grid-cols-4 gap-3">
              <Stat
                label="Verdict"
                value={
                  d.runSummary?.verdict ?? d.run.run.verdict
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
              <Stat
                label="Cost"
                value={formatCost(d.runSummary?.cost?.estimatedCost ?? null)}
              />
            </section>

            {d.runCostBreakdown && d.runCostBreakdown.totalCost > 0 && (
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
                  <code>EvalResult</code> tree. Inclusive total{" "}
                  {formatCost(d.runCostBreakdown.totalCost)} (chart segments above
                  exclude the legacy-flat slice; see breakdown below).
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
                      <tr key={s.id} className="hover:bg-slate-50">
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
                          {s.passed ? (
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
                <DT label="Solution" value={d.run.solution.name} />
                <DT label="Harness" value={d.run.run.harness} />
                <DT label="Seed" value={`${d.run.run.seed ?? "—"}`} />
                <DT label="Schema" value={d.run.schemaVersion} />
                <DT label="OS" value={d.run.environment.os} />
                <DT label=".NET" value={d.run.environment.dotnetVersion} />
                <DT label="Git branch" value={d.run.git.branch ?? "—"} />
                <DT
                  label="Commit"
                  value={
                    d.run.git.commit
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
}: {
  label: string;
  value: string;
  tone?: "neutral" | "warn";
}) {
  const valueClass =
    tone === "warn" ? "text-red-700" : "text-slate-900";
  return (
    <div className="rounded-lg border border-slate-200 bg-white p-3">
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className={`text-lg font-semibold mt-1 ${valueClass}`}>{value}</dd>
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
