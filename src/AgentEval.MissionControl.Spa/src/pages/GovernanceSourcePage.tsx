import { useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { graphqlClient } from "@/lib/graphql-client";

import { EvidenceRecords } from "@/components/EvidenceRecords";
import { EvidenceQueryStatus as Status, type EvidenceOutcome as Outcome } from "@/components/EvidenceQueryStatus";

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
        decisions { line event kind target at identity assurance state reason current }
      }
    }
  }
`;

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
  decisions: { line: number; event: string; kind: string; target: string; at: string; identity: string;
    assurance: string; state: string | null; reason: string | null; current: boolean }[];
}
const button = "rounded border border-slate-300 bg-white px-3 py-2 text-sm disabled:opacity-50";
export function GovernanceSourcePage() {
  const { sourceId = "" } = useParams();
  const [search] = useSearchParams();
  const requested = search.get("view");
  const view = requested === "metrics" || requested === "spans" || requested === "fields" ? requested : "results";
  return <div className="space-y-5 min-w-0">
    <nav aria-label="Evidence views" className="flex flex-wrap gap-4">
      <Link to="/governance" className="text-accent-700 underline">Evidence sources</Link>
      <Link to={`/governance/requirements?source=${encodeURIComponent(sourceId)}`} className="text-accent-700 underline">Requirements for this source</Link>
      <Link to={`/governance/checkpoints?source=${encodeURIComponent(sourceId)}`} className="text-accent-700 underline">Checkpoints for this source</Link>
      {(["results", "metrics", "spans", "fields"] as const).map(tab => <Link key={tab}
        to={`?view=${tab}`} aria-current={view === tab ? "page" : undefined}
        className={view === tab ? "font-semibold" : "text-accent-700 underline"}>{tab[0].toUpperCase() + tab.slice(1)}</Link>)}
    </nav>
    {view === "results" ? <SourceResults key={sourceId} sourceId={sourceId} />
      : <EvidenceRecords key={`${sourceId}:${view}`} sourceId={sourceId} view={view} />}
  </div>;
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
      <h4 className="font-medium">Verified decision history</h4>
      <p className="text-sm text-slate-600">Override, adjudication and review events appear in file order. Timestamps are declarations and do not reorder decisions. Current events supply the effective state or review; earlier events remain visible. Unsealed events are excluded.</p>
      {data.decisions.length === 0 ? <p className="text-sm">No verified decisions for this result or its run.</p>
        : <ol className="space-y-3">{data.decisions.map(decision => <li key={decision.event} className="border rounded p-3 text-sm break-all">
          <p className="font-medium">Line {decision.line}: {decision.kind} — {decision.current ? "Current" : "Superseded"}</p>
          <dl>{[["Event", decision.event], ["Target", decision.target], ["Declared time", decision.at],
            ["Declared identity", decision.identity], ["Established assurance", decision.assurance],
            ["Replacement state", decision.state ?? "Not applicable"], ["Reason", decision.reason ?? "Not declared"]].map(([label, value]) =>
            <div key={label}><dt className="inline font-medium">{label}: </dt><dd className="inline">{value}</dd></div>)}</dl>
        </li>)}</ol>}
      <details><summary className="cursor-pointer font-medium">Sealed result JSON</summary>
        <pre className="mt-3 p-3 bg-slate-50 overflow-auto text-xs max-h-96">{data.sealedJson}</pre>
      </details>
    </>}
  </section>;
}
