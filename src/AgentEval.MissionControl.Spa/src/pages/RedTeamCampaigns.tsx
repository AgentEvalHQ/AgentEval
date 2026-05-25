// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// T3.6 (2026-05-25) — Red-team campaigns list view. Pairs with the
// `redTeamCampaigns` GraphQL resolver. The detail page is deferred to a
// follow-up; this page surfaces the campaign id, primary target, started-at,
// mode, and a target-count badge so operators can confirm campaigns are
// being persisted under `.agenteval/red-team/`.

import { useQuery } from "@tanstack/react-query";
import { Swords } from "lucide-react";
import { gqlRequest } from "@/lib/graphql-client";
import { queryKeys } from "@/lib/keys";
import { DataState } from "@/components/DataState";
import { formatDateTime } from "@/lib/format";

interface RedTeamCampaignsResponse {
  redTeamCampaigns: {
    campaignId: string;
    name: string;
    startedAt: string;
    mode: string;
    targets: { kind: "AGENT" | "WORKFLOW"; name: string }[];
    completedAt: string | null;
  }[];
}

const RED_TEAM_QUERY = /* GraphQL */ `
  query RedTeamCampaigns {
    redTeamCampaigns {
      campaignId
      name
      startedAt
      mode
      targets {
        kind
        name
      }
      completedAt
    }
  }
`;

export function RedTeamCampaigns() {
  const campaignsQ = useQuery({
    queryKey: queryKeys.redTeam.list(),
    queryFn: () => gqlRequest<RedTeamCampaignsResponse>(RED_TEAM_QUERY),
  });

  return (
    <div className="space-y-4">
      <header>
        <h2 className="text-2xl font-bold text-slate-900">Red-team campaigns</h2>
        <p className="text-sm text-slate-600">
          Adversarial-attack campaigns stored under{" "}
          <code className="bg-slate-100 px-1 rounded">
            .agenteval/red-team/
          </code>
          . Each campaign captures the target subjects, attack mode, and a
          findings document (when complete).
        </p>
      </header>

      <DataState
        isPending={campaignsQ.isPending}
        isError={campaignsQ.isError}
        error={campaignsQ.error}
        data={campaignsQ.data}
        isEmpty={(d) => d.redTeamCampaigns.length === 0}
        loadingMessage="Loading red-team campaigns…"
        errorPrefix="Failed to load red-team campaigns"
        emptyMessage={
          <div>
            No red-team campaigns registered yet. Run{" "}
            <code className="bg-slate-100 px-1 rounded">
              agenteval red-team --target ...
            </code>{" "}
            to start one.
          </div>
        }
      >
        {(d) => (
          <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white">
            <table className="w-full text-sm">
              <thead className="bg-slate-50 text-slate-600 text-xs uppercase tracking-wider">
                <tr>
                  <th className="px-4 py-2 text-left">Campaign</th>
                  <th className="px-4 py-2 text-left">Targets</th>
                  <th className="px-4 py-2 text-left">Mode</th>
                  <th className="px-4 py-2 text-left">Started</th>
                  <th className="px-4 py-2 text-center">Status</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {d.redTeamCampaigns
                  .slice()
                  .sort(
                    (a, b) =>
                      Date.parse(b.startedAt) - Date.parse(a.startedAt),
                  )
                  .map((c) => {
                    const primaryTarget = c.targets[0];
                    const extra = c.targets.length - 1;
                    return (
                      <tr key={c.campaignId} className="hover:bg-slate-50">
                        <td className="px-4 py-2">
                          <div className="flex items-center gap-2">
                            <Swords
                              size={14}
                              className="text-red-500 shrink-0"
                            />
                            <div>
                              <div className="font-medium text-slate-900">
                                {c.name}
                              </div>
                              <div className="text-xs font-mono text-slate-500">
                                {c.campaignId}
                              </div>
                            </div>
                          </div>
                        </td>
                        <td className="px-4 py-2 text-slate-700">
                          {primaryTarget ? (
                            <>
                              <span className="text-[10px] uppercase text-slate-400 mr-1">
                                {primaryTarget.kind}
                              </span>
                              {primaryTarget.name}
                              {extra > 0 && (
                                <span className="ml-1 text-xs text-slate-500">
                                  (+{extra} more)
                                </span>
                              )}
                            </>
                          ) : (
                            <span className="text-slate-400 italic">none</span>
                          )}
                        </td>
                        <td className="px-4 py-2 font-mono text-xs text-slate-600">
                          {c.mode}
                        </td>
                        <td className="px-4 py-2 text-slate-600">
                          {formatDateTime(c.startedAt)}
                        </td>
                        <td className="px-4 py-2 text-center">
                          {c.completedAt ? (
                            <span className="text-xs font-medium text-green-700">
                              COMPLETED
                            </span>
                          ) : (
                            <span className="text-xs font-medium text-amber-700">
                              IN PROGRESS
                            </span>
                          )}
                        </td>
                      </tr>
                    );
                  })}
              </tbody>
            </table>
          </div>
        )}
      </DataState>
    </div>
  );
}
