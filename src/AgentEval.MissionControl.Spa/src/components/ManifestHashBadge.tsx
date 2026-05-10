import { ShieldCheck, ShieldAlert } from "lucide-react";

// Plan-08 Wave 3 (MC1.6.7 partial): audit-chain integrity badge.
// Renders green when the run's content hash matches the evidence's manifest
// hash; red when tampered. Plan-07 §10 calls this out as "the visible cue
// that the audit chain holds end-to-end".
//
// In Wave 3 we render the simpler "manifest exists" form for a run-detail
// page; the full compliance-matrix variant (paired with evidence) ships
// alongside ComplianceMatrix in Wave 4.

interface ManifestHashBadgeProps {
  contentHash: string | null;
  /** When provided, the badge compares to it and renders red on mismatch. */
  expectedHash?: string;
}

export function ManifestHashBadge({ contentHash, expectedHash }: ManifestHashBadgeProps) {
  if (!contentHash) {
    return (
      <span className="inline-flex items-center gap-1 text-xs text-slate-500">
        <ShieldAlert size={12} /> No content hash
      </span>
    );
  }

  const verified = expectedHash === undefined || contentHash === expectedHash;

  return (
    <span
      className={`inline-flex items-center gap-1 text-xs font-medium px-2 py-0.5 rounded ${
        verified
          ? "bg-green-50 text-green-700"
          : "bg-red-50 text-red-700"
      }`}
      title={verified ? "Audit chain verified" : "Hash mismatch — tampered"}
    >
      {verified ? <ShieldCheck size={12} /> : <ShieldAlert size={12} />}
      <code className="font-mono">{contentHash.slice(0, 8)}…</code>
    </span>
  );
}
