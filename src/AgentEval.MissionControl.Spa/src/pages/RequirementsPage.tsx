import { useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { graphqlClient } from "@/lib/graphql-client";
import { EvidenceQueryStatus, type EvidenceOutcome } from "@/components/EvidenceQueryStatus";

const REQUIREMENTS_QUERY = /* GraphQL */ `
  query GovernanceRequirements($subjectRef: String!, $subjectVersion: String!, $sourceIds: [String!]!, $at: String!) {
    governanceStatus(subjectRef: $subjectRef, subjectVersion: $subjectVersion, sourceIds: $sourceIds, at: $at) {
      state code
      data { catalogueDigest policyDigest at subject { ref version } selectedRunHashes
        evidence { runId runHash overlayDigest }
        requirements { id baseStatus effectiveStatus status diagnostics
          observations { runHash resultId lane metric path sealedState effectiveState overrideEvent value endedAt }
          reviews { runHash target status event }
        }
      }
    }
  }
`;
interface Selection { subjectRef: string; subjectVersion: string; sourceIds: string[]; at: string }
interface Report {
  catalogueDigest: string; policyDigest: string; at: string; subject: { ref: string; version: string };
  selectedRunHashes: string[]; evidence: { runId: string; runHash: string; overlayDigest: string }[];
  requirements: {
    id: string; baseStatus: string; effectiveStatus: string; status: string; diagnostics: string[];
    observations: { runHash: string; resultId: string; lane: string; metric: string; path: string;
      sealedState: string; effectiveState: string; overrideEvent: string | null; value: number | null; endedAt: string }[];
    reviews: { runHash: string; target: string; status: string; event: string }[];
  }[];
}

export function RequirementsPage() {
  const [search] = useSearchParams();
  const [subjectRef, setSubjectRef] = useState("");
  const [subjectVersion, setSubjectVersion] = useState("");
  const [sources, setSources] = useState(() => search.getAll("source").join("\n"));
  const [at, setAt] = useState(() => new Date().toISOString());
  const [selection, setSelection] = useState<Selection | null>(null);
  const query = useQuery({
    queryKey: ["governance", "requirements", selection],
    queryFn: ({ signal }) => graphqlClient.request<{ governanceStatus: EvidenceOutcome<Report> }>({
      document: REQUIREMENTS_QUERY, variables: selection!, signal,
    }), enabled: selection !== null, retry: false, staleTime: 0,
  });
  const outcome = query.data?.governanceStatus;
  const report = !query.isFetching && !query.isError && outcome?.state === "AVAILABLE" ? outcome.data : null;
  const button = "rounded border border-slate-300 bg-white px-3 py-2 text-sm disabled:opacity-50";
  return <section className="space-y-5 max-w-6xl">
    <Link to="/governance" className="text-accent-700 underline">Evidence sources</Link>
    <h2 className="text-2xl font-semibold">Requirements status</h2>
    <p className="text-sm text-slate-600">Resolve the host's configured catalogue for an exact subject, version, evidence selection and UTC time. No latest run is selected automatically.</p>
    <form className="rounded border bg-white p-4 space-y-4" onSubmit={event => {
      event.preventDefault();
      const next = { subjectRef: subjectRef.trim(), subjectVersion: subjectVersion.trim(),
        sourceIds: [...new Set(sources.split(/\r?\n/).map(value => value.trim()).filter(Boolean))], at: at.trim() };
      if (JSON.stringify(next) === JSON.stringify(selection)) void query.refetch();
      else setSelection(next);
    }}>
      <div className="flex flex-wrap gap-4">
        <label className="text-sm flex flex-col gap-1">Subject reference<input required maxLength={1024} className="rounded border p-2" value={subjectRef} onChange={e => setSubjectRef(e.target.value)} /></label>
        <label className="text-sm flex flex-col gap-1">Subject version<input required maxLength={1024} className="rounded border p-2" value={subjectVersion} onChange={e => setSubjectVersion(e.target.value)} /></label>
        <label className="text-sm flex flex-col gap-1">UTC view time<input required className="rounded border p-2 font-mono" value={at} onChange={e => setAt(e.target.value)} /></label>
      </div>
      <label className="text-sm flex flex-col gap-1">Evidence source IDs (one per line)
        <textarea rows={3} className="rounded border p-2 font-mono" value={sources} onChange={e => setSources(e.target.value)} />
      </label>
      <p className="text-sm text-slate-600">An empty selection evaluates without run evidence. Source IDs come from the evidence browser; only configured AEF sources can supply verified requirement evidence.</p>
      <button className={button} disabled={query.isFetching}>Resolve requirements</button>
    </form>
    {selection && <>
      <p className="text-sm break-all">Submitted selection: {selection.subjectRef} / {selection.subjectVersion}; {selection.sourceIds.length} source IDs; {selection.at}. Form edits apply when submitted.</p>
      <EvidenceQueryStatus fetching={query.isFetching} error={query.isError} outcome={outcome}
        messages={{ STALE_CURSOR: "The index changed during evidence selection. Resolve requirements again to use a consistent revision." }} />
    </>}
    {report && <section aria-label="Requirement report" className="space-y-4">
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm break-all">
        <dt>Subject</dt><dd>{report.subject.ref} / {report.subject.version}</dd><dt>View time</dt><dd>{report.at}</dd>
        <dt>Catalogue digest</dt><dd className="font-mono">{report.catalogueDigest}</dd>
        <dt>Policy digest</dt><dd className="font-mono">{report.policyDigest}</dd>
      </dl>
      <p className="text-sm">Base, effective and final statuses are reported separately. A waiver does not change the sealed measurement.</p>
      {report.requirements.length === 0 && <p>No requirements in the configured catalogue.</p>}
      <div className="overflow-x-auto border rounded bg-white"><table className="w-full text-left text-sm">
        <caption className="p-3 text-left">{report.requirements.length} requirements</caption>
        <thead className="bg-slate-100"><tr>{["Requirement", "Base", "Effective", "Final", "Evidence and reasons"].map(h => <th key={h} className="p-3">{h}</th>)}</tr></thead>
        <tbody>{report.requirements.map(rule => <tr key={rule.id} className="border-t">
          <td className="p-3 align-top break-all">{rule.id}</td><td className="p-3 align-top">{rule.baseStatus}</td>
          <td className="p-3 align-top">{rule.effectiveStatus}</td><td className="p-3 align-top">{rule.status}</td>
          <td className="p-3"><details><summary className="cursor-pointer">{rule.observations.length} observations; {rule.diagnostics.length} diagnostics</summary>
            {rule.diagnostics.length > 0 && <ul className="list-disc pl-5 my-2">{rule.diagnostics.map((reason, i) => <li className="break-all" key={i}>{reason}</li>)}</ul>}
            {rule.observations.length === 0 && <p className="my-2">No selected observations.</p>}
            {rule.observations.map((observation, i) => <dl key={i} className="my-3 border-t pt-2 break-all">
              {[["Run hash", observation.runHash], ["Result", observation.resultId], ["Lane", observation.lane],
                ["Metric", observation.metric], ["Path", observation.path], ["Sealed state", observation.sealedState],
                ["Effective state", observation.effectiveState], ["Override event", observation.overrideEvent],
                ["Value", observation.value], ["Ended at", observation.endedAt]].map(([label, value]) =>
                <div key={String(label)}><dt className="inline font-medium">{label}: </dt><dd className="inline">{value == null ? "Not declared" : String(value)}</dd></div>)}
            </dl>)}
            <p className="font-medium mt-2">Reviews</p>
            {rule.reviews.length === 0 ? <p>No reviews.</p> : rule.reviews.map((review, i) => <p className="break-all" key={i}>{review.runHash} / {review.target}: {review.status} — {review.event}</p>)}
          </details></td>
        </tr>)}</tbody>
      </table></div>
      <details><summary className="cursor-pointer font-medium">Selected evidence identities ({report.evidence.length})</summary>
        {report.evidence.map((evidence, i) => <dl key={i} className="my-3 text-sm break-all">
          <dt>Run ID</dt><dd>{evidence.runId}</dd><dt>Run hash</dt><dd>{evidence.runHash}</dd><dt>Overlay digest</dt><dd>{evidence.overlayDigest}</dd>
        </dl>)}
        <p className="text-sm break-all">Selected run hashes: {report.selectedRunHashes.join(", ") || "None"}</p>
      </details>
    </section>}
  </section>;
}
