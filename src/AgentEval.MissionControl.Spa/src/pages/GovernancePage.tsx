import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { graphqlClient } from "@/lib/graphql-client";
import { graphqlUrl } from "@/lib/env";

const EVIDENCE_SOURCES_QUERY = /* GraphQL */ `
  query GovernanceSources($rootId: String, $cursor: String) {
    evidenceSources(rootId: $rootId, pageSize: 25, cursor: $cursor) {
      state code
      data {
        indexInstanceId generation nextCursor
        items { sourceId rootId relativePath kind verification fingerprint indexedGeneration }
      }
    }
  }
`;

interface Source {
  sourceId: string; rootId: string; relativePath: string; kind: string;
  verification: string; fingerprint: string; indexedGeneration: string;
}
interface SourcesResponse {
  evidenceSources: {
    state: string; code: string | null;
    data: { indexInstanceId: string; generation: string; nextCursor: string | null; items: Source[] } | null;
  };
}

const availability: Record<string, string> = {
  AVAILABLE: "Connected — evidence sources available",
  EMPTY: "Connected — no evidence sources match this selection",
  NOT_CONFIGURED: "Evidence queries are not configured on this host. Configure the evidence index and logical roots on the server.",
  NOT_FOUND: "The requested evidence is no longer indexed.",
  INVALID_REQUEST: "The root filter or continuation was rejected. Check the logical root ID or restart from the first page.",
  STALE_CURSOR: "The index changed while browsing. Restart from the first page to view its current contents.",
  RESOURCE_LIMIT: "The host's query budget was reached. Narrow the selection or try again later.",
  UNAVAILABLE: "The evidence index is unavailable. Check the host and refresh when it is ready.",
};

export function GovernancePage() {
  const [draftRoot, setDraftRoot] = useState("");
  const [rootId, setRootId] = useState("");
  const [cursor, setCursor] = useState<string | null>(null);
  const sources = useQuery({
    queryKey: ["governance", "sources", rootId, cursor],
    queryFn: ({ signal }) => graphqlClient.request<SourcesResponse>({
      document: EVIDENCE_SOURCES_QUERY, variables: { rootId: rootId || null, cursor }, signal,
    }),
    retry: false,
    staleTime: 0,
  });
  const outcome = sources.data?.evidenceSources;
  // Do not present cached evidence as current while refreshing or after a failure.
  const page = !sources.isFetching && !sources.isError &&
    (outcome?.state === "AVAILABLE" || outcome?.state === "EMPTY") ? outcome.data : null;
  const button = "rounded border border-slate-300 bg-white px-3 py-2 text-sm disabled:opacity-50";

  return <section className="space-y-6 max-w-6xl">
    <div>
      <h2 className="text-2xl font-semibold">Governance evidence</h2>
      <p className="text-slate-600 mt-2">Browse evidence from the host's configured workspace. Source integrity and evaluation outcomes are separate facts.</p>
    </div>
    <div className="rounded-lg border border-slate-200 bg-white p-5 space-y-4">
      <h3 className="font-semibold">Workspace connection</h3>
      <p className="text-sm text-slate-600">API host: <span className="font-mono">{new URL(graphqlUrl).origin}</span></p>
      <form className="flex flex-wrap items-end gap-3" onSubmit={event => {
        event.preventDefault(); setRootId(draftRoot.trim()); setCursor(null);
      }}>
        <label className="text-sm flex flex-col gap-1" htmlFor="governance-root">
          Logical root ID (blank for all roots)
          <input id="governance-root" value={draftRoot} onChange={event => setDraftRoot(event.target.value)}
            className="rounded border border-slate-300 px-3 py-2" autoComplete="off" />
        </label>
        <button className={button} type="submit">Apply filter</button>
        <button className={button} type="button" disabled={sources.isFetching} onClick={() => void sources.refetch()}>Refresh</button>
      </form>
      <div role="status" aria-live="polite" className="text-sm">
        {sources.isFetching ? "Connecting to the evidence index…" : sources.isError
          ? "Connection failed. Check host availability and access configuration, then refresh."
          : outcome ? availability[outcome.state] ?? "The host returned an unsupported availability state."
          : "No query response is available."}
      </div>
      {page && <dl className="text-sm text-slate-600 break-all">
        <dt className="font-medium">Index instance</dt><dd className="font-mono">{page.indexInstanceId}</dd>
        <dt className="font-medium mt-2">Generation</dt><dd className="font-mono">{page.generation}</dd>
      </dl>}
    </div>
    {page && <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white">
      <table className="w-full text-sm text-left">
        <caption className="p-4 text-left font-medium">Evidence sources — {page.items.length} on this page</caption>
        <thead className="bg-slate-100"><tr>{["Source", "Root", "Kind", "Integrity", "Indexed generation"].map(label => <th key={label} className="p-3">{label}</th>)}</tr></thead>
        <tbody>{page.items.map(source => <tr key={source.sourceId} className="border-t border-slate-100">
          <td className="p-3 break-all"><div>{source.relativePath}</div><div className="text-xs text-slate-500 font-mono">{source.sourceId}</div></td>
          <td className="p-3 break-all">{source.rootId}</td><td className="p-3">{source.kind}</td>
          <td className="p-3">{source.verification}</td><td className="p-3 font-mono">{source.indexedGeneration}</td>
        </tr>)}</tbody>
      </table>
    </div>}
    <div className="flex gap-3">
      <button className={button} disabled={cursor === null || sources.isFetching} onClick={() => setCursor(null)}>First page</button>
      <button className={button} disabled={!page?.nextCursor || sources.isFetching} onClick={() => setCursor(page?.nextCursor ?? null)}>Next page</button>
    </div>
  </section>;
}
