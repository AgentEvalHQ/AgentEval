import { useQuery } from "@tanstack/react-query";
import { gqlRequest } from "@/lib/graphql-client";
import { fetchVersion } from "@/lib/rest-client";
import { queryKeys } from "@/lib/keys";

// Plan-08 MC1.6.3 smoke deliverable: the dashboard page proves end-to-end
// wiring by querying Query.subjects via graphql-request + TanStack Query and
// rendering the list. Until `npm run codegen` produces typed hooks, response
// shapes are hand-written interfaces — refactor to typed hooks once codegen
// has run.

interface SubjectsQueryResponse {
  subjects: {
    identity: {
      kind: "AGENT" | "WORKFLOW";
      name: string;
    };
    lastRun: {
      runId: string;
      verdict: "PASS" | "WARN" | "FAIL" | "PENDING";
    } | null;
  }[];
}

const SUBJECTS_QUERY = /* GraphQL */ `
  query SubjectsList {
    subjects {
      identity {
        kind
        name
      }
      lastRun {
        runId
        verdict
      }
    }
  }
`;

export function DashboardPage() {
  const versionQ = useQuery({
    queryKey: queryKeys.version(),
    queryFn: fetchVersion,
  });

  const subjectsQ = useQuery({
    queryKey: queryKeys.subjects.list(),
    queryFn: () => gqlRequest<SubjectsQueryResponse>(SUBJECTS_QUERY),
  });

  return (
    <div className="space-y-6">
      <header>
        <h2 className="text-2xl font-bold text-slate-900">Dashboard</h2>
        <p className="text-sm text-slate-600">
          AgentEval Mission Control — Phase 1 SPA scaffolding (plan-08 MC1.6.1-3a).
        </p>
      </header>

      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <h3 className="text-sm font-semibold text-slate-700 mb-2">Server</h3>
        {versionQ.isPending && <p className="text-sm text-slate-500">Loading version…</p>}
        {versionQ.isError && (
          <p className="text-sm text-red-600">
            Failed to reach /api/v1/version. Is the backend running on
            http://localhost:5000? Start it with{" "}
            <code className="bg-slate-100 px-1 rounded">
              dotnet run --project ../AgentEval.MissionControl
            </code>.
          </p>
        )}
        {versionQ.data && (
          <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm">
            <dt className="text-slate-500">Mode</dt>
            <dd className="font-mono text-slate-900">{versionQ.data.mode}</dd>
            <dt className="text-slate-500">AgentEval version</dt>
            <dd className="font-mono text-slate-900">{versionQ.data.agentEvalVersion}</dd>
            <dt className="text-slate-500">GraphQL endpoint</dt>
            <dd className="font-mono text-slate-900">{versionQ.data.graphqlEndpoint}</dd>
          </dl>
        )}
      </section>

      <section className="rounded-lg border border-slate-200 bg-white p-4">
        <h3 className="text-sm font-semibold text-slate-700 mb-2">
          Subjects {subjectsQ.data && (
            <span className="text-slate-400 font-normal">
              ({subjectsQ.data.subjects.length})
            </span>
          )}
        </h3>
        {subjectsQ.isPending && <p className="text-sm text-slate-500">Querying GraphQL…</p>}
        {subjectsQ.isError && (
          <p className="text-sm text-red-600">
            GraphQL request failed: {(subjectsQ.error as Error).message}
          </p>
        )}
        {subjectsQ.data && subjectsQ.data.subjects.length === 0 && (
          <p className="text-sm text-slate-500">
            No subjects registered. Run{" "}
            <code className="bg-slate-100 px-1 rounded">agenteval init</code>{" "}
            in the solution root + ship at least one evaluation, then refresh.
          </p>
        )}
        {subjectsQ.data && subjectsQ.data.subjects.length > 0 && (
          <ul className="divide-y divide-slate-100">
            {subjectsQ.data.subjects.map((s) => (
              <li key={`${s.identity.kind}-${s.identity.name}`} className="py-2 flex items-center justify-between">
                <div>
                  <span className="text-xs uppercase text-slate-400 mr-2">
                    {s.identity.kind}
                  </span>
                  <span className="font-medium text-slate-900">{s.identity.name}</span>
                </div>
                {s.lastRun && (
                  <VerdictBadge verdict={s.lastRun.verdict} />
                )}
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function VerdictBadge({ verdict }: { verdict: "PASS" | "WARN" | "FAIL" | "PENDING" }) {
  const tone = {
    PASS:    "bg-green-100 text-green-800",
    WARN:    "bg-amber-100 text-amber-800",
    FAIL:    "bg-red-100 text-red-800",
    PENDING: "bg-slate-100 text-slate-600",
  }[verdict];
  return (
    <span className={`text-xs font-medium px-2 py-0.5 rounded ${tone}`}>{verdict}</span>
  );
}
