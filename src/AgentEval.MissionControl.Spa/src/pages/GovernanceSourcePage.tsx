import { useState } from "react";
import { Link, useParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { graphqlClient } from "@/lib/graphql-client";

const RESULTS_QUERY = /* GraphQL */ `
  query GovernanceResults($sourceId: String!, $cursor: String) {
    evidenceResults(sourceId: $sourceId, pageSize: 25, cursor: $cursor) {
      state code
      data { indexInstanceId generation sourceKind verification fingerprint nextCursor
        items { resultId caseId trial outcome }
      }
    }
  }
`;
const DETAIL_QUERY = /* GraphQL */ `
  query GovernanceResultDetail($sourceId: String!, $resultId: String!, $at: String!) {
    evidenceResultDetail(sourceId: $sourceId, resultId: $resultId, at: $at) {
      state code
      data { sourceId indexInstanceId indexGeneration runHash snapshotDigest policyDigest at resultId
        sealedJson sealedState effectiveState overrideEvent unsealedEvents
        reviews { target status event }
        assurance { event shown }
      }
    }
  }
`;

interface Outcome<T> { state: string; code: string | null; data: T | null }
interface ResultsPage {
  indexInstanceId: string; generation: string; sourceKind: string; verification: string;
  fingerprint: string; nextCursor: string | null;
  items: { resultId: string; caseId: string | null; trial: string | null; outcome: string | null }[];
}
interface Detail {
  sourceId: string; indexInstanceId: string; indexGeneration: string; runHash: string;
  snapshotDigest: string; policyDigest: string; at: string; resultId: string;
  sealedJson: string; sealedState: string; effectiveState: string; overrideEvent: string | null;
  unsealedEvents: number; reviews: { target: string; status: string; event: string }[];
  assurance: { event: string; shown: string }[];
}
const button = "rounded border border-slate-300 bg-white px-3 py-2 text-sm disabled:opacity-50";
const messages: Record<string, string> = {
  EMPTY: "No indexed results in this page.",
  NOT_CONFIGURED: "This query capability is not configured on the host.",
  NOT_FOUND: "The selected evidence was not found.",
  INVALID_REQUEST: "The request is not valid for this evidence. Check the selection and UTC view time.",
  STALE_CURSOR: "The index changed. Restart from the first page.",
  RESOURCE_LIMIT: "The query exceeded the host's resource budget.",
  UNAVAILABLE: "Verified evidence is currently unavailable. Check the host and refresh.",
};

function Status({ fetching, error, outcome }: { fetching: boolean; error: boolean; outcome?: Outcome<unknown> }) {
  return <p role="status" className="text-sm" aria-live="polite">{fetching ? "Loading evidence…" : error
    ? "Connection failed. Refresh to try again." : outcome?.state === "AVAILABLE" ? "Evidence loaded."
    : outcome ? messages[outcome.state] ?? "Unsupported availability state." : "No response available."}</p>;
}

export function GovernanceSourcePage() {
  const { sourceId = "" } = useParams();
  // A route change starts a fresh traversal and clears the previous selection.
  return <SourceResults key={sourceId} sourceId={sourceId} />;
}

function SourceResults({ sourceId }: { sourceId: string }) {
  const [cursor, setCursor] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const results = useQuery({
    queryKey: ["governance", "results", sourceId, cursor],
    queryFn: ({ signal }) => graphqlClient.request<{ evidenceResults: Outcome<ResultsPage> }>({
      document: RESULTS_QUERY, variables: { sourceId, cursor }, signal,
    }), retry: false, staleTime: 0,
  });
  const outcome = results.data?.evidenceResults;
  const page = !results.isFetching && !results.isError &&
    (outcome?.state === "AVAILABLE" || outcome?.state === "EMPTY") ? outcome.data : null;
  function restart() { setSelected(null); setCursor(null); }
  return <section className="space-y-5 max-w-6xl">
    <Link to="/governance" className="text-accent-700 underline">Back to evidence sources</Link>
    <h2 className="text-2xl font-semibold">Source results</h2>
    <p className="font-mono text-sm break-all">{sourceId}</p>
    <p className="text-sm text-slate-600">Indexed observations are shown below. Open an AEF result to re-verify its sealed content and effective state.</p>
    <div className="flex gap-3">
      <button className={button} disabled={results.isFetching} onClick={() => { setSelected(null); void results.refetch(); }}>Refresh results</button>
      <button className={button} disabled={cursor === null || results.isFetching} onClick={restart}>First page</button>
      <button className={button} disabled={!page?.nextCursor || results.isFetching} onClick={() => { setSelected(null); setCursor(page?.nextCursor ?? null); }}>Next page</button>
    </div>
    <Status fetching={results.isFetching} error={results.isError} outcome={outcome} />
    {page && <>
      <dl className="text-sm break-all grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
        <dt>Kind</dt><dd>{page.sourceKind}</dd><dt>Indexed integrity</dt><dd>{page.verification}</dd>
        <dt>Index instance</dt><dd className="font-mono">{page.indexInstanceId}</dd>
        <dt>Generation</dt><dd className="font-mono">{page.generation}</dd>
        <dt>Fingerprint</dt><dd className="font-mono">{page.fingerprint}</dd>
      </dl>
      <div className="overflow-x-auto rounded border bg-white"><table className="w-full text-sm text-left">
        <caption className="p-3 text-left">Results — {page.items.length} on this page</caption>
        <thead className="bg-slate-100"><tr>{["Result", "Case", "Trial", "Indexed outcome"].map(h => <th className="p-3" key={h}>{h}</th>)}</tr></thead>
        <tbody>{page.items.map(row => <tr key={row.resultId} className="border-t">
          <td className="p-3 break-all">{page.sourceKind === "aef"
            ? <button className="text-accent-700 underline" onClick={() => setSelected(row.resultId)}>{row.resultId}</button> : row.resultId}</td>
          <td className="p-3">{row.caseId ?? "No case (aggregate)"}</td><td className="p-3">{row.trial ?? "Not declared"}</td>
          <td className="p-3">{row.outcome ?? "Not measured"}</td>
        </tr>)}</tbody>
      </table></div>
      {page.sourceKind !== "aef" && <p className="text-sm">Verified sealed-result detail is available for AEF sources only.</p>}
      {selected && page.sourceKind === "aef" && <ResultDetail key={selected} sourceId={sourceId} resultId={selected}
        indexInstance={page.indexInstanceId} indexGeneration={page.generation} />}
    </>}
  </section>;
}

function ResultDetail({ sourceId, resultId, indexInstance, indexGeneration }: {
  sourceId: string; resultId: string; indexInstance: string; indexGeneration: string;
}) {
  const [at, setAt] = useState(() => new Date().toISOString());
  const [draftAt, setDraftAt] = useState(at);
  const detail = useQuery({
    queryKey: ["governance", "detail", sourceId, resultId, at],
    queryFn: ({ signal }) => graphqlClient.request<{ evidenceResultDetail: Outcome<Detail> }>({
      document: DETAIL_QUERY, variables: { sourceId, resultId, at }, signal,
    }), retry: false, staleTime: 0,
  });
  const outcome = detail.data?.evidenceResultDetail;
  const data = !detail.isFetching && !detail.isError && outcome?.state === "AVAILABLE" ? outcome.data : null;
  return <section className="rounded-lg border bg-white p-5 space-y-4" aria-label="Verified result detail">
    <h3 className="font-semibold text-lg">Verified result: {resultId}</h3>
    <form className="flex flex-wrap items-end gap-3" onSubmit={event => { event.preventDefault(); setAt(draftAt.trim()); }}>
      <label className="text-sm flex flex-col gap-1">UTC view time
        <input className="rounded border p-2 font-mono" value={draftAt} onChange={event => setDraftAt(event.target.value)} required />
      </label>
      <button className={button}>Apply time</button>
      <button type="button" className={button} disabled={detail.isFetching} onClick={() => void detail.refetch()}>Re-verify</button>
    </form>
    <Status fetching={detail.isFetching} error={detail.isError} outcome={outcome} />
    {data && <>
      {(data.indexInstanceId !== indexInstance || data.indexGeneration !== indexGeneration) &&
        <p role="alert" className="text-amber-800">The index changed since the results list was loaded. This detail uses the revision shown below; refresh the results to align them.</p>}
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm break-all">
        {[["Sealed state", data.sealedState], ["Effective state", data.effectiveState], ["View time", data.at],
          ["Override event", data.overrideEvent ?? "None"], ["Index instance", data.indexInstanceId],
          ["Index generation", data.indexGeneration], ["Run hash", data.runHash], ["Snapshot digest", data.snapshotDigest],
          ["Policy digest", data.policyDigest], ["Unsealed events", String(data.unsealedEvents)]].map(([label, value]) =>
          <div className="contents" key={label}><dt className="font-medium">{label}</dt><dd className="font-mono">{value}</dd></div>)}
      </dl>
      <h4 className="font-medium">Reviews</h4>
      {data.reviews.length ? <ul className="text-sm space-y-2">{data.reviews.map((review, i) =>
        <li key={i} className="break-all">{review.target}: {review.status} — event {review.event}</li>)}</ul> : <p className="text-sm">No reviews.</p>}
      <h4 className="font-medium">Established event assurance</h4>
      {data.assurance.length ? <ul className="text-sm space-y-2">{data.assurance.map((assurance, i) =>
        <li key={i} className="break-all">{assurance.event}: {assurance.shown}</li>)}</ul> : <p className="text-sm">No event assurance reported.</p>}
      <details><summary className="cursor-pointer font-medium">Sealed result JSON</summary>
        <pre className="mt-3 p-3 bg-slate-50 overflow-auto text-xs max-h-96">{data.sealedJson}</pre>
      </details>
    </>}
  </section>;
}
