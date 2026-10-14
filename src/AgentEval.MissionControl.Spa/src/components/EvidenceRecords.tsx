import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { graphqlClient } from "@/lib/graphql-client";
import { EvidenceQueryStatus, type EvidenceOutcome } from "./EvidenceQueryStatus";

const METRICS_QUERY = /* GraphQL */ `
  query GovernanceMetrics($sourceId: String!, $cursor: String) {
    page: evidenceMetrics(sourceId: $sourceId, pageSize: 25, cursor: $cursor) {
      state code
      data { indexInstanceId generation sourceKind verification fingerprint nextCursor
        items { resultId metricId ordinal score scoreText passed provenance }
      }
    }
  }
`;
const SPANS_QUERY = /* GraphQL */ `
  query GovernanceSpans($sourceId: String!, $cursor: String) {
    page: evidenceSpans(sourceId: $sourceId, pageSize: 25, cursor: $cursor) {
      state code
      data { indexInstanceId generation sourceKind verification fingerprint nextCursor
        items { reportOrdinal groupId groupingRevision groupingBasis mappingRevision traceId spanId parentSpanId
          startNanos endNanos resourceIndex scopeIndex spanIndex }
      }
    }
  }
`;
const FIELDS_QUERY = /* GraphQL */ `
  query GovernanceFields($sourceId: String!, $cursor: String) {
    page: evidenceFields(sourceId: $sourceId, pageSize: 25, cursor: $cursor) {
      state code
      data { indexInstanceId generation sourceKind verification fingerprint nextCursor
        items { reportOrdinal fieldName state textValue integerValue sourcesJson }
      }
    }
  }
`;

const views = {
  metrics: { title: "Metric occurrences", query: METRICS_QUERY,
    description: "Each occurrence retains its score, exceptional score text, nullable pass flag and provenance. No pass rate is inferred.",
    columns: [["resultId", "Result"], ["metricId", "Metric"], ["ordinal", "Occurrence"], ["score", "Finite score"],
      ["scoreText", "Exceptional score"], ["passed", "Passed"], ["provenance", "Provenance"]] },
  spans: { title: "Observed spans", query: SPANS_QUERY,
    description: "Observation timestamps are exact nanosecond strings. Grouping and mapping metadata describe interpretation, not evaluation success.",
    columns: [["reportOrdinal", "Report"], ["groupId", "Group"], ["groupingRevision", "Grouping revision"],
      ["groupingBasis", "Grouping basis"], ["mappingRevision", "Mapping revision"], ["traceId", "Trace"],
      ["spanId", "Span"], ["parentSpanId", "Parent span"], ["startNanos", "Start (ns)"], ["endNanos", "End (ns)"],
      ["resourceIndex", "Resource index"], ["scopeIndex", "Scope index"], ["spanIndex", "Span index"]] },
  fields: { title: "Normalized fields", query: FIELDS_QUERY,
    description: "Field conflicts remain unresolved. Expand source provenance to inspect the original declarations; absent values are not consensus.",
    columns: [["reportOrdinal", "Report"], ["fieldName", "Field"], ["state", "State"], ["textValue", "Text value"],
      ["integerValue", "Integer value"], ["sourcesJson", "Source provenance"]] },
} as const;
export type EvidenceView = keyof typeof views;
interface RecordsPage {
  indexInstanceId: string; generation: string; sourceKind: string; verification: string;
  fingerprint: string; nextCursor: string | null;
  items: Record<string, string | number | boolean | null>[];
}

export function EvidenceRecords({ sourceId, view }: { sourceId: string; view: EvidenceView }) {
  const [cursor, setCursor] = useState<string | null>(null);
  const config = views[view];
  const query = useQuery({
    queryKey: ["governance", view, sourceId, cursor],
    queryFn: ({ signal }) => graphqlClient.request<{ page: EvidenceOutcome<RecordsPage> }>({
      document: config.query, variables: { sourceId, cursor }, signal,
    }), retry: false, staleTime: 0,
  });
  const outcome = query.data?.page;
  const page = !query.isFetching && !query.isError &&
    (outcome?.state === "AVAILABLE" || outcome?.state === "EMPTY") ? outcome.data : null;
  const button = "rounded border border-slate-300 bg-white px-3 py-2 text-sm disabled:opacity-50";
  return <section className="space-y-4 max-w-full min-w-0" aria-label={config.title}>
    <h2 className="text-2xl font-semibold">{config.title}</h2>
    <p className="font-mono text-sm break-all">{sourceId}</p>
    <p className="text-sm text-slate-600">{config.description}</p>
    <div className="flex gap-3">
      <button className={button} disabled={query.isFetching} onClick={() => void query.refetch()}>Refresh records</button>
      <button className={button} disabled={cursor === null || query.isFetching} onClick={() => setCursor(null)}>First page</button>
      <button className={button} disabled={!page?.nextCursor || query.isFetching} onClick={() => setCursor(page?.nextCursor ?? null)}>Next page</button>
    </div>
    <EvidenceQueryStatus fetching={query.isFetching} error={query.isError} outcome={outcome} />
    {page && <>
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 text-sm break-all">
        <dt>Kind</dt><dd>{page.sourceKind}</dd><dt>Indexed integrity</dt><dd>{page.verification}</dd>
        <dt>Index instance</dt><dd>{page.indexInstanceId}</dd><dt>Generation</dt><dd>{page.generation}</dd>
        <dt>Fingerprint</dt><dd>{page.fingerprint}</dd>
      </dl>
      <div className="overflow-x-auto border rounded bg-white"><table className="w-full text-sm text-left">
        <caption className="p-3 text-left">{config.title} — {page.items.length} on this page</caption>
        <thead className="bg-slate-100"><tr>{config.columns.map(([key, label]) => <th className="p-3" key={key}>{label}</th>)}</tr></thead>
        <tbody>{page.items.map((row, index) => <tr key={index} className="border-t">{config.columns.map(([key]) =>
          <td key={key} className="p-3 align-top break-all">{key === "sourcesJson" && row[key] != null
            ? <details><summary className="cursor-pointer">Source provenance</summary><pre className="text-xs whitespace-pre-wrap max-h-64 overflow-auto">{String(row[key])}</pre></details>
            : row[key] == null ? "Not declared" : typeof row[key] === "boolean" ? row[key] ? "True" : "False" : String(row[key])}</td>)}</tr>)}</tbody>
      </table></div>
    </>}
  </section>;
}
