import { useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useQuery } from "@tanstack/react-query";
import { graphqlClient } from "@/lib/graphql-client";
import { EvidenceQueryStatus, type EvidenceOutcome } from "@/components/EvidenceQueryStatus";

const CHECKPOINTS_QUERY = /* GraphQL */ `
  query GovernanceCheckpoints {
    checkpoints { state code data { id checkpointId subjectRef subjectVersion declaredState declaredOutcome manifestDigest } }
  }
`;
const CHECKPOINT_EXPLANATION_QUERY = /* GraphQL */ `
  query GovernanceCheckpointExplanation($checkpointId: String!, $sourceIds: [String!]!, $at: String!) {
    checkpointExplanation(checkpointId: $checkpointId, sourceIds: $sourceIds, at: $at) {
      state code data { checkpointId sourceIds indexInstanceId indexGeneration explanation {
        manifestDigest policyDigest requestedAt evaluatedAt manifestJson manifestProblems
        problems { path code }
        lanes { lane ruleKind evidenceStatus subjectVersion oldestClosedAt axes readsUnknown problems { path code } }
        signedBy anchors envelopePresent signatureChecked envelopeResult signatures { keyId result identity }
      } }
    }
  }
`;
interface Descriptor { id: string; checkpointId: string; subjectRef: string; subjectVersion: string;
  declaredState: string; declaredOutcome: string | null; manifestDigest: string }
interface Problem { path: string; code: string }
interface Explanation {
  manifestDigest: string; policyDigest: string; requestedAt: string; evaluatedAt: string; manifestJson: string;
  manifestProblems: string[]; problems: Problem[];
  lanes: { lane: string; ruleKind: string; evidenceStatus: string | null; subjectVersion: string | null;
    oldestClosedAt: string | null; axes: string[]; readsUnknown: boolean; problems: Problem[] }[];
  signedBy: string[]; anchors: string[]; envelopePresent: boolean; signatureChecked: boolean; envelopeResult: string | null;
  signatures: { keyId: string; result: string; identity: string | null }[];
}
interface Selection { checkpointId: string; sourceIds: string[]; at: string }
interface Report { checkpointId: string; sourceIds: string[]; indexInstanceId: string | null;
  indexGeneration: string | null; explanation: Explanation }
type ObjectValue = Record<string, unknown>;
function object(value: unknown): ObjectValue { return value !== null && typeof value === "object" && !Array.isArray(value) ? value as ObjectValue : {}; }
function text(value: unknown, fallback = "Not declared"): string { return typeof value === "string" ? value : fallback; }
function objects(value: unknown): ObjectValue[] { return Array.isArray(value) ? value.map(object) : []; }
function strings(value: unknown): string[] { return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : []; }
function manifest(json: string): ObjectValue | null { try { return object(JSON.parse(json)); } catch { return null; } }
function parameter(value: unknown): string {
  if (typeof value === "string" || typeof value === "boolean") return String(value);
  if (typeof value === "number") return Number.isInteger(value) && !Number.isSafeInteger(value)
    ? "See exact integer in original manifest" : String(value);
  if (Array.isArray(value) && value.every(item => typeof item === "string")) return value.join(", ");
  return value == null ? "Not declared" : "See original manifest";
}
function RuleParameters({ rule }: { rule: ObjectValue }) {
  return <details className="mt-2"><summary className="cursor-pointer">Rule parameters</summary>
    <dl className="break-all">{Object.entries(rule).filter(([key]) => key !== "kind").map(([key, value]) => <div key={key}>
      <dt className="font-medium">{key}</dt>
      <dd>{value !== null && typeof value === "object" && !Array.isArray(value)
        ? Object.entries(object(value)).map(([name, item]) => <p key={name}>{name}: {parameter(item)}</p>) : parameter(value)}</dd>
    </div>)}</dl>
  </details>;
}
function Problems({ problems }: { problems: Problem[] }) {
  return problems.length ? <ul className="list-disc pl-5">{problems.map((problem, i) =>
    <li key={i} className="break-all">{problem.path}: {problem.code}</li>)}</ul> : <p>No problems reported.</p>;
}

export function CheckpointsPage() {
  const [search] = useSearchParams();
  const [checkpointId, setCheckpointId] = useState("");
  const [sources, setSources] = useState(() => search.getAll("source").join("\n"));
  const [at, setAt] = useState(() => new Date().toISOString());
  const [selection, setSelection] = useState<Selection | null>(null);
  const list = useQuery({ queryKey: ["governance", "checkpoints"],
    queryFn: ({ signal }) => graphqlClient.request<{ checkpoints: EvidenceOutcome<Descriptor[]> }>({ document: CHECKPOINTS_QUERY, signal }),
    retry: false, staleTime: 0 });
  const query = useQuery({ queryKey: ["governance", "checkpoint-explanation", selection],
    queryFn: ({ signal }) => graphqlClient.request<{ checkpointExplanation: EvidenceOutcome<Report> }>({
      document: CHECKPOINT_EXPLANATION_QUERY, variables: selection!, signal,
    }), enabled: selection !== null, retry: false, staleTime: 0 });
  const listed = list.data?.checkpoints;
  const choices = !list.isFetching && !list.isError && listed?.state === "AVAILABLE" ? listed.data ?? [] : [];
  const outcome = query.data?.checkpointExplanation;
  const report = !query.isFetching && !query.isError && outcome?.state === "AVAILABLE" ? outcome.data : null;
  const button = "rounded border border-slate-300 bg-white px-3 py-2 text-sm disabled:opacity-50";
  return <section className="space-y-5 max-w-6xl">
    <Link to="/governance" className="text-accent-700 underline">Evidence sources</Link>
    <h2 className="text-2xl font-semibold">Checkpoint evidence</h2>
    <p className="text-sm text-slate-600">Inspect a configured checkpoint against explicitly selected evidence. Verification does not create a release decision or grant approval.</p>
    <EvidenceQueryStatus fetching={list.isFetching} error={list.isError} outcome={listed}
      messages={{ EMPTY: "No checkpoints are configured.", NOT_CONFIGURED: "Checkpoint queries are not configured on this host." }} />
    <button className={button} disabled={list.isFetching} onClick={() => void list.refetch()}>Refresh checkpoint list</button>
    <form className="rounded border bg-white p-4 space-y-4" onSubmit={event => {
      event.preventDefault();
      if (!choices.some(choice => choice.id === checkpointId)) return;
      const next = { checkpointId, sourceIds: [...new Set(sources.split(/\r?\n/).map(id => id.trim()).filter(Boolean))], at: at.trim() };
      if (JSON.stringify(next) === JSON.stringify(selection)) void query.refetch(); else setSelection(next);
    }}>
      <label className="text-sm flex flex-col gap-1">Configured checkpoint
        <select required className="rounded border p-2" value={checkpointId} onChange={event => setCheckpointId(event.target.value)}>
          <option value="">Select a checkpoint</option>
          {choices.map(choice => <option key={choice.id} value={choice.id}>{choice.id} — {choice.subjectRef} / {choice.subjectVersion} (declared {choice.declaredState})</option>)}
        </select>
      </label>
      <label className="text-sm flex flex-col gap-1">Evidence source IDs (one per line)
        <textarea rows={3} className="rounded border p-2 font-mono" value={sources} onChange={event => setSources(event.target.value)} />
      </label>
      <p className="text-sm text-slate-600">Use source IDs from the evidence browser, including comparison baselines. An empty selection checks without run evidence; no runs are discovered automatically.</p>
      <label className="text-sm flex flex-col gap-1">UTC view time
        <input required className="rounded border p-2 font-mono" value={at} onChange={event => setAt(event.target.value)} />
      </label>
      <button className={button} disabled={query.isFetching || !choices.some(choice => choice.id === checkpointId)}>Explain checkpoint</button>
    </form>
    {selection && <>
      <p className="text-sm break-all">Submitted: {selection.checkpointId}; {selection.sourceIds.length} source IDs; {selection.at}. Form edits apply on submission.</p>
      <EvidenceQueryStatus fetching={query.isFetching} error={query.isError} outcome={outcome}
        messages={{ STALE_CURSOR: "The index changed during selection. Explain the checkpoint again to use a consistent revision." }} />
    </>}
    {report && <CheckpointReport report={report} />}
  </section>;
}

function CheckpointReport({ report }: { report: Report }) {
  const explanation = report.explanation;
  const declared = manifest(explanation.manifestJson);
  const decision = object(declared?.decision);
  const lanes = objects(declared?.lanes);
  return <section aria-label="Checkpoint explanation" className="space-y-5">
    <h3 className="text-lg font-semibold">Declared checkpoint</h3>
    {!declared && <p role="alert">The returned declaration could not be read. See its original JSON below.</p>}
    <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm break-all">
      {[["Configured ID", report.checkpointId], ["Checkpoint ID", text(declared?.checkpointId)],
        ["Subject", text(object(declared?.subject).ref)], ["Version", text(object(declared?.subject).version)],
        ["Declared state", text(declared?.state)], ["Declared outcome", text(declared?.outcome)],
        ["Requested time", explanation.requestedAt], ["Evaluation time", explanation.evaluatedAt],
        ["Manifest digest", explanation.manifestDigest], ["Policy digest", explanation.policyDigest],
        ["Index instance", report.indexInstanceId ?? "No indexed selection"], ["Index generation", report.indexGeneration ?? "No indexed selection"]].map(([label, value]) =>
        <div key={label} className="contents"><dt className="font-medium">{label}</dt><dd className="font-mono">{value}</dd></div>)}
    </dl>
    {explanation.requestedAt !== explanation.evaluatedAt && <p className="text-sm">This checkpoint uses its recorded evaluation time, shown above. The requested time does not replace that recorded input.</p>}
    <p className="text-sm break-all">Selected source IDs: {report.sourceIds.join(", ") || "None"}</p>
    <h4 className="font-medium">Recorded decision reasons</h4>
    {strings(decision.reasons).length ? <ul className="list-disc pl-5 text-sm">{strings(decision.reasons).map((reason, i) => <li key={i} className="break-all">{reason}</li>)}</ul> : <p className="text-sm">No recorded reasons.</p>}
    <h3 className="text-lg font-semibold">Recomputed lane evidence</h3>
    <p className="text-sm text-slate-600">These are evidence-rule results, separate from the declared checkpoint decision. Missing evidence and unknown rules do not become passes.</p>
    <div className="overflow-x-auto border rounded bg-white"><table className="w-full text-sm text-left">
      <thead className="bg-slate-100"><tr>{["Lane / rule", "Evidence", "Recorded lane status", "Version / oldest close", "Explanation"].map(label => <th key={label} className="p-3">{label}</th>)}</tr></thead>
      <tbody>{explanation.lanes.map(lane => <tr key={lane.lane} className="border-t">
        <td className="p-3 align-top">{lane.lane} / {lane.ruleKind}
          <RuleParameters rule={object(lanes.find(item => item.lane === lane.lane)?.rule)} /></td>
        <td className="p-3 align-top">{lane.evidenceStatus ?? "No result"}{lane.readsUnknown && <p>Contains unknown semantics</p>}</td>
        <td className="p-3 align-top">{text(objects(decision.lanes).find(item => item.lane === lane.lane)?.status)}</td>
        <td className="p-3 align-top break-all">{lane.subjectVersion ?? "Not declared"}<br />{lane.oldestClosedAt ?? "Not declared"}</td>
        <td className="p-3 align-top break-all"><Problems problems={lane.problems} />
          {lane.axes.length > 0 && <p>Incomparable axes: {lane.axes.join(", ")}</p>}
          <details className="mt-2"><summary className="cursor-pointer">Declared evidence references</summary>
            {objects(lanes.find(item => item.lane === lane.lane)?.runs).map((run, i) => <p key={i}>{text(run.runId)} / {text(run.runHash)}</p>)}
            {(() => { const baseline = object(object(lanes.find(item => item.lane === lane.lane)?.rule).baseline);
              return typeof baseline.runId === "string" ? <p>Baseline: {baseline.runId} / {text(baseline.runHash)}</p> : null; })()}
          </details>
        </td>
      </tr>)}</tbody>
    </table></div>
    <h3 className="text-lg font-semibold">Verification problems</h3>
    <div className="text-sm space-y-2">
      {explanation.manifestProblems.length ? <ul>{explanation.manifestProblems.map((code, i) => <li key={i}>Manifest: {code}</li>)}</ul> : <p>No manifest consistency problems reported.</p>}
      <Problems problems={explanation.problems} />
    </div>
    <h3 className="text-lg font-semibold">Signature trust and evidence anchors</h3>
    <p className="text-sm">{!explanation.envelopePresent ? "No signature envelope supplied." : !explanation.signatureChecked
      ? "Envelope supplied; signature verification was not performed." : explanation.envelopeResult
        ? `Envelope verification: ${explanation.envelopeResult}` : "Envelope checked. Inspect the signature results below."}</p>
    <ul className="text-sm space-y-2">{explanation.signatures.map((signature, i) => <li key={i} className="break-all">
      {signature.keyId || "No key ID"}: {signature.result} — {signature.identity ?? "No verified identity"}
    </li>)}</ul>
    <p className="text-sm break-all">Established signers: {explanation.signedBy.join(", ") || "None"}</p>
    <p className="text-sm break-all">Anchored run hashes: {explanation.anchors.join(", ") || "None"}</p>
    <p className="text-sm text-slate-600">Anchors require verified evidence and a trusted checkpoint signature. They bind evidence identities; they do not by themselves approve the checkpoint.</p>
    <details><summary className="cursor-pointer font-medium">Original manifest declarations and rule parameters</summary>
      <pre className="text-xs whitespace-pre-wrap break-all max-h-96 overflow-auto rounded border bg-white p-3 mt-2">{explanation.manifestJson}</pre>
    </details>
  </section>;
}
